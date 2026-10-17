using Zapret.Core;
using Zapret.Core.ViewModels;

namespace Zapret.Core.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly ZapretCopy _zapret = new();
    private readonly FakeServiceControl _services = new();
    private readonly MemorySettingsStore _store = new();
    private readonly FakeTime _time = new(T0);
    private readonly FakeAutostart _autostart = new();
    private readonly FakeProbe _probe = FakeProbe.AllOk(80);
    private readonly BypassController _controller;
    private readonly MainViewModel _sut;

    public MainViewModelTests()
    {
        _controller = new BypassController(_zapret.Paths, _zapret.Strategies, _services, new FakeRunner(), _store, _time);
        _sut = new MainViewModel(_controller, new HealthChecker(_probe),
            () => new StrategyTester(new FakeLauncher(), _probe, HealthChecker.AllUrls, TimeSpan.Zero),
            _autostart, new IpsetManager(_zapret.Paths.Lists), _time);
    }

    public void Dispose() => _zapret.Dispose();

    [Fact]
    public async Task Initialize_OnFirstRun_TurnsBypassOnWithoutAnyClick()
    {
        await _sut.InitializeAsync();

        _sut.IsOn.Should().BeTrue();
        _sut.StatusTitle.Should().Be("Обход включён");
        _sut.SelectedStrategy!.Name.Should().Be("general");
        _sut.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_AddsAppToAutostart_ByDefault()
    {
        await _sut.InitializeAsync();

        _autostart.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Initialize_ChecksDiscordAndYoutube_WhenOn()
    {
        await _sut.InitializeAsync();

        _sut.Health.Should().OnlyContain(h => h.Kind == CheckKind.Good && h.Detail == "80 мс");
    }

    [Fact]
    public async Task Initialize_KeepsBypassOff_WhenUserTurnedItOffEarlier()
    {
        _store.Save(new AppSettings { Enabled = false });

        await _sut.InitializeAsync();

        _sut.IsOn.Should().BeFalse();
        _services.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Toggle_TurnsOffThenOnAgain()
    {
        await _sut.InitializeAsync();

        await _sut.ToggleCommand.ExecuteAsync(null);
        _sut.IsOn.Should().BeFalse();
        _sut.Health.Should().OnlyContain(h => h.Kind == CheckKind.None);
        _store.Current.Enabled.Should().BeFalse();

        await _sut.ToggleCommand.ExecuteAsync(null);
        _sut.IsOn.Should().BeTrue();
        _store.Current.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Toggle_ShowsError_AndStaysOff_WhenServiceCannotStart()
    {
        _store.Save(new AppSettings { Enabled = false });
        await _sut.InitializeAsync();
        _services.FailToStart = true;

        await _sut.ToggleCommand.ExecuteAsync(null);

        _sut.IsOn.Should().BeFalse();
        _sut.ErrorMessage.Should().Contain("не запустилась");
        _sut.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task SelectStrategy_WhenOn_SwitchesRunningServiceToIt()
    {
        await _sut.InitializeAsync();
        var alt4 = _sut.Strategies.Single(s => s.Name == "general (ALT4)");

        await _sut.SelectStrategyCommand.ExecuteAsync(alt4);

        _sut.SelectedStrategy.Should().Be(alt4);
        _sut.Strategies.Should().ContainSingle(s => s.IsSelected);
        _services.Tags["zapret/zapret-discord-youtube"].Should().Be("general (ALT4)");
        _sut.IsOn.Should().BeTrue();
    }

    [Fact]
    public async Task SelectStrategy_WhenOff_OnlyRemembersChoice()
    {
        _store.Save(new AppSettings { Enabled = false });
        await _sut.InitializeAsync();
        var alt4 = _sut.Strategies.Single(s => s.Name == "general (ALT4)");

        await _sut.SelectStrategyCommand.ExecuteAsync(alt4);

        _sut.IsOn.Should().BeFalse();
        _store.Current.Strategy.Should().Be("general (ALT4)");
        _sut.SelectedStrategy.Should().Be(alt4);
    }

    [Fact]
    public async Task Tick_ShowsHowLongBypassHasBeenOn()
    {
        await _sut.InitializeAsync();
        _time.Now = T0.AddMinutes(90).AddSeconds(5);

        _sut.Tick();

        _sut.UptimeText.Should().Be("01:30:05");
    }

    [Fact]
    public async Task Tick_NoticesWhenServiceWasStoppedOutsideTheApp()
    {
        await _sut.InitializeAsync();
        await _services.StopAsync(BypassController.ServiceName);

        for (var i = 0; i < 5; i++) _sut.Tick();

        _sut.IsOn.Should().BeFalse();
        _sut.UptimeText.Should().BeEmpty();
    }

    [Fact]
    public async Task HealthCheck_MarksBlockedService()
    {
        await _sut.InitializeAsync();
        _probe.Handler = u => new ProbeResult(!u.Host.Contains("discord"), TimeSpan.FromMilliseconds(80));

        await _sut.CheckHealthCommand.ExecuteAsync(null);

        _sut.Health.Single(h => h.Name == "Discord").Kind.Should().Be(CheckKind.Bad);
        _sut.Health.Single(h => h.Name == "YouTube").Kind.Should().Be(CheckKind.Good);
    }

    [Fact]
    public async Task SetGameFilter_SavesFileAndRestartsBypassWithNewPorts()
    {
        await _sut.InitializeAsync();

        await _sut.SetGameFilterCommand.ExecuteAsync(GameFilterMode.All);

        _sut.GameFilterMode.Should().Be(GameFilterMode.All);
        GameFilter.Load(_zapret.Paths.GameFilterFile).Mode.Should().Be(GameFilterMode.All);
        _services.GetCommandLine("zapret").Should().Contain(",1024-65535");
    }

    [Fact]
    public async Task SetIpset_SwitchesListMode()
    {
        await _sut.InitializeAsync();

        await _sut.SetIpsetCommand.ExecuteAsync(IpsetMode.Loaded);

        _sut.IpsetMode.Should().Be(IpsetMode.Loaded);
        _sut.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task LaunchWithWindows_CanBeTurnedOff()
    {
        await _sut.InitializeAsync();

        await _sut.SetLaunchWithWindowsCommand.ExecuteAsync(false);

        _autostart.Enabled.Should().BeFalse();
        _store.Current.LaunchWithWindows.Should().BeFalse();
    }

    [Fact]
    public async Task AutoSelect_ShowsResultsAndSelectsWinner()
    {
        await _sut.InitializeAsync();

        await _sut.AutoSelectCommand.ExecuteAsync(null);

        _sut.IsAutoSelecting.Should().BeFalse();
        _sut.AutoSelectProgress.Should().Be("Лучшая: general - теперь она по умолчанию");
        _sut.Strategies[0].Kind.Should().Be(CheckKind.Good);
        _sut.Strategies[0].Result.Should().Be("6/6 · 80 мс");
        _sut.IsOn.Should().BeTrue();
    }

    [Fact]
    public async Task AutoSelect_MakesWinnerTheDefault_EvenAfterRestart()
    {
        // Пусть открывает сайты только ALT5 (в ней есть syndata).
        _store.Save(new AppSettings { Enabled = false });
        var launcher = new FakeLauncher();
        var probe = new FakeProbe(_ => new ProbeResult(
            launcher.Started.LastOrDefault()?.Any(a => a.Contains("syndata")) == true, TimeSpan.FromMilliseconds(80)));
        var sut = new MainViewModel(_controller, new HealthChecker(FakeProbe.AllOk()),
            () => new StrategyTester(launcher, probe, HealthChecker.AllUrls, TimeSpan.Zero),
            _autostart, new IpsetManager(_zapret.Paths.Lists), _time);
        await sut.InitializeAsync();

        await sut.AutoSelectCommand.ExecuteAsync(null);

        _store.Current.Strategy.Should().Be("general (ALT5)", "её будет включать служба при каждом старте");
        _store.Current.BestStrategy.Should().Be("general (ALT5)");
        _store.Current.Enabled.Should().BeTrue("автоподбор включает обход с найденной стратегией");
        sut.Strategies.Should().ContainSingle(s => s.IsBest).Which.Name.Should().Be("general (ALT5)");

        // Новый запуск приложения: пометка «лучшая» и выбор сохранились.
        var reopened = new MainViewModel(_controller, new HealthChecker(FakeProbe.AllOk()),
            () => new StrategyTester(launcher, probe, HealthChecker.AllUrls, TimeSpan.Zero),
            _autostart, new IpsetManager(_zapret.Paths.Lists), _time);
        await reopened.InitializeAsync();
        reopened.SelectedStrategy!.Name.Should().Be("general (ALT5)");
        reopened.Strategies.Single(s => s.IsBest).Name.Should().Be("general (ALT5)");
    }

    [Fact]
    public async Task AutoSelect_ReportsError_WhenNoStrategyHelps()
    {
        await _sut.InitializeAsync();
        _probe.Handler = _ => new ProbeResult(false, TimeSpan.FromSeconds(5));

        await _sut.AutoSelectCommand.ExecuteAsync(null);

        _sut.ErrorMessage.Should().Contain("Ни одна стратегия");
        _sut.Strategies.Should().OnlyContain(s => s.Kind == CheckKind.Bad);
        _sut.IsOn.Should().BeTrue("прежняя стратегия возвращается");
    }
}
