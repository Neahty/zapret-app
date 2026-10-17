using System.Text.Json;

namespace Zapret.Core;

public sealed record AppSettings
{
    /// <summary>Обход включён. По умолчанию — да: при первом запуске zapret сразу ставится службой.</summary>
    public bool Enabled { get; init; } = true;
    public string? Strategy { get; init; }
    public DateTimeOffset? EnabledAt { get; init; }
    /// <summary>Запускать приложение (в трее) при входе в Windows.</summary>
    public bool LaunchWithWindows { get; init; } = true;
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            // Битый файл не должен ломать запуск — начинаем с настроек по умолчанию.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
