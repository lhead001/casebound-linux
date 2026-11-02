using System.Runtime.InteropServices;

namespace Casebound.Kit;

/// <summary>
/// Linux twin of the Media Foundation AAC MFT: ffmpeg's built-in (native, LGPL) AAC-LC
/// encoder, PCM in on stdin, ADTS out on stdout. The 7-byte ADTS headers are stripped so
/// Mp4Writer receives the same raw 1024-sample frames it gets on Windows.
///
/// ffmpeg's AAC encoder is single-threaded and ~90x realtime, so one process made long
/// books slow. The timeline is cut into 1024-aligned chunks that are encoded by several
/// ffmpeg processes at once. Each chunk is fed a little audio before (preroll) and after
/// (lookahead) its span; the preroll frames are dropped and exactly chunk/1024 frames are
/// kept, so frame N of the output covers the same samples a single encoder's frame N would.
/// Frame count, priming (1024) and timing are identical to the one-process encoder.
/// </summary>
internal sealed class AacEncoder : IDisposable
{
    public static readonly int[] SupportedBitrates = { 64, 96, 128, 160, 192 };
    public const int SamplesPerFrame = 1024;

    private const int ChunkFrames = 2048;          // ~47 s of audio per job
    private const int OverlapFrames = 16;          // preroll / lookahead, ~0.37 s

    private readonly int rate, channels, bitrate, workers;
    private readonly int chunkShorts, overlapShorts;
    private readonly SemaphoreSlim slots;
    private readonly Queue<Task<List<byte[]>>> pending = new();
    private short[] cur;
    private int curLen;
    private short[]? ready;            // full chunk waiting for the next chunk's head
    private short[]? prevTail;         // last overlap of the chunk before 'ready'
    private bool drained, firstJob = true;
    private int maxPending;

    public byte[] AudioSpecificConfig { get; }
    public int SampleRate => rate;
    public int Channels => channels;
    public long FramesOut { get; private set; }

    public AacEncoder(int rate, int channels, int bitrateKbps)
    {
        if (rate != 44100 && rate != 48000) throw new ArgumentException("AAC encoder needs 44100 or 48000 Hz", nameof(rate));
        if (channels is not (1 or 2)) throw new ArgumentException("AAC encoder: 1 or 2 channels", nameof(channels));
        if (Array.IndexOf(SupportedBitrates, bitrateKbps) < 0)
            throw new ArgumentException($"AAC encoder bitrate must be one of {string.Join("/", SupportedBitrates)} kbps");
        this.rate = rate; this.channels = channels; bitrate = bitrateKbps;
        AudioSpecificConfig = BuildAsc(rate, channels);
        _ = Ffmpeg.Executable; // fail early (selftest relies on this) if ffmpeg is missing
        var env = Environment.GetEnvironmentVariable("CASEBOUND_ENCODE_JOBS");
        workers = int.TryParse(env, out var w) && w > 0 ? w : Math.Clamp(Environment.ProcessorCount, 1, 16);
        chunkShorts = ChunkFrames * SamplesPerFrame * channels;
        overlapShorts = OverlapFrames * SamplesPerFrame * channels;
        slots = new SemaphoreSlim(workers);
        maxPending = workers * 2;
        cur = new short[chunkShorts];
    }

    public void Write(ReadOnlySpan<short> pcm, FrameSink frameSink)
    {
        while (pcm.Length > 0)
        {
            int n = Math.Min(pcm.Length, chunkShorts - curLen);
            pcm[..n].CopyTo(cur.AsSpan(curLen));
            curLen += n; pcm = pcm[n..];
            if (curLen == chunkShorts)
            {
                if (ready != null) Submit(ready, cur.AsSpan(0, overlapShorts).ToArray(), last: false);
                ready = cur; cur = new short[chunkShorts]; curLen = 0;
            }
        }
        Pump(frameSink, block: false);
    }

    private void Submit(short[] chunk, short[]? lookahead, bool last, int chunkLen = -1)
    {
        if (chunkLen < 0) chunkLen = chunk.Length;
        var pre = firstJob ? null : prevTail;
        firstJob = false;
        prevTail = chunkLen >= overlapShorts ? chunk.AsSpan(chunkLen - overlapShorts, overlapShorts).ToArray() : null;
        int dropFrames = pre == null ? 0 : pre.Length / channels / SamplesPerFrame;
        int keep = last ? int.MaxValue : chunkLen / channels / SamplesPerFrame;
        slots.Wait();
        // Dedicated threads: these block on pipes, and pool threads would starve.
        var t = Task.Factory.StartNew(() =>
        {
            try { return EncodeJob(pre, chunk, chunkLen, lookahead, dropFrames, keep); }
            finally { slots.Release(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        pending.Enqueue(t);
    }

    private List<byte[]> EncodeJob(short[]? pre, short[] chunk, int chunkLen, short[]? look, int drop, int keep)
    {
        using var proc = Ffmpeg.Start(Ffmpeg.Executable, new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "s16le", "-ar", rate.ToString(), "-ac", channels.ToString(), "-i", "pipe:0",
            "-c:a", "aac", "-profile:a", "aac_low", "-b:a", $"{bitrate}k", "-f", "adts", "pipe:1"
        }, redirectIn: true);
        var errTask = proc.StandardError.ReadToEndAsync();
        var writer = Task.Factory.StartNew(() =>
        {
            var s = proc.StandardInput.BaseStream;
            try
            {
                if (pre != null) s.Write(MemoryMarshal.AsBytes(pre.AsSpan()));
                s.Write(MemoryMarshal.AsBytes(chunk.AsSpan(0, chunkLen)));
                if (look != null) s.Write(MemoryMarshal.AsBytes(look.AsSpan()));
            }
            finally { s.Close(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var outFrames = new List<byte[]>(Math.Min(keep, ChunkFrames + 4));
        var o = new BufferedStream(proc.StandardOutput.BaseStream, 1 << 16);
        var hdr = new byte[7];
        int index = 0;
        while (ReadExact(o, hdr, 7))
        {
            if (hdr[0] != 0xFF || (hdr[1] & 0xF0) != 0xF0) throw new InvalidDataException("ffmpeg ADTS stream lost sync");
            bool noCrc = (hdr[1] & 1) == 1;
            int len = ((hdr[3] & 3) << 11) | (hdr[4] << 3) | (hdr[5] >> 5);
            int hlen = noCrc ? 7 : 9;
            if (!noCrc) { var crc = new byte[2]; if (!ReadExact(o, crc, 2)) break; }
            var payload = new byte[len - hlen];
            if (!ReadExact(o, payload, payload.Length)) break;
            if (index >= drop && outFrames.Count < keep) outFrames.Add(payload);
            index++;
        }
        writer.Wait();
        proc.WaitForExit();
        if (proc.ExitCode != 0) throw new InvalidDataException("ffmpeg AAC encoder failed: " + errTask.Result.Trim());
        if (keep != int.MaxValue && outFrames.Count != keep)
            throw new InvalidDataException($"AAC encode chunk produced {outFrames.Count} frames, expected {keep}");
        return outFrames;
    }

    private void Pump(FrameSink sink, bool block)
    {
        while (pending.Count > 0 && (block || pending.Peek().IsCompleted || pending.Count > maxPending))
        {
            List<byte[]> frames;
            try { frames = pending.Dequeue().GetAwaiter().GetResult(); }
            catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException("AAC encode failed: " + e.Message, e); }
            foreach (var f in frames) { sink(f); FramesOut++; }
        }
    }

    private static bool ReadExact(Stream s, byte[] b, int n)
    {
        int got = 0;
        while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return false; got += r; }
        return true;
    }

    public void Drain(FrameSink frameSink)
    {
        if (drained) return;
        drained = true;
        if (ready != null)
        {
            if (curLen == 0) Submit(ready, null, last: true);
            else
            {
                Submit(ready, cur.AsSpan(0, Math.Min(curLen, overlapShorts)).ToArray(), last: false);
                Submit(cur, null, last: true, chunkLen: curLen);
            }
        }
        else Submit(cur, null, last: true, chunkLen: curLen);
        Pump(frameSink, block: true);
    }

    /// <summary>AudioSpecificConfig for AAC-LC: objectType 2, frequency index, channel config.</summary>
    internal static byte[] BuildAsc(int rate, int channels)
    {
        int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        int idx = Array.IndexOf(rates, rate);
        if (idx < 0) throw new ArgumentException($"Unsupported AAC sample rate {rate}");
        int v = (2 << 11) | (idx << 7) | (channels << 3);
        return new[] { (byte)(v >> 8), (byte)v };
    }

    public void Dispose()
    {
        // Let in-flight jobs finish (they hold no external resources once done).
        foreach (var t in pending) { try { t.Wait(); } catch { } }
        pending.Clear();
    }
}
