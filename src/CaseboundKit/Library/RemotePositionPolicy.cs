namespace Casebound.Kit;

/// <summary>What to do with a position another device saved for the open book.</summary>
public enum RemoteDecision
{
    /// <summary>Not newer than what this device last did, or too close to matter.</summary>
    Ignore,
    /// <summary>Jump there silently (keeps the other device's "updated").</summary>
    Jump,
    /// <summary>Show "Continue from h:mm:ss on &lt;device&gt;?" and let the user choose.</summary>
    Offer
}

/// <summary>
/// Port of the Apple apps' decideRemote rule (PositionSync.swift), so every platform reacts
/// the same way to a newer position from another device:
///  • only a remote entry newer than this device's last local change counts;
///  • while paused, jump automatically when the remote position is ahead, or at most
///    60 s behind (a small rewind on the other device);
///  • otherwise — playing here, or the remote is more than 60 s behind (a likely rollback,
///    e.g. a stale phone upload) — offer "Continue from…" instead of moving the playhead.
/// Pure and platform-neutral: shared by the Linux app and shareable back to Windows.
/// </summary>
public static class RemotePositionPolicy
{
    public const double MaxAutoRewindSeconds = 60;
    public const double MinDifferenceSeconds = 2;

    public static RemoteDecision Decide(double localSeconds, DateTime localChangeUtc,
                                        double remoteSeconds, DateTime remoteUpdatedUtc,
                                        bool isPlaying)
    {
        if (remoteUpdatedUtc <= localChangeUtc) return RemoteDecision.Ignore;
        if (Math.Abs(remoteSeconds - localSeconds) < MinDifferenceSeconds) return RemoteDecision.Ignore;
        bool ahead = remoteSeconds > localSeconds;
        bool smallRewind = !ahead && localSeconds - remoteSeconds <= MaxAutoRewindSeconds;
        if (!isPlaying && (ahead || smallRewind)) return RemoteDecision.Jump;
        return RemoteDecision.Offer;
    }
}

/// <summary>Library list shaping shared by the Listen screen (matches the Apple LibraryIndex rules).</summary>
public static class LibraryListing
{
    /// <summary>Splits scanned entries into the visible list and the "Removed from this list" section.</summary>
    public static (List<LibraryEntry> visible, List<LibraryEntry> removed) Split(IEnumerable<LibraryEntry> entries, IEnumerable<string> hiddenPaths)
    {
        var hidden = new HashSet<string>(hiddenPaths, StringComparer.Ordinal);
        var visible = new List<LibraryEntry>(); var removed = new List<LibraryEntry>();
        foreach (var e in entries) (hidden.Contains(e.Path) ? removed : visible).Add(e);
        return (visible, removed);
    }

    /// <summary>Continue Listening: started, unfinished books, most recently played first.</summary>
    public static List<T> ContinueListening<T>(IEnumerable<T> books, Func<T, PlaybackPositions.Entry?> position, int max = 5)
        => books.Select(b => (b, e: position(b)))
                .Where(x => x.e is { Finished: false, Seconds: > 0 })
                .OrderByDescending(x => x.e!.UpdatedUtc)
                .Take(max).Select(x => x.b).ToList();
}
