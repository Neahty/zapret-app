namespace Zapret.Core;

/// <summary>
/// Режим IPSet из service.bat:
/// None - в ipset-all.txt заглушка (обход только по спискам доменов);
/// Loaded - загружен полный список подсетей;
/// Any - файл пустой, обходится любой IP.
/// </summary>
public enum IpsetMode { None, Loaded, Any }

public sealed class IpsetManager(string listsDir)
{
    public const string Placeholder = "203.0.113.113/32";

    private string ListFile => Path.Combine(listsDir, "ipset-all.txt");
    private string BackupFile => ListFile + ".backup";

    public bool HasBackup => File.Exists(BackupFile);

    public IpsetMode GetMode()
    {
        if (!File.Exists(ListFile)) return IpsetMode.Any;
        var text = File.ReadAllText(ListFile);
        if (string.IsNullOrWhiteSpace(text)) return IpsetMode.Any;
        return text.Contains(Placeholder, StringComparison.Ordinal) ? IpsetMode.None : IpsetMode.Loaded;
    }

    /// <exception cref="InvalidOperationException">Нужен полный список, а сохранённой копии нет.</exception>
    public void SetMode(IpsetMode target)
    {
        var current = GetMode();
        if (current == target) return;

        if (target == IpsetMode.Loaded)
        {
            if (!HasBackup)
                throw new InvalidOperationException("Полного списка IP нет - его нужно сначала скачать в service.bat");
            File.Move(BackupFile, ListFile, overwrite: true);
            return;
        }

        if (current == IpsetMode.Loaded)
            File.Move(ListFile, BackupFile, overwrite: true);

        File.WriteAllText(ListFile, target == IpsetMode.None ? Placeholder + Environment.NewLine : "");
    }
}

/// <summary>Пользовательские списки, которые стратегии ожидают найти (как load_user_lists в service.bat).</summary>
public static class UserLists
{
    public static void Ensure(string listsDir)
    {
        Directory.CreateDirectory(listsDir);
        CreateIfMissing(Path.Combine(listsDir, "ipset-exclude-user.txt"), IpsetManager.Placeholder + "\r\n");
        CreateIfMissing(Path.Combine(listsDir, "list-general-user.txt"), "# Never leave this file empty\r\ndomain.example.abc\r\n");
        CreateIfMissing(Path.Combine(listsDir, "list-exclude-user.txt"), "domain.example.abc\r\n");
    }

    private static void CreateIfMissing(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }
}

/// <summary>
/// Сайты, которых нет в списках zapret-discord-youtube, но обход для них нужен: SoundCloud и сайт Telegram.
/// Дописываются в list-general.txt - его читают все стратегии. Свой список пользователя
/// (list-general-user.txt) не трогаем, а list-general.txt при новой версии zapret приходит из архива
/// заново - поэтому проверяем при каждом включении.
/// </summary>
public static class ExtraDomains
{
    // Только ASCII: файл читает winws, а не .NET.
    public const string Marker = "# Zapret app: SoundCloud, Telegram";

    public static IReadOnlyList<string> All { get; } =
    [
        // SoundCloud: сайт, API, картинки и аудио (sndcdn.com), новый CDN потоков и короткие ссылки.
        "soundcloud.com", "sndcdn.com", "soundcloud.cloud", "snd.sc",
        // Telegram: сайт, веб-версия, ссылки t.me и CDN. Сам клиент ходит через прокси Telegram.
        "telegram.org", "t.me", "telegram.me", "telesco.pe", "cdn-telegram.org",
    ];

    /// <returns>true - список дополнен, winws нужно перезапустить, чтобы он его перечитал.</returns>
    public static bool Ensure(string listsDir)
    {
        var file = Path.Combine(listsDir, "list-general.txt");
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var present = text.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = All.Where(d => !present.Contains(d)).ToList();
        if (missing.Count == 0) return false;

        Directory.CreateDirectory(listsDir);
        var lines = new List<string>();
        // В исходном файле последней строке может не хватать перевода строки - иначе домены склеятся.
        if (text.Length > 0 && !text.EndsWith('\n')) lines.Add("");
        if (!present.Contains(Marker)) lines.Add(Marker);
        lines.AddRange(missing);
        File.AppendAllText(file, string.Join("\n", lines) + "\n");
        return true;
    }
}
