using Zapret.Core;

namespace Zapret.Core.Tests;

public sealed class GameFilterTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("80", true)]
    [InlineData("1024-65535", true)]
    [InlineData("1024-1934,1936-65535", true)]
    [InlineData(" 1024 - 2000 ", true)]
    [InlineData("65535", true)]
    [InlineData("65536", false)]
    [InlineData("0", false)]
    [InlineData("080", false)]
    [InlineData("2000-1000", false)]
    [InlineData("1-2-3", false)]
    [InlineData("1024-", false)]
    [InlineData("abc", false)]
    [InlineData("80,,443", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidRange_MatchesServiceBatRules(string? input, bool expected) =>
        GameFilter.IsValidRange(input).Should().Be(expected);

    [Fact]
    public void Load_ReturnsDisabled_WhenFileMissing() =>
        GameFilter.Load(_dir.File("nope")).Should().Be(GameFilter.Disabled);

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = _dir.File("game_filter.enabled");
        var filter = new GameFilter(GameFilterMode.Udp, "2000-3000", "4000-5000,6000");

        filter.Save(path);

        GameFilter.Load(path).Should().Be(filter);
    }

    [Theory]
    [InlineData("all", GameFilterMode.All)]
    [InlineData("tcp", GameFilterMode.Tcp)]
    [InlineData("udp", GameFilterMode.Udp)]
    public void Load_UnderstandsOldOneWordFormat(string content, GameFilterMode expected)
    {
        var path = _dir.File("gf");
        File.WriteAllText(path, content);

        GameFilter.Load(path).Mode.Should().Be(expected);
    }

    [Fact]
    public void Load_IgnoresInvalidRange_AndKeepsDefault()
    {
        var path = _dir.File("gf");
        File.WriteAllLines(path, ["mode=all", "tcp=99999", "udp=5000"]);

        var filter = GameFilter.Load(path);

        filter.TcpRange.Should().Be(GameFilter.DefaultRange);
        filter.UdpRange.Should().Be("5000");
    }
}

public sealed class IpsetManagerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly IpsetManager _sut;

    public IpsetManagerTests() => _sut = new IpsetManager(_dir.Path);
    public void Dispose() => _dir.Dispose();

    private string List => _dir.File("ipset-all.txt");
    private string Backup => _dir.File("ipset-all.txt.backup");

    [Fact]
    public void GetMode_DetectsAllThreeStates()
    {
        File.WriteAllText(List, IpsetManager.Placeholder);
        _sut.GetMode().Should().Be(IpsetMode.None);

        File.WriteAllText(List, "1.2.3.0/24\n5.6.7.0/24");
        _sut.GetMode().Should().Be(IpsetMode.Loaded);

        File.WriteAllText(List, "");
        _sut.GetMode().Should().Be(IpsetMode.Any);
    }

    [Fact]
    public void SwitchingAwayFromLoaded_KeepsFullListInBackup_AndCanRestoreIt()
    {
        File.WriteAllText(List, "1.2.3.0/24");

        _sut.SetMode(IpsetMode.None);
        File.ReadAllText(Backup).Should().Be("1.2.3.0/24");
        _sut.GetMode().Should().Be(IpsetMode.None);

        _sut.SetMode(IpsetMode.Any);
        _sut.GetMode().Should().Be(IpsetMode.Any);

        _sut.SetMode(IpsetMode.Loaded);
        File.ReadAllText(List).Should().Be("1.2.3.0/24");
        File.Exists(Backup).Should().BeFalse();
    }

    [Fact]
    public void SetLoaded_Throws_WhenNoBackupExists()
    {
        File.WriteAllText(List, IpsetManager.Placeholder);

        var act = () => _sut.SetMode(IpsetMode.Loaded);

        act.Should().Throw<InvalidOperationException>();
        _sut.GetMode().Should().Be(IpsetMode.None);
    }

    [Fact]
    public void ShippedArchive_StartsInNoneMode_WithFullListInBackup()
    {
        using var zapret = new ZapretCopy();
        var sut = new IpsetManager(zapret.Paths.Lists);

        sut.GetMode().Should().Be(IpsetMode.None);
        sut.HasBackup.Should().BeTrue();
    }
}

public sealed class UserListsTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Ensure_CreatesMissingUserLists_NonEmpty()
    {
        UserLists.Ensure(_dir.Path);

        foreach (var name in new[] { "ipset-exclude-user.txt", "list-general-user.txt", "list-exclude-user.txt" })
            File.ReadAllText(_dir.File(name)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Ensure_DoesNotOverwriteUserEdits()
    {
        File.WriteAllText(_dir.File("list-general-user.txt"), "my.site");

        UserLists.Ensure(_dir.Path);

        File.ReadAllText(_dir.File("list-general-user.txt")).Should().Be("my.site");
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Defaults_BypassIsOnAndAppStartsWithWindows()
    {
        var settings = new JsonSettingsStore(_dir.File("settings.json")).Load();

        settings.Enabled.Should().BeTrue("обход должен быть включён по умолчанию, а не как VPN");
        settings.LaunchWithWindows.Should().BeTrue();
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new JsonSettingsStore(_dir.File("sub/settings.json"));
        var settings = new AppSettings
        {
            Enabled = false, Strategy = "general (ALT3)",
            EnabledAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), LaunchWithWindows = false,
        };

        store.Save(settings);

        store.Load().Should().Be(settings);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileIsCorrupt()
    {
        File.WriteAllText(_dir.File("settings.json"), "{ not json");

        new JsonSettingsStore(_dir.File("settings.json")).Load().Should().Be(new AppSettings());
    }
}

public sealed class TargetListTests
{
    [Fact]
    public void Parse_ReadsHttpsTargets_AndSkipsPingAndComments()
    {
        const string text = """
            # comment
            DiscordMain = "https://discord.com"
            CloudflareDNS1111 = "PING:1.1.1.1"
            YouTubeWeb  =  "https://www.youtube.com"
            """;

        TargetList.Parse(text).Select(u => u.Host).Should().Equal("discord.com", "www.youtube.com");
    }

    [Fact]
    public void Parse_ShippedTargetsFile_Has12WebTargets()
    {
        using var zapret = new ZapretCopy();

        TargetList.Parse(File.ReadAllText(zapret.Paths.TargetsFile)).Should().HaveCount(12);
    }
}

public sealed class InstallerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void EnsureInstalled_ExtractsRealArchive_IntoVersionedFolder()
    {
        var paths = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);

        paths.Root.Should().Be(Path.Combine(_dir.Path, "zapret-" + ZapretInstaller.Version));
        File.Exists(paths.WinwsExe).Should().BeTrue();
        File.Exists(Path.Combine(paths.Bin, "WinDivert64.sys")).Should().BeTrue();
        Directory.GetDirectories(_dir.Path).Should().HaveCount(1, "временная папка распаковки удаляется");
    }

    [Fact]
    public void EnsureInstalled_DoesNotReextract_WhenAlreadyInstalled()
    {
        var paths = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);
        File.WriteAllText(Path.Combine(paths.Lists, "list-general-user.txt"), "my.site");

        ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);

        File.ReadAllText(Path.Combine(paths.Lists, "list-general-user.txt")).Should().Be("my.site");
    }

    [Fact]
    public void EnsureInstalled_RestoresOriginal_WhenWinwsWasReplaced()
    {
        // Подменённый winws.exe запустился бы от имени SYSTEM - его надо вернуть из архива.
        var paths = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);
        var original = File.ReadAllBytes(paths.WinwsExe);
        File.WriteAllText(paths.WinwsExe, "malware");

        ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);

        File.ReadAllBytes(paths.WinwsExe).Should().Equal(original);
    }

    [Fact]
    public void RemoveUntrusted_StopsWhatHoldsFiles_ThenDeletesFolder()
    {
        // Так было у пользователя: работающий winws держал cygwin1.dll и папку нельзя было удалить.
        var untrusted = Directory.CreateDirectory(Path.Combine(_dir.Path, "ZapretApp"));
        var dll = Path.Combine(untrusted.FullName, "cygwin1.dll");
        var locked = new FileStream(dll, FileMode.Create, FileAccess.Write, FileShare.None);

        ZapretInstaller.RemoveUntrusted(untrusted, releaseFiles: locked.Dispose).Should().BeTrue();

        Directory.Exists(untrusted.FullName).Should().BeFalse();
    }

    [Fact]
    public void RemoveUntrusted_ReportsFalse_WhenDriverFileStaysLocked()
    {
        // Загруженный WinDivert64.sys удалить нельзя - тогда папку забирают на месте, а не падают.
        var untrusted = Directory.CreateDirectory(Path.Combine(_dir.Path, "ZapretApp"));
        using var locked = new FileStream(Path.Combine(untrusted.FullName, "WinDivert64.sys"), FileMode.Create, FileAccess.Write, FileShare.None);

        ZapretInstaller.RemoveUntrusted(untrusted, releaseFiles: null, attempts: 2).Should().BeFalse();
    }

    [Fact]
    public void Reinstall_SkipsLockedDriverThatMatchesArchive_AndRestoresDeletedFiles()
    {
        var paths = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);
        File.Delete(paths.WinwsExe);
        Directory.Delete(paths.Lists, recursive: true);
        // Драйвер загружен ядром: читать можно, перезаписать или удалить - нет.
        using var driverInUse = new FileStream(Path.Combine(paths.Bin, "WinDivert64.sys"), FileMode.Open, FileAccess.Read, FileShare.Read);

        ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);

        File.Exists(paths.WinwsExe).Should().BeTrue();
        File.Exists(Path.Combine(paths.Lists, "list-general.txt")).Should().BeTrue();
    }

    [Fact]
    public void Reinstall_RemovesForeignDllsNextToWinws_ButKeepsUserLists()
    {
        var paths = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);
        File.WriteAllText(Path.Combine(paths.Bin, "version.dll"), "planted");
        File.WriteAllText(Path.Combine(paths.Lists, "list-general-user.txt"), "my.site");
        File.WriteAllText(paths.WinwsExe, "tampered");

        ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);

        File.Exists(Path.Combine(paths.Bin, "version.dll")).Should().BeFalse("чужая DLL рядом с winws.exe загрузилась бы от SYSTEM");
        File.ReadAllText(Path.Combine(paths.Lists, "list-general-user.txt")).Should().Be("my.site");
    }

    [Fact]
    public void NewZapretVersion_KeepsUserListsAndGameFilter_FromPreviousVersion()
    {
        // Старая версия с настройками пользователя.
        var old = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path, "1.10.2");
        UserLists.Ensure(old.Lists);
        File.WriteAllText(Path.Combine(old.Lists, "list-general-user.txt"), "my.site");
        new GameFilter(GameFilterMode.All, "1024-65535", "1024-65535").Save(old.GameFilterFile);

        // В приложение вшили новую - при запуске она ставится рядом, старая потом удаляется.
        var fresh = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path, "1.10.3");
        ZapretInstaller.RemoveOtherVersions(_dir.Path, "1.10.3");

        Directory.Exists(old.Root).Should().BeFalse();
        File.ReadAllText(Path.Combine(fresh.Lists, "list-general-user.txt")).Should().Be("my.site");
        GameFilter.Load(fresh.GameFilterFile).Mode.Should().Be(GameFilterMode.All);
    }

    [Fact]
    public void FolderCreatedByOrdinaryUser_IsNotTrusted()
    {
        // Ровно та ситуация, из-за которой падал первый запуск: папку создал не администратор.
        ZapretInstaller.IsOwnedByAdmins(new DirectoryInfo(_dir.Path)).Should().BeFalse();
    }

    [Fact]
    public void EnsureInstalled_ExplainsAntivirus_WhenAccessIsDenied()
    {
        var act = () => ZapretInstaller.EnsureInstalled(() => throw new UnauthorizedAccessException("denied"), _dir.Path);

        act.Should().Throw<AntivirusBlockedException>()
            .Where(e => e.Message.Contains("антивирус") && e.Message.Contains(_dir.Path))
            .WithInnerException<UnauthorizedAccessException>();
    }

    [Fact]
    public void EnsureInstalled_ExplainsAntivirus_WhenDriverSilentlyDisappeared()
    {
        // Так выглядит работа Kaspersky: winws.exe на месте, а WinDivert64.sys удалён.
        var zip = () => ZipBuilder.Build(
            ("z/bin/winws.exe", "x"), ("z/bin/WinDivert.dll", "x"), ("z/general.bat", "x"));

        var act = () => ZapretInstaller.EnsureInstalled(zip, _dir.Path);

        act.Should().Throw<AntivirusBlockedException>();
        Directory.GetDirectories(_dir.Path, ".extract-*").Should().BeEmpty();
    }

    [Fact]
    public void EnsureInstalled_Reinstalls_WhenPreviousInstallLostADriverFile()
    {
        var paths = ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);
        File.Delete(Path.Combine(paths.Bin, "WinDivert64.sys"));

        ZapretInstaller.EnsureInstalled(() => File.OpenRead(ZapretCopy.ZipPath), _dir.Path);

        File.Exists(Path.Combine(paths.Bin, "WinDivert64.sys")).Should().BeTrue();
    }

    [Fact]
    public void EnsureInstalled_Throws_AndCleansUp_WhenArchiveHasNoWinws()
    {
        var act = () => ZapretInstaller.EnsureInstalled(() => ZipBuilder.Build(("readme.txt", "hi")), _dir.Path);

        act.Should().Throw<InvalidDataException>();
        Directory.GetDirectories(_dir.Path).Should().BeEmpty();
    }
}
