using System.Buffers.Binary;
using System.Text;

namespace Casebound.Kit.Mp4;

/// <summary>
/// Streaming M4B muxer. Layout (validated against ffprobe before this port):
///
///   ftyp  M4B / M4A mp42 isom
///   mdat  (64-bit size) — AAC frames in chunks, then the chapter text samples
///   moov
///     mvhd
///     trak 1  audio (mp4a/esds), tref/chap → track 2
///     trak 2  QuickTime 'text' chapter track (disabled; one sample per chapter)
///     udta    chpl (Nero chapters, for players that ignore the text track)
///             meta/ilst: ©nam ©alb ©ART aART ©wrt(narrator) ©gen ©too stik=2 covr
///
/// Chapter samples use a QuickTime 'text' sample description, not tx3g — same
/// finding as the Mac build (gotcha 2): Apple's players want 'text'.
///
/// Unlike AVAssetWriter there is no input pacing here, because the moov is
/// written last: audio streams straight to disk, chapter times come from the
/// real decoded frame counts, and the text samples are appended after the audio.
/// </summary>
public sealed class Mp4Writer : IDisposable
{
    private const int FramesPerChunk = 64;             // ~1.5 s of audio per chunk
    private const int SamplesPerFrame = 1024;

    private readonly FileStream fs;
    private readonly int sampleRate, channels;
    private readonly byte[] asc;
    private readonly long mdatStart;
    private readonly List<int> frameSizes = new(1 << 16);
    private readonly List<long> chunkOffsets = new(1 << 12);
    private readonly List<(int firstChunk, int samplesPerChunk)> stscRuns = new();
    private int framesInCurrentChunk;
    private long audioBytes;
    private bool finished;

    public long FrameCount => frameSizes.Count;
    public long SampleCount => (long)frameSizes.Count * SamplesPerFrame;

    public Mp4Writer(string path, int sampleRate, int channels, byte[] audioSpecificConfig)
    {
        this.sampleRate = sampleRate; this.channels = channels; asc = audioSpecificConfig;
        fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        var b = new BoxBuilder();
        b.Begin("ftyp"); b.FourCC("M4B "); b.U32(0); b.FourCC("M4B "); b.FourCC("M4A "); b.FourCC("mp42"); b.FourCC("isom"); b.End();
        fs.Write(b.ToArray());
        mdatStart = fs.Position;
        Span<byte> hdr = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);            // size == 1 → 64-bit largesize follows
        Encoding.ASCII.GetBytes("mdat", hdr[4..]);
        fs.Write(hdr);                                            // largesize patched in Finish
    }

    public void WriteFrame(ReadOnlySpan<byte> frame)
    {
        if (framesInCurrentChunk == 0) chunkOffsets.Add(fs.Position);
        fs.Write(frame);
        frameSizes.Add(frame.Length);
        audioBytes += frame.Length;
        if (++framesInCurrentChunk == FramesPerChunk) CloseChunk();
    }

    private void CloseChunk()
    {
        if (framesInCurrentChunk == 0) return;
        if (stscRuns.Count == 0 || stscRuns[^1].samplesPerChunk != framesInCurrentChunk)
            stscRuns.Add((chunkOffsets.Count, framesInCurrentChunk));
        framesInCurrentChunk = 0;
    }

    /// <summary>Appends chapter samples, patches mdat, writes moov. <paramref name="chapters"/> starts are in ms.</summary>
    public void Finish(IReadOnlyList<(long startMs, string title)> chapters, BookMetadata meta)
    {
        if (finished) return;
        finished = true;
        CloseChunk();
        long totalSamples = SampleCount;
        long durationMs = totalSamples * 1000 / sampleRate;

        // ---- chapter text samples (still inside mdat) ----
        var chapterList = NormaliseChapters(chapters, durationMs);
        var textOffsets = new List<long>();
        var textSizes = new List<int>();
        var textDurations = new List<long>();
        for (int i = 0; i < chapterList.Count; i++)
        {
            long end = i + 1 < chapterList.Count ? chapterList[i + 1].startMs : durationMs;
            byte[] title = Encoding.UTF8.GetBytes(chapterList[i].title);
            if (title.Length > 0xFFFF) title = title[..0xFFFF];
            var s = new BoxBuilder();
            s.U16(title.Length); s.Bytes(title);
            s.Begin("encd"); s.U32(0x00000100); s.End();         // marks the text as UTF-8
            textOffsets.Add(fs.Position);
            var bytes = s.ToArray();
            fs.Write(bytes);
            textSizes.Add(bytes.Length);
            textDurations.Add(Math.Max(1, end - chapterList[i].startMs));
        }

        long mdatEnd = fs.Position;
        fs.Position = mdatStart + 8;
        Span<byte> size64 = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(size64, mdatEnd - mdatStart);
        fs.Write(size64);
        fs.Position = mdatEnd;

        bool wide = mdatEnd > uint.MaxValue;
        int avgBitrate = totalSamples > 0 ? (int)(audioBytes * 8 * sampleRate / totalSamples) : 0;
        int maxFrame = frameSizes.Count > 0 ? frameSizes.Max() : 0;
        int maxBitrate = Math.Max(avgBitrate, maxFrame * 8 * sampleRate / SamplesPerFrame);

        var m = new BoxBuilder();
        m.Begin("moov");

        // mvhd
        m.BeginFull("mvhd", 0, 0);
        m.U32(0); m.U32(0); m.U32(1000); m.U32(Clamp32(durationMs));
        m.U32(0x00010000); m.U16(0x0100); m.Zeros(10); m.Matrix(); m.Zeros(24); m.U32(3);
        m.End();

        // ---- audio track ----
        m.Begin("trak");
        Tkhd(m, 1, durationMs, enabled: true, audio: true);
        m.Begin("tref"); m.Begin("chap"); m.U32(2); m.End(); m.End();
        m.Begin("mdia");
        Mdhd(m, sampleRate, totalSamples);
        Hdlr(m, "soun", "SoundHandler");
        m.Begin("minf");
        m.BeginFull("smhd", 0, 0); m.U32(0); m.End();
        Dinf(m);
        m.Begin("stbl");
        m.BeginFull("stsd", 0, 0); m.U32(1);
        m.Begin("mp4a");
        m.Zeros(6); m.U16(1); m.Zeros(8); m.U16(channels); m.U16(16); m.U16(0); m.U16(0); m.U32((uint)sampleRate << 16);
        Esds(m, avgBitrate, maxBitrate, Math.Max(maxFrame, 768 * channels));
        m.End(); // mp4a
        m.End(); // stsd
        m.BeginFull("stts", 0, 0); m.U32(1); m.U32(frameSizes.Count); m.U32(SamplesPerFrame); m.End();
        m.BeginFull("stsc", 0, 0); m.U32(stscRuns.Count);
        foreach (var (first, spc) in stscRuns) { m.U32(first); m.U32(spc); m.U32(1); }
        m.End();
        m.BeginFull("stsz", 0, 0); m.U32(0); m.U32(frameSizes.Count);
        foreach (var sz in frameSizes) m.U32(sz);
        m.End();
        ChunkOffsets(m, chunkOffsets, wide);
        m.End(); // stbl
        m.End(); // minf
        m.End(); // mdia
        m.End(); // trak

        // ---- chapter text track ----
        m.Begin("trak");
        Tkhd(m, 2, durationMs, enabled: false, audio: false);
        m.Begin("mdia");
        Mdhd(m, 1000, textDurations.Sum());
        Hdlr(m, "text", "TextHandler");
        m.Begin("minf");
        m.Begin("gmhd");
        m.BeginFull("gmin", 0, 0); m.U16(0x40); m.U16(0x8000); m.U16(0x8000); m.U16(0x8000); m.U16(0); m.U16(0); m.End();
        m.Begin("text"); m.Matrix(); m.End();
        m.End(); // gmhd
        Dinf(m);
        m.Begin("stbl");
        m.BeginFull("stsd", 0, 0); m.U32(1);
        m.Begin("text");
        m.Zeros(6); m.U16(1);            // reserved, data reference index
        m.U32(1); m.U32(1);              // display flags, justification
        m.Zeros(6);                      // background colour
        m.Zeros(8);                      // default text box
        m.Zeros(8);                      // reserved
        m.U16(0); m.U16(0);              // font number, font face
        m.Zeros(1); m.U16(0);            // reserved
        m.Zeros(6);                      // foreground colour
        m.Zeros(1);                      // font name: empty Pascal string
        m.End(); // text
        m.End(); // stsd
        m.BeginFull("stts", 0, 0); m.U32(textDurations.Count);
        foreach (var d in textDurations) { m.U32(1); m.U32(Clamp32(d)); }
        m.End();
        m.BeginFull("stsc", 0, 0); m.U32(1); m.U32(1); m.U32(1); m.U32(1); m.End();
        m.BeginFull("stsz", 0, 0); m.U32(0); m.U32(textSizes.Count);
        foreach (var sz in textSizes) m.U32(sz);
        m.End();
        ChunkOffsets(m, textOffsets, wide);
        m.End(); // stbl
        m.End(); // minf
        m.End(); // mdia
        m.End(); // trak

        // ---- udta: chpl + iTunes metadata ----
        m.Begin("udta");
        m.BeginFull("chpl", 1, 0);
        m.U32(0);
        int chplCount = Math.Min(255, chapterList.Count);
        m.U8(chplCount);
        for (int i = 0; i < chplCount; i++)
        {
            m.U64((ulong)chapterList[i].startMs * 10_000UL);     // 100 ns units
            var t = TruncateUtf8(chapterList[i].title, 255);
            m.U8(t.Length); m.Bytes(t);
        }
        m.End();
        m.BeginFull("meta", 0, 0);
        m.BeginFull("hdlr", 0, 0); m.U32(0); m.FourCC("mdir"); m.FourCC("appl"); m.Zeros(9); m.End();
        m.Begin("ilst");
        Text(m, "©nam", meta.Title);
        Text(m, "©alb", meta.Title);
        Text(m, "©ART", meta.Author);
        Text(m, "aART", meta.Author);
        Text(m, "©wrt", meta.Narrator);                   // narrator: composer field, as Apple Books reads it
        Text(m, "©day", meta.Year);
        Text(m, "desc", meta.Description);
        Text(m, "©gen", "Audiobook");
        Text(m, "©too", "Casebound");
        m.Begin("stik"); m.Begin("data"); m.U32(21); m.U32(0); m.U8(2); m.End(); m.End();   // 2 = audiobook
        if (meta.Cover is { Length: > 0 } cover)
        {
            bool png = cover.Length > 4 && cover[0] == 0x89 && cover[1] == (byte)'P';
            m.Begin("covr"); m.Begin("data"); m.U32(png ? 14u : 13u); m.U32(0); m.Bytes(cover); m.End(); m.End();
        }
        m.End(); // ilst
        m.End(); // meta
        m.End(); // udta

        m.End(); // moov
        fs.Write(m.ToArray());
        fs.Flush(true);
    }

    private static List<(long startMs, string title)> NormaliseChapters(IReadOnlyList<(long startMs, string title)> chapters, long durationMs)
    {
        // Sorted, strictly increasing, starting at 0, none at/after the end.
        var list = new List<(long, string)>();
        foreach (var c in chapters.OrderBy(c => c.startMs))
        {
            long start = Math.Max(0, c.startMs);
            if (list.Count == 0) start = 0;
            else if (start <= list[^1].Item1) continue;       // a zero-length part: drop its mark, keep the timeline monotonic
            if (start >= durationMs && list.Count > 0) continue;
            list.Add((start, string.IsNullOrWhiteSpace(c.title) ? $"Chapter {list.Count + 1}" : c.title.Trim()));
        }
        if (list.Count == 0) list.Add((0, "Chapter 1"));
        return list;
    }

    private void Esds(BoxBuilder m, int avg, int max, int bufferSize)
    {
        // ES_Descriptor(DecoderConfig(DecoderSpecificInfo=ASC), SLConfig), each with a 4-byte length.
        var dsi = Descriptor(0x05, asc);
        var dcdBody = new List<byte> { 0x40, 0x15 };            // AAC, audio stream
        dcdBody.Add((byte)(bufferSize >> 16)); dcdBody.Add((byte)(bufferSize >> 8)); dcdBody.Add((byte)bufferSize);
        dcdBody.AddRange(BE32(max)); dcdBody.AddRange(BE32(avg)); dcdBody.AddRange(dsi);
        var dcd = Descriptor(0x04, dcdBody.ToArray());
        var sl = Descriptor(0x06, new byte[] { 0x02 });
        var esBody = new List<byte> { 0x00, 0x01, 0x00 };       // ES_ID 1, flags 0
        esBody.AddRange(dcd); esBody.AddRange(sl);
        m.BeginFull("esds", 0, 0); m.Bytes(Descriptor(0x03, esBody.ToArray())); m.End();
    }

    private static byte[] Descriptor(byte tag, byte[] body)
    {
        int n = body.Length;
        var d = new byte[5 + n];
        d[0] = tag;
        d[1] = (byte)(0x80 | ((n >> 21) & 0x7F));
        d[2] = (byte)(0x80 | ((n >> 14) & 0x7F));
        d[3] = (byte)(0x80 | ((n >> 7) & 0x7F));
        d[4] = (byte)(n & 0x7F);
        body.CopyTo(d, 5);
        return d;
    }

    private static byte[] BE32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static void Tkhd(BoxBuilder m, int trackId, long durationMs, bool enabled, bool audio)
    {
        m.BeginFull("tkhd", 0, (enabled ? 1 : 0) | 2 | 4);
        m.U32(0); m.U32(0); m.U32(trackId); m.U32(0); m.U32(Clamp32(durationMs));
        m.Zeros(8); m.U16(0); m.U16(0); m.U16(audio ? 0x0100 : 0); m.U16(0); m.Matrix(); m.U32(0); m.U32(0);
        m.End();
    }

    private static void Mdhd(BoxBuilder m, int timescale, long duration)
    {
        if (duration > uint.MaxValue)
        {
            m.BeginFull("mdhd", 1, 0); m.U64(0); m.U64(0); m.U32(timescale); m.U64((ulong)duration);
        }
        else
        {
            m.BeginFull("mdhd", 0, 0); m.U32(0); m.U32(0); m.U32(timescale); m.U32((uint)duration);
        }
        m.U16(0x55C4); m.U16(0);                                 // language 'und'
        m.End();
    }

    private static void Hdlr(BoxBuilder m, string type, string name)
    {
        m.BeginFull("hdlr", 0, 0); m.U32(0); m.FourCC(type); m.Zeros(12); m.Bytes(Encoding.ASCII.GetBytes(name)); m.U8(0); m.End();
    }

    private static void Dinf(BoxBuilder m)
    {
        m.Begin("dinf"); m.BeginFull("dref", 0, 0); m.U32(1); m.BeginFull("url ", 0, 1); m.End(); m.End(); m.End();
    }

    private static void ChunkOffsets(BoxBuilder m, List<long> offsets, bool wide)
    {
        if (wide)
        {
            m.BeginFull("co64", 0, 0); m.U32(offsets.Count);
            foreach (var o in offsets) m.U64((ulong)o);
        }
        else
        {
            m.BeginFull("stco", 0, 0); m.U32(offsets.Count);
            foreach (var o in offsets) m.U32((uint)o);
        }
        m.End();
    }

    private static void Text(BoxBuilder m, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        m.Begin(key); m.Begin("data"); m.U32(1); m.U32(0); m.Bytes(Encoding.UTF8.GetBytes(value.Trim())); m.End(); m.End();
    }

    private static byte[] TruncateUtf8(string s, int maxBytes)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= maxBytes) return b;
        int n = maxBytes;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--;            // don't cut a multi-byte character in half
        return b[..n];
    }

    private static uint Clamp32(long v) => v < 0 ? 0 : v > uint.MaxValue ? uint.MaxValue : (uint)v;

    public void Dispose() => fs.Dispose();
}

/// <summary>Big-endian box builder with begin/end size patching.</summary>
internal sealed class BoxBuilder
{
    private readonly MemoryStream ms = new();
    private readonly Stack<long> open = new();
    private readonly byte[] tmp = new byte[8];

    public void Begin(string type) { open.Push(ms.Position); U32(0); FourCC(type); }
    public void BeginFull(string type, int version, int flags) { Begin(type); U32(((uint)version << 24) | ((uint)flags & 0xFFFFFF)); }

    public void End()
    {
        long start = open.Pop();
        long end = ms.Position;
        long size = end - start;
        if (size > uint.MaxValue) throw new InvalidOperationException("box too large");
        ms.Position = start;
        U32((uint)size);
        ms.Position = end;
    }

    public void FourCC(string s)
    {
        // Latin-1 so "©nam" becomes A9 6E 61 6D.
        if (s.Length != 4) throw new ArgumentException("fourcc must be 4 chars: " + s);
        foreach (char c in s) ms.WriteByte((byte)c);
    }

    public void U8(int v) => ms.WriteByte((byte)v);
    public void U16(int v) { BinaryPrimitives.WriteUInt16BigEndian(tmp, (ushort)v); ms.Write(tmp, 0, 2); }
    public void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(tmp, v); ms.Write(tmp, 0, 4); }
    public void U32(int v) => U32((uint)v);
    public void U64(ulong v) { BinaryPrimitives.WriteUInt64BigEndian(tmp, v); ms.Write(tmp, 0, 8); }
    public void Bytes(ReadOnlySpan<byte> b) => ms.Write(b);
    public void Zeros(int n) { for (int i = 0; i < n; i++) ms.WriteByte(0); }
    public void Matrix() { U32(0x10000); U32(0); U32(0); U32(0); U32(0x10000); U32(0); U32(0); U32(0); U32(0x40000000); }
    public byte[] ToArray() => open.Count == 0 ? ms.ToArray() : throw new InvalidOperationException("unclosed box");
}
