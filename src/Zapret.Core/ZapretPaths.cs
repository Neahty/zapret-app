namespace Zapret.Core;

/// <summary>Где лежат файлы zapret: корень с .bat-стратегиями, bin, lists, utils.</summary>
public sealed record ZapretPaths(string Root)
{
    public string Bin => Path.Combine(Root, "bin") + Path.DirectorySeparatorChar;
    public string Lists => Path.Combine(Root, "lists") + Path.DirectorySeparatorChar;
    public string Utils => Path.Combine(Root, "utils") + Path.DirectorySeparatorChar;
    public string WinwsExe => Path.Combine(Root, "bin", "winws.exe");
    public string GameFilterFile => Path.Combine(Root, "utils", "game_filter.enabled");
    public string TargetsFile => Path.Combine(Root, "utils", "targets.txt");
}
