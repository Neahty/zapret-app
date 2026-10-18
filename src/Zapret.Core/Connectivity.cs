using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Zapret.Core;

public sealed record ProbeResult(bool Ok, TimeSpan Latency, string? Error = null);

/// <summary>Проверка, открывается ли сайт. В тестах подменяется.</summary>
public interface IConnectivityProbe
{
    Task<ProbeResult> ProbeAsync(Uri url, CancellationToken ct = default);
}

/// <summary>
/// Сайт считается доступным, если удалось пройти TLS и получить любой HTTP-ответ (даже 404):
/// блокировка DPI выглядит как сброс соединения или таймаут, а не как код ответа.
/// </summary>
public sealed class HttpConnectivityProbe : IConnectivityProbe, IDisposable
{
    private readonly HttpClient _http;

    public HttpConnectivityProbe(TimeSpan timeout)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.Zero, // каждая проверка - новое соединение
        })
        { Timeout = timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<ProbeResult> ProbeAsync(Uri url, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new ProbeResult(true, sw.Elapsed);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, sw.Elapsed, e.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record ServiceHealth(string Name, bool Ok, TimeSpan? Latency);

/// <summary>Проверка того, ради чего ставят zapret: Discord, YouTube и SoundCloud.</summary>
public sealed class HealthChecker(IConnectivityProbe probe)
{
    public static IReadOnlyList<(string Name, Uri[] Urls)> Groups { get; } =
    [
        ("Discord", [new("https://discord.com"), new("https://gateway.discord.gg"), new("https://cdn.discordapp.com")]),
        ("YouTube", [new("https://www.youtube.com"), new("https://i.ytimg.com"), new("https://redirector.googlevideo.com")]),
        ("SoundCloud", [new("https://soundcloud.com"), new("https://api-v2.soundcloud.com"), new("https://a-v2.sndcdn.com")]),
    ];

    public static IReadOnlyList<Uri> AllUrls { get; } = Groups.SelectMany(g => g.Urls).ToList();

    public async Task<IReadOnlyList<ServiceHealth>> CheckAsync(CancellationToken ct = default)
    {
        var tasks = Groups.Select(async g =>
        {
            var results = await Task.WhenAll(g.Urls.Select(u => probe.ProbeAsync(u, ct)));
            var ok = results.All(r => r.Ok);
            return new ServiceHealth(g.Name, ok, ok ? results.Max(r => r.Latency) : null);
        });
        return await Task.WhenAll(tasks);
    }
}

/// <summary>Читает utils\targets.txt из zapret (строки вида Key = "https://...").</summary>
public static class TargetList
{
    private static readonly Regex Line = new(@"^\s*(\w+)\s*=\s*""(https?://[^""]+)""", RegexOptions.Compiled);

    public static IReadOnlyList<Uri> Parse(string text) =>
        text.Split('\n')
            .Select(l => Line.Match(l))
            .Where(m => m.Success && Uri.TryCreate(m.Groups[2].Value, UriKind.Absolute, out _))
            .Select(m => new Uri(m.Groups[2].Value))
            .ToList();
}
