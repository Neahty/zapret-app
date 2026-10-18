using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Zapret.Core.Telegram;

/// <summary>
/// Прокси Telegram живёт отдельной службой Windows - как и zapret: работает с загрузки компьютера,
/// даже если приложение закрыто. Служба запускает копию приложения из Program Files с ключом --telegram-proxy.
/// </summary>
/// <param name="serviceExe">Возвращает путь к exe, из которого запускается служба (копия в Program Files).</param>
/// <param name="appVersion">Версия приложения: после обновления служба перезапускается уже на новом exe.</param>
/// <param name="log">Журнал действий со службой - чтобы по нему можно было понять, что пошло не так.</param>
public sealed class TelegramController(IServiceControl services, Func<string> serviceExe, string appVersion,
    Action<string>? log = null, TimeSpan? retryDelay = null)
{
    /// <summary>
    /// Сразу после удаления Windows ещё несколько секунд держит службу «помеченной для удаления»
    /// и не даёт создать её заново (ошибка 1072) - поэтому установку повторяем.
    /// </summary>
    private const int InstallAttempts = 6;
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);

    public const string ServiceName = "zapret-telegram";
    public const string ServiceArg = "--telegram-proxy";

    /// <summary>
    /// Прокси нужна только сеть, поэтому служба работает не от SYSTEM, а от учётной записи
    /// с минимальными правами: даже ошибка в разборе трафика не даст доступа к системе.
    /// </summary>
    public const string Account = @"NT AUTHORITY\LocalService";

    public bool IsRunning => services.GetState(ServiceName) == ServiceState.Running;

    public static string BuildCommandLine(string exe, int port, string secretHex, string appVersion) =>
        CommandLine.Join([exe, ServiceArg, "--port", port.ToString(), "--secret", secretHex, "--app-version", appVersion]);

    /// <summary>Ставит и запускает службу, если она ещё не работает с нужными настройками.</summary>
    public async Task EnsureAsync(string secretHex, CancellationToken ct = default)
    {
        var expected = BuildCommandLine(serviceExe(), TelegramProxyServer.DefaultPort, secretHex, appVersion);
        var state = services.GetState(ServiceName);
        if (state != ServiceState.NotInstalled && services.GetCommandLine(ServiceName) == expected)
        {
            if (state != ServiceState.Running) await services.StartAsync(ServiceName, ct);
            return;
        }

        // Новая версия или секрет: служба должна перезапуститься с новой командной строкой.
        log?.Invoke($"Ставлю службу {ServiceName}: {expected.Replace(secretHex, "<секрет>")}");
        await services.StopAsync(ServiceName, ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await services.InstallAsync(ServiceName, "zapret-telegram",
                    "Запрет: прокси для Telegram Desktop (tg-ws-proxy)", expected, ct, Account);
                break;
            }
            catch (InvalidOperationException e) when (attempt < InstallAttempts)
            {
                log?.Invoke($"Попытка {attempt}: {e.Message}");
                await Task.Delay(_retryDelay, ct);
            }
        }
        try
        {
            await services.StartAsync(ServiceName, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            // Сообщение WindowsServiceControl - про службу zapret и VPN; здесь причина почти всегда в порте.
            log?.Invoke($"Служба не запустилась: {e.Message}");
            throw NotStarted(e);
        }
        if (services.GetState(ServiceName) != ServiceState.Running) throw NotStarted(null);
        log?.Invoke("Служба запущена");
    }

    private static InvalidOperationException NotStarted(Exception? inner) => new(
        $"Прокси Telegram не запустился. Возможно, порт {TelegramProxyServer.DefaultPort} занят другой программой (например, TgWsProxy).", inner);

    public async Task DisableAsync(CancellationToken ct = default)
    {
        if (services.GetState(ServiceName) == ServiceState.NotInstalled) return;
        log?.Invoke($"Удаляю службу {ServiceName}");
        await services.StopAsync(ServiceName, ct);
        await services.DeleteAsync(ServiceName, ct);
    }
}

/// <summary>Аргументы службы: «--telegram-proxy --port 1443 --secret …».</summary>
public sealed record TelegramServiceArgs(int Port, byte[] Secret)
{
    public static TelegramServiceArgs? Parse(IReadOnlyList<string> args)
    {
        if (!args.Contains(TelegramController.ServiceArg, StringComparer.OrdinalIgnoreCase)) return null;
        string? Value(string name)
        {
            var i = args.ToList().FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }

        var port = int.TryParse(Value("--port"), out var p) && p is > 0 and < 65536 ? p : TelegramProxyServer.DefaultPort;
        var secret = TelegramSecret.TryParse(Value("--secret"))
                     ?? throw new ArgumentException("Не указан секрет прокси Telegram (--secret, 32 hex-символа)");
        return new TelegramServiceArgs(port, secret);
    }
}

public static class TelegramSecret
{
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static byte[]? TryParse(string? hex)
    {
        if (hex is not { Length: 32 }) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    /// <summary>Ссылка, по которой Telegram Desktop сам добавляет прокси. «dd» - режим со случайной длиной пакетов.</summary>
    public static string Link(string secretHex, int port = TelegramProxyServer.DefaultPort) =>
        $"tg://proxy?server=127.0.0.1&port={port}&secret=dd{secretHex}";
}

/// <summary>Работает ли прокси Telegram на самом деле. В тестах подменяется.</summary>
public interface ITelegramCheck
{
    /// <summary>null - прокси выключен, проверять нечего.</summary>
    Task<ProbeResult?> CheckAsync(CancellationToken ct = default);
}

/// <summary>Проверка службы на этом компьютере: прокси на 127.0.0.1:1443 с секретом из настроек.</summary>
/// <param name="secret">null - прокси выключен.</param>
public sealed class LocalTelegramCheck(Func<string?> secret, TimeSpan timeout) : ITelegramCheck
{
    public async Task<ProbeResult?> CheckAsync(CancellationToken ct = default)
    {
        var bytes = TelegramSecret.TryParse(secret());
        if (bytes is null) return null;
        return await TelegramProbe.CheckAsync(TelegramProxyServer.DefaultPort, bytes, timeout, ct: ct);
    }
}

/// <summary>
/// Проверка «по-настоящему»: подключаемся к своему прокси как Telegram Desktop и отправляем первый
/// запрос протокола (req_pq_multi). Если Telegram ответил (resPQ) - сообщения ходят.
/// </summary>
public static class TelegramProbe
{
    private const uint ReqPqMulti = 0xBE7E8EF1;
    private const uint ResPq = 0x05162463;

    public static async Task<ProbeResult> CheckAsync(int port, byte[] secret, TimeSpan timeout, short dc = 2, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var stream = client.GetStream();

            var (init, encrypt, decrypt) = MtProto.CreateClientInit(secret, MtProto.Intermediate, dc);
            using (encrypt)
            using (decrypt)
            {
                var nonce = RandomNumberGenerator.GetBytes(16);
                var packet = BuildReqPq(nonce);
                encrypt.Transform(packet);
                await stream.WriteAsync(init.Concat(packet).ToArray(), cts.Token);

                var header = new byte[4];
                await stream.ReadExactlyAsync(header, cts.Token);
                decrypt.Transform(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 40 or > 4096) return new ProbeResult(false, sw.Elapsed, $"Странный ответ Telegram: {length} байт");

                var body = new byte[length];
                await stream.ReadExactlyAsync(body, cts.Token);
                decrypt.Transform(body);
                // auth_key_id(8) + message_id(8) + длина(4) + конструктор(4) + nonce(16)
                var ok = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)) == ResPq &&
                         body.AsSpan(24, 16).SequenceEqual(nonce);
                return new ProbeResult(ok, sw.Elapsed, ok ? null : "Telegram ответил не тем");
            }
        }
        catch (Exception e) when (e is SocketException or IOException or EndOfStreamException
                                      or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new ProbeResult(false, sw.Elapsed, e.Message);
        }
    }

    /// <summary>Незашифрованное сообщение MTProto с req_pq_multi в транспорте intermediate (длина + данные).</summary>
    internal static byte[] BuildReqPq(byte[] nonce)
    {
        var packet = new byte[4 + 40];
        BinaryPrimitives.WriteInt32LittleEndian(packet, 40);
        var body = packet.AsSpan(4);
        // auth_key_id = 0: сообщение до обмена ключами.
        var messageId = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32) | (uint)(Random.Shared.Next() & ~3);
        BinaryPrimitives.WriteInt64LittleEndian(body[8..], messageId);
        BinaryPrimitives.WriteInt32LittleEndian(body[16..], 20);
        BinaryPrimitives.WriteUInt32LittleEndian(body[20..], ReqPqMulti);
        nonce.CopyTo(body[24..]);
        return packet;
    }
}
