using Avalonia.Controls;
using Avalonia.Input;
using Casebound.Models;
using Casebound.Views;

namespace Casebound;

public partial class MainWindow : Window
{
    public AudiobookPlayer Player { get; }
    public LibraryModel Library { get; }
    public Mpris Mpris { get; }
    public ListenView ListenView => Listen;

    public MainWindow()
    {
        Player = new AudiobookPlayer();
        Library = new LibraryModel();
        Mpris = new Mpris(Player);
        InitializeComponent();
        Listen.Attach(this);
        Convert.Attach(this);
        Nav.SelectedIndex = Environment.GetEnvironmentVariable("CASEBOUND_START_TAB") switch { "convert" => 1, "help" => 2, _ => 0 };
        _ = Mpris.StartAsync();
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += async (_, _) => await RunDemoAsync();
    }

    /// <summary>Test/screenshot hook. CASEBOUND_DEMO = "convert:&lt;folder&gt;", "chapters:&lt;folder&gt;", "convertrun:&lt;folder&gt;:&lt;out.m4b&gt;" or "play".</summary>
    private async Task RunDemoAsync()
    {
        var demo = Environment.GetEnvironmentVariable("CASEBOUND_DEMO");
        if (string.IsNullOrEmpty(demo)) return;
        try
        {
            var parts = demo.Split(':', 3);
            switch (parts[0])
            {
                case "convert" or "chapters" or "convertrun":
                    ShowTab(1);
                    await Convert.Model.AddAsync(new[] { parts[1] });
                    if (string.IsNullOrWhiteSpace(Convert.Model.BookTitle)) Convert.Model.BookTitle = "Sample Book";
                    if (parts[0] == "chapters") { var d = new ChapterNamingDialog(Convert.Model); _ = d.ShowDialog(this); }
                    if (parts[0] == "convertrun") { await Convert.ConvertToAsync(parts[2]); Console.WriteLine("DEMO CONVERT: " + Convert.Model.Status); }
                    break;
                case "play":
                    await Library.RefreshAsync();
                    await Task.Delay(1500);
                    var book = Library.Books.FirstOrDefault();
                    if (book != null) await Player.OpenAsync(book);
                    await Task.Delay(3000);
                    Console.WriteLine($"DEMO PLAY: playing={Player.IsPlaying} position={Player.Position:F1}");
                    break;
            }
        }
        catch (Exception ex) { Console.WriteLine("DEMO ERROR: " + ex); }
    }

    public void ShowTab(int i) => Nav.SelectedIndex = i;

    private void Nav_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int i = Nav.SelectedIndex;
        if (Listen == null) return;
        Listen.IsVisible = i == 0; Convert.IsVisible = i == 1; Help.IsVisible = i == 2;
        if (i == 1) Convert.UpdateSyncedOption();
    }

    /// <summary>Space plays/pauses (as on the Mac) unless you're typing; Ctrl+←/→ skip.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        if (e.Key == Key.Space && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Control && Player.HasBook) { Player.TogglePlay(); e.Handled = true; }
        else if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Control) { Player.SkipBack(); e.Handled = true; }
        else if (e.Key == Key.Right && e.KeyModifiers == KeyModifiers.Control) { Player.SkipForward(); e.Handled = true; }
    }

    public void Shutdown()
    {
        Player.Dispose();
        Library.Dispose();
        Mpris.Dispose();
    }
}
