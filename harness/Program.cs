using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Casebound.Kit;

int pass = 0, fail = 0;
void Check(bool ok, string what, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(ok || detail == "" ? "" : "  -- " + detail)}");
}
static string Esc(string s)
{
    var sb = new StringBuilder();
    foreach (var r in s.EnumerateRunes())
        sb.Append(r.Value < 0x80 ? ((char)r.Value).ToString() : $"\\u{{{r.Value:X}}}");
    return sb.ToString();
}

Console.WriteLine($".NET {Environment.Version}, OS {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Globalization invariant: {AppContext.TryGetSwitch("System.Globalization.Invariant", out var inv) && inv}; ICU version: {(typeof(object).Assembly.GetType("System.Globalization.GlobalizationMode")?.GetProperty("UseNls", BindingFlags.NonPublic|BindingFlags.Static)?.GetValue(null))?.ToString() ?? "?"} (UseNls)");

// ---------- 1. key test vectors ----------
Console.WriteLine("\n== Book key test vectors (PlaybackPositions.KeyFor) ==");
using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "position-key-test-vectors.json")));
foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
{
    var name = v.GetProperty("fileName").GetString()!;
    var size = v.GetProperty("size").GetInt64();
    var expected = v.GetProperty("expectedKey").GetString()!;
    var mayDiffer = v.GetProperty("dotnetMayDiffer").GetBoolean();
    var got = PlaybackPositions.KeyFor(name, size);
    Check(got == expected, $"key {Esc(v.GetProperty("fileNameScalars").GetString()!)}{(mayDiffer ? "  [dotnetMayDiffer]" : "")}",
          $"got {Esc(got)} expected {Esc(expected)}");
}

// ---------- 2. device-name sanitizing vs Swift DeviceNameTests ----------
Console.WriteLine("\n== Device name / file name (vs Swift DeviceNameTests) ==");
var slug = typeof(PlaybackPositions).GetMethod("Slug", BindingFlags.NonPublic | BindingFlags.Static)
        ?? typeof(PlaybackPositions).GetMethod("SanitizedDeviceName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
string Slug(string s) => (string)slug.Invoke(null, new object[] { s })!;
foreach (var (input, expected) in new[] {
    ("CHEWBACCA", "CHEWBACCA"), ("MacMini2024", "MacMini2024"),
    ("Lowell's iPhone", "Lowell-s-iPhone"), ("Lowell\u2019s Mac mini", "Lowell-s-Mac-mini"),
    ("\U0001F3A7Pod", "-Pod"), ("Rene\u0301e", "Ren-e"), ("Ren\u00E9e", "Ren-e"), ("\U0001F1FA\U0001F1F8", "-"),
    ("Stra\u00DFe\u0663", "Stra-e-"), (new string('a', 40), new string('a', 32)),
    (string.Concat(Enumerable.Repeat("\u00E9", 40)), new string('-', 32)), ("", "device") })
{
    var got = Slug(input);
    Check(got == expected, $"sanitize {Esc(input.Length > 20 ? input[..20] + "…" : input)}", $"got \"{Esc(got)}\" expected \"{expected}\"");
}

// ---------- 3. parsing Swift-written / spec files ----------
Console.WriteLine("\n== Reading position files (vs Swift PositionJSONTests) ==");
var readFile = typeof(PlaybackPositions).GetMethod("ReadFile", BindingFlags.NonPublic | BindingFlags.Static)!;
var tmp = Directory.CreateTempSubdirectory("cbh");
object? Read(string json)
{
    var p = Path.Combine(tmp.FullName, "positions-X-00000000.json");
    File.WriteAllText(p, json);
    return readFile.Invoke(null, new object[] { p });
}
int Count(object? books) => books switch { null => -1, System.Collections.ICollection c => c.Count, _ => (int)(books.GetType().GetProperty("Count")?.GetValue(books) ?? -2) };
object? BooksOf(object? r) => r == null ? null : (r.GetType().GetProperty("Books")?.GetValue(r) ?? r.GetType().GetField("Item2")?.GetValue(r) ?? r);

const string specExample = "{\"format\":1,\"platform\":\"ios\",\"device\":\"Lowell's iPhone\",\"deviceId\":\"0123456789abcdef0123456789abcdef\",\"updated\":\"2026-09-29T21:34:05Z\",\"books\":{\"cry havoc.m4b|676144782\":{\"position\":1834.5,\"updated\":\"2026-09-29T21:34:05Z\",\"finished\":false,\"title\":\"Cry Havoc\"}}}";
// what Swift's encoder writes (.prettyPrinted, .sortedKeys)
const string swiftPretty = "{\n  \"books\" : {\n    \"caf\u00e9 society.m4b|1000\" : {\n      \"finished\" : true,\n      \"position\" : 3,\n      \"updated\" : \"2026-09-29T21:34:05Z\"\n    }\n  },\n  \"device\" : \"Mac\",\n  \"deviceId\" : \"abc\",\n  \"format\" : 1,\n  \"platform\" : \"macos\",\n  \"updated\" : \"2026-09-29T21:34:05Z\"\n}";
Check(Count(BooksOf(Read(specExample))) == 1, "spec example parses (1 book)");
Check(Count(BooksOf(Read(swiftPretty))) == 1, "Swift pretty/sorted output parses (integer position, finished, no title)");
Check(Read(specExample.Replace("\"format\":1", "\"format\":2")) == null, "format 2 ignored");
Check(Read("not json") == null && Read("{") == null, "garbage skipped");
var lossy = "{\"format\":1,\"platform\":\"windows\",\"device\":\"X\",\"deviceId\":\"1\",\"updated\":\"2026-09-29T21:34:05Z\",\"books\":{\"good.m4b|1\":{\"position\":5,\"updated\":\"2026-09-29T21:34:05Z\"},\"bad.m4b|2\":{\"position\":\"soon\",\"updated\":\"yesterday\"}}}";
Check(Count(BooksOf(Read(lossy))) == 1, "one bad entry drops only that entry (Swift: testOneBadEntryDoesNotSinkTheFile)", $"got {Count(BooksOf(Read(lossy)))} books (-1 = whole file rejected)");
var nullTitle = specExample.Replace("\"title\":\"Cry Havoc\"", "\"title\":null");
Check(Count(BooksOf(Read(nullTitle))) == 1, "title:null tolerated");
var nullPos = specExample.Replace("\"position\":1834.5", "\"position\":null");
Check(Count(BooksOf(Read(nullPos))) is 0, "position:null drops the entry, not the file", $"got {Count(BooksOf(Read(nullPos)))}");
var frac = specExample.Replace("\"updated\":\"2026-09-29T21:34:05Z\",\"finished\"", "\"updated\":\"2026-09-29T21:34:05.750Z\",\"finished\"");
{
    var books = BooksOf(Read(frac)) as System.Collections.IDictionary;
    var e = books?["cry havoc.m4b|676144782"];
    var t = e?.GetType().GetProperty("UpdatedUtc")?.GetValue(e) as DateTime?;
    Check(t == new DateTime(2026, 9, 29, 21, 34, 5, DateTimeKind.Utc), "fractional seconds on read truncated to whole second (Swift wholeSeconds)", $"got {t:O}");
}

// ---------- 4. writing: what Windows puts on disk ----------
Console.WriteLine("\n== Writing (what the Swift decoder will see) ==");
var create = typeof(PlaybackPositions).GetMethod("CreateForTest", BindingFlags.NonPublic | BindingFlags.Static)!;
var lib = Path.Combine(tmp.FullName, "Audiobooks"); Directory.CreateDirectory(lib);
var bookPath = Path.Combine(lib, "Caf\u00e9 Society.m4b"); File.WriteAllBytes(bookPath, new byte[1000]);
var pc = (PlaybackPositions)create.Invoke(null, new object[] { Path.Combine(tmp.FullName, "pc.json"), "CHEWBACCA" })!;
pc.SyncFolder = lib;
pc.Set(bookPath, TimeSpan.FromSeconds(1834.25), title: "Lowell's <Book> & more");
pc.Save(flushSync: true);
var written = Directory.GetFiles(Path.Combine(lib, ".casebound"), "positions-*.json").Single();
var text = File.ReadAllText(written);
Console.WriteLine($"  file: {Path.GetFileName(written)}\n" + string.Join("\n", text.Split('\n').Select(l => "  | " + l)));
using (var w = JsonDocument.Parse(text))
{
    var r = w.RootElement;
    Check(r.GetProperty("format").GetInt32() == 1 && r.GetProperty("platform").GetString() == (OperatingSystem.IsWindows() ? "windows" : "linux")
          && r.TryGetProperty("device", out _) && r.GetProperty("deviceId").GetString()!.Length == 32, "top-level fields format/platform/device/deviceId (32 hex)");
    Check(System.Text.RegularExpressions.Regex.IsMatch(r.GetProperty("updated").GetString()!, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$"), "file updated = whole-second ISO 8601 Z");
    var b = r.GetProperty("books").EnumerateObject().Single();
    Check(b.Name == "caf\u00e9 society.m4b|1000", "book key round-trips through JSON", b.Name);
    Check(System.Text.RegularExpressions.Regex.IsMatch(b.Value.GetProperty("updated").GetString()!, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$"), "entry updated = whole-second ISO 8601 Z");
    Check(b.Value.GetProperty("finished").ValueKind == JsonValueKind.False, "finished is a JSON bool");
    Check(Math.Abs(b.Value.GetProperty("position").GetDouble() - 1834.3) < 1e-9, "position 1834.25 -> 1834.3 (Swift rounds half away from zero)", $"wrote {b.Value.GetProperty("position").GetRawText()}");
    Check(!text.Contains("\\u00E9") && !text.Contains("\\u0027"), "no \\uXXXX escaping of non-ASCII/apostrophes (cosmetic; still valid JSON)");
}

// ---------- 5. merge / tie-break ----------
Console.WriteLine("\n== Merge / tie-break (vs Swift PositionMergeTests) ==");
string WriteDevice(string id, string entryJson)
{
    var dir = Path.Combine(lib, ".casebound");
    var p = Path.Combine(dir, $"positions-DEV-{id[..8]}.json");
    File.WriteAllText(p, "{\"format\":1,\"platform\":\"ios\",\"device\":\"DEV\",\"deviceId\":\"" + id + "\",\"updated\":\"2026-09-29T21:34:05Z\",\"books\":{\"caf\u00e9 society.m4b|1000\":" + entryJson + "}}");
    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
    return p;
}
void ClearRemotes() { foreach (var f in Directory.GetFiles(Path.Combine(lib, ".casebound"), "positions-DEV-*.json")) File.Delete(f); }
double? MergedPos(PlaybackPositions p) { p.RefreshRemote(force: true); return p.Get(bookPath)?.Seconds; }
bool? MergedFinished(PlaybackPositions p) { p.RefreshRemote(force: true); return p.Get(bookPath)?.Finished; }

// a fresh Windows instance with no local entry for the book
PlaybackPositions Fresh() { var p = (PlaybackPositions)create.Invoke(null, new object[] { Path.Combine(tmp.FullName, Guid.NewGuid() + ".json"), "READER" })!; p.SyncFolder = lib; return p; }

// hide the CHEWBACCA file for these checks
File.Move(written, written + ".hidden");
ClearRemotes();
WriteDevice("aaaaaaaa00000000000000000000aaaa", "{\"position\":100,\"updated\":\"2026-09-29T21:00:10Z\",\"finished\":false}");
WriteDevice("bbbbbbbb00000000000000000000bbbb", "{\"position\":50,\"updated\":\"2026-09-29T21:00:30Z\",\"finished\":false}");
WriteDevice("cccccccc00000000000000000000cccc", "{\"position\":900,\"updated\":\"2026-09-29T21:00:20Z\",\"finished\":false}");
Check(MergedPos(Fresh()) == 50, "newest updated wins across devices");

bool allOk = true; string detail = "";
for (int i = 0; i < 12; i++)   // order of the sources must not matter
{
    ClearRemotes();
    WriteDevice("ffffffff00000000000000000000ffff", "{\"position\":10,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":true}");
    WriteDevice("0000000000000000000000000000000a", "{\"position\":500,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":false}");
    var f = MergedFinished(Fresh());
    if (f != true) { allOk = false; detail = $"finished={f}"; }
}
Check(allOk, "tie: finished beats larger position", detail);

allOk = true;
for (int i = 0; i < 12; i++)
{
    ClearRemotes();
    WriteDevice("0000000000000000000000000000000a", "{\"position\":100,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":false}");
    WriteDevice("ffffffff00000000000000000000ffff", "{\"position\":200,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":false}");
    var pos = MergedPos(Fresh());
    if (pos != 200) { allOk = false; detail = $"got {pos}"; }
}
Check(allOk, "tie: larger position wins", detail);

allOk = true;
for (int i = 0; i < 12; i++)
{
    ClearRemotes();
    WriteDevice("abcd0000000000000000000000000000", "{\"position\":100,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":false,\"title\":\"from abcd\"}");
    WriteDevice("12340000000000000000000000000000", "{\"position\":100,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":false,\"title\":\"from 1234\"}");
    var p = Fresh(); p.RefreshRemote(force: true);
    var t = p.Get(bookPath)?.Title;
    if (t != "from 1234") { allOk = false; detail = $"got {t}"; }
}
Check(allOk, "tie: smaller deviceId wins (deterministic)", detail);

// randomized ids: remote-vs-remote tie must not depend on directory enumeration order
{
    int wrong = 0;
    for (int i = 0; i < 40; i++)
    {
        ClearRemotes();
        var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
        WriteDevice(a, "{\"position\":100,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":false,\"title\":\"" + a + "\"}");
        WriteDevice(b, "{\"position\":100,\"updated\":\"2026-09-29T21:00:05Z\",\"finished\":false,\"title\":\"" + b + "\"}");
        var p = Fresh(); p.RefreshRemote(force: true);
        var expect = string.CompareOrdinal(a, b) < 0 ? a : b;
        if (p.Get(bookPath)?.Title != expect) wrong++;
    }
    Check(wrong == 0, "tie: smaller deviceId wins for 40 random id pairs", $"{wrong}/40 picked the larger id");
}
// local vs remote tie: remote finished at same second should win over local unfinished
{
    ClearRemotes();
    var p = Fresh();
    p.Set(bookPath, TimeSpan.FromSeconds(100));
    var localStamp = p.Get(bookPath)!.UpdatedUtc;
    WriteDevice("00000000000000000000000000000001", "{\"position\":100,\"updated\":\"" + localStamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\",\"finished\":true}");
    Check(MergedFinished(p) == true, "tie between this device and a remote: finished still wins");
}

// own deviceId under an old device name (renamed PC) must not count as a remote
{
    ClearRemotes();
    var p = Fresh();
    var id = (string)typeof(PlaybackPositions).GetField("deviceId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;
    var ghost = Path.Combine(lib, ".casebound", $"positions-OLDNAME-{id[..8]}.json");
    File.WriteAllText(ghost, "{\"format\":1,\"platform\":\"windows\",\"device\":\"OLDNAME\",\"deviceId\":\"" + id + "\",\"updated\":\"2030-01-01T00:00:00Z\",\"books\":{\"caf\u00e9 society.m4b|1000\":{\"position\":777,\"updated\":\"2030-01-01T00:00:00Z\",\"finished\":false}}}");
    p.RefreshRemote(force: true);
    Check(!p.RemoteFiles().Contains(Path.GetFileName(ghost)), "own file under an old device name is not read as another device");
    File.Delete(ghost);
}

// forget / delete clears only this device's entry
{
    var m = typeof(PlaybackPositions).GetMethod("Forget");
    Check(m != null, "has Forget(path) to clear only this device's entry on delete");
}

// ---------- 6. re-stamp: an adopted position saved unchanged must not get a fresh "updated" ----------
// Observed: CHEWBACCA opened at 20435 s (adopted from the Mac), paused/closed without listening, and
// stamped 20435 at 7:56:13 PM CT, beating the iPhone's real 20515 s from 7:55:04 PM CT.
// This replays what AudiobookPlayer does (baseline at open, SavePosition on pause/close, adopt while paused).
Console.WriteLine("\n== Re-stamp: unchanged adopted position (CHEWBACCA 20435 vs iPhone 20515) ==");
{
    var setIfChanged = typeof(PlaybackPositions).GetMethod("SetByKeyIfChanged");
    Check(setIfChanged != null, "has SetByKeyIfChanged (record only a meaningful change from the opened/adopted position)");
    if (setIfChanged != null)
    {
        var key = PlaybackPositions.KeyFor(bookPath);
        string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        DateTime Trunc(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        PlaybackPositions.Entry? Record(PlaybackPositions p, PlaybackPositions.Entry? baseline, double seconds, bool finished = false, bool userSeek = false) =>
            (PlaybackPositions.Entry?)setIfChanged.Invoke(p, new object?[] { key, baseline, TimeSpan.FromSeconds(seconds), finished, userSeek, null });
        PlaybackPositions.Entry? OwnEntry(PlaybackPositions p)
        {
            var id = (string)typeof(PlaybackPositions).GetField("deviceId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;
            var f = Path.Combine(lib, ".casebound", p.DeviceFileName);
            if (!File.Exists(f)) return null;
            var r = readFile.Invoke(null, new object[] { f });
            return (BooksOf(r) as System.Collections.IDictionary)?[key] as PlaybackPositions.Entry;
        }
        PlaybackPositions.Entry? Merged() { var r = Fresh(); r.RefreshRemote(force: true); return r.Get(bookPath); }

        var now = Trunc(DateTime.UtcNow);
        var macAt = now.AddMinutes(-10); var phoneAt = now.AddSeconds(-69);
        ClearRemotes();
        WriteDevice("aaaa0000000000000000000000000mac", "{\"position\":20435,\"updated\":\"" + Iso(macAt) + "\",\"finished\":false}");

        // open: adopt the Mac's position
        var pcPath = Path.Combine(tmp.FullName, Guid.NewGuid() + ".json");
        var pcR = (PlaybackPositions)create.Invoke(null, new object[] { pcPath, "CHEWBACCA" })!; pcR.SyncFolder = lib;
        pcR.RefreshRemote(force: true);
        var saved = pcR.Get(bookPath);
        Check(saved?.Seconds == 20435 && saved.UpdatedUtc == macAt, "open adopts the Mac's 20435 s", $"got {saved?.Seconds} @ {saved?.UpdatedUtc:O}");
        var baseline = new PlaybackPositions.Entry { Seconds = saved!.Seconds, UpdatedUtc = saved.UpdatedUtc };

        // the iPhone's real listening lands in iCloud before the PC pauses, but the PC hasn't read it yet
        WriteDevice("ffff00000000000000000000000phone", "{\"position\":20515,\"updated\":\"" + Iso(phoneAt) + "\",\"finished\":false}");

        // control: the old unconditional save re-stamps 20435 and beats the iPhone (the observed bug)
        {
            var old = (PlaybackPositions)create.Invoke(null, new object[] { Path.Combine(tmp.FullName, Guid.NewGuid() + ".json"), "OLDPC" })!; old.SyncFolder = lib;
            old.SetByKey(key, TimeSpan.FromSeconds(20435.3)); old.Save(flushSync: true);
            var m = Merged();
            Check(m != null && Math.Abs(m.Seconds - 20435) < 1 && m.UpdatedUtc > phoneAt, "control: an unconditional re-stamp of 20435 beats the iPhone's 20515 (the bug)", $"got {m?.Seconds}");
            foreach (var f in Directory.GetFiles(Path.Combine(lib, ".casebound"), old.DeviceFileName)) File.Delete(f);
        }

        // pause / close / switch books after < 1 s of playback: nothing recorded, nothing re-stamped
        var rec = Record(pcR, baseline, 20435.4);
        pcR.Save(flushSync: true);
        Check(rec == null, "pause 0.4 s after opening at an adopted position records nothing");
        var own = OwnEntry(pcR);
        Check(own == null || own.UpdatedUtc <= baseline.UpdatedUtc, "PC file has no newer 'updated' for the unchanged position", $"own {own?.Seconds} @ {own?.UpdatedUtc:O}");
        var merged = Merged();
        Check(merged?.Seconds == 20515 && merged.UpdatedUtc == phoneAt, "the iPhone's later 20515 s wins everywhere", $"got {merged?.Seconds} @ {merged?.UpdatedUtc:O}");

        // a newer remote arrives while open and paused: adopt it, without re-stamping
        pcR.RefreshRemote(force: true);
        var remoteE = pcR.Get(bookPath)!;
        Check(remoteE.Seconds == 20515 && remoteE.UpdatedUtc > baseline.UpdatedUtc, "paused PC sees the iPhone's newer 20515 s and adopts it");
        baseline = new PlaybackPositions.Entry { Seconds = remoteE.Seconds, UpdatedUtc = remoteE.UpdatedUtc };
        rec = Record(pcR, baseline, 20515.0);   // then close / switch books
        pcR.Save(flushSync: true);
        own = OwnEntry(pcR);
        merged = Merged();
        Check(rec == null && (own == null || own.UpdatedUtc <= phoneAt) && merged?.Seconds == 20515 && merged.UpdatedUtc == phoneAt,
              "close after adopting keeps the iPhone's entry and its original 'updated'", $"rec={rec?.Seconds} own={own?.Seconds}@{own?.UpdatedUtc:O} merged={merged?.Seconds}@{merged?.UpdatedUtc:O}");

        // real listening on the PC is still recorded, with a fresh stamp, and wins
        rec = Record(pcR, baseline, 20545.2);
        pcR.Save(flushSync: true);
        merged = Merged();
        Check(rec != null && merged?.Seconds == 20545.2 && merged.UpdatedUtc >= now, "30 s of real listening is recorded and wins", $"got {merged?.Seconds}");
        baseline = rec ?? baseline;
        Check(Record(pcR, baseline, 20546.1) == null, "0.9 s past the last recorded position: not recorded");
        Check(Record(pcR, baseline, 20546.2) != null, "1.0 s past it: recorded");
        baseline = pcR.Get(bookPath)!;
        Check(Record(pcR, baseline, baseline.Seconds, userSeek: true) != null, "an explicit seek is recorded even to the same position");
        Check(Record(pcR, baseline, baseline.Seconds, finished: true) != null, "a finished-state change at the same position is recorded");
        var meaningful = typeof(PlaybackPositions).GetMethod("IsMeaningfulChange")!;
        bool Meaningful(double s) => (bool)meaningful.Invoke(null, new object?[] { null, s, false })!;
        Check(!Meaningful(0.9) && Meaningful(1.0), "no saved entry: baseline is 0 s, not finished");
        foreach (var f in Directory.GetFiles(Path.Combine(lib, ".casebound"), pcR.DeviceFileName)) File.Delete(f);
    }
}

ClearRemotes();
Console.WriteLine($"\n{pass} passed, {fail} failed");
try { tmp.Delete(true); } catch { }
return fail == 0 ? 0 : 1;
