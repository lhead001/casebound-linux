using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Casebound.Kit;
using Casebound.Models;

namespace Casebound.Views;

public partial class ConvertView : UserControl
{
    private MainWindow window = null!;
    public ConverterModel Model { get; } = new();

    public sealed record BitrateChoice(int Kbps, string Label) { public override string ToString() => Label; }

    public ConvertView()
    {
        InitializeComponent();
        DataContext = this;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void Attach(MainWindow w)
    {
        window = w;
        var choices = new[]
        {
            new BitrateChoice(64, "64 kbps — spoken word (smallest)"),
            new BitrateChoice(96, "96 kbps — spoken word"),
            new BitrateChoice(128, "128 kbps"),
            new BitrateChoice(160, "160 kbps"),
            new BitrateChoice(192, "192 kbps — music-heavy productions"),
        };
        BitrateBox.ItemsSource = choices;
        BitrateBox.SelectedItem = choices.FirstOrDefault(c => c.Kbps == App.Settings.BitrateKbps) ?? choices[1];
        UpdateSyncedOption();
        if (!Ffmpeg.Available) SyncedHint.Text = "ffmpeg wasn't found, so converting won't work. Install it with your package manager (for example: sudo apt install ffmpeg).";
    }

    public void UpdateSyncedOption()
    {
        var shared = CloudLibrary.SharedLibraryFolder(App.Settings);
        if (shared != null)
        {
            SyncedBox.IsVisible = true;
            SyncedBox.IsChecked = App.Settings.SaveToICloud;
            SyncedHint.Text = $"Books saved here show up on your other devices that use this folder.\n{shared}";
            SyncedChoose.Content = "Change…";
        }
        else
        {
            SyncedBox.IsVisible = false; SyncedBox.IsChecked = false;
            SyncedChoose.Content = "Choose synced folder…";
            SyncedHint.Text = "No synced library folder yet. Choose a folder kept in sync by Nextcloud, Dropbox, Syncthing or rclone to share books and your place in them with other devices. Otherwise you'll be asked where to save.";
        }
    }

    private async void AddFiles_Click(object? s, RoutedEventArgs e)
    {
        var files = await Pickers.OpenFilesAsync(MetadataReader.AudioExtensions);
        if (files.Count > 0) await Model.AddAsync(files);
    }
    private async void AddFolder_Click(object? s, RoutedEventArgs e)
    {
        if (await Pickers.FolderAsync("Add a folder of audio files") is { } f) await Model.AddAsync(new[] { f });
    }

    private void OnDragOver(object? s, DragEventArgs e)
        => e.DragEffects = Model.IsIdle && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;


    private async void OnDrop(object? s, DragEventArgs e)
    {
        e.Handled = true;
        if (!Model.IsIdle) return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is { Count: > 0 }) await Model.AddAsync(paths);
    }

    private static PartItem? PartOf(object? s) => (s as Control)?.Tag as PartItem;
    private void Remove_Click(object? s, RoutedEventArgs e) { if (PartOf(s) is { } p) Model.Remove(p); }
    private void Up_Click(object? s, RoutedEventArgs e) { if (PartOf(s) is { } p) { int i = Model.Parts.IndexOf(p); Model.Move(i, i - 1); } }
    private void Down_Click(object? s, RoutedEventArgs e) { if (PartOf(s) is { } p) { int i = Model.Parts.IndexOf(p); Model.Move(i, i + 1); } }
    private void Sort_Click(object? s, RoutedEventArgs e) => Model.SortNaturally();
    private void Clear_Click(object? s, RoutedEventArgs e) => Model.Clear();

    private async void NameChapters_Click(object? s, RoutedEventArgs e)
    {
        var dialog = new ChapterNamingDialog(Model);
        await dialog.ShowDialog(window);
        if (dialog.Result is { } titles) Model.ApplyTitles(titles);
    }

    private async void ChooseCover_Click(object? s, RoutedEventArgs e)
    {
        if (await Pickers.OpenImageAsync() is { } path) await Model.SetCoverAsync(await File.ReadAllBytesAsync(path));
    }
    private async void RemoveCover_Click(object? s, RoutedEventArgs e) => await Model.SetCoverAsync(null);

    private void Bitrate_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (BitrateBox.SelectedItem is BitrateChoice c && window != null) { App.Settings.BitrateKbps = c.Kbps; App.Settings.Save(); }
    }
    private void Synced_Click(object? s, RoutedEventArgs e) { App.Settings.SaveToICloud = SyncedBox.IsChecked == true; App.Settings.Save(); }

    private async void ChooseSynced_Click(object? s, RoutedEventArgs e)
    {
        if (await Pickers.FolderAsync("Choose your synced library folder") is { } f)
        {
            await window.Library.SetSyncedFolderAsync(f);
            UpdateSyncedOption();
        }
    }

    private async void Convert_Click(object? s, RoutedEventArgs e)
    {
        string? output;
        var shared = CloudLibrary.SharedLibraryFolder(App.Settings);
        if (SyncedBox.IsChecked == true && shared != null) output = UniquePath(Path.Combine(shared, Model.SuggestedFileName + ".m4b"));
        else
        {
            output = await Pickers.SaveM4bAsync(Model.SuggestedFileName);
            if (output == null) return;
        }
        int kbps = (BitrateBox.SelectedItem as BitrateChoice)?.Kbps ?? 96;
        await Model.ConvertAsync(output, kbps);
        if (Model.LastOutput != null) _ = window.ListenView.RefreshLibraryAsync();
    }

    public Task ConvertToAsync(string output) => Model.ConvertAsync(output, (BitrateBox.SelectedItem as BitrateChoice)?.Kbps ?? 96);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++) { var p = Path.Combine(dir, $"{name} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }

    private void Cancel_Click(object? s, RoutedEventArgs e) => Model.Cancel();

    private void Reveal_Click(object? s, RoutedEventArgs e)
    {
        if (Model.LastOutput is { } p)
            try { Process.Start(new ProcessStartInfo("xdg-open", $"\"{Path.GetDirectoryName(p)}\"") { UseShellExecute = false }); } catch { }
    }

    private async void ListenNow_Click(object? s, RoutedEventArgs e)
    {
        if (Model.LastOutput is not { } p) return;
        if (CloudLibrary.Folders(App.Settings).All(f => !p.StartsWith(f.Path.TrimEnd('/') + "/")))
            await window.Library.AddFolderAsync(Path.GetDirectoryName(p)!);
        else await window.Library.RefreshAsync();
        window.ShowTab(0);
        if (window.Library.Books.FirstOrDefault(b => b.Path == p) is { } book) await window.Player.OpenAsync(book);
    }
}
