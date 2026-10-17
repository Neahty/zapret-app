using Zapret.Core;
using Zapret.Core.ViewModels;

namespace Zapret.Core.Tests;

/// <summary>«GitHub» в тестах: отдаёт заданный релиз и «скачивает» заданный архив.</summary>
public sealed class FakeReleaseSource(ReleaseInfo? latest, Func<Stream>? archive = null) : IReleaseSource
{
    public int Downloads { get; private set; }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default) => Task.FromResult(latest);

    public async Task DownloadAsync(ReleaseInfo release, string destinationFile, CancellationToken ct = default)
    {
        Downloads++;
        await using var from = (archive ?? (() => File.OpenRead(ZapretCopy.ZipPath)))();
        await using var to = File.Create(destinationFile);
        await from.CopyToAsync(to, ct);
    }
}

public sealed class UpdaterTests : IDisposable
{
    private static readonly ReleaseInfo NewRelease = new("1.10.4",
        new Uri("https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/zapret-discord-youtube-1.10.4.zip"), 1);

    private readonly ZapretCopy _zapret = new();
    private string InstallRoot => Path.GetDirectoryName(_zapret.Paths.Root)!;

    public void Dispose() => _zapret.Dispose();

    [Theory]
    [InlineData("1.10.4", "1.10.3", 1)]
    [InlineData("1.10.10", "1.10.9", 1)]
    [InlineData("1.10.3", "1.10.3", 0)]
    [InlineData("1.9", "1.10.3", -1)]
    [InlineData("2", "1.99.99", 1)]
    public void CompareVersions_ComparesNumbersNotText(string a, string b, int expected) =>
        Math.Sign(ZapretUpdater.CompareVersions(a, b)).Should().Be(expected);

    [Theory]
    [InlineData("1.10.3", true)]
    [InlineData("2", true)]
    [InlineData("../evil", false)]
    [InlineData("1.10.3\\..\\x", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidVersion_AllowsOnlyPlainVersionNumbers(string? version, bool expected) =>
        ZapretInstaller.IsValidVersion(version).Should().Be(expected);

    private static string ReleaseJson(string tag, string url, long size = 1500000) => $$"""
        {
          "tag_name": "{{tag.Replace("\\", "\\\\")}}",
          "assets": [
            { "name": "zapret-discord-youtube.rar", "size": 1200000,
              "browser_download_url": "https://github.com/Flowseal/zapret-discord-youtube/releases/download/x/x.rar" },
            { "name": "zapret-discord-youtube.zip", "size": {{size}}, "browser_download_url": "{{url}}" }
          ]
        }
        """;

    [Fact]
    public void ParseRelease_PicksZipFromOfficialReleases()
    {
        const string url = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/zapret-discord-youtube-1.10.4.zip";

        var release = GitHubReleaseSource.ParseRelease(ReleaseJson("1.10.4", url));

        release.Should().Be(new ReleaseInfo("1.10.4", new Uri(url), 1500000));
    }

    [Theory]
    [InlineData("https://evil.example.com/zapret.zip")]
    [InlineData("https://github.com/someone-else/zapret/releases/download/1.10.4/z.zip")]
    [InlineData("http://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/z.zip")]
    public void ParseRelease_RefusesArchivesFromAnywhereElse(string url) =>
        GitHubReleaseSource.ParseRelease(ReleaseJson("1.10.4", url)).Should().BeNull("файлы из архива запускаются от SYSTEM");

    [Fact]
    public void ParseRelease_RefusesStrangeVersionTag() =>
        GitHubReleaseSource.ParseRelease(ReleaseJson("..\\..\\Windows",
            "https://github.com/Flowseal/zapret-discord-youtube/releases/download/x/z.zip")).Should().BeNull();

    [Fact]
    public async Task Check_ReturnsRelease_OnlyWhenItIsNewer()
    {
        var updater = new ZapretUpdater(InstallRoot, new FakeReleaseSource(NewRelease));

        (await updater.CheckAsync("1.10.3")).Should().Be(NewRelease);
        (await updater.CheckAsync("1.10.4")).Should().BeNull();
        (await updater.CheckAsync("1.11")).Should().BeNull();
    }

    [Fact]
    public async Task DownloadAndInstall_PutsNewVersionNextToOld_AndCarriesOverUserSettings()
    {
        UserLists.Ensure(_zapret.Paths.Lists);
        File.WriteAllText(Path.Combine(_zapret.Paths.Lists, "list-general-user.txt"), "my.site");
        new GameFilter(GameFilterMode.All, "1024-65535", "1024-65535").Save(_zapret.Paths.GameFilterFile);
        var updater = new ZapretUpdater(InstallRoot, new FakeReleaseSource(NewRelease));

        var installed = await updater.DownloadAndInstallAsync(NewRelease, _zapret.Paths);

        installed.Root.Should().Be(Path.Combine(InstallRoot, "zapret-1.10.4"));
        File.Exists(installed.WinwsExe).Should().BeTrue();
        StrategyCatalog.Load(installed.Root).Should().NotBeEmpty();
        File.ReadAllText(Path.Combine(installed.Lists, "list-general-user.txt")).Should().Be("my.site");
        GameFilter.Load(installed.GameFilterFile).Mode.Should().Be(GameFilterMode.All);
        File.Exists(updater.PackageFor("1.10.4")).Should().BeTrue("по архиву потом сверяются файлы при каждом запуске");
        File.Exists(_zapret.Paths.WinwsExe).Should().BeTrue("старая версия остаётся, пока служба не переключится");
    }

    [Fact]
    public async Task DownloadAndInstall_Fails_WhenArchiveIsBroken_AndLeavesNoHalfDownload()
    {
        var broken = new FakeReleaseSource(NewRelease, () => ZipBuilder.Build(("readme.txt", "not zapret")));
        var updater = new ZapretUpdater(InstallRoot, broken);

        var act = () => updater.DownloadAndInstallAsync(NewRelease, _zapret.Paths);

        await act.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(Path.Combine(InstallRoot, "zapret-1.10.4")).Should().BeFalse();
        Directory.GetFiles(Path.Combine(InstallRoot, "packages"), "*.part").Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveOtherVersions_KeepsOnlyCurrentFolderAndArchive()
    {
        var updater = new ZapretUpdater(InstallRoot, new FakeReleaseSource(NewRelease));
        await updater.DownloadAndInstallAsync(NewRelease, _zapret.Paths);
        File.WriteAllText(updater.PackageFor("1.10.3"), "old");

        ZapretInstaller.RemoveOtherVersions(InstallRoot, "1.10.4");

        Directory.GetDirectories(InstallRoot, "zapret-*").Select(Path.GetFileName).Should().Equal("zapret-1.10.4");
        File.Exists(updater.PackageFor("1.10.4")).Should().BeTrue();
        File.Exists(updater.PackageFor("1.10.3")).Should().BeFalse();
    }
}

public sealed class UpdateFlowTests : IDisposable
{
    private static readonly ReleaseInfo NewRelease = new("1.10.4",
        new Uri("https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/z.zip"), 1);

    private readonly ZapretCopy _zapret = new();
    private readonly FakeServiceControl _services = new();
    private readonly MemorySettingsStore _store = new();
    private readonly BypassController _controller;

    public UpdateFlowTests() =>
        _controller = new BypassController(_zapret.Paths, _zapret.Strategies, _services, new FakeRunner(), _store,
            new FakeTime(DateTimeOffset.UnixEpoch));

    public void Dispose() => _zapret.Dispose();

    private MainViewModel CreateVm(IReleaseSource source) => new(
        _controller, new HealthChecker(FakeProbe.AllOk()),
        () => new StrategyTester(new FakeLauncher(), FakeProbe.AllOk(), HealthChecker.AllUrls, TimeSpan.Zero),
        new FakeAutostart(), new IpsetManager(_zapret.Paths.Lists), new FakeTime(DateTimeOffset.UnixEpoch),
        new ZapretUpdater(Path.GetDirectoryName(_zapret.Paths.Root)!, source), "1.10.3");

    [Fact]
    public async Task Switch_StopsOldBypassAndDriver_AndRemembersNewVersion()
    {
        await _controller.EnableAsync(null);
        _services.Preinstall("WinDivert", "old driver", ServiceState.Running);

        await _controller.SwitchZapretVersionAsync("1.10.4");

        _services.GetState("zapret").Should().Be(ServiceState.Stopped);
        _services.GetState("WinDivert").Should().Be(ServiceState.NotInstalled, "драйвер старой версии должен уйти");
        _store.Current.ZapretVersion.Should().Be("1.10.4");
        _store.Current.Enabled.Should().BeTrue("после перезапуска обход поднимется уже на новой версии");
    }

    [Fact]
    public async Task CheckUpdates_SaysLatest_WhenNothingNew()
    {
        var vm = CreateVm(new FakeReleaseSource(new ReleaseInfo("1.10.3", NewRelease.ZipUrl, 1)));
        await vm.InitializeAsync();

        await vm.CheckUpdatesCommand.ExecuteAsync(null);

        vm.UpdateStatus.Should().Be("Установлена последняя версия 1.10.3");
        vm.UpdateFailed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckUpdates_InstallsNewVersion_AndAsksAppToRestart()
    {
        var source = new FakeReleaseSource(NewRelease);
        var vm = CreateVm(source);
        await vm.InitializeAsync();
        var restarted = false;
        vm.RestartRequested += () => restarted = true;

        await vm.CheckUpdatesCommand.ExecuteAsync(null);

        source.Downloads.Should().Be(1);
        restarted.Should().BeTrue();
        _store.Current.ZapretVersion.Should().Be("1.10.4");
        vm.UpdateStatus.Should().Contain("1.10.4");
    }

    [Fact]
    public async Task CheckUpdates_KeepsBypassRunning_WhenDownloadIsBroken()
    {
        var vm = CreateVm(new FakeReleaseSource(NewRelease, () => ZipBuilder.Build(("readme.txt", "x"))));
        await vm.InitializeAsync();
        var restarted = false;
        vm.RestartRequested += () => restarted = true;

        await vm.CheckUpdatesCommand.ExecuteAsync(null);

        vm.UpdateFailed.Should().BeTrue();
        vm.UpdateStatus.Should().StartWith("Не удалось обновить");
        restarted.Should().BeFalse();
        vm.IsOn.Should().BeTrue();
        _store.Current.ZapretVersion.Should().BeNull();
    }
}
