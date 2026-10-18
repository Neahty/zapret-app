using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Zapret.Core.Telegram;

/// <summary>
/// Локальный MTProto-прокси для Telegram Desktop (порт tg-ws-proxy от Flowseal на C#).
/// Telegram подключается к 127.0.0.1 с секретом, прокси по первому пакету узнаёт дата-центр
/// и перекладывает поток в WebSocket до Telegram, перешифровывая его своим ключом.
/// </summary>
public sealed class TelegramProxyServer(int port, byte[] secret, ITelegramUpstream upstream, Action<string>? log = null)
    : IAsyncDisposable
{
    public const int DefaultPort = 1443;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Telegram держит десяток-другой соединений; больше - уже что-то не так.</summary>
    private const int MaxConnections = 512;

    private TcpListener? _listener;
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private int _active;
    private int _disposed;

    /// <summary>Настоящий порт - в тестах сервер слушает 0, то есть любой свободный.</summary>
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? port;
    public int ActiveConnections => _active;

    /// <exception cref="SocketException">Порт занят - например, запущен оригинальный tg-ws-proxy.</exception>
    public void Start()
    {
        // Только локальный адрес: прокси не должен быть открыт для всей сети.
        _listener = new TcpListener(IPAddress.Loopback, port);
        // Чужая программа не сможет занять тот же порт поверх нас и перехватить Telegram.
        _listener.ExclusiveAddressUse = true;
        _listener.Server.NoDelay = true;
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
        log?.Invoke($"Прокси Telegram слушает 127.0.0.1:{Port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; } // слушатель уже остановлен
            catch (SocketException e)
            {
                log?.Invoke($"Ошибка приёма соединения: {e.Message}");
                try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            if (_active >= MaxConnections)
            {
                client.Dispose();
                continue;
            }
            var task = HandleClientAsync(client, ct);
            _clients.TryAdd(task, 0);
            _ = task.ContinueWith(t => _clients.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var handshake = new byte[MtProto.HandshakeLength];
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(HandshakeTimeout);
                await stream.ReadExactlyAsync(handshake, timeout.Token);
            }

            var init = MtProto.TryParseClientInit(handshake, secret);
            if (init is null)
            {
                log?.Invoke("Неверный секрет или не MTProto - соединение закрыто");
                return;
            }

            var dc = init.Dc;
            var isTest = dc >= 10000;
            if (isTest) dc -= 10000;

            var relayInit = MtProto.GenerateRelayInit(init.ProtoTag, (short)(init.IsMedia ? -dc : dc));
            using var crypto = MtProto.BuildContext(init, secret, relayInit);

            await using var link = await upstream.ConnectAsync(dc, init.IsMedia, isTest, ct);
            if (link is null)
            {
                log?.Invoke($"DC{dc}{(init.IsMedia ? " media" : "")}: Telegram недоступен ни одним способом");
                return;
            }

            await link.SendAsync(relayInit, ct);
            await BridgeAsync(stream, link, crypto, link.IsFramed ? new MsgSplitter(init.ProtoTag) : null, ct);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException
                                      or EndOfStreamException or System.Net.WebSockets.WebSocketException)
        {
            // Клиент или Telegram закрыли соединение - для прокси это обычное дело.
        }
        catch (Exception e)
        {
            log?.Invoke($"Непредвиденная ошибка: {e}");
        }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>
    /// Клиент → расшифровать его ключом → зашифровать ключом Telegram → в канал; и обратно.
    /// Когда закрывается одна сторона, закрываем и другую.
    /// </summary>
    internal static async Task BridgeAsync(Stream client, ITelegramLink link, CryptoContext crypto, MsgSplitter? splitter,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Up()
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var n = await client.ReadAsync(buffer, cts.Token);
                if (n == 0)
                {
                    if (splitter?.Flush() is { } tail) await link.SendAsync(tail, cts.Token);
                    return;
                }
                var plain = buffer.AsSpan(0, n).ToArray();
                crypto.ClientDecrypt.Transform(plain);
                var cipher = (byte[])plain.Clone();
                crypto.TelegramEncrypt.Transform(cipher);

                if (splitter is null)
                {
                    await link.SendAsync(cipher, cts.Token);
                    continue;
                }
                foreach (var part in splitter.Split(cipher, plain))
                    await link.SendAsync(part, cts.Token);
            }
        }

        async Task Down()
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var n = await link.ReceiveAsync(buffer, cts.Token);
                if (n == 0) return;
                var data = buffer.AsMemory(0, n);
                crypto.TelegramDecrypt.Transform(data.Span);
                crypto.ClientEncrypt.Transform(data.Span);
                await client.WriteAsync(data, cts.Token);
            }
        }

        var up = Up();
        var down = Down();
        await Task.WhenAny(up, down);
        cts.Cancel();
        try { await Task.WhenAll(up, down); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException
                                      or System.Net.WebSockets.WebSocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null) await _acceptLoop;
        // Даём открытым соединениям закрыться самим, но не ждём вечно.
        await Task.WhenAny(Task.WhenAll(_clients.Keys), Task.Delay(TimeSpan.FromSeconds(3)));
        _stop.Dispose();
    }
}
