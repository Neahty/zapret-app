namespace Zapret.Core;

public sealed record BypassStatus(ServiceState Service, string? Strategy, DateTimeOffset? EnabledAt)
{
    public bool IsOn => Service == ServiceState.Running;
}

public enum StartupAction { None, Started, Installed }

/// <summary>
/// Включает и выключает обход. Обход живёт в службе Windows «zapret» с автозапуском —
/// поэтому после перезагрузки он поднимается сам, без открытия приложения.
/// Имя службы и значение в реестре те же, что у service.bat, так что они не конфликтуют.
/// </summary>
public sealed class BypassController(
    ZapretPaths paths,
    IReadOnlyList<Strategy> strategies,
    IServiceControl services,
    ICommandRunner runner,
    ISettingsStore store,
    TimeProvider time)
{
    public const string ServiceName = "zapret";
    public const string StrategyTag = "zapret-discord-youtube";
    private static readonly string[] WinDivertServices = ["WinDivert", "WinDivert14"];

    public IReadOnlyList<Strategy> Strategies => strategies;
    public ZapretPaths Paths => paths;

    private AppSettings? _settings;

    /// <summary>Настройки из памяти: окно спрашивает их каждую секунду, читать файл так часто незачем.</summary>
    public AppSettings Settings => _settings ??= store.Load();

    private void Save(AppSettings settings)
    {
        store.Save(settings);
        _settings = settings;
    }

    public Strategy Resolve(string? name) =>
        strategies.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? strategies.FirstOrDefault(s => s.Name == Strategy.DefaultName)
        ?? strategies.FirstOrDefault()
        ?? throw new InvalidOperationException("Не найдено ни одной стратегии zapret");

    public string BuildCommandLine(Strategy strategy) =>
        CommandLine.Quote(paths.WinwsExe) + " " +
        CommandLine.Join(strategy.Render(paths, GameFilter.Load(paths.GameFilterFile)));

    public BypassStatus GetStatus()
    {
        var settings = Settings;
        return new BypassStatus(services.GetState(ServiceName), settings.Strategy, settings.EnabledAt);
    }

    /// <summary>Ставит службу со стратегией и запускает её. Настройка «включено» запоминается.</summary>
    public async Task EnableAsync(string? strategyName, CancellationToken ct = default)
    {
        var strategy = Resolve(strategyName);
        UserLists.Ensure(paths.Lists);
        var commandLine = BuildCommandLine(strategy);
        var settings = Settings;

        await StopRuntimeAsync(ct);
        // Как tcp_enable в service.bat: некоторые стратегии требуют TCP timestamps.
        await runner.RunAsync("netsh", ["interface", "tcp", "set", "global", "timestamps=enabled"], ct);

        try
        {
            await services.InstallAsync(ServiceName, "zapret", "Zapret DPI bypass software", commandLine, ct);
            services.SetTag(ServiceName, StrategyTag, strategy.Name);
            await services.StartAsync(ServiceName, ct);

            if (services.GetState(ServiceName) != ServiceState.Running)
                throw new InvalidOperationException("Служба zapret не запустилась. Проверьте, не запущен ли другой обход или VPN.");
        }
        catch when (!settings.Enabled)
        {
            // Обход был выключен — не оставляем службу с автозапуском, иначе после перезагрузки
            // он внезапно заработает, хотя приложение будет показывать «выключен».
            await TryAsync(() => services.DeleteAsync(ServiceName, CancellationToken.None));
            throw;
        }

        Save(settings with
        {
            Enabled = true,
            Strategy = strategy.Name,
            EnabledAt = settings.Enabled && settings.Strategy == strategy.Name && settings.EnabledAt is not null
                ? settings.EnabledAt
                : time.GetUtcNow(),
        });
    }

    /// <summary>Полностью выключает обход (как «Remove Services» в service.bat) и запоминает это.</summary>
    public async Task DisableAsync(CancellationToken ct = default)
    {
        await StopRuntimeAsync(ct);
        await services.DeleteAsync(ServiceName, ct);
        foreach (var driver in WinDivertServices)
        {
            await TryAsync(() => services.StopAsync(driver, ct));
            await TryAsync(() => services.DeleteAsync(driver, ct));
        }
        Save(Settings with { Enabled = false, EnabledAt = null });
    }

    /// <summary>
    /// Вызывается при каждом запуске приложения: если обход должен быть включён, но не работает —
    /// запускает или переустанавливает службу. Если пользователь выключил обход — ничего не трогает.
    /// </summary>
    public async Task<StartupAction> EnsureStartupStateAsync(CancellationToken ct = default)
    {
        var settings = Settings;
        if (!settings.Enabled) return StartupAction.None;

        var strategy = Resolve(settings.Strategy);
        var expected = BuildCommandLine(strategy);
        var installed = services.GetCommandLine(ServiceName);
        var state = services.GetState(ServiceName);

        if (state != ServiceState.NotInstalled && installed == expected)
        {
            if (state == ServiceState.Running) return StartupAction.None;
            await services.StartAsync(ServiceName, ct);
            return StartupAction.Started;
        }

        await EnableAsync(strategy.Name, ct);
        return StartupAction.Installed;
    }

    /// <summary>Запоминает стратегию, не включая обход (когда он выключен).</summary>
    public void SetPreferredStrategy(string name) =>
        Save(Settings with { Strategy = Resolve(name).Name });

    public void SetLaunchWithWindows(bool value) =>
        Save(Settings with { LaunchWithWindows = value });

    /// <summary>
    /// Готовит переход на новую версию zapret: останавливает обход и драйвер WinDivert старой версии
    /// (его служба указывает на старый WinDivert64.sys) и запоминает новую версию. Саму службу
    /// перенастроит следующий запуск приложения — путь к winws.exe у неё изменится.
    /// </summary>
    public async Task SwitchZapretVersionAsync(string version, CancellationToken ct = default)
    {
        await StopRuntimeAsync(ct);
        foreach (var driver in WinDivertServices)
        {
            await TryAsync(() => services.StopAsync(driver, ct));
            await TryAsync(() => services.DeleteAsync(driver, ct));
        }
        Save(Settings with { ZapretVersion = version });
    }

    /// <summary>Перезапускает обход, чтобы подхватить новые настройки (игровой фильтр, IPSet).</summary>
    public async Task ReapplyAsync(CancellationToken ct = default)
    {
        var settings = Settings;
        if (settings.Enabled) await EnableAsync(settings.Strategy, ct);
    }

    /// <summary>
    /// Перебирает стратегии и включает лучшую. Если ни одна не помогла — возвращает прежнюю.
    /// </summary>
    public async Task<StrategyScore?> AutoSelectAsync(
        StrategyTester tester, IProgress<StrategyScore>? progress, CancellationToken ct = default)
    {
        var before = Settings;
        var gameFilter = GameFilter.Load(paths.GameFilterFile);
        var scores = new List<StrategyScore>();
        StrategyScore? best = null;
        Exception? restoreError = null;

        try
        {
            await StopRuntimeAsync(ct);
            UserLists.Ensure(paths.Lists);
            foreach (var strategy in strategies)
            {
                ct.ThrowIfCancellationRequested();
                var score = await tester.TestAsync(strategy, paths, gameFilter, ct);
                scores.Add(score);
                progress?.Report(score);
                if (score.AllPassed) break; // идеальная найдена — дальше можно не искать
            }
            best = StrategyTester.PickBest(scores);
        }
        finally
        {
            // Восстановление не должно прятать исходную ошибку (например, отмену) — запоминаем отдельно.
            try
            {
                if (best is not null || before.Enabled)
                    await EnableAsync(best?.Name ?? before.Strategy, CancellationToken.None);
                else
                    await runner.RunAsync("taskkill", ["/IM", "winws.exe", "/F"], CancellationToken.None);
            }
            catch (Exception e) { restoreError = e; }
        }
        if (restoreError is not null) throw restoreError;
        return best;
    }

    private async Task StopRuntimeAsync(CancellationToken ct)
    {
        await TryAsync(() => services.StopAsync(ServiceName, ct));
        // Вдруг запущен обычный general.bat — он тоже держит WinDivert.
        await runner.RunAsync("taskkill", ["/IM", "winws.exe", "/F"], ct);
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException) { }
    }
}
