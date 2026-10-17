using System.Xml.Linq;
using Zapret.Core;

namespace Zapret.Core.Tests;

public sealed class StrategyTesterTests
{
    private static readonly Uri[] Urls = [new("https://a.test"), new("https://b.test")];
    private static readonly Strategy Sample = new("s", ["--wf-tcp=80"]);
    private static readonly ZapretPaths Paths = new(@"C:\z");

    [Fact]
    public async Task TestAsync_CountsOpenedSites_AndAveragesLatencyOfSuccessfulOnes()
    {
        var probe = new FakeProbe(u => u.Host == "a.test"
            ? new ProbeResult(true, TimeSpan.FromMilliseconds(200))
            : new ProbeResult(false, TimeSpan.FromSeconds(5)));
        var sut = new StrategyTester(new FakeLauncher(), probe, Urls, TimeSpan.Zero);

        var score = await sut.TestAsync(Sample, Paths, GameFilter.Disabled);

        score.Should().Be(new StrategyScore("s", 1, 2, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task TestAsync_StopsWinwsAfterCheck()
    {
        var launcher = new FakeLauncher();
        var sut = new StrategyTester(launcher, FakeProbe.AllOk(), Urls, TimeSpan.Zero);

        await sut.TestAsync(Sample, Paths, GameFilter.Disabled);

        launcher.Started.Should().HaveCount(1);
        launcher.Running.Should().Be(0);
    }

    [Fact]
    public async Task TestAsync_ReturnsZeroScoreWithReason_WhenWinwsCrashes()
    {
        var launcher = new FakeLauncher { FailWhen = _ => true };
        var sut = new StrategyTester(launcher, FakeProbe.AllOk(), Urls, TimeSpan.Zero);

        var score = await sut.TestAsync(Sample, Paths, GameFilter.Disabled);

        score.Passed.Should().Be(0);
        score.Error.Should().Contain("завершился");
    }

    [Fact]
    public void PickBest_PrefersMoreOpenedSites_ThenLowerLatency_AndIgnoresUseless()
    {
        StrategyScore[] scores =
        [
            new("slow-full", 6, 6, TimeSpan.FromMilliseconds(900)),
            new("fast-full", 6, 6, TimeSpan.FromMilliseconds(100)),
            new("fast-half", 3, 6, TimeSpan.FromMilliseconds(10)),
            new("broken", 0, 6, null),
        ];

        StrategyTester.PickBest(scores)!.Name.Should().Be("fast-full");
        StrategyTester.PickBest([scores[3]]).Should().BeNull();
    }
}

public sealed class HealthCheckerTests
{
    [Fact]
    public async Task Service_IsOk_OnlyWhenAllItsAddressesOpen()
    {
        var probe = new FakeProbe(u => new ProbeResult(u.Host != "i.ytimg.com", TimeSpan.FromMilliseconds(50)));

        var results = await new HealthChecker(probe).CheckAsync();

        results.Should().ContainSingle(r => r.Name == "Discord" && r.Ok);
        results.Should().ContainSingle(r => r.Name == "YouTube" && !r.Ok && r.Latency == null);
    }

    [Fact]
    public async Task Latency_IsTheSlowestAddressOfTheService()
    {
        var probe = new FakeProbe(u => new ProbeResult(true, TimeSpan.FromMilliseconds(u.Host == "gateway.discord.gg" ? 300 : 50)));

        var results = await new HealthChecker(probe).CheckAsync();

        results.Single(r => r.Name == "Discord").Latency.Should().Be(TimeSpan.FromMilliseconds(300));
    }
}

public sealed class AppAutostartTests
{
    private const string Exe = @"C:\Program Files\Zapret & Co\Zapret.App.exe";

    [Fact]
    public void TaskXml_IsValid_AndRunsElevatedTrayAppOnLogon_EvenOnBattery()
    {
        var xml = XDocument.Parse(AppAutostart.BuildTaskXml(Exe, @"PC\user"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        xml.Descendants(ns + "Command").Single().Value.Should().Be(Exe);
        xml.Descendants(ns + "Arguments").Single().Value.Should().Be("--tray");
        xml.Descendants(ns + "RunLevel").Single().Value.Should().Be("HighestAvailable");
        xml.Descendants(ns + "LogonTrigger").Should().ContainSingle();
        xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value.Should().Be("false");
        xml.Descendants(ns + "StopIfGoingOnBatteries").Single().Value.Should().Be("false");
        xml.Descendants(ns + "ExecutionTimeLimit").Single().Value.Should().Be("PT0S", "иначе Windows закроет приложение через 72 часа");
    }

    [Fact]
    public async Task Enable_RegistersTaskFromXml_AndDeletesTempFile()
    {
        string? xmlPath = null;
        var runner = new FakeRunner
        {
            Handler = (_, args) =>
            {
                xmlPath = args[args.ToList().IndexOf("/XML") + 1];
                File.Exists(xmlPath).Should().BeTrue();
                return new CommandResult(0, "");
            },
        };

        using var dir = new TempDir();
        await new AppAutostart(runner, Exe, "user", dir.Path).SetAsync(true);

        runner.Calls.Single().Args.Should().Contain(new[] { "/Create", "/TN", AppAutostart.TaskName, "/F" });
        Path.GetDirectoryName(xmlPath).Should().Be(dir.Path, "XML нельзя класть в общий %TEMP% - его успеют подменить");
        File.Exists(xmlPath).Should().BeFalse();
    }

    [Fact]
    public async Task Enable_Throws_WhenSchtasksFails()
    {
        var runner = new FakeRunner { Handler = (_, _) => new CommandResult(1, "Access denied") };
        using var dir = new TempDir();

        var act = () => new AppAutostart(runner, Exe, "user", dir.Path).SetAsync(true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Access denied*");
    }

    [Fact]
    public async Task Disable_DeletesTask_OnlyIfItExists()
    {
        var runner = new FakeRunner { Handler = (_, args) => new CommandResult(args[0] == "/Query" ? 1 : 0, "") };

        await new AppAutostart(runner, Exe, "user", Path.GetTempPath()).SetAsync(false);

        runner.Calls.Should().ContainSingle().Which.Args[0].Should().Be("/Query");
    }

    [Theory]
    [InlineData(true, @"<Command>C:\Program Files\Zapret &amp; Co\Zapret.App.exe</Command>", true)]
    [InlineData(true, @"<Command>C:\Users\evil\fake.exe</Command>", false)]
    [InlineData(false, "", false)]
    public async Task IsEnabled_OnlyWhenTaskRunsOurExe(bool exists, string taskXml, bool expected)
    {
        var runner = new FakeRunner { Handler = (_, _) => new CommandResult(exists ? 0 : 1, taskXml) };

        (await new AppAutostart(runner, Exe, "user", Path.GetTempPath()).IsEnabledAsync()).Should().Be(expected);
    }
}

public sealed class AppDeploymentTests : IDisposable
{
    private readonly TempDir _src = new();
    private readonly TempDir _dst = new();

    public void Dispose() { _src.Dispose(); _dst.Dispose(); }

    [Fact]
    public void EnsureDeployed_CopiesWholeAppFolder_AndReturnsCopiedExe()
    {
        File.WriteAllText(_src.File("Zapret.exe"), "exe");
        Directory.CreateDirectory(_src.File("runtimes"));
        File.WriteAllText(Path.Combine(_src.Path, "runtimes", "lib.dll"), "dll");

        var exe = AppDeployment.EnsureDeployed(_src.Path, _dst.Path, "Zapret.exe");

        exe.Should().Be(_dst.File("Zapret.exe"));
        File.ReadAllText(Path.Combine(_dst.Path, "runtimes", "lib.dll")).Should().Be("dll");
    }

    [Fact]
    public void EnsureDeployed_CopiesOnlyAppFiles_WhenRunFromDownloadsFolder()
    {
        File.WriteAllText(_src.File("Zapret.exe"), "exe");
        File.WriteAllText(_src.File("wpfgfx_cor3.dll"), "dll");
        File.WriteAllText(_src.File("photo.jpg"), "чужой файл");
        File.WriteAllText(_src.File("setup.exe"), "чужая программа");
        Directory.CreateDirectory(_src.File("Сериалы"));
        File.WriteAllText(Path.Combine(_src.Path, "Сериалы", "1.mkv"), "видео");

        AppDeployment.EnsureDeployed(_src.Path, _dst.Path, "Zapret.exe");

        Directory.GetFileSystemEntries(_dst.Path).Select(Path.GetFileName)
            .Should().BeEquivalentTo("Zapret.exe", "wpfgfx_cor3.dll");
    }

    [Fact]
    public void EnsureDeployed_ReplacesChangedFiles()
    {
        File.WriteAllText(_src.File("Zapret.exe"), "v1");
        AppDeployment.EnsureDeployed(_src.Path, _dst.Path, "Zapret.exe");
        File.WriteAllText(_src.File("Zapret.exe"), "version 2");

        AppDeployment.EnsureDeployed(_src.Path, _dst.Path, "Zapret.exe");

        File.ReadAllText(_dst.File("Zapret.exe")).Should().Be("version 2");
    }

    [Fact]
    public void EnsureDeployed_DoesNothing_WhenAlreadyRunningFromTarget() =>
        AppDeployment.EnsureDeployed(_dst.Path, _dst.Path + "\\", "Zapret.exe").Should().Be(_dst.File("Zapret.exe"));
}

public sealed class SystemToolTests
{
    [Theory]
    [InlineData("sc.exe", "sc.exe")]
    [InlineData("taskkill", "taskkill.exe")]
    public void ResolveSystemTool_UsesFullSystem32Path(string name, string file) =>
        ProcessCommandRunner.ResolveSystemTool(name).Should().Be(Path.Combine(Environment.SystemDirectory, file));
}

public sealed class FormatTests
{
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(3_725, "01:02:05")]
    [InlineData(90_061, "1 д 01:01:01")]
    [InlineData(-5, "00:00:00")]
    public void Duration_FormatsUptime(int seconds, string expected) =>
        ViewModels.Format.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
