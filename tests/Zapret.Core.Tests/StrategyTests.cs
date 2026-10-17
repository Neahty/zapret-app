using System.Runtime.InteropServices;
using Zapret.Core;

namespace Zapret.Core.Tests;

public sealed class StrategyParserTests
{
    [Fact]
    public void Parse_ExtractsArguments_AfterWinwsExe()
    {
        const string bat = """
            @echo off
            start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443 --dpi-desync=fake
            """;

        var strategy = StrategyParser.Parse("test", bat);

        strategy.ArgTemplates.Should().Equal("--wf-tcp=80,443", "--dpi-desync=fake");
    }

    [Fact]
    public void Parse_JoinsLinesEndingWithCaret()
    {
        const string bat = "start \"z\" /min \"%BIN%winws.exe\" --a=1 ^\r\n--new ^\r\n--b=2\r\n";

        StrategyParser.Parse("t", bat).ArgTemplates.Should().Equal("--a=1", "--new", "--b=2");
    }

    [Fact]
    public void Parse_KeepsQuotedValueWithSpacesAsOneArgument_WithoutQuotes()
    {
        const string bat = "start \"z\" /min \"%BIN%winws.exe\" --hostlist=\"%LISTS%my list.txt\" --x";

        StrategyParser.Parse("t", bat).ArgTemplates.Should().Equal("--hostlist=%LISTS%my list.txt", "--x");
    }

    [Fact]
    public void Parse_Throws_WhenBatDoesNotStartWinws()
    {
        var act = () => StrategyParser.Parse("broken", "@echo off\r\necho hello");

        act.Should().Throw<FormatException>().WithMessage("*broken*");
    }

    [Fact]
    public void Render_UsesDummyPort12_WhenGameFilterDisabled()
    {
        var strategy = new Strategy("t", ["--wf-tcp=80,%GameFilterTCP%", "--wf-udp=443,%GameFilterUDP%"]);

        var args = strategy.Render(new ZapretPaths(@"C:\z"), GameFilter.Disabled);

        args.Should().Equal("--wf-tcp=80,12", "--wf-udp=443,12");
    }

    [Fact]
    public void Render_UsesConfiguredRanges_WhenGameFilterEnabled()
    {
        var strategy = new Strategy("t", ["--wf-tcp=80,%GameFilterTCP%", "--wf-udp=443,%GameFilterUDP%"]);
        var filter = new GameFilter(GameFilterMode.Tcp, "2000-3000", "4000-5000");

        var args = strategy.Render(new ZapretPaths(@"C:\z"), filter);

        args.Should().Equal("--wf-tcp=80,2000-3000", "--wf-udp=443,12");
    }

    [Fact]
    public void Render_ReplacesBinAndListsWithAbsolutePaths()
    {
        var strategy = new Strategy("t", ["--fake=%BIN%a.bin", "--hostlist=%LISTS%b.txt"]);

        strategy.Render(new ZapretPaths(@"C:\z"), GameFilter.Disabled)
            .Should().Equal(@"--fake=C:\z\bin\a.bin", @"--hostlist=C:\z\lists\b.txt");
    }
}

/// <summary>Проверки на настоящих стратегиях из zapret-discord-youtube 1.10.3.</summary>
public sealed class ShippedStrategiesTests : IDisposable
{
    private readonly ZapretCopy _zapret = new();

    public void Dispose() => _zapret.Dispose();

    [Fact]
    public void Catalog_FindsAll22Strategies_AndSkipsServiceBat()
    {
        _zapret.Strategies.Should().HaveCount(22);
        _zapret.Strategies.Select(s => s.Name).Should().NotContain(n => n.StartsWith("service"));
    }

    [Fact]
    public void Catalog_OrdersLikeServiceBat_GeneralFirst_Alt2BeforeAlt10()
    {
        var names = _zapret.Strategies.Select(s => s.Name).ToList();

        names[0].Should().Be("general");
        names.IndexOf("general (ALT2)").Should().BeLessThan(names.IndexOf("general (ALT10)"));
    }

    [Fact]
    public void EveryStrategy_RendersWithoutUnresolvedVariables()
    {
        foreach (var strategy in _zapret.Strategies)
        {
            var args = strategy.Render(_zapret.Paths, GameFilter.Disabled);

            args.Should().NotContain(a => a.Contains('%'), $"в «{strategy.Name}» не должно остаться %переменных%");
            args.Should().NotContain(a => a.Contains('"'), $"в «{strategy.Name}» кавычки должны быть сняты");
            args[0].Should().StartWith("--wf-", $"«{strategy.Name}» начинается с фильтра WinDivert");
        }
    }

    [Fact]
    public void EveryStrategy_ReferencesOnlyFilesThatExist()
    {
        UserLists.Ensure(_zapret.Paths.Lists);

        foreach (var strategy in _zapret.Strategies)
        {
            var files = strategy.Render(_zapret.Paths, GameFilter.Disabled)
                .Where(a => a.Contains(_zapret.Paths.Root, StringComparison.OrdinalIgnoreCase))
                .Select(a => a[(a.IndexOf('=') + 1)..]);

            files.Should().OnlyContain(f => File.Exists(f), $"все файлы стратегии «{strategy.Name}» должны быть на месте");
        }
    }

    [Fact]
    public void GameFilterPorts_AppearInWinDivertFilter_WhenEnabled()
    {
        var filter = new GameFilter(GameFilterMode.All, "1024-65535", "1024-65535");

        var args = _zapret.Strategies.Single(s => s.Name == "general").Render(_zapret.Paths, filter);

        args.Should().Contain(a => a.StartsWith("--wf-tcp=") && a.EndsWith(",1024-65535"));
        args.Should().Contain(a => a.StartsWith("--wf-udp=") && a.EndsWith(",1024-65535"));
    }
}

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData(@"C:\path with spaces\winws.exe")]
    [InlineData(@"--hostlist=C:\a b\list.txt")]
    [InlineData("say \"hi\"")]
    [InlineData(@"ends with backslash\")]
    [InlineData(@"C:\dir with space\")]
    [InlineData("")]
    public void Join_IsParsedBackByWindowsIntoTheSameArguments(string arg)
    {
        string[] args = ["--first", arg, "--last"];

        var parsed = ParseLikeWindows("prog.exe " + CommandLine.Join(args));

        parsed.Skip(1).Should().Equal(args);
    }

    [Fact]
    public void Quote_LeavesPlainArgumentsUntouched() =>
        CommandLine.Quote(@"--hostlist=C:\ProgramData\lists\a.txt").Should().Be(@"--hostlist=C:\ProgramData\lists\a.txt");

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static string[] ParseLikeWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)
                .ToArray();
        }
        finally { LocalFree(argv); }
    }
}
