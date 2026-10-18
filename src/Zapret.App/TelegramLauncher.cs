using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Zapret.Core.Telegram;

namespace Zapret.App;

/// <summary>
/// Открывает tg://-ссылку в установленном клиенте Telegram, запуская его exe напрямую.
/// ShellExecute не годится: если у выбранного в Windows ProgId (например AyuGram.tg) пустая команда,
/// Windows открывает вместо клиента папку «Документы».
/// </summary>
internal static class TelegramLauncher
{
    /// <returns>false - рабочий обработчик tg:// не найден.</returns>
    public static bool Open(string url)
    {
        foreach (var command in CandidateCommands())
        {
            if (UrlHandlerCommand.Build(command, url) is not { } target || !File.Exists(target.Exe)) continue;
            Process.Start(new ProcessStartInfo(target.Exe, target.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target.Exe)!,
            });
            return true;
        }
        return false;
    }

    private static IEnumerable<string?> CandidateCommands()
    {
        // Сначала то, что пользователь выбрал в «Приложениях по умолчанию», потом сама схема tg.
        var progId = ReadValue(Registry.CurrentUser,
            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\tg\UserChoice", "ProgId");
        if (!string.IsNullOrEmpty(progId))
        {
            yield return ReadValue(Registry.CurrentUser, $@"Software\Classes\{progId}\shell\open\command", null);
            yield return ReadValue(Registry.LocalMachine, $@"Software\Classes\{progId}\shell\open\command", null);
        }
        yield return ReadValue(Registry.CurrentUser, @"Software\Classes\tg\shell\open\command", null);
        yield return ReadValue(Registry.LocalMachine, @"Software\Classes\tg\shell\open\command", null);
    }

    private static string? ReadValue(RegistryKey root, string path, string? name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
