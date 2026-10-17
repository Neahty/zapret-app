using System.IO.Compression;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Zapret.Core;

/// <summary>
/// Запуск приложения при входе в Windows через Планировщик задач - с правами администратора,
/// но без окна UAC. Задача не останавливается на батарее и не ограничена по времени.
/// </summary>
public interface IAppAutostart
{
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
    Task SetAsync(bool enabled, CancellationToken ct = default);
}

/// <param name="exePath">Exe в папке, куда обычный пользователь писать не может (иначе подменят и получат админа).</param>
/// <param name="xmlDir">Папка только для админов: XML задачи нельзя класть в общий %TEMP%, его успеют подменить.</param>
public sealed class AppAutostart(ICommandRunner runner, string exePath, string userId, string xmlDir) : IAppAutostart
{
    public const string TaskName = "ZapretApp";

    /// <summary>Задача есть и запускает именно наш exe (а не чужую задачу с таким же именем).</summary>
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync("schtasks", ["/Query", "/TN", TaskName, "/XML"], ct);
        return result.Success && result.Output.Contains(SecurityElement.Escape(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetAsync(bool enabled, CancellationToken ct = default)
    {
        if (!enabled)
        {
            if ((await runner.RunAsync("schtasks", ["/Query", "/TN", TaskName], ct)).Success)
                await runner.RunAsync("schtasks", ["/Delete", "/TN", TaskName, "/F"], ct);
            return;
        }

        Directory.CreateDirectory(xmlDir);
        var xmlPath = Path.Combine(xmlDir, $"task-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(xmlPath, BuildTaskXml(exePath, userId), System.Text.Encoding.Unicode, ct);
        try
        {
            var result = await runner.RunAsync("schtasks", ["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"], ct);
            if (!result.Success)
                throw new InvalidOperationException("Не удалось добавить в автозапуск: " + result.Output.Trim());
        }
        finally { File.Delete(xmlPath); }
    }

    internal static string BuildTaskXml(string exePath, string userId) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Запрет: значок в трее при входе в Windows</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(userId)}</UserId></LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exePath)}</Command>
              <Arguments>--tray</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
}

/// <summary>
/// Копия приложения в Program Files для автозапуска: задача запускает exe с правами администратора без UAC,
/// поэтому он не должен лежать там, где его может подменить обычный пользователь (Рабочий стол, OneDrive).
/// </summary>
public static class AppDeployment
{
    public static string EnsureDeployed(string sourceDir, string targetDir, string exeName)
    {
        var src = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar);
        var dst = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return Path.Combine(dst, exeName);

        foreach (var file in AppFiles(src, exeName))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, file));
            var from = new FileInfo(file);
            var existing = new FileInfo(to);
            if (existing.Exists && existing.Length == from.Length && existing.LastWriteTimeUtc == from.LastWriteTimeUtc) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
        return Path.Combine(dst, exeName);
    }

    private static readonly string[] AppExtensions = [".dll", ".json", ".pdb"];

    /// <summary>
    /// Только файлы самого приложения: человек может запустить exe прямо из «Загрузок»,
    /// и тогда копировать всю папку в Program Files было бы катастрофой.
    /// </summary>
    internal static IEnumerable<string> AppFiles(string dir, string exeName)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, exeName, StringComparison.OrdinalIgnoreCase) ||
                AppExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                yield return file;
        }
        // Платформенные сборки .NET-пакетов (например, ServiceController) лежат в runtimes.
        var runtimes = Path.Combine(dir, "runtimes");
        if (Directory.Exists(runtimes))
            foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                yield return file;
    }
}

/// <summary>
/// Не удалось распаковать winws.exe / WinDivert. Приложение к этому моменту уже работает с правами
/// администратора, поэтому самая частая оставшаяся причина - антивирус.
/// </summary>
public sealed class AntivirusBlockedException(string folder, Exception? inner) : Exception(
    "Не удалось распаковать файлы zapret (winws.exe / WinDivert). Чаще всего их блокирует антивирус: " +
    "так он реагирует на любые программы обхода блокировок.\n\nЕсли у вас Kaspersky или другой антивирус - " +
    $"добавьте папку {folder} в его исключения и запустите приложение снова." +
    (inner is null ? "" : $"\n\nТехническая причина: {inner.Message}"), inner)
{
    public string Folder { get; } = folder;
}

/// <summary>Распаковывает zapret из встроенного архива в папку без кириллицы и OneDrive.</summary>
public static class ZapretInstaller
{
    public const string Version = "1.10.3";

    /// <summary>
    /// Исполняемые файлы, которые запускаются от SYSTEM. Их сверяем с архивом по хешу:
    /// подмена любого из них - это права системы для того, кто подменил.
    /// </summary>
    internal static readonly string[] CriticalFiles = ["winws.exe", "WinDivert64.sys", "WinDivert.dll", "cygwin1.dll"];

    /// <summary>Версия как в релизах zapret: «1.10.3». Ничего другого в имя папки не попадает.</summary>
    public static bool IsValidVersion(string? version) =>
        version is { Length: > 0 and <= 32 } && System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+(\.\d+){0,3}$");

    public static string FolderFor(string installRoot, string version) =>
        IsValidVersion(version)
            ? Path.Combine(installRoot, $"zapret-{version}")
            : throw new ArgumentException($"Некорректная версия zapret: «{version}»", nameof(version));

    /// <summary>Удаляет папки и архивы других версий zapret (после перехода на новую). Занятые - пропускает.</summary>
    public static void RemoveOtherVersions(string installRoot, string currentVersion)
    {
        if (!Directory.Exists(installRoot)) return;
        var keepDir = Path.GetFileName(FolderFor(installRoot, currentVersion));
        foreach (var dir in Directory.EnumerateDirectories(installRoot, "zapret-*"))
            if (!string.Equals(Path.GetFileName(dir), keepDir, StringComparison.OrdinalIgnoreCase))
                TryDelete(dir);

        var packages = Path.Combine(installRoot, "packages");
        if (!Directory.Exists(packages)) return;
        foreach (var zip in Directory.EnumerateFiles(packages))
            if (!string.Equals(Path.GetFileName(zip), $"zapret-{currentVersion}.zip", StringComparison.OrdinalIgnoreCase))
                try { File.Delete(zip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static ZapretPaths EnsureInstalled(Func<Stream> openZip, string installRoot, string version = Version)
    {
        var target = FolderFor(installRoot, version);
        var paths = new ZapretPaths(target);
        if (IsComplete(paths) && MatchesArchive(paths, openZip)) return paths;

        Directory.CreateDirectory(installRoot);
        var tmp = Path.Combine(installRoot, $".extract-{Guid.NewGuid():N}");
        try
        {
            using (var zip = openZip())
                ZipFile.ExtractToDirectory(zip, tmp);

            var source = FindZapretRoot(tmp)
                         ?? throw new InvalidDataException("В архиве нет bin\\winws.exe");
            if (Directory.Exists(target)) MergeInto(source, target);
            else Directory.Move(source, target);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new AntivirusBlockedException(installRoot, e);
        }
        finally
        {
            // Уборка не должна прятать настоящую ошибку: если папку держит антивирус - просто оставляем её.
            TryDelete(tmp);
        }

        // Антивирус может удалить файл уже после распаковки, без всякой ошибки.
        if (!IsComplete(paths)) throw new AntivirusBlockedException(installRoot, null);
        CarryOverUserFiles(installRoot, paths);
        return paths;
    }

    /// <summary>Настройки пользователя, которые переезжают в новую версию zapret.</summary>
    internal static readonly string[] UserFiles =
    [
        Path.Combine("lists", "list-general-user.txt"),
        Path.Combine("lists", "list-exclude-user.txt"),
        Path.Combine("lists", "ipset-exclude-user.txt"),
        Path.Combine("utils", "game_filter.enabled"),
    ];

    /// <summary>
    /// Когда в приложение вшита новая версия zapret, переносим свои списки сайтов и игровой фильтр
    /// из самой свежей из прежних версий - иначе они пропали бы вместе со старой папкой.
    /// Уже существующие файлы новой версии не трогаем.
    /// </summary>
    private static void CarryOverUserFiles(string installRoot, ZapretPaths target)
    {
        var previous = Directory.EnumerateDirectories(installRoot, "zapret-*")
            .Where(d => !string.Equals(Path.GetFullPath(d), Path.GetFullPath(target.Root), StringComparison.OrdinalIgnoreCase))
            .Select(d => (Dir: d, Version: Path.GetFileName(d)["zapret-".Length..]))
            .Where(x => IsValidVersion(x.Version))
            .OrderByDescending(x => x.Version.Split('.').Select(int.Parse).ToArray(), VersionComparer.Instance)
            .Select(x => x.Dir)
            .FirstOrDefault();
        if (previous is null) return;

        foreach (var relative in UserFiles)
        {
            var from = Path.Combine(previous, relative);
            var to = Path.Combine(target.Root, relative);
            if (!File.Exists(from) || File.Exists(to)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
        }
    }

    private sealed class VersionComparer : IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(int[]? a, int[]? b)
        {
            a ??= []; b ??= [];
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var diff = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
                if (diff != 0) return diff;
            }
            return 0;
        }
    }

    /// <summary>
    /// Обновляет установленную папку файлами из архива, не удаляя её целиком: загруженный драйвер
    /// WinDivert64.sys удалить нельзя, а совпадающий файл и не нужно. Пользовательские списки
    /// (*-user.txt) остаются. Лишние файлы в bin удаляются - рядом с winws.exe не должно быть чужих DLL.
    /// </summary>
    internal static void MergeInto(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(dest) && SameContent(file, dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        var sourceBin = Path.Combine(source, "bin");
        var targetBin = Path.Combine(target, "bin");
        foreach (var extra in Directory.EnumerateFileSystemEntries(targetBin))
        {
            if (File.Exists(Path.Combine(sourceBin, Path.GetFileName(extra))) ||
                Directory.Exists(Path.Combine(sourceBin, Path.GetFileName(extra)))) continue;
            if (Directory.Exists(extra)) Directory.Delete(extra, recursive: true);
            else File.Delete(extra);
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    private static bool IsComplete(ZapretPaths paths) =>
        CriticalFiles.All(f => File.Exists(Path.Combine(paths.Bin, f)));

    /// <summary>Совпадают ли установленные исполняемые файлы с файлами из архива.</summary>
    internal static bool MatchesArchive(ZapretPaths paths, Func<Stream> openZip)
    {
        using var zip = new ZipArchive(openZip(), ZipArchiveMode.Read);
        foreach (var name in CriticalFiles)
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("bin/" + name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;

            using var fromZip = entry.Open();
            // Загруженный драйвер открыт системой - читаем с разрешением совместного доступа.
            using var installed = new FileStream(Path.Combine(paths.Bin, name), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!SHA256.HashData(fromZip).AsSpan().SequenceEqual(SHA256.HashData(installed))) return false;
        }
        return true;
    }

    private static void TryDelete(string dir)
    {
        for (var attempt = 0; attempt < 3 && Directory.Exists(dir); attempt++)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }

    internal static string? FindZapretRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "bin", "winws.exe"))) return dir;
        var subdirs = Directory.GetDirectories(dir);
        return subdirs.Length == 1 ? FindZapretRoot(subdirs[0]) : null;
    }

    /// <summary>
    /// Готовит папку, из которой служба zapret запускается от SYSTEM: писать туда могут только
    /// SYSTEM и администраторы. Если папку заранее создал кто-то другой (или это ссылка
    /// на другую папку) - она удаляется и создаётся заново: её владелец мог подложить свои файлы
    /// и в любой момент вернуть себе права.
    /// </summary>
    /// <param name="releaseFiles">
    /// Остановить всё, что держит файлы папки открытыми (службу zapret и winws.exe), - иначе
    /// Windows не даст удалить, например, используемый cygwin1.dll.
    /// </param>
    public static void PrepareSecureRoot(string installRoot, Action? releaseFiles = null)
    {
        var dir = new DirectoryInfo(installRoot);
        var security = AdminOnlySecurity();
        if (dir.Exists && !IsOwnedByAdmins(dir))
        {
            if (!RemoveUntrusted(dir, releaseFiles))
            {
                // Файл занят (например, загруженный драйвер WinDivert64.sys) - тогда забираем папку
                // на месте: владелец - администраторы, лишние права у всего содержимого сбрасываются.
                // Подменённые исполняемые файлы потом поймает проверка по хешу в EnsureInstalled.
                TakeOverInPlace(dir, security);
                return;
            }
            dir.Refresh();
        }

        if (dir.Exists) dir.SetAccessControl(security);
        else dir.Create(security);
    }

    /// <summary>Удаляет недоверенную папку. false - если что-то в ней так и осталось занятым.</summary>
    internal static bool RemoveUntrusted(DirectoryInfo dir, Action? releaseFiles, int attempts = 12)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete(); // удаляем саму ссылку, не папку, на которую она указывает
            return true;
        }

        releaseFiles?.Invoke();
        // Процесс завершается и отпускает файлы не мгновенно - пробуем несколько раз.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                dir.Delete(recursive: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                dir.Refresh();
                if (!dir.Exists) return true;
                if (attempt >= attempts) return false;
                Thread.Sleep(250);
            }
        }
    }

    private static void TakeOverInPlace(DirectoryInfo root, DirectorySecurity rootSecurity)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        root.SetAccessControl(rootSecurity);

        foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete(); // ссылки внутри могли вести куда угодно - убираем сами ссылки
                continue;
            }
            // Только наследуемые права от корня: явные разрешения, выданные кем-то ещё, исчезают.
            if (entry is DirectoryInfo d)
            {
                var s = new DirectorySecurity();
                s.SetOwner(admins);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                d.SetAccessControl(s);
            }
            else if (entry is FileInfo f)
            {
                var s = new FileSecurity();
                s.SetOwner(admins);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                f.SetAccessControl(s);
            }
        }
    }

    internal static bool IsOwnedByAdmins(DirectoryInfo dir)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var owner = dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null &&
               (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
    }

    private static DirectorySecurity AdminOnlySecurity()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins,
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
