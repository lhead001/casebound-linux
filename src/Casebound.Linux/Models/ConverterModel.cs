using System.Collections.ObjectModel;
using Casebound.Kit;
using Avalonia.Threading;
using Avalonia.Media.Imaging;

namespace Casebound.Models;

public sealed class PartItem : ObservableObject
{
    private string title;
    private int number;

    public PartInfo Info { get; }
    public string FileName => Info.FileName;
    public string DurationText => Info.Duration > TimeSpan.Zero ? Fmt.Time(Info.Duration) : "—";

    public string Title { get => title; set => Set(ref title, value); }
    public int Number { get => number; set { if (Set(ref number, value)) OnPropertyChanged(nameof(NumberText)); } }
    public string NumberText => Number.ToString();

    public PartItem(PartInfo info, string title)
    {
        Info = info;
        this.title = title;
    }
}

/// <summary>State and actions behind the Convert tab. UI-thread object; the build runs on the thread pool.</summary>
public sealed class ConverterModel : ObservableObject
{
    private string bookTitle = "";
    private string author = "";
    private string narrator = "";
    private byte[]? cover;
    private Bitmap? coverImage;
    private bool isConverting;
    private double progress;
    private string status = "";
    private string? lastOutput;
    private ConversionCancellation? cancel;

    public ObservableCollection<PartItem> Parts { get; } = new();

    public string BookTitle { get => bookTitle; set => Set(ref bookTitle, value); }
    public string Author { get => author; set => Set(ref author, value); }
    public string Narrator { get => narrator; set => Set(ref narrator, value); }
    public Bitmap? CoverImage { get => coverImage; private set => Set(ref coverImage, value); }
    public bool HasCover => cover != null;
    public bool NoCover => cover == null;
    public bool IsConverting
    {
        get => isConverting;
        private set { if (Set(ref isConverting, value)) { OnPropertyChanged(nameof(CanConvert)); OnPropertyChanged(nameof(IsIdle)); } }
    }
    public bool IsIdle => !IsConverting;
    public bool HasParts => Parts.Count > 0;
    public bool NoParts => Parts.Count == 0;
    public void Move(int from, int to) { if (from < 0 || to < 0 || from >= Parts.Count || to >= Parts.Count || from == to) return; Parts.Move(from, to); }
    public bool CanConvert => HasParts && !IsConverting;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string? LastOutput { get => lastOutput; private set { if (Set(ref lastOutput, value)) OnPropertyChanged(nameof(HasLastOutput)); } }
    public bool HasLastOutput => LastOutput != null;
    public string TotalText => Parts.Count == 0 ? "" :
        $"{Parts.Count} file{(Parts.Count == 1 ? "" : "s")} · {Fmt.Long(TimeSpan.FromTicks(Parts.Sum(p => p.Info.Duration.Ticks)))}";

    public ConverterModel()
    {

        Parts.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Parts.Count; i++) Parts[i].Number = i + 1;
            OnPropertyChanged(nameof(HasParts));
            OnPropertyChanged(nameof(CanConvert));
            OnPropertyChanged(nameof(TotalText)); OnPropertyChanged(nameof(NoParts));
        };
    }

    /// <summary>Adds files (or every audio file in dropped folders), reads their tags off the UI thread.</summary>
    public async Task AddAsync(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(MetadataReader.IsAudioFile));
            else if (MetadataReader.IsAudioFile(p)) files.Add(p);
        }
        files = files.Where(f => Parts.All(x => x.Info.Path != f)).ToList();
        if (files.Count == 0) return;

        Status = $"Reading {files.Count} file{(files.Count == 1 ? "" : "s")}…";
        bool wasEmpty = Parts.Count == 0;
        var (ordered, guess) = await Task.Run(() =>
        {
            var infos = files.Select(MetadataReader.ReadPart).ToList();
            var all = Parts.Select(p => p.Info).Concat(infos).ToList();
            var o = MetadataReader.NaturalOrder(all);
            return (o, wasEmpty ? MetadataReader.GuessBook(o) : null);
        });

        if (guess != null)
        {
            BookTitle = guess.Title;
            Author = guess.Author ?? "";
            Narrator = guess.Narrator ?? "";
            await SetCoverAsync(guess.Cover);
        }

        // Keep titles the user already typed; name the rest smartly.
        var existing = Parts.ToDictionary(p => p.Info.Path, p => p.Title, StringComparer.Ordinal);
        var smart = MetadataReader.SmartChapterTitles(ordered, string.IsNullOrWhiteSpace(BookTitle) ? null : BookTitle);
        Parts.Clear();
        for (int i = 0; i < ordered.Count; i++)
            Parts.Add(new PartItem(ordered[i], existing.TryGetValue(ordered[i].Path, out var t) ? t : smart[i]));
        Status = "";
    }

    public void Remove(PartItem item) => Parts.Remove(item);

    public void Clear()
    {
        Parts.Clear();
        BookTitle = Author = Narrator = "";
        _ = SetCoverAsync(null);
        Status = "";
        Progress = 0;
    }

    public void SortNaturally()
    {
        var titles = Parts.ToDictionary(p => p.Info.Path, p => p.Title);
        var ordered = MetadataReader.NaturalOrder(Parts.Select(p => p.Info));
        Parts.Clear();
        foreach (var o in ordered) Parts.Add(new PartItem(o, titles[o.Path]));
    }

    public void ApplyTitles(IReadOnlyList<string> titles)
    {
        for (int i = 0; i < Parts.Count && i < titles.Count; i++) Parts[i].Title = titles[i];
    }

    public List<string> TitlesFromTags() =>
        MetadataReader.SmartChapterTitles(Parts.Select(p => p.Info).ToList(), NullIfEmpty(BookTitle));

    public List<string> TitlesFromFilenames() =>
        MetadataReader.FilenameTitles(Parts.Select(p => p.Info).ToList(), NullIfEmpty(BookTitle));

    public async Task SetCoverAsync(byte[]? bytes)
    {
        cover = bytes;
        CoverImage = await Images.FromBytesAsync(bytes, 400);
        OnPropertyChanged(nameof(HasCover)); OnPropertyChanged(nameof(NoCover));
    }

    public string SuggestedFileName
    {
        get
        {
            var s = string.IsNullOrWhiteSpace(BookTitle) ? "Audiobook" : BookTitle.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.TrimEnd('.');
        }
    }

    public async Task ConvertAsync(string outputPath, int bitrateKbps)
    {
        if (!CanConvert) return;
        IsConverting = true;
        Progress = 0;
        LastOutput = null;
        Status = "Starting…";
        cancel = new ConversionCancellation();

        var parts = Parts.Select(p => new ConversionPart(
            new FileAudioPart(p.Info.Path, p.Info.SampleRate, p.Info.Channels),
            p.Title, p.Info.Duration)).ToList();
        var meta = new BookMetadata
        {
            Title = string.IsNullOrWhiteSpace(BookTitle) ? "Untitled" : BookTitle.Trim(),
            Author = NullIfEmpty(Author),
            Narrator = NullIfEmpty(Narrator),
            Cover = cover,
            Year = Parts.Select(p => p.Info.Year).FirstOrDefault(y => y != null)
        };
        var progressSink = new SyncProgress<ConversionProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Fraction * 100;
            Status = p.Fraction >= 1 ? "Verifying…" : $"Part {p.PartIndex + 1} of {p.PartCount} · {Fmt.Time(p.Encoded)} encoded · {p.PartName}";
        }));
        var token = cancel;
        try
        {
            var result = await Task.Run(() => M4BBuilder.Build(parts, meta, outputPath,
                new ConversionOptions { BitrateKbps = bitrateKbps }, progressSink, token));
            Progress = 100;
            LastOutput = result.OutputPath;
            Status = $"Done — {result.Verified.Chapters.Count} chapters, {Fmt.Time(result.Verified.Duration)}, verified. " +
                     $"{result.Elapsed.TotalSeconds:0}s (~{result.SpeedFactor:0}x realtime).";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            Progress = 0;
        }
        catch (Exception e)
        {
            Status = "Failed: " + e.Message;
            Progress = 0;
        }
        finally
        {
            IsConverting = false;
            cancel = null;
        }
    }

    public void Cancel() => cancel?.Cancel();

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
