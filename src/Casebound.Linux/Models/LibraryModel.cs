using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Casebound.Kit;

namespace Casebound.Models;

public sealed class BookItem : ObservableObject
{
    private string title;
    private string author = "";
    private string detail = "";
    private Bitmap? cover;
    private double progressPercent;
    private bool isPlaying;

    public LibraryEntry Entry { get; private set; }
    public string Path => Entry.Path;
    public AudiobookInfo? Info { get; private set; }

    public string Title { get => title; private set => Set(ref title, value); }
    public string Author { get => author; private set => Set(ref author, value); }
    public string Detail { get => detail; private set => Set(ref detail, value); }
    public Bitmap? Cover { get => cover; private set { if (Set(ref cover, value)) OnPropertyChanged(nameof(HasCover)); } }
    public bool HasCover => Cover != null;
    public bool NoCover => Cover == null;
    public bool IsShared => Entry.Kind == LibraryFolderKind.ICloud;
    public double ProgressPercent { get => progressPercent; set { if (Set(ref progressPercent, value)) OnPropertyChanged(nameof(ProgressText)); } }
    public string ProgressText => ProgressPercent >= 100 ? "Finished" : ProgressPercent > 0 ? $"{ProgressPercent:0}%" : "";
    public bool IsPlaying { get => isPlaying; set => Set(ref isPlaying, value); }

    public BookItem(LibraryEntry entry)
    {
        Entry = entry;
        title = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
    }

    public void Update(LibraryEntry entry)
    {
        bool changed = entry.Size != Entry.Size || entry.ModifiedUtc != Entry.ModifiedUtc;
        Entry = entry;
        if (changed) Info = null;
    }

    public async Task LoadAsync(bool force = false)
    {
        if (Info != null && !force) return;
        var path = Entry.Path;
        var info = await Task.Run(() => { try { return AudiobookInspector.Inspect(path); } catch (Exception) { return null; } });
        if (info == null) { Detail = "Couldn't read this file"; return; }
        Info = info;
        if (!string.IsNullOrWhiteSpace(info.Title)) Title = info.Title!;
        Author = info.Author ?? "";
        Detail = $"{Fmt.Long(info.Duration)} · {info.Chapters.Count} chapter{(info.Chapters.Count == 1 ? "" : "s")}";
        Cover = await Images.FromBytesAsync(info.Cover, 160);
        OnPropertyChanged(nameof(NoCover));
        RefreshProgress();
    }

    public void RefreshProgress()
    {
        var e = App.Positions.Get(Entry.Path);
        if (e == null || Info == null || Info.Duration.TotalSeconds <= 0) { ProgressPercent = 0; return; }
        ProgressPercent = e.Finished ? 100 : Math.Min(100, e.Seconds / Info.Duration.TotalSeconds * 100);
    }
}

/// <summary>The Listen screen's library: every library folder, rescanned when a watcher fires.</summary>
public sealed class LibraryModel : ObservableObject, IDisposable
{
    private IDisposable? watcher;
    private string folderSummary = "";
    private bool isEmpty = true;
    private readonly DispatcherTimer rescanTimer;
    private bool refreshing, refreshAgain;

    public ObservableCollection<BookItem> Books { get; } = new();
    public ObservableCollection<BookItem> ContinueListening { get; } = new();
    /// <summary>Books removed with "Remove from list": the files are untouched (Apple's "Removed from this list (N)").</summary>
    public ObservableCollection<BookItem> Removed { get; } = new();
    public string FolderSummary { get => folderSummary; private set => Set(ref folderSummary, value); }
    public bool IsEmpty { get => isEmpty; private set => Set(ref isEmpty, value); }
    public bool HasContinue => ContinueListening.Count > 0;
    public bool HasRemoved => Removed.Count > 0;
    public string RemovedHeader => $"Removed from this list ({Removed.Count})";
    public List<LibraryFolder> Folders { get; private set; } = new();
    public event Action? Refreshed;

    public LibraryModel()
    {
        // Belt and braces for inotify: sync clients (rclone mounts, some FUSE folders) don't always notify.
        rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        rescanTimer.Tick += async (_, _) => await RefreshAsync();
        rescanTimer.Start();
    }

    public async Task RefreshAsync()
    {
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        try { do { refreshAgain = false; await RefreshCoreAsync(); } while (refreshAgain); }
        finally { refreshing = false; }
    }

    private static void Sync(ObservableCollection<BookItem> target, IEnumerable<LibraryEntry> entries, Dictionary<string, BookItem> pool)
    {
        var list = entries.OrderBy(e => System.IO.Path.GetFileNameWithoutExtension(e.Path), MetadataReader.NaturalComparer.Instance).ToList();
        var items = list.Select(e =>
        {
            if (!pool.TryGetValue(e.Path, out var b)) { b = new BookItem(e); pool[e.Path] = b; }
            else b.Update(e);
            return b;
        }).ToList();
        for (int i = target.Count - 1; i >= 0; i--) if (!items.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < items.Count; i++)
        {
            int at = target.IndexOf(items[i]);
            if (at < 0) target.Insert(i, items[i]); else if (at != i) target.Move(at, i);
        }
    }

    private async Task RefreshCoreAsync()
    {
        var folders = CloudLibrary.Folders(App.Settings);
        var entries = await Task.Run(() => CloudLibrary.Scan(folders));
        Folders = folders;
        var (visible, removed) = LibraryListing.Split(entries, App.Settings.HiddenBooks);
        var pool = Books.Concat(Removed).GroupBy(b => b.Path).ToDictionary(g => g.Key, g => g.First());
        Sync(Books, visible, pool);
        Sync(Removed, removed, pool);
        IsEmpty = Books.Count == 0;
        OnPropertyChanged(nameof(HasRemoved)); OnPropertyChanged(nameof(RemovedHeader));

        var shared = folders.FirstOrDefault(f => f.IsSharedLibrary);
        FolderSummary = shared != null
            ? $"Synced library: {shared.Path}"
            : "No synced library folder yet: your place in each book stays on this computer. Click ⋯ › Choose synced library folder… to pick a Nextcloud, Dropbox, Syncthing or rclone folder.";

        watcher?.Dispose();
        watcher = CloudLibrary.Watch(folders, () => Dispatcher.UIThread.Post(async () => await RefreshAsync()));

        App.Positions.SyncFolder = shared?.Path;
        App.Positions.Save();
        await Task.Run(() => App.Positions.RefreshRemote(force: true));

        foreach (var b in Books.Concat(Removed).ToList()) await b.LoadAsync();
        foreach (var b in Books) b.RefreshProgress();
        RebuildContinue();
        Refreshed?.Invoke();
    }

    public void RebuildContinue()
    {
        var pick = LibraryListing.ContinueListening(Books, b => App.Positions.Get(b.Path));
        ContinueListening.Clear();
        foreach (var b in pick) ContinueListening.Add(b);
        OnPropertyChanged(nameof(HasContinue));
    }

    public async Task AddFolderAsync(string path)
    {
        if (!App.Settings.ExtraLibraryFolders.Contains(path))
        {
            App.Settings.ExtraLibraryFolders.Add(path);
            App.Settings.Save();
        }
        await RefreshAsync();
    }

    /// <summary>Removes the book from the list and remembers that; the file stays where it is.</summary>
    public async Task HideAsync(BookItem book)
    {
        if (!App.Settings.HiddenBooks.Contains(book.Path)) App.Settings.HiddenBooks.Add(book.Path);
        App.Settings.Save();
        await RefreshAsync();
    }

    public async Task UnhideAsync(BookItem book)
    {
        App.Settings.HiddenBooks.Remove(book.Path);
        App.Settings.Save();
        await RefreshAsync();
    }

    public async Task UnhideAllAsync()
    {
        App.Settings.HiddenBooks.Clear();
        App.Settings.Save();
        await RefreshAsync();
    }

    /// <summary>Moves the file to the Trash and drops it from the list.</summary>
    public async Task DeleteAsync(BookItem book)
    {
        var path = book.Path;
        var key = PlaybackPositions.KeyFor(path);
        await Task.Run(() => RecycleBin.Send(path));
        App.Positions.Forget(key);
        App.Positions.Save(flushSync: true);
        await RefreshAsync();
    }

    public async Task SetSyncedFolderAsync(string? folder)
    {
        App.Settings.ICloudFolder = folder;   // field name kept for settings compatibility with Windows
        App.Settings.Save();
        await RefreshAsync();
    }

    public void Dispose()
    {
        rescanTimer.Stop();
        watcher?.Dispose();
    }
}
