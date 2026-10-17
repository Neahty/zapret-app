using System.Diagnostics;
using System.Text;

namespace Zapret.Core;

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Запуск системных утилит (sc, netsh, taskkill, schtasks). В тестах подменяется.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ResolveSystemTool(exe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {exe}");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        return new CommandResult(process.ExitCode, await stdout + await stderr);
    }

    /// <summary>
    /// «sc.exe» → «C:\Windows\System32\sc.exe». Без полного пути Windows сначала ищет exe рядом
    /// с приложением, и подложенный туда «sc.exe» запустился бы с правами администратора.
    /// </summary>
    internal static string ResolveSystemTool(string exe)
    {
        if (Path.IsPathRooted(exe)) return exe;
        var name = Path.HasExtension(exe) ? exe : exe + ".exe";
        return Path.Combine(Environment.SystemDirectory, name);
    }
}

public static class CommandLine
{
    /// <summary>Склеивает аргументы в одну строку по правилам Windows (как это делает ArgumentList).</summary>
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')) return arg;

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
