using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casebound.Kit;

/// <summary>
/// Per-book playback positions, synced through the shared iCloud library folder.
///
/// Every device writes only its own file:
///     iCloud Drive/Documents/Audiobooks/.casebound/positions-&lt;device&gt;-&lt;id8&gt;.json
/// and reads everyone's. For each book the newest entry, by "updated", wins; on a tie
/// (same whole second) finished wins, then the larger position, then the smaller
/// deviceId (ordinal) — the same order as Swift's PositionFiles.wins(_:over:).
///
/// The format is specified in NOTES.md ("Positions sync") and docs/1.1-windows-sync-plan.md
/// on the Apple side. Keep the two in step: format version, key rule, field names,
/// timestamp format, merge order.
///
/// A local copy (%LOCALAPPDATA%\Casebound\positions.json) is always kept too, so
/// positions work with no iCloud at all.
/// </summary>
public sealed class PlaybackPositions
{
    public const int FormatVersion = 1;
    public const string SyncFolderName = ".casebound";
    private static readonly TimeSpan SyncWriteInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RemoteRescanInterval = TimeSpan.FromSeconds(3);

    public sealed class Entry
    {
        public double Seconds { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public bool Finished { get; set; }
        public string? Title { get; set; }
    }

    private sealed record RemoteFile(DateTime Mtime, string DeviceId, Dictionary<string, Entry> Books);

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> local;                          // what THIS device has set
    private readonly Dictionary<string, RemoteFile> remote = new(StringComparer.OrdinalIgnoreCase);
    private readonly string deviceId, deviceName, localFile;
    private string? syncFolder;
    private DateTime lastRemoteScan = DateTime.MinValue, lastSyncWrite = DateTime.MinValue;
    private bool syncDirty;

    private PlaybackPositions(Dictionary<string, Entry> local, string deviceId, string localFile, string? deviceName = null)
    {
        this.local = local;
        this.deviceId = deviceId.ToLowerInvariant();
        this.localFile = localFile;
        this.deviceName = deviceName ?? Environment.MachineName;
    }

    /// <summary>An isolated instance for the self-test: its own local file and device identity.</summary>
    internal static PlaybackPositions CreateForTest(string localFile, string deviceName) =>
        new(new Dictionary<string, Entry>(), Guid.NewGuid().ToString("N"), localFile, deviceName);

    public static PlaybackPositions Load(AppSettings settings)
    {
        // 32 lowercase hex, no hyphens — the same style the Apple apps now generate.
        if (string.IsNullOrWhiteSpace(settings.DeviceId) || settings.DeviceId.Length < 8 || !settings.DeviceId.All(char.IsAsciiHexDigit))
        {
            settings.DeviceId = Guid.NewGuid().ToString("N");
            settings.Save();
        }
        Dictionary<string, Entry>? map = null;
        try
        {
            if (File.Exists(AppPaths.PositionsFile))
                map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(AppPaths.PositionsFile));
        }
        catch (Exception) { }
        return new PlaybackPositions(map ?? new Dictionary<string, Entry>(), settings.DeviceId!, AppPaths.PositionsFile);
    }

    /// <summary>
    /// The library folder positions sync through (the iCloud Documents/Audiobooks folder),
    /// or null for local-only. Setting it marks everything local for upload.
    /// </summary>
    public string? SyncFolder
    {
        get { lock (gate) return syncFolder; }
        set
        {
            lock (gate)
            {
                if (string.Equals(syncFolder, value, StringComparison.OrdinalIgnoreCase)) return;
                syncFolder = value;
                remote.Clear();
                lastRemoteScan = DateTime.MinValue;
                syncDirty = local.Count > 0;
            }
        }
    }

    public string DeviceFileName => FileNameFor(deviceName, deviceId);

    public static string FileNameFor(string device, string id) =>
        $"positions-{SanitizedDeviceName(device)}-{(id.Length >= 8 ? id[..8] : id).ToLowerInvariant()}.json";

    /// <summary>
    /// The book key, identical on every platform: the file name (no folder), Unicode
    /// NFC-normalised and lower-cased the way Swift's String.lowercased() does it, then
    /// "|", then the file size in bytes. Example: "harry potter and the chamber of secrets.m4b|412337152".
    /// Checked against docs/position-key-test-vectors.json (on the Mac).
    /// </summary>
    public static string KeyFor(string path)
    {
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (Exception) { }   // was IOException only: UnauthorizedAccess escaped to the UI thread
        return KeyFor(Path.GetFileName(path), size);
    }

    public static string KeyFor(string fileName, long size) =>
        SwiftLowercased(fileName.Normalize(NormalizationForm.FormC)) + "|" + size.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// ToLowerInvariant is simple (1:1) case mapping; Swift's lowercased() applies the full
    /// mapping. The only unconditional 1:many lowercase mapping in Unicode is U+0130
    /// (İ → i + U+0307), which .NET leaves unchanged. Georgian Mtavruli (Unicode 11) is
    /// mapped explicitly so the key doesn't depend on the OS's ICU/NLS tables.
    /// Order matters: NFC first, lowercase second, no re-normalisation (as in Swift).
    /// </summary>
    internal static string SwiftLowercased(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            if (c == '\u0130') sb.Append("i\u0307");
            else if ((c >= '\u1C90' && c <= '\u1CBA') || (c >= '\u1CBD' && c <= '\u1CBF')) sb.Append((char)(c - 0x1C90 + 0x10D0));
            else sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Newest known position for the book across this device and every synced device. Cache only — never touches the network.</summary>
    public Entry? Get(string path) => GetByKey(KeyFor(path));

    public Entry? GetByKey(string key)
    {
        lock (gate)
        {
            Entry? best = null;
            string bestDevice = "";
            if (local.TryGetValue(key, out var mine)) { best = mine; bestDevice = deviceId; }
            foreach (var file in remote.Values)
                if (file.Books.TryGetValue(key, out var e) && (best == null || Wins(e, file.DeviceId, best, bestDevice)))
                { best = e; bestDevice = file.DeviceId; }
            return best;
        }
    }

    /// <summary>Swift PositionFiles.wins(_:over:): newest whole second, then finished, then larger position, then smaller deviceId.</summary>
    public static bool Wins(Entry a, string aDevice, Entry b, string bDevice)
    {
        var ta = TruncateToSecond(a.UpdatedUtc); var tb = TruncateToSecond(b.UpdatedUtc);
        if (ta != tb) return ta > tb;
        if (a.Finished != b.Finished) return a.Finished;
        var pa = RoundPosition(a.Seconds); var pb = RoundPosition(b.Seconds);
        if (pa != pb) return pa > pb;
        return string.CompareOrdinal(aDevice, bDevice) < 0;
    }

    public void Set(string path, TimeSpan position, bool finished = false, string? title = null) =>
        SetByKey(KeyFor(path), position, finished, title);

    /// <summary>Records this device's entry with a fresh "updated" — unconditionally (explicit actions: mark as not started, a seek).</summary>
    public Entry SetByKey(string key, TimeSpan position, bool finished = false, string? title = null)
    {
        lock (gate)
        {
            var entry = new Entry
            {
                Seconds = RoundPosition(position.TotalSeconds),
                UpdatedUtc = TruncateToSecond(DateTime.UtcNow),
                Finished = finished,
                Title = title ?? (local.TryGetValue(key, out var old) ? old.Title : null)
            };
            local[key] = entry;
            syncDirty = true;
            return entry;
        }
    }

    /// <summary>A position closer than this to the baseline isn't listening (seek granularity, a stray tick).</summary>
    public const double MinMeaningfulChangeSeconds = 1.0;

    /// <summary>
    /// True when (seconds, finished) really differs from <paramref name="baseline"/>: the entry the
    /// open book was loaded or adopted at, or last recorded here (null = nothing saved: 0, not finished).
    /// </summary>
    public static bool IsMeaningfulChange(Entry? baseline, double seconds, bool finished)
    {
        bool baseFinished = baseline?.Finished ?? false;
        double baseSeconds = baseline?.Seconds ?? 0;
        if (finished != baseFinished) return true;
        return Math.Abs(RoundPosition(seconds) - RoundPosition(baseSeconds)) >= MinMeaningfulChangeSeconds - 1e-6;
    }

    /// <summary>
    /// Records this device's position only if it's a meaningful change from <paramref name="baseline"/>,
    /// or <paramref name="userSeek"/>. Returns the recorded entry, or null when nothing changed — then
    /// nothing is written and the adopted entry keeps its original "updated". Re-stamping an unchanged
    /// position (open at a position adopted from the iPhone, then pause/close/switch) would make it the
    /// newest, and newest wins: it would beat real, later listening on another device.
    /// </summary>
    public Entry? SetByKeyIfChanged(string key, Entry? baseline, TimeSpan position, bool finished = false, bool userSeek = false, string? title = null) =>
        userSeek || IsMeaningfulChange(baseline, position.TotalSeconds, finished) ? SetByKey(key, position, finished, title) : null;

    /// <summary>Drops THIS device's entry only (a deleted book). Other devices' files are theirs.</summary>
    public void Forget(string key)
    {
        lock (gate)
        {
            if (local.Remove(key)) syncDirty = true;
        }
    }

    /// <summary>
    /// Writes the local file every time, and this device's sync file at most every
    /// 30 s while playing — or immediately with <paramref name="flushSync"/> (pause,
    /// seek, close, finishing a book).
    /// </summary>
    public void Save(bool flushSync = false)
    {
        string localJson;
        string? syncPath = null, syncJson = null;
        lock (gate)
        {
            localJson = JsonSerializer.Serialize(local);
            if (syncFolder != null && syncDirty && (flushSync || DateTime.UtcNow - lastSyncWrite >= SyncWriteInterval))
            {
                syncPath = Path.Combine(syncFolder, SyncFolderName, DeviceFileName);
                syncJson = JsonSerializer.Serialize(ToFile(), FileJson);
                syncDirty = false;
                lastSyncWrite = DateTime.UtcNow;
            }
        }
        try { AppSettings.AtomicWrite(localFile, localJson); } catch (Exception) { }
        if (syncPath != null && syncJson != null)
        {
            try
            {
                var dir = Path.GetDirectoryName(syncPath)!;
                EnsureHiddenFolder(dir);
                AppSettings.AtomicWrite(syncPath, syncJson);
                RemoveStaleOwnFiles(dir);
            }
            catch (Exception e) { lock (gate) syncDirty = true; Log($"positions write failed: {e.GetType().Name}: {e.Message}"); }   // try again next save
        }
    }

    /// <summary>This device's file under an old machine name (same deviceId) — delete it, as the Swift side does.</summary>
    private void RemoveStaleOwnFiles(string dir)
    {
        var suffix = $"-{deviceId[..8]}.json";
        foreach (var file in SafeEnumerate(dir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(DeviceFileName, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (ReadFile(file) is { } f && f.DeviceId == deviceId)
                try { File.Delete(file); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Re-reads other devices' files that changed since last time. Blocking, and may make
    /// iCloud download a small file — call it off the UI thread.
    /// </summary>
    public void RefreshRemote(bool force = false)
    {
        string? folder;
        lock (gate)
        {
            folder = syncFolder;
            if (folder == null) return;
            if (!force && DateTime.UtcNow - lastRemoteScan < RemoteRescanInterval) return;
            lastRemoteScan = DateTime.UtcNow;
        }
        var dir = Path.Combine(folder, SyncFolderName);
        if (!Directory.Exists(dir)) return;
        EnsureHiddenFolder(dir);   // iCloud may have reset it since the last write

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SafeEnumerate(dir))
        {
            var name = Path.GetFileName(file);
            if (!IsPositionsFileName(name)) continue;
            if (name.Equals(DeviceFileName, StringComparison.OrdinalIgnoreCase)) continue;   // our own: `local` is authoritative
            seen.Add(file);
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(file); } catch (Exception) { continue; }
            lock (gate)
                if (remote.TryGetValue(file, out var cached) && cached.Mtime == mtime) continue;
            var parsed = ReadFile(file);
            if (parsed == null) continue;                  // half-synced: keep the last good copy
            lock (gate)
            {
                if (parsed.Value.DeviceId == deviceId) { remote.Remove(file); continue; }   // our own file under an old name
                remote[file] = new RemoteFile(mtime, parsed.Value.DeviceId, parsed.Value.Books);
            }
        }
        lock (gate)
            foreach (var gone in remote.Keys.Where(k => !seen.Contains(k)).ToList()) remote.Remove(gone);
    }

    /// <summary>Other devices currently contributing positions (for diagnostics).</summary>
    public IReadOnlyList<string> RemoteFiles()
    {
        lock (gate) return remote.Keys.Select(k => Path.GetFileName(k)).ToList();
    }

    // ---------- file format ----------

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // é and ' as themselves, not \u00E9 / \u0027 (still valid JSON either way)
    };

    public sealed class PositionsFile
    {
        public int Format { get; set; }
        public string Platform { get; set; } = "";
        public string Device { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Updated { get; set; } = "";
        public Dictionary<string, BookPosition> Books { get; set; } = new();
    }

    public sealed class BookPosition
    {
        public double Position { get; set; }
        public string Updated { get; set; } = "";
        public bool Finished { get; set; }
        public string? Title { get; set; }
    }

    private PositionsFile ToFile() => new()
    {
        Format = FormatVersion,
        Platform = OperatingSystem.IsWindows() ? "windows" : "linux",
        Device = deviceName,
        DeviceId = deviceId,
        Updated = Iso(DateTime.UtcNow),
        Books = local.ToDictionary(kv => kv.Key, kv => new BookPosition
        {
            Position = RoundPosition(kv.Value.Seconds),
            Updated = Iso(kv.Value.UpdatedUtc),
            Finished = kv.Value.Finished,
            Title = string.IsNullOrEmpty(kv.Value.Title) ? null : kv.Value.Title
        })
    };

    /// <summary>
    /// Tolerant reader, matching Swift's PositionFile decoder: a newer format or an
    /// unparseable file is skipped (null); one bad entry costs only that entry;
    /// a missing "finished" reads as false; fractional seconds are truncated.
    /// </summary>
    private static (string DeviceId, Dictionary<string, Entry> Books)? ReadFile(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var fmt) || !fmt.TryGetInt32(out var format) || format > FormatVersion) return null;
            if (!root.TryGetProperty("deviceId", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var books = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (root.TryGetProperty("books", out var list) && list.ValueKind == JsonValueKind.Object)
                foreach (var p in list.EnumerateObject())
                {
                    var b = p.Value;
                    if (b.ValueKind != JsonValueKind.Object) continue;
                    if (!b.TryGetProperty("position", out var pos) || pos.ValueKind != JsonValueKind.Number || !pos.TryGetDouble(out var seconds)) continue;
                    if (!b.TryGetProperty("updated", out var up) || up.ValueKind != JsonValueKind.String || !TryParseIso(up.GetString(), out var updated)) continue;
                    bool finished = false;
                    if (b.TryGetProperty("finished", out var fin))
                    {
                        if (fin.ValueKind is JsonValueKind.True or JsonValueKind.False) finished = fin.GetBoolean();
                        else if (fin.ValueKind != JsonValueKind.Null) continue;
                    }
                    string? title = b.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    books[p.Name] = new Entry { Seconds = RoundPosition(seconds), UpdatedUtc = updated, Finished = finished, Title = title };
                }
            return (id.GetString()!.ToLowerInvariant(), books);
        }
        catch (Exception) { return null; }   // half-synced or foreign file: skip until it settles
    }

    private static readonly string[] IsoFormats = { "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" };

    private static bool TryParseIso(string? text, out DateTime utc)
    {
        utc = default;
        if (text == null) return false;
        if (!DateTimeOffset.TryParseExact(text, IsoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dto)) return false;
        utc = TruncateToSecond(dto.UtcDateTime);
        return true;
    }

    /// <summary>Only positions-*.json: never a .tmp mid-write or anything else in the folder.</summary>
    public static bool IsPositionsFileName(string name) =>
        name.StartsWith("positions-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);

    /// <summary>
    /// Creates .casebound if needed and sets the Hidden attribute on it. The leading dot
    /// hides it on macOS/iOS; Windows ignores the dot and needs the attribute.
    ///
    /// The attribute is local to this PC and isn't synced, and iCloud for Windows can reset
    /// a folder's attributes when it turns it into a cloud placeholder after uploading. So
    /// this runs on every write AND every read of the folder, and the result is logged to
    /// %LOCALAPPDATA%\Casebound\sync.log whenever the attribute won't stick.
    /// Returns a short status for diagnostics.
    /// </summary>
    public static string EnsureHiddenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var before = File.GetAttributes(dir);
            if ((before & FileAttributes.Hidden) != 0) return "hidden";
            File.SetAttributes(dir, before | FileAttributes.Hidden);
            var after = File.GetAttributes(dir);
            if ((after & FileAttributes.Hidden) != 0) return "hidden (just set)";
            Log($"Hidden attribute didn't stick on {dir}: before={before}, after={after}");
            return $"NOT hidden — attribute didn't stick ({after})";
        }
        catch (Exception e)
        {
            Log($"Couldn't hide {dir}: {e.GetType().Name}: {e.Message}");
            return $"NOT hidden — {e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>The .casebound folder for the current sync folder, or null.</summary>
    public string? SyncDirectory
    {
        get { lock (gate) return syncFolder == null ? null : Path.Combine(syncFolder, SyncFolderName); }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.Root, "sync.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\n"); }
        catch (Exception) { }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.GetFiles(dir, "positions-*.json").Where(f => IsPositionsFileName(Path.GetFileName(f))).ToArray(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>ISO 8601, UTC, whole seconds, "Z" — what Swift's default ISO8601DateFormatter reads.</summary>
    private static string Iso(DateTime utc) => TruncateToSecond(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime TruncateToSecond(DateTime d)
    {
        if (d.Kind == DateTimeKind.Local) d = d.ToUniversalTime();
        return new(d.Ticks - d.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>Swift roundedPosition: one decimal, half away from zero, never negative/NaN.</summary>
    public static double RoundPosition(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? Math.Round(seconds * 10, MidpointRounding.AwayFromZero) / 10 : 0;

    /// <summary>
    /// Swift PositionFiles.sanitizedDeviceName: per user-perceived character (Swift
    /// Character = extended grapheme cluster), ASCII letters/digits kept, anything else
    /// becomes ONE "-", at most 32, empty becomes "device". No trimming.
    /// </summary>
    public static string SanitizedDeviceName(string s)
    {
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext() && sb.Length < 32)
        {
            var el = e.GetTextElement();
            sb.Append(el.Length == 1 && char.IsAsciiLetterOrDigit(el[0]) ? el[0] : '-');
        }
        return sb.Length == 0 ? "device" : sb.ToString();
    }
}
