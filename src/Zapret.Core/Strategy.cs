using System.Text;
using System.Text.RegularExpressions;

namespace Zapret.Core;

/// <summary>
/// Одна стратегия обхода - это один general*.bat. Внутри него одна команда winws.exe
/// с аргументами, где встречаются %BIN%, %LISTS%, %GameFilterTCP%, %GameFilterUDP%.
/// </summary>
public sealed record Strategy(string Name, IReadOnlyList<string> ArgTemplates)
{
    public const string DefaultName = "general";

    /// <summary>Подставляет реальные пути и порты игрового фильтра.</summary>
    public IReadOnlyList<string> Render(ZapretPaths paths, GameFilter gameFilter) =>
        ArgTemplates.Select(a => a
                .Replace("%BIN%", paths.Bin, StringComparison.OrdinalIgnoreCase)
                .Replace("%LISTS%", paths.Lists, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilterTCP%", gameFilter.TcpPorts, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilterUDP%", gameFilter.UdpPorts, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilter%", gameFilter.TcpPorts, StringComparison.OrdinalIgnoreCase))
            .ToList();
}

public static class StrategyParser
{
    private const string Marker = "winws.exe\"";

    /// <summary>Разбирает содержимое .bat-файла стратегии.</summary>
    /// <exception cref="FormatException">В файле нет запуска winws.exe.</exception>
    public static Strategy Parse(string name, string batContent)
    {
        var joined = JoinContinuations(batContent);
        var line = joined.FirstOrDefault(l => l.Contains(Marker, StringComparison.OrdinalIgnoreCase))
                   ?? throw new FormatException($"В стратегии «{name}» не найден запуск winws.exe");

        var argsPart = line[(line.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) + Marker.Length)..];
        var args = Tokenize(argsPart);
        if (args.Count == 0)
            throw new FormatException($"В стратегии «{name}» у winws.exe нет аргументов");
        return new Strategy(name, args);
    }

    /// <summary>Склеивает строки, оканчивающиеся на ^ (перенос строки в cmd).</summary>
    internal static List<string> JoinContinuations(string content)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.EndsWith('^'))
            {
                current.Append(line, 0, line.Length - 1).Append(' ');
                continue;
            }
            current.Append(line);
            result.Add(current.ToString());
            current.Clear();
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>Делит строку на аргументы по пробелам, учитывая кавычки (сами кавычки убираются).</summary>
    internal static List<string> Tokenize(string text)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var ch in text)
        {
            if (ch == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (hasToken) { args.Add(current.ToString()); current.Clear(); hasToken = false; }
                continue;
            }
            current.Append(ch);
            hasToken = true;
        }
        if (hasToken) args.Add(current.ToString());
        return args.Where(a => a != "^").ToList();
    }
}

public static class StrategyCatalog
{
    /// <summary>Все стратегии (.bat, кроме service*) в папке zapret, в том же порядке, что и в service.bat.</summary>
    public static IReadOnlyList<Strategy> Load(string root) =>
        Directory.EnumerateFiles(root, "*.bat")
            .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
            .Select(TryParse)
            .OfType<Strategy>()
            .OrderBy(s => NaturalKey(s.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>В новой версии zapret может появиться служебный .bat без winws - его просто пропускаем.</summary>
    private static Strategy? TryParse(string file)
    {
        try { return StrategyParser.Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)); }
        catch (FormatException) { return null; }
    }

    /// <summary>Числа дополняются нулями: ALT2 идёт раньше ALT10.</summary>
    internal static string NaturalKey(string name) =>
        Regex.Replace(name, @"\d+", m => m.Value.PadLeft(8, '0'));
}
