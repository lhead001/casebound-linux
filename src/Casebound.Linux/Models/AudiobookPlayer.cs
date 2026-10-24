using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Casebound.Kit;
using LibVLCSharp.Shared;

namespace Casebound.Models;

public sealed class ChapterItem : ObservableObject
{
    private bool isCurrent;
    public Chapter Chapter { get; }
    public string Title => Chapter.Title;
    public string NumberText => (Chapter.Index + 1).ToString();
    public string DurationText => Fmt.Time(Chapter.Duration);
    public bool IsCurrent { get => isCurrent; set { if (Set(ref isCurrent, value)) OnPropertyChanged(nameof(Marker)); } }
    public string Marker => IsCurrent ? "▶" : NumberText;
    public ChapterItem(Chapter c) => Chapter = c;
}

public sealed record SleepOption(string Label, int Minutes) { public override string ToString() => Label; }
public sealed record SpeedOption(string Label, double Rate) { public override string ToString() => Label; }

/// <summary>
/// libVLC wrapped for audiobooks, a port of the Windows AudiobookPlayer: chapters (read by
/// AudiobookInspector), speed with pitch correction (VLC's scaletempo), skip intervals, sleep
/// timer, saved positions with the same baseline/opening/ended rules, plus the Apple
/// "Continue from…" guard (RemotePositionPolicy) for newer positions from other devices.
/// </summary>
public sealed class AudiobookPlayer : ObservableObject, IDisposable
{
    private static LibVLC? vlc;
    private readonly MediaPlayer player;
    private readonly DispatcherTimer tick;
    private Media? media;
    private bool started;                 // media handed to VLC and playing/paused (not merely loaded)
    private BookItem? book;
    private double position, duration;
    private bool isPlaying, isLoading;
    private int chapterIndex = -1;
    private string sleepText = "";
    private DateTime? sleepAt;
    private bool sleepAtChapterEnd;
    private int sleepChapter = -1;
    private DateTime lastSave = DateTime.MinValue;
    private DateTime lastLocalChangeUtc = DateTime.MinValue;
    private string? bookKey;
    private bool opening, ended;
    private PlaybackPositions.Entry? baseline;
    private string error = "";
    private double rate = 1.0;
    private PlaybackPositions.Entry? offer;   // pending "Continue from…"
    private DateTime lastOfferIgnored = DateTime.MinValue;

    public ObservableCollection<ChapterItem> Chapters { get; } = new();
    public BookItem? Book { get => book; private set { if (Set(ref book, value)) { OnPropertyChanged(nameof(HasBook)); OnPropertyChanged(nameof(NoBook)); OnPropertyChanged(nameof(Cover)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Author)); } } }
    public bool HasBook => Book != null;
    public bool NoBook => Book == null;
    public Bitmap? Cover => Book?.Cover;
    public string Title => Book?.Title ?? "";
    public string Author => Book?.Author ?? "";
    public double Position { get => position; private set { if (Set(ref position, value)) { OnPropertyChanged(nameof(PositionText)); OnPropertyChanged(nameof(RemainingText)); } } }
    public double Duration { get => duration; private set { if (Set(ref duration, value)) OnPropertyChanged(nameof(DurationText)); } }
    public string PositionText => Fmt.Time(Position);
    public string DurationText => Fmt.Time(Duration);
    public string RemainingText => "−" + Fmt.Time(Math.Max(0, (Duration - Position) / Math.Max(0.25, Rate)));
    public bool IsPlaying { get => isPlaying; private set { if (Set(ref isPlaying, value)) { OnPropertyChanged(nameof(PlayGlyph)); StateChanged?.Invoke(); } } }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public string PlayGlyph => IsPlaying ? "⏸" : "▶";
    public string Error { get => error; private set => Set(ref error, value); }
    public double Rate => rate;
    public string SleepText { get => sleepText; private set => Set(ref sleepText, value); }
    public ChapterItem? CurrentChapter => chapterIndex >= 0 && chapterIndex < Chapters.Count ? Chapters[chapterIndex] : null;
    public string ChapterTitle => CurrentChapter?.Title ?? "";
    public string ChapterProgressText
    {
        get
        {
            var c = CurrentChapter;
            if (c == null) return "";
            var left = c.Chapter.End.TotalSeconds - Position;
            return $"Chapter {c.Chapter.Index + 1} of {Chapters.Count} · {Fmt.Time(Math.Max(0, left) / Math.Max(0.25, Rate))} left";
        }
    }
    public bool HasOffer => offer != null;
    public string OfferText => offer == null ? "" : $"Continue from {Fmt.Time(offer.Seconds)}? Another device saved a newer position.";

    public static IReadOnlyList<SpeedOption> Speeds { get; } = new[]
    {
        new SpeedOption("0.75×", 0.75), new SpeedOption("1×", 1.0), new SpeedOption("1.1×", 1.1),
        new SpeedOption("1.25×", 1.25), new SpeedOption("1.5×", 1.5), new SpeedOption("1.75×", 1.75),
        new SpeedOption("2×", 2.0), new SpeedOption("2.5×", 2.5), new SpeedOption("3×", 3.0)
    };

    public static IReadOnlyList<SleepOption> SleepOptions { get; } = new[]
    {
        new SleepOption("Sleep timer off", 0), new SleepOption("5 minutes", 5), new SleepOption("15 minutes", 15),
        new SleepOption("30 minutes", 30), new SleepOption("45 minutes", 45), new SleepOption("1 hour", 60),
        new SleepOption("End of chapter", -1)
    };

    public event Action? ChapterChanged;
    public event Action? StateChanged;      // MPRIS
    public event Action? Seeked;            // MPRIS

    public static LibVLC Vlc
    {
        get
        {
            if (vlc != null) return vlc;
            Core.Initialize();
            var extra = (Environment.GetEnvironmentVariable("CASEBOUND_VLC_ARGS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            vlc = new LibVLC(new[] { "--no-video", "--quiet", "--audio-time-stretch" }.Concat(extra).ToArray());
            return vlc;
        }
    }

    public AudiobookPlayer()
    {
        rate = App.Settings.PlaybackRate > 0 ? App.Settings.PlaybackRate : 1.0;
        player = new MediaPlayer(Vlc);
        player.Playing += (_, _) => Dispatcher.UIThread.Post(() => { IsLoading = false; IsPlaying = true; player.SetRate((float)rate); });
        player.Paused += (_, _) => Dispatcher.UIThread.Post(() => { bool was = IsPlaying; IsPlaying = false; if (was) SavePosition(flush: true); });
        player.Stopped += (_, _) => Dispatcher.UIThread.Post(() => IsPlaying = false);
        player.LengthChanged += (_, e) => Dispatcher.UIThread.Post(() => { if (e.Length > 0) Duration = e.Length / 1000.0; });
        player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => { IsLoading = false; Error = "Couldn't play this file."; });
        player.EndReached += (_, _) => Dispatcher.UIThread.Post(OnEnded);
        tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        tick.Tick += (_, _) => OnTick();
        tick.Start();
    }

    private void OnEnded()
    {
        IsPlaying = false;
        started = false;          // VLC is stopped at the end; a Play restarts from Position
        if (Book != null && bookKey != null)
        {
            Position = Duration;
            if (App.Positions.SetByKeyIfChanged(bookKey, baseline, TimeSpan.FromSeconds(Duration), finished: true, title: Book.Title) is { } recorded)
            { baseline = recorded; lastLocalChangeUtc = DateTime.UtcNow; }
            App.Positions.Save(flushSync: true);
            ended = true;
            Book.RefreshProgress();
        }
    }

    public async Task OpenAsync(BookItem item, bool autoplay = true)
    {
        if (Book == item) { if (autoplay && !IsPlaying) Play(); return; }
        SavePosition(flush: true);
        opening = true;
        StopVlc();
        if (Book != null) Book.IsPlaying = false;
        Error = "";
        IsLoading = true;
        try { await OpenCoreAsync(item, autoplay); }
        finally { opening = false; IsLoading = IsLoading && autoplay; }
    }

    private async Task OpenCoreAsync(BookItem item, bool autoplay)
    {
        await item.LoadAsync(force: true);
        if (item.Info == null) { IsLoading = false; Error = "Couldn't read this book's chapters."; return; }

        Book = item;
        bookKey = PlaybackPositions.KeyFor(item.Path);
        ended = false; baseline = null; ClearOffer();
        Position = 0;
        item.IsPlaying = true;
        Chapters.Clear();
        foreach (var c in item.Info.Chapters) Chapters.Add(new ChapterItem(c));
        chapterIndex = -1;
        Duration = item.Info.Duration.TotalSeconds;

        media?.Dispose();
        media = new Media(Vlc, item.Path, FromType.FromPath);

        await Task.Run(() => App.Positions.RefreshRemote(force: true));
        var saved = App.Positions.GetByKey(bookKey);
        lastLocalChangeUtc = saved?.UpdatedUtc ?? DateTime.MinValue;
        double start = saved is { Finished: false } ? saved.Seconds : 0;
        if (start >= Duration - 5) start = 0;
        Position = start;
        baseline = new PlaybackPositions.Entry { Seconds = start, Finished = false, UpdatedUtc = saved?.UpdatedUtc ?? DateTime.MinValue };
        UpdateChapter();
        opening = false;
        App.Settings.LastBookPath = item.Path;
        App.Settings.Save();
        OnPropertyChanged(nameof(Cover)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Author));
        StateChanged?.Invoke();
        if (autoplay) Play(); else IsLoading = false;
    }

    private void StopVlc()
    {
        started = false;
        if (player.IsPlaying || player.State == VLCState.Paused) player.Stop();
        IsPlaying = false;
    }

    public void Close()
    {
        SavePosition(flush: true);
        StopVlc();
        media?.Dispose(); media = null;
        if (Book != null) Book.IsPlaying = false;
        Book = null; bookKey = null; ended = false; baseline = null; ClearOffer();
        Chapters.Clear(); chapterIndex = -1;
        Position = 0; Duration = 0;
        SleepText = ""; sleepAt = null; sleepAtChapterEnd = false;
        StateChanged?.Invoke();
    }

    public void Play()
    {
        if (Book == null || media == null) return;
        if (ended) { Position = 0; ended = false; }
        if (!started)
        {
            // libVLC can't seek before playback starts, so the resume point rides along as start-time.
            media.AddOption($":start-time={Position.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            player.Play(media);
            started = true;
        }
        else player.SetPause(false);
    }

    public void Pause() { if (started) player.SetPause(true); }
    public void TogglePlay() { if (IsPlaying) Pause(); else Play(); }

    public void Seek(double seconds, bool save = true)
    {
        if (Book == null) return;
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.5));
        if (started) player.Time = (long)(seconds * 1000);
        Position = seconds;
        UpdateChapter();
        lastLocalChangeUtc = DateTime.UtcNow;
        ended = false;
        if (save) SavePosition(userSeek: true, flush: !IsPlaying);
        Seeked?.Invoke();
    }

    /// <summary>
    /// Checks for a newer position from another device and applies the Apple decideRemote rule:
    /// jump silently while paused when it's ahead or ≤60 s behind, otherwise offer "Continue from…".
    /// </summary>
    public async Task SyncFromOtherDevicesAsync()
    {
        if (Book == null || IsLoading || bookKey == null) return;
        var b = Book;
        await Task.Run(() => App.Positions.RefreshRemote());
        if (Book != b || bookKey == null) return;
        var e = App.Positions.GetByKey(bookKey);
        if (e == null) return;
        double target = e.Finished ? 0 : e.Seconds;
        switch (RemotePositionPolicy.Decide(Position, lastLocalChangeUtc, target, e.UpdatedUtc, IsPlaying))
        {
            case RemoteDecision.Jump:
                ClearOffer();
                Seek(target, save: false);
                lastLocalChangeUtc = e.UpdatedUtc;
                baseline = new PlaybackPositions.Entry { Seconds = Position, Finished = false, UpdatedUtc = e.UpdatedUtc };
                b.RefreshProgress();
                break;
            case RemoteDecision.Offer:
                if (offer?.UpdatedUtc == e.UpdatedUtc) return;
                offer = new PlaybackPositions.Entry { Seconds = target, UpdatedUtc = e.UpdatedUtc };
                OnPropertyChanged(nameof(HasOffer)); OnPropertyChanged(nameof(OfferText));
                break;
        }
    }

    public void AcceptOffer()
    {
        if (offer == null) return;
        var o = offer; ClearOffer();
        Seek(o.Seconds, save: false);
        lastLocalChangeUtc = o.UpdatedUtc;
        baseline = new PlaybackPositions.Entry { Seconds = Position, UpdatedUtc = o.UpdatedUtc };
        Book?.RefreshProgress();
    }

    /// <summary>"Stay here": treat the remote entry as seen, so it isn't offered again.</summary>
    public void DeclineOffer()
    {
        if (offer == null) return;
        lastLocalChangeUtc = offer.UpdatedUtc;
        ClearOffer();
        SavePosition(userSeek: true, flush: true);   // our place becomes the newest, everywhere
    }

    private void ClearOffer()
    {
        if (offer == null) return;
        offer = null;
        OnPropertyChanged(nameof(HasOffer)); OnPropertyChanged(nameof(OfferText));
    }

    public void SkipBack() => Seek(Position - App.Settings.SkipBackSeconds);
    public void SkipForward() => Seek(Position + App.Settings.SkipForwardSeconds);

    public void NextChapter()
    {
        if (chapterIndex + 1 < Chapters.Count) Seek(Chapters[chapterIndex + 1].Chapter.Start.TotalSeconds);
    }

    public void PreviousChapter()
    {
        var c = CurrentChapter;
        if (c == null) { Seek(0); return; }
        if (Position - c.Chapter.Start.TotalSeconds > 3 || chapterIndex == 0) Seek(c.Chapter.Start.TotalSeconds);
        else Seek(Chapters[chapterIndex - 1].Chapter.Start.TotalSeconds);
    }

    public void JumpTo(ChapterItem c) { Seek(c.Chapter.Start.TotalSeconds); if (!IsPlaying) Play(); }

    public void SetRate(double r)
    {
        rate = r;
        if (started) player.SetRate((float)r);
        App.Settings.PlaybackRate = r;
        App.Settings.Save();
        OnPropertyChanged(nameof(Rate)); OnPropertyChanged(nameof(RemainingText)); OnPropertyChanged(nameof(ChapterProgressText));
    }

    public void SetSleep(SleepOption option)
    {
        sleepAtChapterEnd = option.Minutes == -1;
        sleepChapter = sleepAtChapterEnd ? chapterIndex : -1;
        sleepAt = option.Minutes > 0 ? DateTime.Now.AddMinutes(option.Minutes) : null;
        UpdateSleepText();
    }

    private void OnTick()
    {
        if (Book == null) return;
        if (started && IsPlaying)
        {
            var t = player.Time;
            if (t >= 0) Position = t / 1000.0;
        }
        UpdateChapter();
        OnPropertyChanged(nameof(ChapterProgressText));
        if (sleepAt is { } at)
        {
            if (DateTime.Now >= at) { Pause(); sleepAt = null; }
            UpdateSleepText();
        }
        if (sleepAtChapterEnd && chapterIndex != sleepChapter && IsPlaying)
        {
            Pause();
            Seek(Chapters[chapterIndex].Chapter.Start.TotalSeconds);
            sleepAtChapterEnd = false;
            UpdateSleepText();
        }
        if (IsPlaying && DateTime.Now - lastSave > TimeSpan.FromSeconds(10)) SavePosition();
    }

    private void UpdateSleepText()
    {
        if (sleepAt is { } at)
        {
            var left = at - DateTime.Now;
            SleepText = left > TimeSpan.Zero ? $"Sleep in {Fmt.Time(left)}" : "";
        }
        else SleepText = sleepAtChapterEnd ? "Sleep at end of chapter" : "";
    }

    private void UpdateChapter()
    {
        int idx = -1;
        for (int i = 0; i < Chapters.Count; i++)
        {
            if (Chapters[i].Chapter.Start.TotalSeconds <= Position + 0.05) idx = i; else break;
        }
        if (idx == chapterIndex) return;
        if (CurrentChapter != null) CurrentChapter.IsCurrent = false;
        chapterIndex = idx;
        if (CurrentChapter != null) CurrentChapter.IsCurrent = true;
        OnPropertyChanged(nameof(CurrentChapter));
        OnPropertyChanged(nameof(ChapterTitle));
        ChapterChanged?.Invoke();
        StateChanged?.Invoke();
    }

    private void SavePosition(bool userSeek = false, bool flush = false)
    {
        if (Book == null || bookKey == null || opening || ended) return;
        if (!userSeek && Position <= 0) return;
        var recorded = App.Positions.SetByKeyIfChanged(bookKey, baseline, TimeSpan.FromSeconds(Position), userSeek: userSeek, title: Book.Title);
        if (recorded == null) { if (flush) App.Positions.Save(flushSync: true); return; }
        baseline = recorded;
        App.Positions.Save(flushSync: flush);
        Book.RefreshProgress();
        lastSave = DateTime.Now;
        lastLocalChangeUtc = DateTime.UtcNow;
    }

    public void Dispose()
    {
        SavePosition(flush: true);
        tick.Stop();
        try { player.Stop(); } catch { }
        player.Dispose();
        media?.Dispose();
    }
}
