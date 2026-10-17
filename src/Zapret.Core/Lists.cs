namespace Zapret.Core;

/// <summary>
/// Режим IPSet из service.bat:
/// None — в ipset-all.txt заглушка (обход только по спискам доменов);
/// Loaded — загружен полный список подсетей;
/// Any — файл пустой, обходится любой IP.
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
                throw new InvalidOperationException("Полного списка IP нет — его нужно сначала скачать в service.bat");
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
