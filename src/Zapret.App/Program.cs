using System.IO;
using System.ServiceProcess;
using Zapret.Core.Telegram;

namespace Zapret.App;

/// <summary>
/// Один exe - две роли: обычное окно или служба «zapret-telegram» (ключ --telegram-proxy),
/// которую Windows запускает сама. Так пользователю не нужно скачивать ничего, кроме Zapret.exe.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var telegram = TelegramServiceArgs.Parse(args);
        if (telegram is not null)
        {
            ServiceBase.Run(new TelegramProxyService(telegram));
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

/// <summary>Служба Windows с прокси Telegram. Журнал - в ProgramData\ZapretApp\logs\telegram.log.</summary>
public sealed class TelegramProxyService : ServiceBase
{
    private const long MaxLogBytes = 1024 * 1024;
    // Не через App.InstallRoot: службе незачем загружать окно и его ресурсы.
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretApp", "logs", "telegram.log");
    private readonly TelegramServiceArgs _args;
    private readonly object _logLock = new();
    private TelegramProxyServer? _server;

    public TelegramProxyService(TelegramServiceArgs args)
    {
        _args = args;
        ServiceName = TelegramController.ServiceName;
        CanStop = true;
        AutoLog = false; // журнал событий Windows не нужен - пишем в свой файл
    }

    protected override void OnStart(string[] startArgs)
    {
        _server = new TelegramProxyServer(_args.Port, _args.Secret, new TelegramUpstream(Log), Log);
        try
        {
            _server.Start();
        }
        catch (Exception e)
        {
            // Порт занят: служба останавливается, приложение покажет «недоступен».
            Log($"Не удалось запустить прокси: {e.Message}");
            throw;
        }
    }

    protected override void OnStop()
    {
        _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        Log("Прокси Telegram остановлен");
    }

    private void Log(string message)
    {
        lock (_logLock)
        {
            try
            {
                var file = new FileInfo(LogFile);
                // Журнал не растёт бесконечно: при переполнении начинаем заново.
                if (file.Exists && file.Length > MaxLogBytes) file.Delete();
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
