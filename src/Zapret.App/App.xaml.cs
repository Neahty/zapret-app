using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Zapret.Core;
using Zapret.Core.ViewModels;

namespace Zapret.App;

public partial class App : Application
{
    private const string MutexName = @"Global\ZapretApp-SingleInstance";
    private const string ShowEventName = @"Global\ZapretApp-Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private HttpConnectivityProbe? _probe;
    private bool _hintShown;
    private bool _exiting;

    public static string InstallRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretApp");

    /// <summary>Копия приложения для автозапуска — в папке, куда без прав администратора не записать.</summary>
    private static string DeployRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZapretApp");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => _exiting = true; // не мешаем Windows выключаться

        // Манифест просит права администратора, но при запуске из терминала или IDE Windows может
        // его проигнорировать. Без прав нельзя ни распаковать zapret в ProgramData, ни поставить службу.
        if (!IsElevated())
        {
            RelaunchElevated(e.Args);
            Shutdown();
            return;
        }

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }
        ListenForShowRequests();

        MainViewModel vm;
        try
        {
            vm = BuildViewModel();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось подготовить zapret:\n" + ex.Message, "Запрет", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _window = new MainWindow { DataContext = vm };
        _window.Closing += (_, args) =>
        {
            if (_exiting) return;
            // Крестик прячет окно в трей — обход работает дальше.
            args.Cancel = true;
            _window.Hide();
            if (!_hintShown) { _tray?.ShowHint("Окно свёрнуто в трей. Обход продолжает работать."); _hintShown = true; }
        };
        _tray = new TrayIcon(vm, ShowWindow, ExitApp);

        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!startHidden) _window.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => vm.Tick();
        timer.Start();

        await vm.InitializeAsync();
    }

    private MainViewModel BuildViewModel()
    {
        var runner = new ProcessCommandRunner();

        // Без защищённой папки дальше нельзя: из неё служба запускается от имени SYSTEM.
        // Если папку надо пересоздать, сначала останавливаем работающий из неё winws.
        ZapretInstaller.PrepareSecureRoot(InstallRoot, releaseFiles: () => Task.Run(async () =>
        {
            await runner.RunAsync("sc.exe", ["stop", BypassController.ServiceName]);
            await runner.RunAsync("taskkill", ["/IM", "winws.exe", "/F"]);
            // WinDivert64.sys — драйвер ядра: он держит файл, пока его служба не остановлена.
            await runner.RunAsync("sc.exe", ["stop", "WinDivert"]);
            await runner.RunAsync("sc.exe", ["stop", "WinDivert14"]);
        }).GetAwaiter().GetResult());

        var paths = ZapretInstaller.EnsureInstalled(
            () => Assembly.GetExecutingAssembly().GetManifestResourceStream("zapret.zip")
                  ?? throw new InvalidOperationException("Архив zapret не вшит в приложение"),
            InstallRoot);

        var controller = new BypassController(
            paths, StrategyCatalog.Load(paths.Root), new WindowsServiceControl(runner), runner,
            new JsonSettingsStore(Path.Combine(InstallRoot, "settings.json")), TimeProvider.System);

        _probe = new HttpConnectivityProbe(TimeSpan.FromSeconds(6));
        var probe = _probe;
        return new MainViewModel(
            controller,
            new HealthChecker(probe),
            () => new StrategyTester(new ProcessWinwsLauncher(), probe, HealthChecker.AllUrls, TimeSpan.FromSeconds(2)),
            new DeployingAutostart(runner, Path.Combine(InstallRoot, "tmp")),
            new IpsetManager(paths.Lists),
            TimeProvider.System);
    }

    /// <summary>Перед включением автозапуска копирует приложение в Program Files и регистрирует копию.</summary>
    private sealed class DeployingAutostart(ICommandRunner runner, string xmlDir) : IAppAutostart
    {
        private AppAutostart Target()
        {
            var exe = AppDeployment.EnsureDeployed(AppContext.BaseDirectory, DeployRoot, Path.GetFileName(Environment.ProcessPath!));
            return new AppAutostart(runner, exe, WindowsIdentity.GetCurrent().Name, xmlDir);
        }

        private static AppAutostart Registered(ICommandRunner runner, string xmlDir) =>
            new(runner, Path.Combine(DeployRoot, Path.GetFileName(Environment.ProcessPath!)), WindowsIdentity.GetCurrent().Name, xmlDir);

        public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Registered(runner, xmlDir).IsEnabledAsync(ct);

        public Task SetAsync(bool enabled, CancellationToken ct = default) =>
            enabled ? Target().SetAsync(true, ct) : Registered(runner, xmlDir).SetAsync(false, ct);
    }

    /// <summary>
    /// Один экземпляр на компьютер. Мьютекс и событие доступны только администраторам, поэтому
    /// обычный пользователь не может заранее «занять» их имена и не дать приложению запуститься.
    /// </summary>
    private bool AcquireSingleInstance()
    {
        var mutexSecurity = new MutexSecurity();
        var eventSecurity = new EventWaitHandleSecurity();
        foreach (var sid in TrustedSids())
        {
            mutexSecurity.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
            eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(sid, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        }

        _singleInstance = MutexAcl.Create(true, MutexName, out var createdNew, mutexSecurity);
        if (!createdNew)
        {
            if (!IsTrustedOwner(_singleInstance.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
            {
                MessageBox.Show("Запуск заблокирован: другая программа заняла имя, которое использует Запрет.",
                    "Запрет", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            // Уже запущены мы сами — просто показываем то окно.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var other)) other.Set();
            return false;
        }

        _showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out var eventIsNew, eventSecurity);
        if (eventIsNew || IsTrustedOwner(_showEvent.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) return true;

        // Событие заранее создал кто-то чужой и мог бы дёргать наше окно — работаем без него.
        _showEvent.Dispose();
        _showEvent = null;
        return true;
    }

    private static IEnumerable<SecurityIdentifier> TrustedSids() =>
    [
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
    ];

    private static bool IsTrustedOwner(IdentityReference? owner) =>
        owner is SecurityIdentifier sid && (TrustedSids().Contains(sid) || sid == WindowsIdentity.GetCurrent().User);

    private static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static void RelaunchElevated(string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = CommandLine.Join(args),
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Пользователь нажал «Нет» в окне UAC.
            MessageBox.Show("Запрету нужны права администратора: без них Windows не даёт включить обход блокировок.",
                "Запрет", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ListenForShowRequests()
    {
        var thread = new Thread(() =>
        {
            while (_showEvent?.WaitOne() == true && !_exiting)
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true, Name = "ShowWindowListener" };
        thread.Start();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        _tray?.Dispose();
        _probe?.Dispose();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Запрет — ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
