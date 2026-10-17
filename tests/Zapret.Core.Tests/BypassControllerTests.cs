using Zapret.Core;

namespace Zapret.Core.Tests;

public sealed class BypassControllerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly ZapretCopy _zapret = new();
    private readonly FakeServiceControl _services = new();
    private readonly FakeRunner _runner = new();
    private readonly MemorySettingsStore _store = new();
    private readonly FakeTime _time = new(T0);
    private readonly BypassController _sut;

    public BypassControllerTests() =>
        _sut = new BypassController(_zapret.Paths, _zapret.Strategies, _services, _runner, _store, _time);

    public void Dispose() => _zapret.Dispose();

    private ServiceState ZapretState => _services.GetState(BypassController.ServiceName);

    // ---------- Главное требование: обход включён по умолчанию ----------

    [Fact]
    public async Task FirstLaunch_InstallsAndStartsAutoStartService_WithDefaultStrategy()
    {
        var action = await _sut.EnsureStartupStateAsync();

        action.Should().Be(StartupAction.Installed);
        ZapretState.Should().Be(ServiceState.Running);
        _store.Current.Enabled.Should().BeTrue();
        _store.Current.Strategy.Should().Be("general");
        _services.Tags["zapret/zapret-discord-youtube"].Should().Be("general");
    }

    [Fact]
    public async Task Startup_DoesNothing_WhenUserTurnedBypassOff()
    {
        _store.Save(new AppSettings { Enabled = false });

        var action = await _sut.EnsureStartupStateAsync();

        action.Should().Be(StartupAction.None);
        _services.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Startup_OnlyStartsService_WhenInstalledCorrectlyButStopped()
    {
        await _sut.EnableAsync("general (ALT2)");
        await _services.StopAsync(BypassController.ServiceName);
        _services.Calls.Clear();

        var action = await _sut.EnsureStartupStateAsync();

        action.Should().Be(StartupAction.Started);
        _services.Calls.Should().Equal("start zapret");
    }

    [Fact]
    public async Task Startup_DoesNothing_WhenAlreadyRunningWithSameSettings()
    {
        await _sut.EnableAsync(null);
        _services.Calls.Clear();

        (await _sut.EnsureStartupStateAsync()).Should().Be(StartupAction.None);
        _services.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Startup_Reinstalls_WhenServiceWasInstalledWithOtherArguments()
    {
        _services.Preinstall("zapret", "\"C:\\old\\winws.exe\" --old", ServiceState.Running);

        var action = await _sut.EnsureStartupStateAsync();

        action.Should().Be(StartupAction.Installed);
        _services.GetCommandLine("zapret").Should().Be(_sut.BuildCommandLine(_sut.Resolve("general")));
    }

    // ---------- Включение / выключение ----------

    [Fact]
    public async Task Enable_InstallsServiceThatRunsWinwsWithStrategyArguments()
    {
        await _sut.EnableAsync("general (ALT)");

        var commandLine = _services.GetCommandLine("zapret")!;
        commandLine.Should().StartWith(CommandLine.Quote(_zapret.Paths.WinwsExe) + " --wf-tcp=");
        commandLine.Should().Contain(_zapret.Paths.Lists).And.NotContain("%");
        _store.Current.Strategy.Should().Be("general (ALT)");
        _store.Current.EnabledAt.Should().Be(T0);
    }

    [Fact]
    public async Task Enable_KillsStandaloneWinws_AndEnablesTcpTimestamps()
    {
        await _sut.EnableAsync(null);

        _runner.Calls.Should().Contain(c => c.Exe == "taskkill" && c.Args.SequenceEqual(new[] { "/IM", "winws.exe", "/F" }));
        _runner.Calls.Should().Contain(c => c.Exe == "netsh" && c.Args.Contains("timestamps=enabled"));
    }

    [Fact]
    public async Task Enable_CreatesUserListsThatStrategiesNeed()
    {
        await _sut.EnableAsync(null);

        File.Exists(Path.Combine(_zapret.Paths.Lists, "list-general-user.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Enable_FallsBackToDefault_WhenStrategyNameUnknown()
    {
        await _sut.EnableAsync("deleted strategy");

        _store.Current.Strategy.Should().Be("general");
    }

    [Fact]
    public async Task Enable_Throws_AndDoesNotRememberStrategy_WhenServiceFailsToStart()
    {
        _store.Save(new AppSettings { Strategy = "general" });
        _services.FailToStart = true;

        var act = () => _sut.EnableAsync("general (ALT5)");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*не запустилась*");
        _store.Current.Strategy.Should().Be("general");
    }

    [Fact]
    public async Task Enable_RemovesHalfInstalledService_WhenBypassWasOffAndStartFailed()
    {
        // Иначе служба с автозапуском включилась бы после перезагрузки, хотя приложение пишет «выключен».
        _store.Save(new AppSettings { Enabled = false });
        _services.FailToStart = true;

        var act = () => _sut.EnableAsync(null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        ZapretState.Should().Be(ServiceState.NotInstalled);
        _store.Current.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task AutoSelect_KillsTestWinws_WhenBypassWasOffAndNothingWorked()
    {
        _store.Save(new AppSettings { Enabled = false });

        var best = await _sut.AutoSelectAsync(TesterWhereWorks(), null);

        best.Should().BeNull();
        ZapretState.Should().Be(ServiceState.NotInstalled);
        _runner.Calls.Last().Exe.Should().Be("taskkill");
    }

    [Fact]
    public async Task Enable_KeepsUptime_WhenSameStrategyReapplied_ButResetsOnNewStrategy()
    {
        await _sut.EnableAsync("general");
        _time.Now = T0.AddHours(1);

        await _sut.EnableAsync("general");
        _store.Current.EnabledAt.Should().Be(T0);

        await _sut.EnableAsync("general (ALT2)");
        _store.Current.EnabledAt.Should().Be(T0.AddHours(1));
    }

    [Fact]
    public async Task Disable_RemovesServiceAndWinDivert_AndRemembersItIsOff()
    {
        await _sut.EnableAsync(null);
        _services.Preinstall("WinDivert", "driver", ServiceState.Running);

        await _sut.DisableAsync();

        ZapretState.Should().Be(ServiceState.NotInstalled);
        _services.GetState("WinDivert").Should().Be(ServiceState.NotInstalled);
        _store.Current.Enabled.Should().BeFalse();
        _store.Current.EnabledAt.Should().BeNull();
        (await _sut.EnsureStartupStateAsync()).Should().Be(StartupAction.None, "после выключения при запуске ничего не включается");
    }

    [Fact]
    public async Task GameFilterChange_IsPickedUpByReapply()
    {
        await _sut.EnableAsync(null);
        new GameFilter(GameFilterMode.All, "1024-65535", "1024-65535").Save(_zapret.Paths.GameFilterFile);

        await _sut.ReapplyAsync();

        _services.GetCommandLine("zapret").Should().Contain(",1024-65535");
    }

    // ---------- Автоподбор стратегии ----------

    private StrategyTester TesterWhereWorks(params string[] workingStrategyFragments)
    {
        // «Сайт открывается», только если запущена стратегия с нужным фрагментом в аргументах.
        var launcher = new FakeLauncher();
        var probe = new FakeProbe(_ =>
        {
            var current = launcher.Started.LastOrDefault();
            var works = current is not null && workingStrategyFragments.Any(f => current.Any(a => a.Contains(f)));
            return new ProbeResult(works, TimeSpan.FromMilliseconds(120));
        });
        return new StrategyTester(launcher, probe, HealthChecker.AllUrls, TimeSpan.Zero);
    }

    [Fact]
    public async Task AutoSelect_EnablesFirstFullyWorkingStrategy_AndStopsSearching()
    {
        // syndata есть только в ALT5 и ALT7 — первой по порядку проверяется ALT5.
        var progress = new List<StrategyScore>();

        var best = await _sut.AutoSelectAsync(TesterWhereWorks("syndata"), new SyncProgress<StrategyScore>(progress.Add));

        best!.Name.Should().Be("general (ALT5)");
        best.AllPassed.Should().BeTrue();
        _store.Current.Strategy.Should().Be("general (ALT5)");
        ZapretState.Should().Be(ServiceState.Running);
        progress.Last().Name.Should().Be("general (ALT5)", "после идеальной стратегии поиск останавливается");
        progress.Count.Should().BeLessThan(_zapret.Strategies.Count);
    }

    [Fact]
    public async Task AutoSelect_RestoresPreviousStrategy_WhenNothingWorks()
    {
        await _sut.EnableAsync("general (ALT3)");

        var best = await _sut.AutoSelectAsync(TesterWhereWorks(), null);

        best.Should().BeNull();
        _store.Current.Strategy.Should().Be("general (ALT3)");
        ZapretState.Should().Be(ServiceState.Running);
    }

    [Fact]
    public async Task AutoSelect_RestoresPreviousStrategy_WhenCancelled()
    {
        await _sut.EnableAsync("general (ALT3)");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _sut.AutoSelectAsync(TesterWhereWorks("syndata"), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Current.Strategy.Should().Be("general (ALT3)");
        ZapretState.Should().Be(ServiceState.Running);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
