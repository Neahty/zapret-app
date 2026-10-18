using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zapret.Core;
using Zapret.Core.ViewModels;

namespace Preview;

/// <summary>Снимки экранов приложения для проверки дизайна: dotnet run -- &lt;папка&gt; &lt;zapret.zip&gt;</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        var app = new Zapret.App.App();
        app.InitializeComponent();
        Zapret.App.Reveal.Enabled = false; // снимаем готовый кадр, без анимации появления
        // Без Application.Run ресурсы остаются «отложенными» и ломаются на втором окне - загружаем их сразу.
        foreach (var key in app.Resources.Keys.Cast<object>().ToList()) _ = app.Resources[key];

        var tmp = Path.Combine(Path.GetTempPath(), "zapret-preview", Guid.NewGuid().ToString("N"));
        var paths = ZapretInstaller.EnsureInstalled(() => File.OpenRead(args[1]), tmp);
        var services = new DemoServices();
        var probe = new DemoProbe();
        var controller = new BypassController(paths, StrategyCatalog.Load(paths.Root), services, new DemoRunner(),
            new DemoStore(), new DemoTime());
        var vm = new MainViewModel(controller, new HealthChecker(probe),
            () => new StrategyTester(new DemoLauncher(), probe, HealthChecker.AllUrls, TimeSpan.Zero),
            new DemoAutostart(), new IpsetManager(paths.Lists), new DemoTime { Shift = TimeSpan.FromMinutes(42).Add(TimeSpan.FromSeconds(17)) },
            new Zapret.Core.Telegram.TelegramController(services, () => @"C:\Program Files\ZapretApp\Zapret.exe", "1.1.0"),
            new DemoTelegramCheck());

        vm.InitializeAsync().GetAwaiter().GetResult();
        vm.Tick();

        if (Environment.GetEnvironmentVariable("PREVIEW_NAV_TEST") == "1")
        {
            // Как в настоящем приложении: одно окно, переключаем разделы.
            var win = new Zapret.App.MainWindow { DataContext = vm, Left = -6000, ShowActivated = false };
            win.Show();
            foreach (var page in new[] { AppPage.Strategies, AppPage.Settings, AppPage.Home })
            {
                vm.CurrentPage = page;
                win.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                win.UpdateLayout();
                Console.WriteLine($"OK: {page}");
            }
            return;
        }

        // Один снимок на запуск (третий аргумент): у второго окна без Application.Run ломаются ресурсы.
        foreach (var (s, i) in vm.Strategies.Select((s, i) => (s, i)).Take(6))
            s.Apply(new StrategyScore(s.Name, i == 2 ? 3 : i == 4 ? 0 : 6, 6, TimeSpan.FromMilliseconds(90 + i * 37)));
        vm.Strategies[1].IsBest = true;
        var shot = args.Length > 2 ? args[2] : "home-on";
        switch (shot)
        {
            case "strategies":
                vm.CurrentPage = AppPage.Strategies;
                vm.AutoSelectProgress = "Лучшая: ALT - теперь она по умолчанию";
                break;
            case "settings":
                vm.CurrentPage = AppPage.Settings;
                break;
            case "home-off":
                vm.ToggleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                break;
            case "home-error":
                vm.ToggleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                services.FailToStart = true;
                vm.ToggleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                break;
        }
        Snap(vm, outDir, shot + ".png");

        try { Directory.Delete(tmp, true); } catch (IOException) { }
    }

    private static IEnumerable<string> FindText(DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock tb) yield return tb.Text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var t in FindText(VisualTreeHelper.GetChild(root, i))) yield return t;
    }

    private static void Snap(MainViewModel vm, string dir, string name)
    {
        // Каждый снимок - со свежего окна: RenderTargetBitmap у скрытого окна отдаёт устаревшую картинку.
        var w = new Zapret.App.MainWindow
        {
            DataContext = vm, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowActivated = false,
        };
        w.Show();
        try { Snap(w, dir, name); }
        finally { w.Hide(); } // Close() ломает отложенные ресурсы приложения для следующих окон
    }

    private static void Snap(Window w, string dir, string name)
    {
        // Даём WPF реально перерисовать окно: без цикла сообщений снимок берёт устаревшую картинку.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        var content = (FrameworkElement)w.Content;
        // Окно за пределами экрана может не получить размер - раскладываем содержимое вручную.
        content.Measure(new Size(w.Width, w.Height));
        content.Arrange(new Rect(0, 0, w.Width, w.Height));
        content.UpdateLayout();
        const double scale = 2;
        var width = (int)(content.ActualWidth * scale);
        var height = (int)(content.ActualHeight * scale);

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        if (Environment.GetEnvironmentVariable("PREVIEW_DEBUG") == "1")
            Console.WriteLine($"{name}: vm.StatusTitle={((MainViewModel)w.DataContext).StatusTitle}; на экране: " +
                string.Join(" | ", FindText(content).Where(t => t.StartsWith("Обход"))));
        var bmp = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(background);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, name));
        encoder.Save(fs);
    }
}

internal static class TreeHelpers { }

internal sealed class DemoTelegramCheck : Zapret.Core.Telegram.ITelegramCheck
{
    public Task<ProbeResult?> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult<ProbeResult?>(new ProbeResult(true, TimeSpan.FromMilliseconds(214)));
}

internal sealed class DemoServices : IServiceControl
{
    private readonly Dictionary<string, (ServiceState State, string Cmd)> _s = new();
    public bool FailToStart { get; set; }
    public ServiceState GetState(string name) => _s.TryGetValue(name, out var v) ? v.State : ServiceState.NotInstalled;
    public string? GetCommandLine(string name) => _s.TryGetValue(name, out var v) ? v.Cmd : null;
    public Task InstallAsync(string name, string d, string desc, string cmd, CancellationToken ct = default, string? account = null) { _s[name] = (ServiceState.Stopped, cmd); return Task.CompletedTask; }
    public Task StartAsync(string name, CancellationToken ct = default) { if (!FailToStart) _s[name] = (ServiceState.Running, _s[name].Cmd); return Task.CompletedTask; }
    public Task StopAsync(string name, CancellationToken ct = default) { if (_s.TryGetValue(name, out var v)) _s[name] = (ServiceState.Stopped, v.Cmd); return Task.CompletedTask; }
    public Task DeleteAsync(string name, CancellationToken ct = default) { _s.Remove(name); return Task.CompletedTask; }
    public void SetTag(string name, string valueName, string value) { }
}

internal sealed class DemoRunner : ICommandRunner
{
    public Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default) =>
        Task.FromResult(new CommandResult(0, ""));
}

internal sealed class DemoStore : ISettingsStore
{
    private AppSettings _s = new();
    public AppSettings Load() => _s;
    public void Save(AppSettings settings) => _s = settings;
}

internal sealed class DemoTime : TimeProvider
{
    private static readonly DateTimeOffset Base = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    public TimeSpan Shift { get; init; }
    public override DateTimeOffset GetUtcNow() => Base + Shift;
}

internal sealed class DemoProbe : IConnectivityProbe
{
    public Task<ProbeResult> ProbeAsync(Uri url, CancellationToken ct = default) =>
        Task.FromResult(new ProbeResult(true, TimeSpan.FromMilliseconds(url.Host.Contains("discord") ? 87 : 134)));
}

internal sealed class DemoLauncher : IWinwsLauncher
{
    public Task<IAsyncDisposable> StartAsync(string exe, IReadOnlyList<string> args, string workDir, CancellationToken ct = default) =>
        Task.FromResult<IAsyncDisposable>(new Nop());

    private sealed class Nop : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}

internal sealed class DemoAutostart : IAppAutostart
{
    private bool _on;
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(_on);
    public Task SetAsync(bool enabled, CancellationToken ct = default) { _on = enabled; return Task.CompletedTask; }
}
