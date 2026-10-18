using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Zapret.Core.Telegram;

/// <summary>
/// AES-256-CTR: им шифруется весь трафик MTProto-прокси. В .NET нет готового режима CTR,
/// поэтому шифруем счётчики в режиме ECB и накладываем их на данные.
/// </summary>
public sealed class AesCtr : IDisposable
{
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[16];
    private byte[] _counters = new byte[256];
    private byte[] _keystream = new byte[256];
    private readonly byte[] _block = new byte[16];
    private int _used = 16; // сколько байт текущего блока гаммы уже израсходовано

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        _ecb = aes.CreateEncryptor();
        iv.CopyTo(_counter);
    }

    /// <summary>Шифрует (или расшифровывает - это одно и то же) данные на месте.</summary>
    public void Transform(Span<byte> data)
    {
        var i = 0;
        // Сначала дорасходуем остаток прошлого блока гаммы.
        while (i < data.Length && _used < 16) data[i++] ^= _block[_used++];
        if (i == data.Length) return;

        var rest = data.Length - i;
        var blocks = (rest + 15) / 16;
        if (_counters.Length < blocks * 16)
        {
            _counters = new byte[blocks * 16];
            _keystream = new byte[blocks * 16];
        }
        for (var b = 0; b < blocks; b++)
        {
            _counter.CopyTo(_counters, b * 16);
            Increment();
        }
        _ecb.TransformBlock(_counters, 0, blocks * 16, _keystream, 0);

        for (var k = 0; k < rest; k++) data[i + k] ^= _keystream[k];
        var tail = rest % 16;
        if (tail == 0) { _used = 16; return; }
        Array.Copy(_keystream, (blocks - 1) * 16, _block, 0, 16);
        _used = tail;
    }

    /// <summary>Пропускает n байт гаммы (как шифрование n нулей).</summary>
    public void Skip(int n) => Transform(new byte[n]);

    private void Increment()
    {
        for (var j = 15; j >= 0; j--)
            if (++_counter[j] != 0) break;
    }

    public void Dispose() => _ecb.Dispose();
}

/// <summary>Шифры одного соединения: клиент ↔ прокси и прокси ↔ Telegram.</summary>
public sealed class CryptoContext(AesCtr clientDecrypt, AesCtr clientEncrypt, AesCtr telegramEncrypt, AesCtr telegramDecrypt) : IDisposable
{
    public AesCtr ClientDecrypt { get; } = clientDecrypt;
    public AesCtr ClientEncrypt { get; } = clientEncrypt;
    public AesCtr TelegramEncrypt { get; } = telegramEncrypt;
    public AesCtr TelegramDecrypt { get; } = telegramDecrypt;

    public void Dispose()
    {
        ClientDecrypt.Dispose();
        ClientEncrypt.Dispose();
        TelegramEncrypt.Dispose();
        TelegramDecrypt.Dispose();
    }
}

/// <param name="Dc">Номер дата-центра Telegram (1-5, 203); тестовые DC приходят как 10000+N.</param>
/// <param name="IsMedia">Соединение для файлов и медиа (в пакете DC с минусом).</param>
/// <param name="ProtoTag">Транспорт MTProto: abridged (EF), intermediate (EE) или padded (DD).</param>
/// <param name="PrekeyAndIv">48 байт ключа и IV из первого пакета клиента.</param>
public sealed record ClientInit(int Dc, bool IsMedia, uint ProtoTag, byte[] PrekeyAndIv);

/// <summary>
/// Обфускация MTProto (obfuscated2), как в tg-ws-proxy от Flowseal: Telegram Desktop подключается к прокси
/// с секретом, а прокси - уже к Telegram по WebSocket, перешифровывая поток.
/// </summary>
public static class MtProto
{
    public const int HandshakeLength = 64;
    private const int SkipLength = 8;
    private const int PrekeyLength = 32;
    private const int IvLength = 16;
    private const int ProtoTagPos = 56;
    private const int DcIndexPos = 60;

    public const uint Abridged = 0xEFEFEFEF;
    public const uint Intermediate = 0xEEEEEEEE;
    public const uint PaddedIntermediate = 0xDDDDDDDD;

    /// <summary>
    /// Разбирает первые 64 байта от клиента. null - неверный секрет (после расшифровки
    /// на месте транспорта мусор) или вообще не MTProto.
    /// </summary>
    public static ClientInit? TryParseClientInit(ReadOnlySpan<byte> handshake, ReadOnlySpan<byte> secret)
    {
        if (handshake.Length < HandshakeLength) return null;
        var prekeyIv = handshake.Slice(SkipLength, PrekeyLength + IvLength).ToArray();
        using var decrypt = new AesCtr(DeriveKey(prekeyIv.AsSpan(0, PrekeyLength), secret), prekeyIv.AsSpan(PrekeyLength));
        var plain = handshake[..HandshakeLength].ToArray();
        decrypt.Transform(plain);

        var tag = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(ProtoTagPos));
        if (tag is not (Abridged or Intermediate or PaddedIntermediate)) return null;

        var dcIndex = BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(DcIndexPos));
        return new ClientInit(Math.Abs((int)dcIndex), dcIndex < 0, tag, prekeyIv);
    }

    /// <summary>
    /// Первый пакет прокси для Telegram: обычная обфускация без секрета, со своим случайным ключом.
    /// Открыты первые 56 байт (из них берутся ключ и IV), зашифрованы последние 8: транспорт и DC.
    /// </summary>
    public static byte[] GenerateRelayInit(uint protoTag, short dcIndex)
    {
        var init = RandomInit();
        using var encrypt = new AesCtr(init.AsSpan(SkipLength, PrekeyLength), init.AsSpan(SkipLength + PrekeyLength, IvLength));
        var encrypted = (byte[])init.Clone();
        encrypt.Transform(encrypted);

        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, protoTag);
        BinaryPrimitives.WriteInt16LittleEndian(tail[4..], dcIndex);
        RandomNumberGenerator.Fill(tail[6..]);
        // Гамма на позициях 56..63 = зашифрованное XOR открытое.
        for (var i = 0; i < 8; i++)
            init[ProtoTagPos + i] = (byte)(tail[i] ^ encrypted[ProtoTagPos + i] ^ init[ProtoTagPos + i]);
        return init;
    }

    public static CryptoContext BuildContext(ClientInit client, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> relayInit)
    {
        // Клиент → прокси: ключ = SHA256(prekey + secret). Обратное направление - те же 48 байт задом наперёд.
        var prekeyIv = client.PrekeyAndIv;
        var clientDecrypt = new AesCtr(DeriveKey(prekeyIv.AsSpan(0, PrekeyLength), secret), prekeyIv.AsSpan(PrekeyLength));
        clientDecrypt.Skip(HandshakeLength); // первый пакет клиента уже разобран
        var reversed = prekeyIv.Reverse().ToArray();
        var clientEncrypt = new AesCtr(DeriveKey(reversed.AsSpan(0, PrekeyLength), secret), reversed.AsSpan(PrekeyLength));

        // Прокси → Telegram: ключ берётся из пакета как есть, без секрета.
        var relayKeyIv = relayInit.Slice(SkipLength, PrekeyLength + IvLength).ToArray();
        var telegramEncrypt = new AesCtr(relayKeyIv.AsSpan(0, PrekeyLength), relayKeyIv.AsSpan(PrekeyLength));
        telegramEncrypt.Skip(HandshakeLength);
        var relayReversed = relayKeyIv.Reverse().ToArray();
        var telegramDecrypt = new AesCtr(relayReversed.AsSpan(0, PrekeyLength), relayReversed.AsSpan(PrekeyLength));

        return new CryptoContext(clientDecrypt, clientEncrypt, telegramEncrypt, telegramDecrypt);
    }

    /// <summary>
    /// Первый пакет от имени клиента (как его шлёт Telegram Desktop): нужен для проверки работы прокси.
    /// Возвращает пакет и шифры клиента - для отправки (уже после пакета) и для приёма.
    /// </summary>
    public static (byte[] Init, AesCtr Encrypt, AesCtr Decrypt) CreateClientInit(ReadOnlySpan<byte> secret, uint protoTag, short dcIndex)
    {
        var init = RandomInit();
        BinaryPrimitives.WriteUInt32LittleEndian(init.AsSpan(ProtoTagPos), protoTag);
        BinaryPrimitives.WriteInt16LittleEndian(init.AsSpan(DcIndexPos), dcIndex);

        var encrypt = new AesCtr(DeriveKey(init.AsSpan(SkipLength, PrekeyLength), secret), init.AsSpan(SkipLength + PrekeyLength, IvLength));
        var encrypted = (byte[])init.Clone();
        encrypt.Transform(encrypted);
        encrypted.AsSpan(ProtoTagPos).CopyTo(init.AsSpan(ProtoTagPos));

        var reversed = init.AsSpan(SkipLength, PrekeyLength + IvLength).ToArray().Reverse().ToArray();
        var decrypt = new AesCtr(DeriveKey(reversed.AsSpan(0, PrekeyLength), secret), reversed.AsSpan(PrekeyLength));
        return (init, encrypt, decrypt);
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> prekey, ReadOnlySpan<byte> secret)
    {
        Span<byte> buffer = stackalloc byte[prekey.Length + secret.Length];
        prekey.CopyTo(buffer);
        secret.CopyTo(buffer[prekey.Length..]);
        return SHA256.HashData(buffer);
    }

    /// <summary>Случайные 64 байта, которые нельзя спутать с HTTP, TLS или другим транспортом MTProto.</summary>
    private static byte[] RandomInit()
    {
        var init = new byte[HandshakeLength];
        while (true)
        {
            RandomNumberGenerator.Fill(init);
            if (init[0] == 0xEF) continue;
            var start = BinaryPrimitives.ReadUInt32BigEndian(init);
            if (start is 0x48454144 /* HEAD */ or 0x504F5354 /* POST */ or 0x47455420 /* GET  */ or 0x4F505449 /* OPTI */
                or 0xEEEEEEEE or 0xDDDDDDDD or 0x16030102) continue;
            if (BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(4)) == 0) continue;
            return init;
        }
    }
}

/// <summary>
/// Делит поток клиента на отдельные пакеты MTProto, чтобы каждый ушёл своим кадром WebSocket -
/// так их ждёт Telegram. Если разобрать поток не получилось, дальше пересылает как есть.
/// </summary>
public sealed class MsgSplitter(uint protoTag)
{
    private readonly List<byte> _cipher = [];
    private readonly List<byte> _plain = [];
    private bool _disabled;

    /// <param name="cipher">Данные, уже зашифрованные для Telegram.</param>
    /// <param name="plain">Те же данные в открытом виде - по ним видны длины пакетов.</param>
    public List<byte[]> Split(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> plain)
    {
        var parts = new List<byte[]>();
        if (cipher.IsEmpty) return parts;
        if (_disabled) { parts.Add(cipher.ToArray()); return parts; }

        foreach (var b in cipher) _cipher.Add(b);
        foreach (var b in plain) _plain.Add(b);

        var offset = 0;
        while (offset < _cipher.Count)
        {
            var length = NextPacketLength(offset, _cipher.Count - offset);
            if (length is null) break;
            if (length <= 0)
            {
                parts.Add(_cipher.GetRange(offset, _cipher.Count - offset).ToArray());
                offset = _cipher.Count;
                _disabled = true;
                break;
            }
            parts.Add(_cipher.GetRange(offset, length.Value).ToArray());
            offset += length.Value;
        }
        _cipher.RemoveRange(0, offset);
        _plain.RemoveRange(0, offset);
        return parts;
    }

    /// <summary>Остаток недособранного пакета - при закрытии соединения.</summary>
    public byte[]? Flush()
    {
        if (_cipher.Count == 0) return null;
        var tail = _cipher.ToArray();
        _cipher.Clear();
        _plain.Clear();
        return tail;
    }

    /// <returns>Длина пакета; null - пакет ещё не пришёл целиком; 0 - формат непонятен.</returns>
    private int? NextPacketLength(int offset, int available)
    {
        if (protoTag == MtProto.Abridged)
        {
            var first = _plain[offset];
            int payload, header;
            if (first is 0x7F or 0xFF)
            {
                if (available < 4) return null;
                payload = (_plain[offset + 1] | _plain[offset + 2] << 8 | _plain[offset + 3] << 16) * 4;
                header = 4;
            }
            else
            {
                payload = (first & 0x7F) * 4;
                header = 1;
            }
            if (payload <= 0) return 0;
            return available < header + payload ? null : header + payload;
        }

        if (protoTag is MtProto.Intermediate or MtProto.PaddedIntermediate)
        {
            if (available < 4) return null;
            var payload = (_plain[offset] | _plain[offset + 1] << 8 | _plain[offset + 2] << 16 | _plain[offset + 3] << 24) & 0x7FFFFFFF;
            if (payload <= 0) return 0;
            return available < 4 + payload ? null : 4 + payload;
        }
        return 0;
    }
}
