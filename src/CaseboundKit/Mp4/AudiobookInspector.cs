using System.Buffers.Binary;
using System.Text;

namespace Casebound.Kit;

public sealed record Chapter(int Index, string Title, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

public sealed record AudiobookInfo(
    string Path,
    string? Title,
    string? Author,
    string? Narrator,
    string? Album,
    TimeSpan Duration,
    IReadOnlyList<Chapter> Chapters,
    int? MediaKind,               // stik: 2 = audiobook
    byte[]? Cover,
    int SampleRate,
    int Channels,
    string ChapterSource);        // "text track", "chpl", or "none"

/// <summary>
/// Reads a finished MP4/M4B back: duration, chapters (QuickTime/tx3g chapter
/// track via tref/chap, falling back to Nero chpl), iTunes metadata and cover.
/// Verification after every conversion runs against this, and so does the
/// player — so the Windows app reads chapters from Apple-made books the same way
/// it reads its own. Only the moov box is loaded; the audio is never touched.
/// </summary>
public static class AudiobookInspector
{
    public static AudiobookInfo Inspect(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        byte[] moov = ReadTopLevel(fs, "moov") ?? throw new InvalidDataException($"{System.IO.Path.GetFileName(path)}: no moov box — not an MP4/M4B file");
        var root = new Region(moov, 0, moov.Length);

        var tracks = new List<Track>();
        foreach (var (type, s, e) in Children(moov, root.Start, root.End))
            if (type == "trak") tracks.Add(ParseTrack(moov, s, e));

        TimeSpan duration = TimeSpan.Zero;
        var mvhd = Find(moov, root, "mvhd");
        if (mvhd is { } mv)
        {
            int v = moov[mv.Start];
            long ts = v == 1 ? BE32(moov, mv.Start + 20) : BE32(moov, mv.Start + 12);
            long dur = v == 1 ? (long)BE64(moov, mv.Start + 24) : BE32(moov, mv.Start + 16);
            if (ts > 0) duration = TimeSpan.FromMilliseconds(dur * 1000.0 / ts);
        }
        var audio = tracks.FirstOrDefault(t => t.Handler == "soun");
        if (audio != null && audio.Timescale > 0)
            duration = TimeSpan.FromMilliseconds(audio.Duration * 1000.0 / audio.Timescale);

        var chapters = new List<Chapter>();
        string source = "none";
        var chapterTrack = audio?.ChapterRefs.Select(id => tracks.FirstOrDefault(t => t.Id == id)).FirstOrDefault(t => t != null)
                           ?? tracks.FirstOrDefault(t => t.Handler is "text" or "sbtl" && !t.Enabled);
        if (chapterTrack != null)
        {
            var marks = ReadTextSamples(fs, moov, chapterTrack);
            if (marks.Count > 0) { chapters = ToChapters(marks, duration); source = "text track"; }
        }
        if (chapters.Count == 0 && Find(moov, root, "udta", "chpl") is { } chpl)
        {
            var marks = ReadChpl(moov, chpl);
            if (marks.Count > 0) { chapters = ToChapters(marks, duration); source = "chpl"; }
        }

        var ilst = Find(moov, root, "udta", "meta", "ilst") ?? Find(moov, root, "meta", "ilst");
        var tags = ilst is { } il ? ReadIlst(moov, il) : new Dictionary<string, byte[]>();
        string? Str(string key) => tags.TryGetValue(key, out var b) ? Encoding.UTF8.GetString(b).Trim('\0', ' ') : null;
        int? stik = tags.TryGetValue("stik", out var sb) && sb.Length > 0 ? sb[^1] : null;

        return new AudiobookInfo(
            path,
            Str("©nam"),
            Str("aART") ?? Str("©ART"),
            Str("©nrt") ?? Str("©wrt"),
            Str("©alb"),
            duration,
            chapters,
            stik,
            tags.TryGetValue("covr", out var cover) ? cover : null,
            audio?.SampleRate ?? 0,
            audio?.Channels ?? 0,
            source);
    }

    // ---------- structure ----------

    private readonly record struct Region(byte[] Data, int Start, int End);

    private sealed class Track
    {
        public int Id;
        public bool Enabled;
        public string Handler = "";
        public long Timescale;
        public long Duration;
        public List<int> ChapterRefs = new();
        public int SampleRate, Channels;
        public byte[] Moov = Array.Empty<byte>();
        public Region Stbl;
    }

    private static byte[]? ReadTopLevel(FileStream fs, string wanted)
    {
        long pos = 0, len = fs.Length;
        Span<byte> h = stackalloc byte[16];
        while (pos + 8 <= len)
        {
            fs.Position = pos;
            if (fs.Read(h[..8]) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = Encoding.Latin1.GetString(h.Slice(4, 4));
            int hdr = 8;
            if (size == 1)
            {
                if (fs.Read(h.Slice(8, 8)) < 8) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(h[8..]);
                hdr = 16;
            }
            else if (size == 0) size = len - pos;
            if (size < hdr) break;
            if (type == wanted)
            {
                long body = size - hdr;
                if (body > 512L * 1024 * 1024) throw new InvalidDataException("moov is implausibly large");
                var buf = new byte[body];
                fs.Position = pos + hdr;
                fs.ReadExactly(buf);
                return buf;
            }
            pos += size;
        }
        return null;
    }

    private static IEnumerable<(string type, int start, int end)> Children(byte[] d, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = BE32(d, p);
            string type = Encoding.Latin1.GetString(d, p + 4, 4);
            int hdr = 8;
            if (size == 1) { size = (long)BE64(d, p + 8); hdr = 16; }
            else if (size == 0) size = end - p;
            if (size < hdr || p + size > end) yield break;
            yield return (type, p + hdr, (int)(p + size));
            p += (int)size;
        }
    }

    private static Region? Find(byte[] d, Region within, params string[] path)
    {
        var cur = within;
        foreach (var name in path)
        {
            Region? next = null;
            foreach (var (type, s, e) in Children(d, cur.Start, cur.End))
            {
                if (type != name) continue;
                // 'meta' is a full box in iTunes files (4 bytes version/flags before children)
                int skip = type == "meta" && s + 4 <= e && BE32(d, s) == 0 ? 4 : 0;
                next = new Region(d, s + skip, e);
                break;
            }
            if (next == null) return null;
            cur = next.Value;
        }
        return cur;
    }

    private static Track ParseTrack(byte[] d, int s, int e)
    {
        var t = new Track { Moov = d };
        var trak = new Region(d, s, e);
        if (Find(d, trak, "tkhd") is { } tk)
        {
            int v = d[tk.Start];
            t.Enabled = (d[tk.Start + 3] & 1) != 0;
            t.Id = (int)BE32(d, tk.Start + (v == 1 ? 20 : 12));
        }
        if (Find(d, trak, "mdia", "mdhd") is { } md)
        {
            int v = d[md.Start];
            t.Timescale = BE32(d, md.Start + (v == 1 ? 20 : 12));
            t.Duration = v == 1 ? (long)BE64(d, md.Start + 24) : BE32(d, md.Start + 16);
        }
        if (Find(d, trak, "mdia", "hdlr") is { } hd)
            t.Handler = Encoding.Latin1.GetString(d, hd.Start + 8, 4);
        if (Find(d, trak, "tref", "chap") is { } chap)
            for (int p = chap.Start; p + 4 <= chap.End; p += 4) t.ChapterRefs.Add((int)BE32(d, p));
        if (Find(d, trak, "mdia", "minf", "stbl") is { } stbl)
        {
            t.Stbl = stbl;
            if (t.Handler == "soun" && Find(d, stbl, "stsd") is { } stsd && stsd.End - stsd.Start >= 8 + 36)
            {
                int entry = stsd.Start + 8;                    // version/flags + count, then first sample entry
                t.Channels = (int)BE16(d, entry + 8 + 16);
                t.SampleRate = (int)(BE32(d, entry + 8 + 24) >> 16);
            }
        }
        return t;
    }

    // ---------- chapters ----------

    private static List<(long startMs, string title)> ReadTextSamples(FileStream fs, byte[] d, Track t)
    {
        var result = new List<(long, string)>();
        var stbl = t.Stbl;
        if (t.Timescale <= 0 || stbl.Data == null) return result;

        var durations = new List<long>();
        if (Find(d, stbl, "stts") is { } stts)
        {
            long n = BE32(d, stts.Start + 4);
            for (int k = 0; k < n; k++)
            {
                long count = BE32(d, stts.Start + 8 + k * 8), delta = BE32(d, stts.Start + 12 + k * 8);
                for (long c = 0; c < count && durations.Count < 100_000; c++) durations.Add(delta);
            }
        }
        var sizes = new List<int>();
        if (Find(d, stbl, "stsz") is { } stsz)
        {
            int fixedSize = (int)BE32(d, stsz.Start + 4);
            int count = (int)BE32(d, stsz.Start + 8);
            for (int k = 0; k < count && k < 100_000; k++)
                sizes.Add(fixedSize != 0 ? fixedSize : (int)BE32(d, stsz.Start + 12 + k * 4));
        }
        var chunkOffsets = new List<long>();
        if (Find(d, stbl, "stco") is { } stco)
        {
            int n = (int)BE32(d, stco.Start + 4);
            for (int k = 0; k < n; k++) chunkOffsets.Add(BE32(d, stco.Start + 8 + k * 4));
        }
        else if (Find(d, stbl, "co64") is { } co64)
        {
            int n = (int)BE32(d, co64.Start + 4);
            for (int k = 0; k < n; k++) chunkOffsets.Add((long)BE64(d, co64.Start + 8 + k * 8));
        }
        var stscRuns = new List<(int firstChunk, int spc)>();
        if (Find(d, stbl, "stsc") is { } stsc)
        {
            int n = (int)BE32(d, stsc.Start + 4);
            for (int k = 0; k < n; k++)
                stscRuns.Add(((int)BE32(d, stsc.Start + 8 + k * 12), (int)BE32(d, stsc.Start + 12 + k * 12)));
        }
        if (sizes.Count == 0 || chunkOffsets.Count == 0 || stscRuns.Count == 0) return result;

        // sample index → file offset
        var offsets = new List<long>(sizes.Count);
        int si = 0, run = 0;
        for (int ci = 0; ci < chunkOffsets.Count && si < sizes.Count; ci++)
        {
            while (run + 1 < stscRuns.Count && stscRuns[run + 1].firstChunk <= ci + 1) run++;
            long o = chunkOffsets[ci];
            for (int k = 0; k < stscRuns[run].spc && si < sizes.Count; k++) { offsets.Add(o); o += sizes[si++]; }
        }

        long time = 0;
        var buf = new byte[4096];
        for (int k = 0; k < offsets.Count; k++)
        {
            int size = Math.Min(sizes[k], buf.Length);
            string title = "";
            if (size >= 2)
            {
                fs.Position = offsets[k];
                fs.ReadExactly(buf, 0, size);
                int len = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(buf), size - 2);
                if (len >= 2 && buf[2] == 0xFE && buf[3] == 0xFF) title = Encoding.BigEndianUnicode.GetString(buf, 4, len - 2);
                else if (len >= 2 && buf[2] == 0xFF && buf[3] == 0xFE) title = Encoding.Unicode.GetString(buf, 4, len - 2);
                else title = Encoding.UTF8.GetString(buf, 2, len);
            }
            result.Add((time * 1000 / t.Timescale, title));
            time += k < durations.Count ? durations[k] : 0;
        }
        return result;
    }

    private static List<(long startMs, string title)> ReadChpl(byte[] d, Region chpl)
    {
        var result = new List<(long, string)>();
        int p = chpl.Start;
        int version = d[p];
        p += 4;                              // version/flags
        if (version == 1) p += 4;            // reserved
        if (p >= chpl.End) return result;
        int count = d[p++];
        for (int i = 0; i < count && p + 9 <= chpl.End; i++)
        {
            long start100ns = (long)BE64(d, p);
            int len = d[p + 8];
            if (p + 9 + len > chpl.End) break;
            result.Add((start100ns / 10_000, Encoding.UTF8.GetString(d, p + 9, len)));
            p += 9 + len;
        }
        return result;
    }

    private static List<Chapter> ToChapters(List<(long startMs, string title)> marks, TimeSpan duration)
    {
        var list = new List<Chapter>(marks.Count);
        for (int i = 0; i < marks.Count; i++)
        {
            var start = TimeSpan.FromMilliseconds(marks[i].startMs);
            var end = i + 1 < marks.Count ? TimeSpan.FromMilliseconds(marks[i + 1].startMs) : duration;
            if (end < start) end = start;
            list.Add(new Chapter(i, string.IsNullOrWhiteSpace(marks[i].title) ? $"Chapter {i + 1}" : marks[i].title, start, end));
        }
        return list;
    }

    // ---------- metadata ----------

    private static Dictionary<string, byte[]> ReadIlst(byte[] d, Region ilst)
    {
        var tags = new Dictionary<string, byte[]>();
        foreach (var (key, s, e) in Children(d, ilst.Start, ilst.End))
        {
            foreach (var (t, ds, de) in Children(d, s, e))
            {
                if (t != "data" || de - ds < 8) continue;
                tags.TryAdd(key, d[(ds + 8)..de]);          // skip type + locale
                break;
            }
        }
        return tags;
    }

    private static uint BE16(byte[] d, int p) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p, 2));
    private static long BE32(byte[] d, int p) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p, 4));
    private static ulong BE64(byte[] d, int p) => BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p, 8));
}
