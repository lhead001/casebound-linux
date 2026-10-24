using System.Runtime.InteropServices;

namespace Casebound.Kit;

/// <summary>
/// Linux twin of the Media Foundation decoder: ffmpeg decodes one part to interleaved
/// s16le at the book's rate/channel count and streams it through a pipe. One short-lived
/// process per part, same as one short-lived source reader per part on Windows.
/// </summary>
internal sealed class AudioDecoder : IDisposable
{
    private readonly string path;
    private readonly int rate, channels;

    public AudioDecoder(string path, int dstRate, int dstChannels)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Input file not found", path);
        this.path = path; rate = dstRate; channels = dstChannels;
    }

    public void DecodeTo(PcmSink sink, ConversionCancellation cancel)
    {
        using var p = Ffmpeg.Start(Ffmpeg.Executable, new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-f", "s16le", "-acodec", "pcm_s16le", "-ar", rate.ToString(), "-ac", channels.ToString(), "pipe:1"
        }, redirectIn: false);
        var errTask = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.BaseStream;
        var buf = new byte[65536 * 2];
        int carry = 0;
        try
        {
            while (true)
            {
                if (cancel.IsCancelled) { try { p.Kill(); } catch { } cancel.ThrowIfCancelled(); }
                int n = stdout.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                int total = carry + n;
                int usable = total - total % (2 * channels);
                if (usable > 0) sink(MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, usable)));
                carry = total - usable;
                if (carry > 0) Buffer.BlockCopy(buf, usable, buf, 0, carry);
            }
        }
        finally { if (!p.HasExited) p.WaitForExit(); }
        if (p.ExitCode != 0)
            throw new InvalidDataException($"ffmpeg couldn't decode {Path.GetFileName(path)}: {errTask.Result.Trim()}");
    }

    public void Dispose() { }
}
