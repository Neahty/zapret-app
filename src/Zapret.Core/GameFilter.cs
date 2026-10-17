namespace Zapret.Core;

public enum GameFilterMode { Disabled, All, Tcp, Udp }

/// <summary>
/// Игровой фильтр из service.bat: расширяет обход на порты игр.
/// Хранится в utils\game_filter.enabled строками mode=/tcp=/udp=.
/// Когда фильтр выключен, вместо портов подставляется «12» - как в оригинале.
/// </summary>
public sealed record GameFilter(GameFilterMode Mode, string TcpRange, string UdpRange)
{
    public const string DefaultRange = "1024-65535";
    private const string DisabledPort = "12";

    public static GameFilter Disabled { get; } = new(GameFilterMode.Disabled, DefaultRange, DefaultRange);

    public string TcpPorts => Mode is GameFilterMode.All or GameFilterMode.Tcp ? TcpRange : DisabledPort;
    public string UdpPorts => Mode is GameFilterMode.All or GameFilterMode.Udp ? UdpRange : DisabledPort;

    public static GameFilter Load(string path)
    {
        if (!File.Exists(path)) return Disabled;

        var mode = GameFilterMode.Disabled;
        string tcp = DefaultRange, udp = DefaultRange;
        foreach (var raw in File.ReadAllLines(path))
        {
            var parts = raw.Split('=', 2);
            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts.Length > 1 ? parts[1].Trim() : "";
            switch (key)
            {
                case "mode": mode = ParseMode(value); break;
                case "all": mode = GameFilterMode.All; break;
                case "tcp" when value.Length == 0: mode = GameFilterMode.Tcp; break;
                case "udp" when value.Length == 0: mode = GameFilterMode.Udp; break;
                case "tcp" when IsValidRange(value): tcp = Normalize(value); break;
                case "udp" when IsValidRange(value): udp = Normalize(value); break;
            }
        }
        return new GameFilter(mode, tcp, udp);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[]
        {
            $"mode={Mode.ToString().ToLowerInvariant()}",
            $"tcp={TcpRange}",
            $"udp={UdpRange}",
        });
    }

    /// <summary>Порты через запятую: «80», «1024-65535», «1024-1934,1936-65535».</summary>
    public static bool IsValidRange(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        foreach (var item in Normalize(input).Split(','))
        {
            var bounds = item.Split('-');
            if (bounds.Length > 2) return false;
            if (!TryPort(bounds[0], out var start)) return false;
            var end = start;
            if (bounds.Length == 2 && !TryPort(bounds[1], out end)) return false;
            if (start > end) return false;
        }
        return true;
    }

    private static bool TryPort(string s, out int port)
    {
        port = 0;
        if (s.Length is 0 or > 5 || s[0] == '0' || !s.All(char.IsAsciiDigit)) return false;
        port = int.Parse(s);
        return port <= 65535;
    }

    private static string Normalize(string s) => s.Replace(" ", "");

    private static GameFilterMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "all" => GameFilterMode.All,
        "tcp" => GameFilterMode.Tcp,
        "udp" => GameFilterMode.Udp,
        _ => GameFilterMode.Disabled,
    };
}
