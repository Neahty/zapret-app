using System.Text.Json;

namespace Zapret.Core;

public sealed record ReleaseInfo(string Version, Uri ZipUrl, long Size);

/// <summary>Откуда берутся новые версии zapret. В тестах подменяется.</summary>
public interface IReleaseSource
{
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default);
    Task DownloadAsync(ReleaseInfo release, string destinationFile, CancellationToken ct = default);
}

/// <summary>Релизы Flowseal/zapret-discord-youtube на GitHub — тот же источник, что у service.bat.</summary>
public sealed class GitHubReleaseSource(HttpClient http) : IReleaseSource
{
    public const string Repo = "Flowseal/zapret-discord-youtube";
    private const string LatestApi = $"https://api.github.com/repos/{Repo}/releases/latest";
    /// <summary>Скачиваем только отсюда: файлы из архива потом запускаются от имени SYSTEM.</summary>
    private const string TrustedDownloadPrefix = $"https://github.com/{Repo}/releases/download/";
    private const long MaxZipSize = 100 * 1024 * 1024;

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.ParseAdd("ZapretApp");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Достаёт из ответа GitHub версию и .zip-архив. null — если чего-то не хватает или адрес чужой.</summary>
    internal static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString()?.TrimStart('v') : null;
        if (!ZapretInstaller.IsValidVersion(version) || !root.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!url.StartsWith(TrustedDownloadPrefix, StringComparison.Ordinal)) return null;
            if (size is <= 0 or > MaxZipSize) return null;
            return new ReleaseInfo(version!, new Uri(url), size);
        }
        return null;
    }

    public async Task DownloadAsync(ReleaseInfo release, string destinationFile, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using (var file = File.Create(destinationFile))
            await response.Content.CopyToAsync(file, ct);
        if (new FileInfo(destinationFile).Length != release.Size)
            throw new InvalidDataException("Архив скачался не полностью — попробуйте ещё раз.");
    }
}

/// <summary>
/// Обновление zapret: найти новую версию, скачать, распаковать в отдельную папку рядом
/// и перенести настройки пользователя. Переключение на новую версию делает контроллер.
/// </summary>
public sealed class ZapretUpdater(string installRoot, IReleaseSource source)
{
    /// <summary>Файлы с настройками пользователя, которые переезжают в новую версию.</summary>
    private static readonly string[] UserFiles =
    [
        Path.Combine("lists", "list-general-user.txt"),
        Path.Combine("lists", "list-exclude-user.txt"),
        Path.Combine("lists", "ipset-exclude-user.txt"),
        Path.Combine("utils", "game_filter.enabled"),
    ];

    public string PackageFor(string version) => Path.Combine(installRoot, "packages", $"zapret-{version}.zip");

    /// <summary>Новая версия, если она есть; null — если стоит последняя.</summary>
    public async Task<ReleaseInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var latest = await source.GetLatestAsync(ct);
        return latest is not null && CompareVersions(latest.Version, currentVersion) > 0 ? latest : null;
    }

    public async Task<ZapretPaths> DownloadAndInstallAsync(ReleaseInfo release, ZapretPaths current, CancellationToken ct = default)
    {
        var package = PackageFor(release.Version);
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        var part = package + ".part";
        try
        {
            await source.DownloadAsync(release, part, ct);
            File.Move(part, package, overwrite: true);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }

        var installed = ZapretInstaller.EnsureInstalled(() => File.OpenRead(package), installRoot, release.Version);
        if (StrategyCatalog.Load(installed.Root).Count == 0)
            throw new InvalidDataException($"В версии {release.Version} не нашлось ни одной стратегии — обновление отменено.");

        foreach (var relative in UserFiles)
        {
            var from = Path.Combine(current.Root, relative);
            var to = Path.Combine(installed.Root, relative);
            if (!File.Exists(from)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
        }
        return installed;
    }

    /// <summary>«1.10.10» новее «1.10.9» — сравниваем числами, а не строками.</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.').Select(int.Parse).ToArray();
        var pb = b.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var diff = (i < pa.Length ? pa[i] : 0).CompareTo(i < pb.Length ? pb[i] : 0);
            if (diff != 0) return diff;
        }
        return 0;
    }
}
