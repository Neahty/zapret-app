using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Zapret.Core.ViewModels;

public enum AppPage { Home, Strategies, Settings }

public enum CheckKind { None, Checking, Good, Partial, Bad }

public sealed partial class StrategyItem(string name) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsRecommended => Name == Strategy.DefaultName;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _result;
    [ObservableProperty] private CheckKind _kind;

    public void Apply(StrategyScore score)
    {
        Kind = score.Passed == 0 ? CheckKind.Bad : score.AllPassed ? CheckKind.Good : CheckKind.Partial;
        Result = score.Passed == 0
            ? "не работает"
            : $"{score.Passed}/{score.Total} · {Format.Ms(score.AverageLatency)}";
    }
}

public sealed partial class HealthItem(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private CheckKind _kind;
    [ObservableProperty] private string _detail = "—";
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly BypassController _controller;
    private readonly HealthChecker _health;
    private readonly Func<StrategyTester> _testerFactory;
    private readonly IAppAutostart _autostart;
    private readonly IpsetManager _ipset;
    private readonly TimeProvider _time;
    private int _ticks;
    private int _healthVersion;

    private readonly ZapretUpdater? _updater;

    public MainViewModel(BypassController controller, HealthChecker health, Func<StrategyTester> testerFactory,
        IAppAutostart autostart, IpsetManager ipset, TimeProvider time,
        ZapretUpdater? updater = null, string zapretVersion = ZapretInstaller.Version)
    {
        _updater = updater;
        ZapretVersion = zapretVersion;
        _controller = controller;
        _health = health;
        _testerFactory = testerFactory;
        _autostart = autostart;
        _ipset = ipset;
        _time = time;

        foreach (var s in controller.Strategies) Strategies.Add(new StrategyItem(s.Name));
        foreach (var g in HealthChecker.Groups) Health.Add(new HealthItem(g.Name));
    }

    public ObservableCollection<StrategyItem> Strategies { get; } = [];
    public ObservableCollection<HealthItem> Health { get; } = [];

    [ObservableProperty] private AppPage _currentPage;
    [ObservableProperty] private bool _isOn;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTitle))]
    private bool _isBusy;
    [ObservableProperty] private string _uptimeText = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private StrategyItem? _selectedStrategy;
    [ObservableProperty] private GameFilterMode _gameFilterMode;
    [ObservableProperty] private IpsetMode _ipsetMode;
    [ObservableProperty] private bool _launchWithWindows;
    [ObservableProperty] private bool _isAutoSelecting;
    [ObservableProperty] private string? _autoSelectProgress;

    /// <summary>Установленная версия zapret — показывается в настройках.</summary>
    public string ZapretVersion { get; }
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _updateFailed;

    /// <summary>Новая версия zapret установлена — приложению нужно перезапуститься на неё.</summary>
    public event Action? RestartRequested;

    public string StatusTitle => IsBusy ? "Подождите…" : IsOn ? "Обход включён" : "Обход выключен";

    partial void OnIsOnChanged(bool value) => OnPropertyChanged(nameof(StatusTitle));

    public async Task InitializeAsync()
    {
        var settings = _controller.Settings;
        MarkSelected(_controller.Resolve(settings.Strategy).Name);
        GameFilterMode = GameFilter.Load(_controller.Paths.GameFilterFile).Mode;
        IpsetMode = _ipset.GetMode();
        LaunchWithWindows = settings.LaunchWithWindows;

        // Главное: обход включён по умолчанию и сам поднимается при каждом запуске.
        await RunBusyAsync(() => _controller.EnsureStartupStateAsync());
        await SyncAutostartAsync(settings.LaunchWithWindows);
        if (IsOn) await CheckHealthAsync();
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsBusy) return;
        var turnOn = !IsOn;
        await RunBusyAsync(() => turnOn ? _controller.EnableAsync(SelectedStrategy?.Name) : _controller.DisableAsync());
        if (IsOn) await CheckHealthAsync();
        else ResetHealth();
    }

    [RelayCommand]
    private async Task SelectStrategyAsync(StrategyItem? item)
    {
        if (item is null || IsBusy || item == SelectedStrategy) return;
        if (_controller.Settings.Enabled)
        {
            await RunBusyAsync(() => _controller.EnableAsync(item.Name));
            if (IsOn) await CheckHealthAsync();
        }
        else
        {
            _controller.SetPreferredStrategy(item.Name);
            MarkSelected(item.Name);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AutoSelectAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsAutoSelecting = true;
        AutoSelectProgress = "Запускаю проверку…";
        foreach (var s in Strategies) { s.Kind = CheckKind.None; s.Result = null; }
        var done = 0;
        // Не Progress<T>: он откладывает обновление в пул потоков, и строки списка меняются с опозданием.
        // Контроллер и так продолжает работу в потоке окна, поэтому применяем результат сразу.
        var progress = new InlineProgress<StrategyScore>(score =>
        {
            Strategies.FirstOrDefault(s => s.Name == score.Name)?.Apply(score);
            AutoSelectProgress = $"Проверено {++done} из {Strategies.Count}";
        });

        StrategyScore? best = null;
        await RunBusyAsync(async () =>
        {
            best = await _controller.AutoSelectAsync(_testerFactory(), progress, ct);
            if (best is null)
                throw new InvalidOperationException(
                    "Ни одна стратегия не открыла Discord и YouTube. Проверьте интернет и отключите VPN.");
        });
        IsAutoSelecting = false;
        AutoSelectProgress = best is null ? null : $"Лучшая: {best.Name}";
        if (IsOn) await CheckHealthAsync();
    }

    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        var version = ++_healthVersion;
        foreach (var h in Health) { h.Kind = CheckKind.Checking; h.Detail = "проверяю…"; }
        var results = await _health.CheckAsync();
        // Пока шла проверка, обход могли выключить или запустить новую проверку — старый результат не показываем.
        if (version != _healthVersion) return;
        foreach (var r in results)
        {
            var item = Health.FirstOrDefault(h => h.Name == r.Name);
            if (item is null) continue;
            item.Kind = r.Ok ? CheckKind.Good : CheckKind.Bad;
            item.Detail = r.Ok ? Format.Ms(r.Latency) : "недоступен";
        }
    }

    /// <summary>Есть ли новая версия zapret; если есть — скачать, установить и перезапуститься на неё.</summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (_updater is null || IsBusy || IsUpdating) return;
        IsUpdating = true;
        IsBusy = true;
        UpdateFailed = false;
        UpdateStatus = "Проверяю…";
        var switched = false;
        try
        {
            var release = await _updater.CheckAsync(ZapretVersion);
            if (release is null)
            {
                UpdateStatus = $"Установлена последняя версия {ZapretVersion}";
                return;
            }

            UpdateStatus = $"Нашлась версия {release.Version}, скачиваю…";
            await _updater.DownloadAndInstallAsync(release, _controller.Paths);
            UpdateStatus = $"Переключаюсь на {release.Version}…";
            await _controller.SwitchZapretVersionAsync(release.Version);
            switched = true;
            UpdateStatus = $"Версия {release.Version} установлена, перезапускаю…";
        }
        catch (Exception e)
        {
            UpdateFailed = true;
            UpdateStatus = "Не удалось обновить: " + e.Message;
            // Если сбой случился уже при переключении, обход мог остаться остановленным — поднимаем прежнюю версию.
            if (_controller.Settings.Enabled && !_controller.GetStatus().IsOn)
            {
                try { await _controller.ReapplyAsync(); }
                catch (Exception restore) { ErrorMessage = restore.Message; }
            }
        }
        finally
        {
            IsBusy = false;
            IsUpdating = false;
            if (!switched) RefreshStatus();
        }
        if (switched) RestartRequested?.Invoke();
    }

    [RelayCommand]
    private void Navigate(AppPage page) => CurrentPage = page;

    [RelayCommand]
    private async Task SetGameFilterAsync(GameFilterMode mode)
    {
        if (mode == GameFilterMode || IsBusy) return;
        var file = _controller.Paths.GameFilterFile;
        var previous = GameFilter.Load(file);
        (previous with { Mode = mode }).Save(file);
        GameFilterMode = mode;
        await RunBusyAsync(() => _controller.ReapplyAsync());
        if (ErrorMessage is null) return;

        // Не применилось — возвращаем как было, чтобы экран не врал о настройке.
        previous.Save(file);
        GameFilterMode = previous.Mode;
    }

    [RelayCommand]
    private async Task SetIpsetAsync(IpsetMode mode)
    {
        if (mode == IpsetMode || IsBusy) return;
        await RunBusyAsync(async () =>
        {
            _ipset.SetMode(mode);
            await _controller.ReapplyAsync();
        });
        IpsetMode = _ipset.GetMode();
    }

    [RelayCommand]
    private async Task SetLaunchWithWindowsAsync(bool value)
    {
        _controller.SetLaunchWithWindows(value);
        LaunchWithWindows = value;
        await SyncAutostartAsync(value);
    }

    /// <summary>Вызывается окном раз в секунду: обновляет таймер и раз в 5 секунд — статус службы.</summary>
    public void Tick()
    {
        if (!IsBusy && ++_ticks % 5 == 0) RefreshStatus();
        else UpdateUptime();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { ErrorMessage = e.Message; }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    private async Task SyncAutostartAsync(bool wanted)
    {
        try
        {
            if (await _autostart.IsEnabledAsync() != wanted) await _autostart.SetAsync(wanted);
        }
        catch (Exception e) { ErrorMessage = e.Message; }
    }

    private void RefreshStatus()
    {
        var status = _controller.GetStatus();
        IsOn = status.IsOn;
        if (status.Strategy is not null) MarkSelected(status.Strategy);
        UpdateUptime();
    }

    private void UpdateUptime()
    {
        var since = IsOn ? _controller.Settings.EnabledAt : null;
        UptimeText = since is null ? "" : Format.Duration(_time.GetUtcNow() - since.Value);
    }

    private void MarkSelected(string name)
    {
        foreach (var s in Strategies) s.IsSelected = s.Name == name;
        SelectedStrategy = Strategies.FirstOrDefault(s => s.IsSelected);
    }

    private void ResetHealth()
    {
        _healthVersion++;
        foreach (var h in Health) { h.Kind = CheckKind.None; h.Detail = "—"; }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

public static class Format
{
    public static string Ms(TimeSpan? t) => t is null ? "—" : $"{(int)t.Value.TotalMilliseconds} мс";

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var clock = $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
        return t.Days > 0 ? $"{t.Days} д {clock}" : clock;
    }
}
