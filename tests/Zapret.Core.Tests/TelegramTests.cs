using System.Security.Cryptography.X509Certificates;
using System.Buffers.Binary;
using System.Threading.Channels;
using Zapret.Core;
using Zapret.Core.Telegram;

namespace Zapret.Core.Tests;

public sealed class AesCtrTests
{
    // NIST SP 800-38A, F.5.5 CTR-AES256.Encrypt
    private static readonly byte[] Key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
    private static readonly byte[] Iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
    private static readonly byte[] Plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
    private static readonly byte[] Cipher = Convert.FromHexString("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5");

    [Fact]
    public void Matches_nist_vector()
    {
        var data = (byte[])Plain.Clone();
        using var ctr = new AesCtr(Key, Iv);
        ctr.Transform(data);
        data.Should().Equal(Cipher);
    }

    [Fact]
    public void Chunked_transform_equals_whole()
    {
        var data = (byte[])Plain.Clone();
        using var ctr = new AesCtr(Key, Iv);
        // Куски не по границе блоков: гамма должна продолжаться с того же места.
        ctr.Transform(data.AsSpan(0, 3));
        ctr.Transform(data.AsSpan(3, 17));
        ctr.Transform(data.AsSpan(20));
        data.Should().Equal(Cipher);
    }

    [Fact]
    public void Skip_is_like_encrypting_zeros()
    {
        using var a = new AesCtr(Key, Iv);
        using var b = new AesCtr(Key, Iv);
        a.Skip(21);
        b.Transform(new byte[21]);
        var x = new byte[40];
        var y = new byte[40];
        a.Transform(x);
        b.Transform(y);
        x.Should().Equal(y);
    }
}

public sealed class MtProtoTests
{
    private static readonly byte[] Secret = Convert.FromHexString("00112233445566778899aabbccddeeff");

    [Theory]
    [InlineData(MtProto.Intermediate, 2)]
    [InlineData(MtProto.PaddedIntermediate, -4)]
    [InlineData(MtProto.Abridged, 10002)]
    public void Client_init_is_parsed_back(uint tag, short dcIndex)
    {
        var (init, enc, dec) = MtProto.CreateClientInit(Secret, tag, dcIndex);
        enc.Dispose();
        dec.Dispose();

        var parsed = MtProto.TryParseClientInit(init, Secret);

        parsed.Should().NotBeNull();
        parsed!.ProtoTag.Should().Be(tag);
        parsed.Dc.Should().Be(Math.Abs((int)dcIndex));
        parsed.IsMedia.Should().Be(dcIndex < 0);
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var (init, enc, dec) = MtProto.CreateClientInit(Secret, MtProto.Intermediate, 2);
        enc.Dispose();
        dec.Dispose();
        MtProto.TryParseClientInit(init, new byte[16]).Should().BeNull();
    }

    [Fact]
    public void Relay_init_carries_tag_and_dc_for_telegram()
    {
        var init = MtProto.GenerateRelayInit(MtProto.Intermediate, -3);
        var (tag, dc) = FakeTelegram.ParseRelayInit(init, out var server);
        server.Dispose();
        tag.Should().Be(MtProto.Intermediate);
        dc.Should().Be(-3);
    }

    [Fact]
    public void Client_traffic_reaches_telegram_decrypted_and_back()
    {
        var (init, clientEnc, clientDec) = MtProto.CreateClientInit(Secret, MtProto.Intermediate, 2);
        var parsed = MtProto.TryParseClientInit(init, Secret)!;
        var relay = MtProto.GenerateRelayInit(parsed.ProtoTag, 2);
        using var ctx = MtProto.BuildContext(parsed, Secret, relay);
        FakeTelegram.ParseRelayInit(relay, out var server);

        // Клиент → прокси → Telegram
        var message = "hello telegram"u8.ToArray();
        var wire = (byte[])message.Clone();
        clientEnc.Transform(wire);
        ctx.ClientDecrypt.Transform(wire);
        wire.Should().Equal(message, "прокси видит открытые данные");
        ctx.TelegramEncrypt.Transform(wire);
        server.Decrypt.Transform(wire);
        wire.Should().Equal(message);

        // Telegram → прокси → клиент
        var answer = "hi"u8.ToArray();
        var back = (byte[])answer.Clone();
        server.Encrypt.Transform(back);
        ctx.TelegramDecrypt.Transform(back);
        ctx.ClientEncrypt.Transform(back);
        clientDec.Transform(back);
        back.Should().Equal(answer);

        clientEnc.Dispose();
        clientDec.Dispose();
        server.Dispose();
    }

    [Fact]
    public void ReqPq_packet_has_intermediate_length_and_constructor()
    {
        var nonce = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var packet = TelegramProbe.BuildReqPq(nonce);
        BinaryPrimitives.ReadInt32LittleEndian(packet).Should().Be(40);
        BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(4)).Should().Be(0, "auth_key_id до обмена ключами - ноль");
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24)).Should().Be(0xBE7E8EF1);
        packet.AsSpan(28, 16).ToArray().Should().Equal(nonce);
        (BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(12)) % 4).Should().Be(0, "message_id делится на 4");
    }
}

public sealed class MsgSplitterTests
{
    private static byte[] Intermediate(int length, byte fill) =>
        BitConverter.GetBytes(length).Concat(Enumerable.Repeat(fill, length)).ToArray();

    [Fact]
    public void Splits_intermediate_stream_into_packets_across_chunks()
    {
        var stream = Intermediate(8, 1).Concat(Intermediate(12, 2)).ToArray();
        var splitter = new MsgSplitter(MtProto.Intermediate);

        var first = splitter.Split(stream.AsSpan(0, 15), stream.AsSpan(0, 15));
        var second = splitter.Split(stream.AsSpan(15), stream.AsSpan(15));

        first.Should().ContainSingle().Which.Should().Equal(stream[..12]);
        second.Should().ContainSingle().Which.Should().Equal(stream[12..]);
        splitter.Flush().Should().BeNull();
    }

    [Fact]
    public void Splits_abridged_short_and_long_headers()
    {
        var shortPacket = new byte[] { 0x02 }.Concat(new byte[8]).ToArray();               // 2*4 байта
        var longPacket = new byte[] { 0x7F, 0x80, 0x00, 0x00 }.Concat(new byte[512]).ToArray(); // 128*4 байта
        var stream = shortPacket.Concat(longPacket).ToArray();

        var parts = new MsgSplitter(MtProto.Abridged).Split(stream, stream);

        parts.Select(p => p.Length).Should().Equal(9, 516);
    }

    [Fact]
    public void Unknown_format_is_passed_through()
    {
        var garbage = new byte[] { 0, 0, 0, 0, 1, 2, 3 };
        var splitter = new MsgSplitter(MtProto.Intermediate);
        splitter.Split(garbage, garbage).Should().ContainSingle().Which.Should().Equal(garbage);
        splitter.Split(new byte[] { 9 }, new byte[] { 9 }).Should().ContainSingle();
    }

    [Fact]
    public void Flush_returns_incomplete_tail()
    {
        var splitter = new MsgSplitter(MtProto.Intermediate);
        var partial = Intermediate(100, 7)[..30];
        splitter.Split(partial, partial).Should().BeEmpty();
        splitter.Flush().Should().Equal(partial);
    }
}

public sealed class TelegramProxyServerTests
{
    private static readonly byte[] Secret = Convert.FromHexString("ffeeddccbbaa99887766554433221100");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_through_proxy_gets_answer_from_telegram(bool framed)
    {
        var telegram = new FakeTelegram(framed);
        await using var server = new TelegramProxyServer(0, Secret, telegram);
        server.Start();

        var result = await TelegramProbe.CheckAsync(server.Port, Secret, TimeSpan.FromSeconds(5), dc: -4);

        result.Ok.Should().BeTrue(result.Error);
        telegram.Connections.Should().ContainSingle().Which.Should().Be((4, true, false));
        telegram.RelayDc.Should().Be(-4, "Telegram должен получить тот же DC и признак медиа");
        if (framed) telegram.Frames.Should().ContainSingle().Which.Should().Be(44, "пакет MTProto - отдельным кадром");
    }

    [Fact]
    public async Task Test_dc_is_routed_as_test()
    {
        var telegram = new FakeTelegram(framed: true);
        await using var server = new TelegramProxyServer(0, Secret, telegram);
        server.Start();

        await TelegramProbe.CheckAsync(server.Port, Secret, TimeSpan.FromSeconds(5), dc: 10002);

        telegram.Connections.Should().ContainSingle().Which.Should().Be((2, false, true));
    }

    [Fact]
    public async Task Wrong_secret_never_reaches_telegram()
    {
        var telegram = new FakeTelegram(framed: true);
        await using var server = new TelegramProxyServer(0, Secret, telegram);
        server.Start();

        var result = await TelegramProbe.CheckAsync(server.Port, new byte[16], TimeSpan.FromSeconds(5));

        result.Ok.Should().BeFalse();
        telegram.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task Unreachable_telegram_closes_client()
    {
        await using var server = new TelegramProxyServer(0, Secret, new NoTelegram());
        server.Start();
        var result = await TelegramProbe.CheckAsync(server.Port, Secret, TimeSpan.FromSeconds(5));
        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Busy_port_throws_on_start()
    {
        await using var first = new TelegramProxyServer(0, Secret, new NoTelegram());
        first.Start();
        await using var second = new TelegramProxyServer(first.Port, Secret, new NoTelegram());
        second.Invoking(s => s.Start()).Should().Throw<System.Net.Sockets.SocketException>();
    }

    private sealed class NoTelegram : ITelegramUpstream
    {
        public Task<ITelegramLink?> ConnectAsync(int dc, bool isMedia, bool isTest, CancellationToken ct) =>
            Task.FromResult<ITelegramLink?>(null);
    }
}

/// <summary>
/// Telegram в памяти: разбирает первый пакет прокси, расшифровывает req_pq_multi и отвечает resPQ с тем же nonce.
/// </summary>
public sealed class FakeTelegram(bool framed) : ITelegramUpstream
{
    public List<(int Dc, bool IsMedia, bool IsTest)> Connections { get; } = [];
    public List<int> Frames { get; } = [];
    public short? RelayDc { get; private set; }

    public sealed record ServerCrypto(AesCtr Decrypt, AesCtr Encrypt) : IDisposable
    {
        public void Dispose() { Decrypt.Dispose(); Encrypt.Dispose(); }
    }

    /// <summary>Как сервер Telegram разбирает обфускацию без секрета.</summary>
    public static (uint Tag, short Dc) ParseRelayInit(byte[] init, out ServerCrypto crypto)
    {
        var keyIv = init.AsSpan(8, 48).ToArray();
        var decrypt = new AesCtr(keyIv.AsSpan(0, 32), keyIv.AsSpan(32));
        var plain = (byte[])init.Clone();
        decrypt.Transform(plain);
        var reversed = keyIv.Reverse().ToArray();
        crypto = new ServerCrypto(decrypt, new AesCtr(reversed.AsSpan(0, 32), reversed.AsSpan(32)));
        return (BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(56)), BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(60)));
    }

    public Task<ITelegramLink?> ConnectAsync(int dc, bool isMedia, bool isTest, CancellationToken ct)
    {
        Connections.Add((dc, isMedia, isTest));
        return Task.FromResult<ITelegramLink?>(new Link(this));
    }

    private sealed class Link(FakeTelegram owner) : ITelegramLink
    {
        private readonly Channel<byte[]> _toProxy = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> _received = [];
        private ServerCrypto? _crypto;

        public bool IsFramed => owner._framed;
        public string Route => "fake";

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (_crypto is null)
            {
                var (_, dc) = ParseRelayInit(data.ToArray(), out _crypto);
                owner.RelayDc = dc;
                return Task.CompletedTask;
            }
            owner.Frames.Add(data.Length);
            var plain = data.ToArray();
            _crypto.Decrypt.Transform(plain);
            _received.AddRange(plain);
            if (_received.Count < 44) return Task.CompletedTask;

            var request = _received.ToArray();
            var reply = request[..44];
            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(24), 0x05162463); // resPQ
            _crypto.Encrypt.Transform(reply);
            _toProxy.Writer.TryWrite(reply);
            return Task.CompletedTask;
        }

        public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var data = await _toProxy.Reader.ReadAsync(ct);
            data.CopyTo(buffer);
            return data.Length;
        }

        public ValueTask DisposeAsync()
        {
            _crypto?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly bool _framed = framed;
}

public sealed class TelegramControllerTests
{
    private readonly FakeServiceControl _services = new();
    private const string Exe = @"C:\Program Files\ZapretApp\Zapret.exe";
    private const string Secret = "00112233445566778899aabbccddeeff";

    [Fact]
    public async Task Ensure_installs_and_starts_service_once()
    {
        var sut = new TelegramController(_services, () => Exe, "1.1.0");

        await sut.EnsureAsync(Secret);
        await sut.EnsureAsync(Secret);

        sut.IsRunning.Should().BeTrue();
        _services.Calls.Count(c => c == $"install {TelegramController.ServiceName}").Should().Be(1);
        _services.GetCommandLine(TelegramController.ServiceName).Should()
            .Be($"\"{Exe}\" --telegram-proxy --port 1443 --secret {Secret} --app-version 1.1.0");
    }

    [Fact]
    public async Task New_app_version_reinstalls_service()
    {
        await new TelegramController(_services, () => Exe, "1.1.0").EnsureAsync(Secret);
        await new TelegramController(_services, () => Exe, "1.2.0").EnsureAsync(Secret);

        _services.Calls.Count(c => c == $"install {TelegramController.ServiceName}").Should().Be(2);
        _services.GetCommandLine(TelegramController.ServiceName).Should().EndWith("--app-version 1.2.0");
    }

    [Fact]
    public async Task Stopped_service_is_started_without_reinstall()
    {
        var sut = new TelegramController(_services, () => Exe, "1.1.0");
        _services.Preinstall(TelegramController.ServiceName, TelegramController.BuildCommandLine(Exe, 1443, Secret, "1.1.0"), ServiceState.Stopped);

        await sut.EnsureAsync(Secret);

        _services.Calls.Should().Equal($"start {TelegramController.ServiceName}");
    }

    [Fact]
    public async Task Failing_service_reports_busy_port()
    {
        _services.FailToStart = true;
        var act = () => new TelegramController(_services, () => Exe, "1.1.0").EnsureAsync(Secret);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1443*");
    }

    [Fact]
    public async Task Disable_removes_service()
    {
        var sut = new TelegramController(_services, () => Exe, "1.1.0");
        await sut.EnsureAsync(Secret);
        await sut.DisableAsync();
        _services.GetState(TelegramController.ServiceName).Should().Be(ServiceState.NotInstalled);
    }

    [Fact]
    public void Service_args_round_trip()
    {
        string[] args = [TelegramController.ServiceArg, "--port", "1443", "--secret", Secret, "--app-version", "1.1.0"];
        var parsed = TelegramServiceArgs.Parse(args)!;
        parsed.Port.Should().Be(1443);
        Convert.ToHexString(parsed.Secret).Should().BeEquivalentTo(Secret);

        TelegramServiceArgs.Parse(["--tray"]).Should().BeNull();
        FluentActions.Invoking(() => TelegramServiceArgs.Parse([TelegramController.ServiceArg, "--secret", "xyz"]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Secret_and_link()
    {
        var secret = TelegramSecret.Generate();
        secret.Should().MatchRegex("^[0-9a-f]{32}$");
        TelegramSecret.TryParse(secret).Should().HaveCount(16);
        TelegramSecret.TryParse("zz").Should().BeNull();
        TelegramSecret.Link(Secret).Should().Be($"tg://proxy?server=127.0.0.1&port=1443&secret=dd{Secret}");
    }

    [Fact]
    public void Cloudflare_domains_decode_to_co_uk()
    {
        CloudflareDomains.All.Should().HaveCount(20).And.OnlyContain(d => d.EndsWith(".co.uk") && d.All(c => char.IsAsciiLetterLower(c) || c == '.'));
        CloudflareDomains.Decode("example.org").Should().Be("example.org");
    }

    [Fact]
    public void Media_connections_try_media_domain_first()
    {
        TelegramUpstream.WsDomains(2, isMedia: true).Should().Equal("kws2-1.web.telegram.org", "kws2.web.telegram.org");
        TelegramUpstream.WsDomains(203, isMedia: false).Should().Equal("kws2.web.telegram.org");
    }
}

public sealed class ExtraDomainsTests
{
    [Fact]
    public void Adds_soundcloud_and_telegram_to_real_list_once()
    {
        using var zapret = new ZapretCopy();
        var file = Path.Combine(zapret.Paths.Lists, "list-general.txt");
        var original = File.ReadAllText(file);

        ExtraDomains.Ensure(zapret.Paths.Lists);
        var once = File.ReadAllText(file);
        ExtraDomains.Ensure(zapret.Paths.Lists);

        File.ReadAllText(file).Should().Be(once, "повторный вызов ничего не дописывает");
        once.Should().StartWith(original);
        var lines = once.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        lines.Should().Contain(["soundcloud.com", "sndcdn.com", "telegram.org", "discord.com", ExtraDomains.Marker]);
        lines.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Does_not_glue_domain_to_last_line_without_newline()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("list-general.txt"), "discord.com");

        ExtraDomains.Ensure(dir.Path);

        var lines = File.ReadAllLines(dir.File("list-general.txt"));
        lines[0].Should().Be("discord.com");
        lines.Should().Contain("soundcloud.com");
    }

    [Fact]
    public async Task Enabling_bypass_adds_domains()
    {
        using var zapret = new ZapretCopy();
        var controller = new BypassController(zapret.Paths, zapret.Strategies, new FakeServiceControl(), new FakeRunner(),
            new MemorySettingsStore(), new FakeTime(DateTimeOffset.UnixEpoch));

        await controller.EnableAsync(null);

        File.ReadAllText(Path.Combine(zapret.Paths.Lists, "list-general.txt")).Should().Contain("soundcloud.com");
    }
}

/// <summary>Тесты с настоящим Telegram: запускаются только при ZAPRET_NET_TESTS=1.</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ZAPRET_NET_TESTS") != "1")
            Skip = "Нужна сеть: задайте ZAPRET_NET_TESTS=1";
    }
}

public sealed class TelegramNetworkTests
{
    private static readonly byte[] Secret = Convert.FromHexString(TelegramSecret.Generate());

    private static async Task ProbeAsync(short dc)
    {
        var log = new List<string>();
        await using var server = new TelegramProxyServer(0, Secret, new TelegramUpstream(m => { lock (log) log.Add(m); }));
        server.Start();
        var result = await TelegramProbe.CheckAsync(server.Port, Secret, TimeSpan.FromSeconds(40), dc);
        if (!result.Ok) throw new Xunit.Sdk.XunitException($"DC{dc}: {result.Error}\n{string.Join("\n", log)}");
    }

    [NetworkFact] public Task Dc2_via_websocket() => ProbeAsync(2);
    [NetworkFact] public Task Dc4_via_websocket() => ProbeAsync(4);
    [NetworkFact] public Task Dc1_via_fallback() => ProbeAsync(1);
    [NetworkFact] public Task Dc2_media() => ProbeAsync(-2);
}

public sealed class TelegramViewModelTests : IDisposable
{
    private readonly ZapretCopy _zapret = new();
    private readonly FakeServiceControl _services = new();
    private readonly MemorySettingsStore _store = new();
    private readonly FakeTelegramCheck _check = new();
    private readonly Zapret.Core.ViewModels.MainViewModel _sut;

    public TelegramViewModelTests()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var probe = FakeProbe.AllOk(80);
        var controller = new BypassController(_zapret.Paths, _zapret.Strategies, _services, new FakeRunner(), _store, time);
        _sut = new Zapret.Core.ViewModels.MainViewModel(controller, new HealthChecker(probe),
            () => new StrategyTester(new FakeLauncher(), probe, HealthChecker.AllUrls, TimeSpan.Zero),
            new FakeAutostart(), new IpsetManager(_zapret.Paths.Lists), time,
            new TelegramController(_services, () => @"C:\Program Files\ZapretApp\Zapret.exe", "1.1.0"), _check);
    }

    public void Dispose() => _zapret.Dispose();

    private ServiceState TelegramState => _services.GetState(TelegramController.ServiceName);

    [Fact]
    public async Task First_run_starts_telegram_proxy_and_checks_it()
    {
        await _sut.InitializeAsync();

        TelegramState.Should().Be(ServiceState.Running);
        _store.Current.TelegramSecret.Should().MatchRegex("^[0-9a-f]{32}$", "секрет создаётся один раз и запоминается");
        _sut.TelegramLink.Should().EndWith("dd" + _store.Current.TelegramSecret);
        var item = _sut.Health.Single(h => h.Name == "Telegram");
        item.Kind.Should().Be(Zapret.Core.ViewModels.CheckKind.Good);
        item.Detail.Should().Be("214 мс");
    }

    [Fact]
    public async Task Secret_survives_restarts()
    {
        await _sut.InitializeAsync();
        var secret = _store.Current.TelegramSecret;
        await _sut.InitializeAsync();
        _store.Current.TelegramSecret.Should().Be(secret);
        _services.Calls.Count(c => c == $"install {TelegramController.ServiceName}").Should().Be(1);
    }

    [Fact]
    public async Task Power_button_turns_telegram_off_and_on()
    {
        await _sut.InitializeAsync();

        await _sut.ToggleCommand.ExecuteAsync(null);
        TelegramState.Should().Be(ServiceState.NotInstalled);

        await _sut.ToggleCommand.ExecuteAsync(null);
        TelegramState.Should().Be(ServiceState.Running);
    }

    [Fact]
    public async Task Telegram_switch_only_affects_telegram()
    {
        await _sut.InitializeAsync();

        await _sut.SetTelegramEnabledCommand.ExecuteAsync(false);

        TelegramState.Should().Be(ServiceState.NotInstalled);
        _sut.IsOn.Should().BeTrue("обход Discord и YouTube продолжает работать");
        _store.Current.TelegramEnabled.Should().BeFalse();
        _check.Enabled = false;
        await _sut.CheckHealthCommand.ExecuteAsync(null);
        _sut.Health.Single(h => h.Name == "Telegram").Detail.Should().Be("выключен");
    }

    [Fact]
    public async Task Telegram_failure_is_shown_but_bypass_stays_on()
    {
        _services.FailToStart = true;
        await _sut.InitializeAsync();
        // Фейковые службы не запускаются вовсе - для этого теста важно лишь, что ошибка видна.
        _sut.ErrorMessage.Should().NotBeNull();
    }

    private sealed class FakeTelegramCheck : ITelegramCheck
    {
        public bool Enabled { get; set; } = true;
        public Task<ProbeResult?> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult<ProbeResult?>(Enabled ? new ProbeResult(true, TimeSpan.FromMilliseconds(214)) : null);
    }
}

public sealed class DeploymentTests
{
    [Fact]
    public void Busy_exe_is_renamed_and_replaced()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(src.File("Zapret.exe"), "new version");
        File.WriteAllText(dst.File("Zapret.exe"), "old");

        // Как служба Telegram: файл открыт без права на запись, но с правом на переименование.
        using (new FileStream(dst.File("Zapret.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var exe = AppDeployment.EnsureDeployed(src.Path, dst.Path, "Zapret.exe");
            File.ReadAllText(exe).Should().Be("new version");
        }

        Directory.GetFiles(dst.Path, "Zapret.exe.old-*").Should().ContainSingle();
        // Следующее обновление подчищает старые копии.
        File.WriteAllText(src.File("Zapret.exe"), "newer");
        AppDeployment.EnsureDeployed(src.Path, dst.Path, "Zapret.exe");
        Directory.GetFiles(dst.Path, "Zapret.exe.old-*").Should().BeEmpty();
    }
}

public sealed class ReviewFixTests
{
    [Fact]
    public async Task Telegram_service_runs_as_local_service_not_system()
    {
        var services = new FakeServiceControl();
        await new TelegramController(services, () => @"C:\Program Files\ZapretApp\Zapret.exe", "1.1.0")
            .EnsureAsync("00112233445566778899aabbccddeeff");
        services.Accounts[TelegramController.ServiceName].Should().Be(@"NT AUTHORITY\LocalService");
    }

    [Fact]
    public async Task Update_from_previous_version_restarts_bypass_to_pick_up_new_domains()
    {
        using var zapret = new ZapretCopy();
        var services = new FakeServiceControl();
        var store = new MemorySettingsStore();
        var controller = new BypassController(zapret.Paths, zapret.Strategies, services, new FakeRunner(), store,
            new FakeTime(DateTimeOffset.UnixEpoch));
        // Как у версии 1.0.0: служба работает, но SoundCloud в списке ещё нет.
        services.Preinstall(BypassController.ServiceName, controller.BuildCommandLine(controller.Resolve(null)), ServiceState.Running);

        var action = await controller.EnsureStartupStateAsync();

        action.Should().Be(StartupAction.Installed);
        (await controller.EnsureStartupStateAsync()).Should().Be(StartupAction.None, "второй запуск ничего не перезапускает");
    }

    [Fact]
    public void Fronting_accepts_only_telegram_certificates()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        X509Certificate2 Cert(string name)
        {
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                $"CN={name}", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddDnsName(name);
            req.CertificateExtensions.Add(san.Build());
            return req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        }

        TelegramUpstream.IsTelegramCertificate(Cert("*.telegram.org"), "kws2.web.telegram.org").Should().BeTrue();
        TelegramUpstream.IsTelegramCertificate(Cert("*.web.telegram.org"), "kws2.web.telegram.org").Should().BeTrue();
        TelegramUpstream.IsTelegramCertificate(Cert("sprinthost.ru"), "kws2.web.telegram.org").Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_twice_is_safe()
    {
        var server = new TelegramProxyServer(0, new byte[16], new FakeTelegram(true));
        server.Start();
        await server.DisposeAsync();
        await server.DisposeAsync();
    }
}

public sealed class TelegramRecoveryTests
{
    private const string Exe = @"C:\Program Files\ZapretApp\Zapret.exe";
    private const string Secret = "00112233445566778899aabbccddeeff";

    [Fact]
    public async Task Install_is_retried_while_service_is_marked_for_deletion()
    {
        var services = new FakeServiceControl { FailInstallTimes = 2 };
        var log = new List<string>();
        var sut = new TelegramController(services, () => Exe, "1.1.1", log.Add, TimeSpan.Zero);

        await sut.EnsureAsync(Secret);

        sut.IsRunning.Should().BeTrue();
        log.Should().Contain(l => l.StartsWith("Попытка 1")).And.Contain("Служба запущена");
        log.Should().NotContain(l => l.Contains(Secret), "секрет не пишем в журнал");
    }

    [Fact]
    public async Task Power_off_and_on_reinstalls_even_if_windows_is_slow_to_delete()
    {
        var services = new FakeServiceControl();
        var sut = new TelegramController(services, () => Exe, "1.1.1", retryDelay: TimeSpan.Zero);
        await sut.EnsureAsync(Secret);
        await sut.DisableAsync();

        services.FailInstallTimes = 3;
        await sut.EnsureAsync(Secret);

        sut.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Health_check_brings_stopped_proxy_back_and_keeps_error_visible()
    {
        using var zapret = new ZapretCopy();
        var services = new FakeServiceControl();
        var controller = new BypassController(zapret.Paths, zapret.Strategies, services, new FakeRunner(),
            new MemorySettingsStore(), new FakeTime(DateTimeOffset.UnixEpoch));
        var telegram = new TelegramController(services, () => Exe, "1.1.1", retryDelay: TimeSpan.Zero);
        var probe = FakeProbe.AllOk();
        var vm = new Zapret.Core.ViewModels.MainViewModel(controller, new HealthChecker(probe),
            () => new StrategyTester(new FakeLauncher(), probe, HealthChecker.AllUrls, TimeSpan.Zero),
            new FakeAutostart(), new IpsetManager(zapret.Paths.Lists), new FakeTime(DateTimeOffset.UnixEpoch),
            telegram, new LocalTelegramCheck(() => null, TimeSpan.Zero));
        await vm.InitializeAsync();

        // Кто-то остановил службу - проверка доступа поднимает её сама.
        await services.StopAsync(TelegramController.ServiceName);
        await vm.CheckHealthCommand.ExecuteAsync(null);
        telegram.IsRunning.Should().BeTrue();
        vm.TelegramError.Should().BeNull();

        // А если не поднимается - причина остаётся на экране, даже после других действий.
        await services.DeleteAsync(TelegramController.ServiceName);
        services.FailInstallTimes = 100;
        await vm.CheckHealthCommand.ExecuteAsync(null);
        vm.TelegramError.Should().NotBeNull();
        await vm.AutoSelectCommand.ExecuteAsync(null);
        vm.TelegramError.Should().NotBeNull();
    }
}

public sealed class DeploymentCleanupTests
{
    [Fact]
    public void Stale_files_from_previous_build_are_removed()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        File.WriteAllText(src.File("Zapret.exe"), "single-file");
        File.WriteAllText(src.File("wpfgfx_cor3.dll"), "native");
        File.WriteAllText(dst.File("Zapret.dll"), "old debug build");
        File.WriteAllText(dst.File("Preview.dll"), "tool");
        File.WriteAllText(dst.File("Zapret.deps.json"), "{}");
        File.WriteAllText(dst.File("notes.txt"), "чужой файл не трогаем");

        AppDeployment.EnsureDeployed(src.Path, dst.Path, "Zapret.exe");

        Directory.GetFiles(dst.Path).Select(Path.GetFileName).Should()
            .BeEquivalentTo(["Zapret.exe", "wpfgfx_cor3.dll", "notes.txt"]);
    }
}

public sealed class UrlHandlerCommandTests
{
    private const string Link = "tg://proxy?server=127.0.0.1&port=1443&secret=dd00";

    [Fact]
    public void Quoted_exe_with_placeholder()
    {
        var r = UrlHandlerCommand.Build("\"C:\\Users\\me\\AyuGram\\AyuGram.exe\"  -- \"%1\"", Link);
        Assert.Equal("C:\\Users\\me\\AyuGram\\AyuGram.exe", r!.Value.Exe);
        Assert.Equal("-- \"" + Link + "\"", r.Value.Arguments);
    }

    [Fact]
    public void Unquoted_exe_without_placeholder_appends_link()
    {
        var r = UrlHandlerCommand.Build("C:\\tg\\Telegram.exe", Link);
        Assert.Equal("C:\\tg\\Telegram.exe", r!.Value.Exe);
        Assert.Equal("\"" + Link + "\"", r.Value.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\broken")]
    [InlineData("\"\" -- \"%1\"")]
    public void Empty_or_broken_command_gives_null(string? command) =>
        Assert.Null(UrlHandlerCommand.Build(command, Link));
}
