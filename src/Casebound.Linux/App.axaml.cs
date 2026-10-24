using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Casebound.Kit;

namespace Casebound;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static PlaybackPositions Positions { get; private set; } = null!;
    public static MainWindow? Window { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = AppSettings.Load();
        Positions = PlaybackPositions.Load(Settings);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Window = new MainWindow();
            desktop.MainWindow = Window;
            desktop.ShutdownRequested += (_, _) => Window.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
