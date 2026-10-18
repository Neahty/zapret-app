using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;

namespace Zapret.Core.Telegram;

/// <summary>Канал от прокси до Telegram: WebSocket или обычный TCP.</summary>
public interface ITelegramLink : IAsyncDisposable
{
    /// <summary>WebSocket: каждый пакет MTProto уходит своим кадром.</summary>
    bool IsFramed { get; }
    string Route { get; }
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    /// <returns>Сколько байт пришло; 0 - Telegram закрыл соединение.</returns>
    Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
}

/// <summary>Как достучаться до нужного дата-центра Telegram. В тестах подменяется.</summary>
public interface ITelegramUpstream
{
    /// <returns>null - не получилось никаким способом.</returns>
    Task<ITelegramLink?> ConnectAsync(int dc, bool isMedia, bool isTest, CancellationToken ct);
}

/// <summary>
/// Маршруты как в tg-ws-proxy: сначала WebSocket к веб-версии Telegram (kwsN.web.telegram.org) через
/// 149.154.167.220 - этот адрес провайдеры не режут, ведь через него работает web.telegram.org.
/// Если не вышло - через Cloudflare-домены tg-ws-proxy, в конце - напрямую к дата-центру по TCP.
/// Списки вшиты в приложение: ничего не скачивается.
/// </summary>
public sealed class TelegramUpstream(Action<string>? log = null) : ITelegramUpstream
{
    public const string WebFrontIp = "149.154.167.220";
    private const string FrontingSni = "sprinthost.ru";
    private static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(60);
    /// <summary>Сколько Cloudflare-доменов пробуем одновременно: перебор всех по очереди занял бы минуты.</summary>
    private const int CloudflareAttempts = 3;

    /// <summary>Для каких DC идём через WebSocket веб-версии (по умолчанию tg-ws-proxy - 2 и 4).</summary>
    public static IReadOnlyDictionary<int, string> DcRedirects { get; } = new Dictionary<int, string>
    {
        [2] = WebFrontIp,
        [4] = WebFrontIp,
    };

    public static IReadOnlyDictionary<int, string> DcIps { get; } = new Dictionary<int, string>
    {
        [1] = "149.154.175.50", [2] = "149.154.167.51", [3] = "149.154.175.100",
        [4] = "149.154.167.91", [5] = "149.154.171.5", [203] = "91.105.192.100",
    };

    public static IReadOnlyDictionary<int, string> TestDcIps { get; } = new Dictionary<int, string>
    {
        [1] = "149.154.175.10", [2] = "149.154.167.40", [3] = "149.154.175.117",
    };

    public static IReadOnlyList<string> WsDomains(int dc, bool isMedia)
    {
        if (dc == 203) dc = 2;
        return isMedia ? [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"] : [$"kws{dc}.web.telegram.org"];
    }

    private readonly ConcurrentDictionary<(int, bool), DateTimeOffset> _wsFailedUntil = new();
    private readonly ConcurrentDictionary<int, string> _cfDomainForDc = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _cfFailedUntil = new();
    private volatile bool _frontingFirst;

    public async Task<ITelegramLink?> ConnectAsync(int dc, bool isMedia, bool isTest, CancellationToken ct)
    {
        var path = isTest ? "/apiws_test" : "/apiws";

        if (DcRedirects.TryGetValue(dc, out var ip) &&
            (!_wsFailedUntil.TryGetValue((dc, isMedia), out var until) || until < DateTimeOffset.UtcNow))
        {
            foreach (var domain in WsDomains(dc, isMedia))
                foreach (var fronting in _frontingFirst ? new[] { true, false } : [false, true])
                {
                    var link = await TryWebSocketAsync(ip, domain, path, fronting ? FrontingSni : null,
                        TimeSpan.FromSeconds(fronting ? 7 : 8), ct);
                    if (link is null) continue;
                    _frontingFirst = fronting;
                    return link;
                }
            _wsFailedUntil[(dc, isMedia)] = DateTimeOffset.UtcNow + FailureMemory;
        }

        if (!isTest && (!_cfFailedUntil.TryGetValue(dc, out var cfUntil) || cfUntil < DateTimeOffset.UtcNow))
        {
            var link = await RaceCloudflareAsync(dc, path, ct);
            if (link is not null) return link;
            _cfFailedUntil[dc] = DateTimeOffset.UtcNow + FailureMemory;
        }

        if ((isTest ? TestDcIps : DcIps).TryGetValue(dc, out var dcIp))
        {
            try
            {
                var socket = await ConnectSocketAsync(dcIp, TimeSpan.FromSeconds(10), ct);
                return new TcpLink(new NetworkStream(socket, ownsSocket: true), $"tcp {dcIp}");
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                log?.Invoke($"DC{dc}: TCP {dcIp} недоступен: {e.Message}");
            }
        }
        return null;
    }

    /// <summary>Несколько доменов сразу: побеждает первый ответивший, остальные закрываются.</summary>
    private async Task<ITelegramLink?> RaceCloudflareAsync(int dc, string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = CloudflareDomainsFor(dc).Take(CloudflareAttempts).Select(async baseDomain =>
        {
            var domain = $"kws{dc}.{baseDomain}";
            return (Base: baseDomain, Link: await TryWebSocketAsync(domain, domain, path, null, TimeSpan.FromSeconds(10), cts.Token));
        }).ToList();

        ITelegramLink? winner = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            (string Base, ITelegramLink? Link) result;
            try { result = await done; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            if (result.Link is null) continue;
            if (winner is null)
            {
                winner = result.Link;
                _cfDomainForDc[dc] = result.Base;
                cts.Cancel();
            }
            else await result.Link.DisposeAsync();
        }
        ct.ThrowIfCancellationRequested();
        return winner;
    }

    /// <summary>Сначала домен, который уже сработал для этого DC, потом остальные вперемешку.</summary>
    private IEnumerable<string> CloudflareDomainsFor(int dc)
    {
        _cfDomainForDc.TryGetValue(dc, out var current);
        if (current is not null) yield return current;
        foreach (var d in CloudflareDomains.All.OrderBy(_ => Random.Shared.Next()))
            if (d != current) yield return d;
    }

    private async Task<ITelegramLink?> TryWebSocketAsync(string connectHost, string domain, string path, string? sni,
        TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var ws = await ConnectWebSocketAsync(connectHost, domain, path, sni, timeout, cts.Token);
            return new WebSocketLink(ws, sni is null ? $"ws {domain}" : $"ws {domain} (sni {sni})");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log?.Invoke($"WS {domain} через {connectHost}{(sni is null ? "" : $" (sni {sni})")}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// WebSocket, у которого адрес подключения, имя в TLS (SNI) и заголовок Host задаются отдельно.
    /// TLS делаем сами в ConnectCallback, поэтому для .NET это «ws://» поверх уже готового TLS.
    /// </summary>
    internal static async Task<WebSocket> ConnectWebSocketAsync(string connectHost, string domain, string path, string? sni,
        TimeSpan timeout, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = await ConnectSocketAsync(connectHost, timeout, token);
                var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true));
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = sni ?? domain,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                        // С подставным SNI сервер отдаёт сертификат *.telegram.org: цепочку проверяем как обычно,
                        // а имя - на принадлежность Telegram, а не тому домену, что в SNI.
                        RemoteCertificateValidationCallback = sni is null
                            ? null
                            : (_, cert, _, errors) =>
                                (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None &&
                                cert is not null && IsTelegramCertificate(new X509Certificate2(cert), domain),
                    }, token);
                    return ssl;
                }
                catch
                {
                    await ssl.DisposeAsync();
                    throw;
                }
            },
        };
        using var invoker = new HttpMessageInvoker(handler);
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("binary");
        try
        {
            await ws.ConnectAsync(new Uri($"ws://{domain}{path}"), invoker, ct);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    internal static bool IsTelegramCertificate(X509Certificate2 cert, string domain) =>
        cert.MatchesHostname(domain) || cert.MatchesHostname("web.telegram.org");

    private static async Task<Socket> ConnectSocketAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, 443, cts.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class WebSocketLink(WebSocket ws, string route) : ITelegramLink
{
    public bool IsFramed => true;
    public string Route { get; } = route;

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct).AsTask();

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return 0;
            // Границы кадров не важны: дальше это сплошной зашифрованный поток.
            if (result.Count > 0) return result.Count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        ws.Dispose();
    }
}

public sealed class TcpLink(Stream stream, string route) : ITelegramLink
{
    public bool IsFramed => false;
    public string Route { get; } = route;

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => stream.WriteAsync(data, ct).AsTask();

    public Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) => stream.ReadAsync(buffer, ct).AsTask();

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

/// <summary>
/// Запасные домены tg-ws-proxy за Cloudflare: через них идут DC без прямого WebSocket (1, 3, 5).
/// Записаны так же, как в исходнике tg-ws-proxy (proxy/config.py), - чтобы их не собирали поиском.
/// </summary>
public static class CloudflareDomains
{
    private static readonly string[] Encoded =
    [
        "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com", "twdmbzcm.com",
        "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com", "bxaxtxmrw.com", "dmohrsgmohcrwb.com",
        "vwbmtmoi.com", "khgrre.com", "ulihssf.com", "tmhqsdqmfpmk.com", "xwuwoqbm.com",
        "orgcnunpj.com", "zhkuldz.com", "zypoljnslxa.com", "efabnxaowuzs.com", "zaftuzsftqdq.com",
    ];

    public static IReadOnlyList<string> All { get; } = Encoded.Select(Decode).ToList();

    /// <summary>Сдвиг букв назад на их количество, «.com» → «.co.uk» (функция _dd в tg-ws-proxy).</summary>
    internal static string Decode(string s)
    {
        if (!s.EndsWith(".com", StringComparison.Ordinal)) return s;
        var name = s[..^4];
        var n = name.Count(char.IsAsciiLetter);
        var chars = name.Select(c =>
        {
            if (!char.IsAsciiLetter(c)) return c;
            var a = char.IsAsciiLetterLower(c) ? 'a' : 'A';
            return (char)(((c - a - n) % 26 + 26) % 26 + a);
        });
        return string.Concat(chars) + ".co.uk";
    }
}
