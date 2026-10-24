using System.Runtime.InteropServices;

namespace Casebound.Kit;

/// <summary>
/// Linux twin of the Media Foundation AAC MFT: ffmpeg's built-in (native, LGPL) AAC-LC
/// encoder, PCM in on stdin, ADTS out on stdout. The 7-byte ADTS headers are stripped so
/// Mp4Writer receives the same raw 1024-sample frames it gets on Windows.
/// </summary>
internal sealed class AacEncoder : IDisposable
{
    public static readonly int[] SupportedBitrates = { 64, 96, 128, 160, 192 };
    public const int SamplesPerFrame = 1024;

    private readonly System.Diagnostics.Process proc;
    private readonly Stream stdin;
    private readonly Thread reader;
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> frames = new();
    private readonly Task<string> errTask;
    private Exception? readError;
    private readonly int rate, channels;
    private bool drained;

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
        this.rate = rate; this.channels = channels;
        AudioSpecificConfig = BuildAsc(rate, channels);
        proc = Ffmpeg.Start(Ffmpeg.Executable, new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "s16le", "-ar", rate.ToString(), "-ac", channels.ToString(), "-i", "pipe:0",
            "-c:a", "aac", "-profile:a", "aac_low", "-b:a", $"{bitrateKbps}k", "-f", "adts", "pipe:1"
        }, redirectIn: true);
        stdin = proc.StandardInput.BaseStream;
        errTask = proc.StandardError.ReadToEndAsync();
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "aac-adts-reader" };
        reader.Start();
    }

    private void ReadLoop()
    {
        try
        {
            var s = proc.StandardOutput.BaseStream;
            var hdr = new byte[7];
            while (ReadExact(s, hdr, 7))
            {
                if (hdr[0] != 0xFF || (hdr[1] & 0xF0) != 0xF0) throw new InvalidDataException("ffmpeg ADTS stream lost sync");
                bool noCrc = (hdr[1] & 1) == 1;
                int len = ((hdr[3] & 3) << 11) | (hdr[4] << 3) | (hdr[5] >> 5);
                int hlen = noCrc ? 7 : 9;
                if (!noCrc) { var crc = new byte[2]; if (!ReadExact(s, crc, 2)) break; }
                var payload = new byte[len - hlen];
                if (!ReadExact(s, payload, payload.Length)) break;
                frames.Add(payload);
            }
        }
        catch (Exception e) { readError = e; }
        finally { frames.CompleteAdding(); }
    }

    private static bool ReadExact(Stream s, byte[] b, int n)
    {
        int got = 0;
        while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return false; got += r; }
        return true;
    }

    private void Pump(FrameSink sink)
    {
        while (frames.TryTake(out var f)) { sink(f); FramesOut++; }
    }

    public void Write(ReadOnlySpan<short> pcm, FrameSink frameSink)
    {
        stdin.Write(MemoryMarshal.AsBytes(pcm));
        Pump(frameSink);
        if (readError != null) throw new InvalidDataException("AAC encode failed: " + readError.Message, readError);
    }

    public void Drain(FrameSink frameSink)
    {
        if (drained) return;
        drained = true;
        stdin.Flush(); stdin.Close();
        foreach (var f in frames.GetConsumingEnumerable()) { frameSink(f); FramesOut++; }
        proc.WaitForExit();
        if (readError != null) throw new InvalidDataException("AAC encode failed: " + readError.Message, readError);
        if (proc.ExitCode != 0) throw new InvalidDataException("ffmpeg AAC encoder failed: " + errTask.Result.Trim());
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
        try { if (!drained) { stdin.Close(); } } catch { }
        try { if (!proc.HasExited) proc.Kill(); } catch { }
        proc.Dispose();
    }
}
