using System.Diagnostics;

namespace Zapret.Core;

public sealed record StrategyScore(string Name, int Passed, int Total, TimeSpan? AverageLatency, string? Error = null)
{
    public bool AllPassed => Total > 0 && Passed == Total;
}

/// <summary>Запуск winws.exe напрямую (без службы) на время проверки стратегии.</summary>
public interface IWinwsLauncher
{
    Task<IAsyncDisposable> StartAsync(string exe, IReadOnlyList<string> args, string workDir, CancellationToken ct = default);
}

public sealed class ProcessWinwsLauncher : IWinwsLauncher
{
    public async Task<IAsyncDisposable> StartAsync(string exe, IReadOnlyList<string> args, string workDir, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("winws.exe не запустился");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // Например, антивирус удалил или заблокировал winws.exe.
            throw new InvalidOperationException("winws.exe не запустился: " + e.Message, e);
        }

        var running = new RunningProcess(process);
        try
        {
            await Task.Delay(500, ct);
            if (process.HasExited)
                throw new InvalidOperationException($"winws.exe сразу завершился (код {process.ExitCode})");
            return running;
        }
        catch
        {
            // Отмена или ошибка — winws не должен остаться работать сам по себе.
            await running.DisposeAsync();
            throw;
        }
    }

    private sealed class RunningProcess(Process process) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            finally { process.Dispose(); }
        }
    }
}

/// <summary>Проверяет стратегию: запускает winws с ней и смотрит, открываются ли Discord и YouTube.</summary>
public sealed class StrategyTester(IWinwsLauncher launcher, IConnectivityProbe probe, IReadOnlyList<Uri> urls, TimeSpan warmup)
{
    public async Task<StrategyScore> TestAsync(Strategy strategy, ZapretPaths paths, GameFilter gameFilter, CancellationToken ct = default)
    {
        IAsyncDisposable running;
        try
        {
            running = await launcher.StartAsync(paths.WinwsExe, strategy.Render(paths, gameFilter), paths.Bin, ct);
        }
        catch (InvalidOperationException e)
        {
            return new StrategyScore(strategy.Name, 0, urls.Count, null, e.Message);
        }

        await using (running)
        {
            await Task.Delay(warmup, ct);
            var results = await Task.WhenAll(urls.Select(u => probe.ProbeAsync(u, ct)));
            var passed = results.Where(r => r.Ok).ToList();
            TimeSpan? avg = passed.Count > 0 ? TimeSpan.FromTicks((long)passed.Average(r => r.Latency.Ticks)) : null;
            return new StrategyScore(strategy.Name, passed.Count, urls.Count, avg);
        }
    }

    /// <summary>Лучшая — та, где открылось больше сайтов; при равенстве — где быстрее. Null, если не помогла ни одна.</summary>
    public static StrategyScore? PickBest(IEnumerable<StrategyScore> scores) =>
        scores.Where(s => s.Passed > 0)
            .OrderByDescending(s => s.Passed)
            .ThenBy(s => s.AverageLatency ?? TimeSpan.MaxValue)
            .FirstOrDefault();
}
