namespace Casebound.Kit;

/// <summary>One input part of a book: something that can decode itself to PCM.</summary>
public interface IAudioPart
{
    string DisplayName { get; }
    /// <summary>Native sample rate / channels, used to choose the book's target format. 0 = unknown.</summary>
    int NativeSampleRate { get; }
    int NativeChannels { get; }
    /// <summary>Decode the whole part at the given format, interleaved 16-bit, into <paramref name="sink"/>.</summary>
    void Decode(int sampleRate, int channels, PcmSink sink, ConversionCancellation cancel);
}

/// <summary>A file on disk, decoded by Media Foundation.</summary>
public sealed class FileAudioPart : IAudioPart
{
    public string Path { get; }
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public int NativeSampleRate { get; }
    public int NativeChannels { get; }

    public FileAudioPart(string path, int nativeSampleRate = 0, int nativeChannels = 0)
    {
        Path = path;
        if (nativeSampleRate <= 0 || nativeChannels <= 0)
            (nativeSampleRate, nativeChannels) = MetadataReader.ProbeFormat(path);
        NativeSampleRate = nativeSampleRate;
        NativeChannels = nativeChannels;
    }

    public void Decode(int sampleRate, int channels, PcmSink sink, ConversionCancellation cancel)
    {
        using var decoder = new AudioDecoder(Path, sampleRate, channels);
        decoder.DecodeTo(sink, cancel);
    }
}

/// <summary>A synthesised tone — self-test and scale-test input that needs no files.</summary>
public sealed class TonePart : IAudioPart
{
    public double Frequency { get; }
    public double Seconds { get; }
    public string DisplayName => $"tone {Frequency:0} Hz, {Seconds:0.##} s";
    public int NativeSampleRate { get; }
    public int NativeChannels { get; }

    public TonePart(double frequency, double seconds, int sampleRate = 44100, int channels = 2)
    {
        Frequency = frequency; Seconds = seconds; NativeSampleRate = sampleRate; NativeChannels = channels;
    }

    public void Decode(int sampleRate, int channels, PcmSink sink, ConversionCancellation cancel)
        => ToneGenerator.Generate(Frequency, Seconds, sampleRate, channels, sink, cancel);
}

/// <summary>
/// Decodes the parts one at a time onto a single timeline.
///
/// This is the Windows twin of the Mac's SequentialAudioSource, and it exists for
/// the same reason: never build one giant composition of all parts. Each part
/// gets a short-lived decoder that is disposed before the next one opens, so a
/// 95-file / 15-hour book holds exactly one decoder at a time. The timeline is
/// the running count of PCM frames actually decoded, so chapter starts are exact
/// (not estimated from declared durations, which lie for VBR MP3s).
/// </summary>
public sealed class SequentialAudioSource
{
    private readonly IReadOnlyList<IAudioPart> parts;
    public int SampleRate { get; }
    public int Channels { get; }

    public SequentialAudioSource(IReadOnlyList<IAudioPart> parts, int sampleRate, int channels)
    {
        this.parts = parts; SampleRate = sampleRate; Channels = channels;
    }

    /// <summary>Picks the book format: 48 kHz only if every part is 48 kHz, otherwise 44.1; mono only if every part is mono.</summary>
    public static (int rate, int channels) ChooseFormat(IReadOnlyList<IAudioPart> parts)
    {
        bool all48 = parts.Count > 0 && parts.All(p => p.NativeSampleRate == 48000);
        bool allMono = parts.Count > 0 && parts.All(p => p.NativeChannels == 1);
        return (all48 ? 48000 : 44100, allMono ? 1 : 2);
    }

    /// <summary>
    /// Runs every part in order. <paramref name="onPartStart"/> gets the part index and
    /// the timeline position (in frames) where it begins.
    /// </summary>
    public long Run(Action<int, long> onPartStart, PcmSink onPcm, ConversionCancellation cancel)
    {
        long frames = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            cancel.ThrowIfCancelled();
            onPartStart(i, frames);
            int ch = Channels;
            parts[i].Decode(SampleRate, Channels, pcm =>
            {
                frames += pcm.Length / ch;
                onPcm(pcm);
            }, cancel);
        }
        return frames;
    }
}

/// <summary>Receives interleaved 16-bit PCM. (Own delegate rather than Action&lt;Span&gt; so nothing depends on ref-struct generics.)</summary>
public delegate void PcmSink(ReadOnlySpan<short> pcm);

/// <summary>Receives one encoded AAC frame.</summary>
public delegate void FrameSink(ReadOnlySpan<byte> frame);
