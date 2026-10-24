using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Casebound.Kit;
using Casebound.Models;

namespace Casebound.Views;

public partial class ListenView : UserControl
{
    private MainWindow window = null!;
    private bool dragging, settingSlider;
    private DateTime lastSyncCheck = DateTime.MinValue;

    public AudiobookPlayer Player => window.Player;
    public LibraryModel Library => window.Library;

    public ListenView() => InitializeComponent();

    public void Attach(MainWindow w)
    {
        window = w;
        DataContext = this;
        SpeedBox.ItemsSource = AudiobookPlayer.Speeds;
        SpeedBox.SelectedItem = AudiobookPlayer.Speeds.MinBy(s => Math.Abs(s.Rate - App.Settings.PlaybackRate));
        SleepBox.ItemsSource = AudiobookPlayer.SleepOptions;
        SleepBox.SelectedIndex = 0;
        BackButton.Content = $"↺ {App.Settings.SkipBackSeconds}";
        ToolTip.SetTip(BackButton, $"Back {App.Settings.SkipBackSeconds} seconds (Ctrl+←)");
        ForwardButton.Content = $"{App.Settings.SkipForwardSeconds} ↻";
        ToolTip.SetTip(ForwardButton, $"Forward {App.Settings.SkipForwardSeconds} seconds (Ctrl+→)");

        Scrubber.AddHandler(PointerPressedEvent, (_, _) => dragging = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Scrubber.AddHandler(PointerReleasedEvent, (_, _) => { dragging = false; Player.Seek(Scrubber.Value); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudiobookPlayer.Position) && !dragging)
            {
                settingSlider = true; Scrubber.Value = Player.Position; settingSlider = false;
                if (DateTime.Now - lastSyncCheck > TimeSpan.FromSeconds(15)) { lastSyncCheck = DateTime.Now; _ = Player.SyncFromOtherDevicesAsync(); }
            }
            if (e.PropertyName == nameof(AudiobookPlayer.IsPlaying) && !Player.IsPlaying) Library.RebuildContinue();
        };
        Player.ChapterChanged += () => { if (Player.CurrentChapter is { } c) ChapterList.ScrollIntoView(c); };
        Library.Refreshed += async () =>
        {
            // After a rescan (which may bring another device's file), apply the decideRemote rule.
            await Player.SyncFromOtherDevicesAsync();
            if (Player.Book == null && App.Settings.LastBookPath is { } last && Library.Books.FirstOrDefault(b => b.Path == last) is { } book)
                await Player.OpenAsync(book, autoplay: false);
        };
        _ = Library.RefreshAsync();
    }

    public Task RefreshLibraryAsync() => Library.RefreshAsync();

    private static BookItem? BookOf(object? sender) => (sender as Control)?.DataContext as BookItem ?? (sender as Control)?.Tag as BookItem;

    private async void Book_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is BookItem b) await Player.OpenAsync(b);
    }
    private void Book_DoubleTapped(object? sender, TappedEventArgs e) { }
    private async void Continue_Click(object? sender, RoutedEventArgs e) { if (BookOf(sender) is { } b) await Player.OpenAsync(b); }
    private async void BookPlay_Click(object? sender, RoutedEventArgs e) { if (BookOf(sender) is { } b) await Player.OpenAsync(b); }

    private void BookReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is not { } b) return;
        try { Process.Start(new ProcessStartInfo("xdg-open", $"\"{Path.GetDirectoryName(b.Path)}\"") { UseShellExecute = false }); } catch { }
    }

    private void BookReset_Click(object? sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is not { } b) return;
        if (Player.Book == b) Player.Seek(0);
        else { App.Positions.SetByKey(PlaybackPositions.KeyFor(b.Path), TimeSpan.Zero); App.Positions.Save(flushSync: true); }
        b.RefreshProgress();
        Library.RebuildContinue();
    }

    private async void BookHide_Click(object? sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is not { } b) return;
        if (Player.Book == b) Player.Close();
        await Library.HideAsync(b);
    }

    private async void BookDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (BookOf(sender) is not { } b) return;
        var shared = b.IsShared ? " It's in your synced library folder, so it will disappear from your other devices too." : "";
        if (!await Dialogs.ConfirmAsync(window, "Move to Trash?", $"\"{b.Title}\" will be moved to the Trash.{shared}", "Move to Trash")) return;
        if (Player.Book == b) Player.Close();
        try { await Library.DeleteAsync(b); }
        catch (Exception ex) { await Dialogs.ConfirmAsync(window, "Couldn't delete", ex.Message, "OK", cancel: null); }
    }

    private async void Unhide_Click(object? sender, RoutedEventArgs e) { if (BookOf(sender) is { } b) await Library.UnhideAsync(b); }
    private async void UnhideAll_Click(object? sender, RoutedEventArgs e) => await Library.UnhideAllAsync();
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await Library.RefreshAsync();

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.FolderAsync("Add a folder of books") is { } f) await Library.AddFolderAsync(f);
    }
    private async void SetSynced_Click(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.FolderAsync("Choose your synced library folder") is { } f) await Library.SetSyncedFolderAsync(f);
    }
    private async void ClearSynced_Click(object? sender, RoutedEventArgs e) => await Library.SetSyncedFolderAsync(null);

    private void AcceptOffer_Click(object? sender, RoutedEventArgs e) => Player.AcceptOffer();
    private void DeclineOffer_Click(object? sender, RoutedEventArgs e) => Player.DeclineOffer();
    private void PlayPause_Click(object? sender, RoutedEventArgs e) => Player.TogglePlay();
    private void Back_Click(object? sender, RoutedEventArgs e) => Player.SkipBack();
    private void Forward_Click(object? sender, RoutedEventArgs e) => Player.SkipForward();
    private void PrevChapter_Click(object? sender, RoutedEventArgs e) => Player.PreviousChapter();
    private void NextChapter_Click(object? sender, RoutedEventArgs e) => Player.NextChapter();

    private void Chapter_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is ChapterItem c) Player.JumpTo(c);
    }
    private void Speed_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is SpeedOption s && window != null && Math.Abs(s.Rate - Player.Rate) > 0.001) Player.SetRate(s.Rate);
    }
    private void Sleep_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SleepBox.SelectedItem is SleepOption s && window != null) Player.SetSleep(s);
    }
}
