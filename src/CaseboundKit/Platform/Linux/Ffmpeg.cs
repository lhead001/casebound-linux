using System.Diagnostics;

namespace Casebound.Kit;

/// <summary>Locates ffmpeg/ffprobe (CASEBOUND_FFMPEG, then PATH, then the Flatpak /app/bin).</summary>
public static class Ffmpeg
{
    public static string Executable => Find("ffmpeg", Environment.GetEnvironmentVariable("CASEBOUND_FFMPEG"));
    public static string ProbeExecutable => Find("ffprobe", Environment.GetEnvironmentVariable("CASEBOUND_FFPROBE"));

    private static string Find(string name, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Append("/app/bin").Append("/usr/bin"))
        {
            if (dir.Length == 0) continue;
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        throw new InvalidOperationException($"{name} wasn't found. Install ffmpeg (for example: sudo apt install ffmpeg).");
    }

    public static bool Available { get { try { _ = Executable; return true; } catch { return false; } } }

    internal static Process Start(string exe, IEnumerable<string> args, bool redirectIn)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectIn,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {exe}");
        return p;
    }
}
