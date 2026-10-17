using System.ServiceProcess;
using Microsoft.Win32;

namespace Zapret.Core;

public enum ServiceState { NotInstalled, Stopped, StartPending, Running, StopPending }

/// <summary>Управление службами Windows. Реальная реализация — через sc.exe и ServiceController.</summary>
public interface IServiceControl
{
    ServiceState GetState(string name);
    /// <summary>Командная строка установленной службы (ImagePath) или null.</summary>
    string? GetCommandLine(string name);
    Task InstallAsync(string name, string displayName, string description, string commandLine, CancellationToken ct = default);
    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
    void SetTag(string name, string valueName, string value);
}

public sealed class WindowsServiceControl(ICommandRunner runner) : IServiceControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public ServiceState GetState(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.StartPending => ServiceState.StartPending,
                ServiceControllerStatus.StopPending => ServiceState.StopPending,
                _ => ServiceState.Stopped,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
    }

    public string? GetCommandLine(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("ImagePath") as string;
    }

    public async Task InstallAsync(string name, string displayName, string description, string commandLine, CancellationToken ct = default)
    {
        // Уже установленную службу перенастраиваем: удалить и сразу создать заново Windows
        // часто не даёт («служба помечена для удаления»).
        var verb = GetState(name) == ServiceState.NotInstalled ? "create" : "config";
        var create = await runner.RunAsync("sc.exe",
            [verb, name, "binPath=", commandLine, "DisplayName=", displayName, "start=", "auto"], ct);
        if (!create.Success)
            throw new InvalidOperationException($"Не удалось установить службу {name}: {create.Output.Trim()}");

        await runner.RunAsync("sc.exe", ["description", name, description], ct);
        // Если winws упадёт — Windows сама перезапустит его через 5 секунд.
        await runner.RunAsync("sc.exe", ["failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"], ct);
    }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        using var sc = new ServiceController(name);
        // Сразу после входа в Windows служба может ещё стартовать сама — тогда просто ждём.
        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
        try
        {
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, Timeout), ct);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new InvalidOperationException(
                "Служба zapret не запустилась за 15 секунд. Проверьте, не запущен ли другой обход или VPN.");
        }
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        if (GetState(name) is ServiceState.NotInstalled or ServiceState.Stopped) return;
        using var sc = new ServiceController(name);
        if (sc.CanStop) sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout), ct);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        if (GetState(name) == ServiceState.NotInstalled) return;
        var result = await runner.RunAsync("sc.exe", ["delete", name], ct);
        if (!result.Success && GetState(name) != ServiceState.NotInstalled)
            throw new InvalidOperationException($"Не удалось удалить службу {name}: {result.Output.Trim()}");
        // Удаление завершается, когда закроются все дескрипторы службы — ждём до 10 секунд.
        for (var i = 0; i < 20 && GetState(name) != ServiceState.NotInstalled; i++)
            await Task.Delay(500, ct);
    }

    public void SetTag(string name, string valueName, string value)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
        key?.SetValue(valueName, value, RegistryValueKind.String);
    }
}
