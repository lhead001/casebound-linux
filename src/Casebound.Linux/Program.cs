using Avalonia;
using Casebound.Kit;

namespace Casebound;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run(Console.Out) ? 0 : 1;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(AppPaths.Root, "crash.log"), $"{DateTime.Now:u} {e.ExceptionObject}\n"); } catch { }
        };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().With(new X11PlatformOptions { WmClass = "io.github.lhead001.Casebound" }).WithInterFont().LogToTrace();
}
