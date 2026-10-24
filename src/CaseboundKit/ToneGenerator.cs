using System.Buffers.Binary;

namespace Casebound.Kit;

/// <summary>Synthesises PCM test input: sine tones, streamed or written as WAV.</summary>
public static class ToneGenerator
{
    public static void Generate(double frequency, double seconds, int sampleRate, int channels,
        PcmSink sink, ConversionCancellation? cancel = null)
    {
        long total = (long)Math.Round(seconds * sampleRate);
        const int block = 4096;
        var buf = new short[block * channels];
        double phaseStep = 2 * Math.PI * frequency / sampleRate;
        long n = 0;
        while (n < total)
        {
            cancel?.ThrowIfCancelled();
            int count = (int)Math.Min(block, total - n);
            for (int i = 0; i < count; i++)
            {
                // -12 dBFS with a 5 ms fade at each end so part joins don't click
                double env = Math.Min(1.0, Math.Min((n + i) / (0.005 * sampleRate), (total - n - i) / (0.005 * sampleRate)));
                short v = (short)(Math.Sin((n + i) * phaseStep) * 8192 * env);
                for (int c = 0; c < channels; c++) buf[i * channels + c] = v;
            }
            sink(new ReadOnlySpan<short>(buf, 0, count * channels));
            n += count;
        }
    }

    /// <summary>Writes a 16-bit PCM WAV — a real file for the decoder path of the self-test.</summary>
    public static void WriteWav(string path, double frequency, double seconds, int sampleRate = 44100, int channels = 2)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> h = stackalloc byte[44];
        long dataLen = (long)Math.Round(seconds * sampleRate) * channels * 2;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], (int)(36 + dataLen));
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], (int)dataLen);
        fs.Write(h);
        Generate(frequency, seconds, sampleRate, channels, pcm =>
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pcm);
            fs.Write(bytes);
        });
    }
}

/// <summary>
/// Explicit cancel flag, checked at every pump step (per decoded buffer). The
/// encode doesn't run "inside" a Task's cancellation scope in any useful way —
/// same lesson as the Mac's gotcha 12 — so the flag is the source of truth.
/// </summary>
public sealed class ConversionCancellation
{
    private volatile bool cancelled;
    public bool IsCancelled => cancelled;
    public void Cancel() => cancelled = true;
    public void ThrowIfCancelled()
    {
        if (cancelled) throw new OperationCanceledException("Conversion cancelled.");
    }
}
