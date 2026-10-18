namespace Zapret.Core.Telegram;

/// <summary>
/// Разбор команды обработчика ссылок из реестра (shell\open\command), например
/// <c>"C:\AyuGram\AyuGram.exe"  -- "%1"</c>. Нужен, чтобы запускать клиент Telegram напрямую:
/// ассоциация tg:// у Windows бывает сломана (пустая команда у выбранного ProgId), и тогда
/// ShellExecute вместо Telegram открывает «Документы».
/// </summary>
public static class UrlHandlerCommand
{
    /// <summary>Программа и аргументы для открытия <paramref name="url"/>; null, если команда пустая или битая.</summary>
    public static (string Exe, string Arguments)? Build(string? command, string url)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();

        string exe, rest;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            exe = text[1..end];
            rest = text[(end + 1)..];
        }
        else
        {
            var space = text.IndexOf(' ');
            exe = space < 0 ? text : text[..space];
            rest = space < 0 ? "" : text[space..];
        }
        if (exe.Length == 0) return null;

        rest = rest.Trim();
        var args = rest.Contains("%1") ? rest.Replace("%1", url) : (rest + " \"" + url + "\"").Trim();
        return (Environment.ExpandEnvironmentVariables(exe), args);
    }
}
