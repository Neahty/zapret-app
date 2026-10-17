using System.IO.Compression;
using Zapret.Core;

namespace Zapret.Core.Tests;

/// <summary>Временная папка, которая удаляется после теста.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zapret-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Свежая копия настоящего zapret 1.10.3 из архива - на каждый тест своя.</summary>
public sealed class ZapretCopy : IDisposable
{
    public static string ZipPath => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "zapret.zip");

    private readonly TempDir _dir = new();
    public ZapretPaths Paths { get; }
    public IReadOnlyList<Strategy> Strategies { get; }

    public ZapretCopy()
    {
        Paths = ZapretInstaller.EnsureInstalled(() => System.IO.File.OpenRead(ZipPath), _dir.Path);
        Strategies = StrategyCatalog.Load(Paths.Root);
    }

    public void Dispose() => _dir.Dispose();
}

public sealed class FakeServiceControl : IServiceControl
{
    private readonly Dictionary<string, (ServiceState State, string CommandLine)> _services = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Tags { get; } = new();
    public List<string> Calls { get; } = [];
    public bool FailToStart { get; set; }

    public void Preinstall(string name, string commandLine, ServiceState state) => _services[name] = (state, commandLine);

    public ServiceState GetState(string name) => _services.TryGetValue(name, out var s) ? s.State : ServiceState.NotInstalled;
    public string? GetCommandLine(string name) => _services.TryGetValue(name, out var s) ? s.CommandLine : null;

    public Task InstallAsync(string name, string displayName, string description, string commandLine, CancellationToken ct = default)
    {
        Calls.Add($"install {name}");
        var state = _services.TryGetValue(name, out var s) ? s.State : ServiceState.Stopped;
        _services[name] = (state, commandLine);
        return Task.CompletedTask;
    }

    public Task StartAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"start {name}");
        if (!FailToStart) _services[name] = (ServiceState.Running, _services[name].CommandLine);
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"stop {name}");
        if (_services.TryGetValue(name, out var s)) _services[name] = (ServiceState.Stopped, s.CommandLine);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"delete {name}");
        _services.Remove(name);
        return Task.CompletedTask;
    }

    public void SetTag(string name, string valueName, string value) => Tags[$"{name}/{valueName}"] = value;
}

public sealed class FakeRunner : ICommandRunner
{
    public List<(string Exe, string[] Args)> Calls { get; } = [];
    public Func<string, IReadOnlyList<string>, CommandResult> Handler { get; set; } = (_, _) => new CommandResult(0, "");

    public Task<CommandResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        Calls.Add((exe, args.ToArray()));
        return Task.FromResult(Handler(exe, args));
    }
}

public sealed class MemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();
    public AppSettings Load() => Current;
    public void Save(AppSettings settings) => Current = settings;
}

public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class FakeProbe(Func<Uri, ProbeResult> handler) : IConnectivityProbe
{
    public static FakeProbe AllOk(int ms = 100) => new(_ => new ProbeResult(true, TimeSpan.FromMilliseconds(ms)));
    public static FakeProbe AllBlocked() => new(_ => new ProbeResult(false, TimeSpan.FromSeconds(5), "reset"));

    public Func<Uri, ProbeResult> Handler { get; set; } = handler;
    public Task<ProbeResult> ProbeAsync(Uri url, CancellationToken ct = default) => Task.FromResult(Handler(url));
}

/// <summary>Вместо winws.exe - запоминает, с какими аргументами его «запустили».</summary>
public sealed class FakeLauncher : IWinwsLauncher
{
    public List<IReadOnlyList<string>> Started { get; } = [];
    public int Running { get; private set; }
    public Func<IReadOnlyList<string>, bool> FailWhen { get; set; } = _ => false;

    public Task<IAsyncDisposable> StartAsync(string exe, IReadOnlyList<string> args, string workDir, CancellationToken ct = default)
    {
        if (FailWhen(args)) throw new InvalidOperationException("winws.exe сразу завершился (код 1)");
        Started.Add(args);
        Running++;
        return Task.FromResult<IAsyncDisposable>(new Handle(this));
    }

    private sealed class Handle(FakeLauncher owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { owner.Running--; return ValueTask.CompletedTask; }
    }
}

public sealed class FakeAutostart : IAppAutostart
{
    public bool Enabled { get; set; }
    public int SetCalls { get; private set; }
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(Enabled);
    public Task SetAsync(bool enabled, CancellationToken ct = default) { SetCalls++; Enabled = enabled; return Task.CompletedTask; }
}

public static class ZipBuilder
{
    public static MemoryStream Build(params (string Path, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }
}
