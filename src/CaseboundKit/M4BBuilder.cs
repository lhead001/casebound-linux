using System.Diagnostics;
using Casebound.Kit.Mp4;

namespace Casebound.Kit;

public sealed record ConversionPart(IAudioPart Source, string ChapterTitle, TimeSpan EstimatedDuration);

public sealed record ConversionOptions
{
    /// <summary>96, 128, 160 or 192 — the only rates Windows' AAC encoder accepts.</summary>
    public int BitrateKbps { get; init; } = 96;
    /// <summary>Folder for the in-progress file. Must NOT be a synced folder; see NOTES.md.</summary>
    public string? WorkFolder { get; init; }
}

public sealed record ConversionProgress(int PartIndex, int PartCount, string PartName, double Fraction, TimeSpan Encoded, TimeSpan Elapsed);

public sealed record ConversionResult(string OutputPath, AudiobookInfo Verified, TimeSpan Elapsed, int SampleRate, int Channels)
{
    public double SpeedFactor => Elapsed.TotalSeconds > 0 ? Verified.Duration.TotalSeconds / Elapsed.TotalSeconds : 0;
}

/// <summary>
/// Per-file decode → AAC encode → M4B with a chapter text track, then read the
/// result back with AudiobookInspector and refuse to call it done unless the
/// chapters survived.
/// </summary>
public static class M4BBuilder
{
    public static ConversionResult Build(
        IReadOnlyList<ConversionPart> parts,
        BookMetadata metadata,
        string outputPath,
        ConversionOptions? options = null,
        IProgress<ConversionProgress>? progress = null,
        ConversionCancellation? cancel = null)
    {
        if (parts.Count == 0) throw new ArgumentException("Nothing to convert.");
        options ??= new ConversionOptions();
        cancel ??= new ConversionCancellation();
        var clock = Stopwatch.StartNew();

        var sources = parts.Select(p => p.Source).ToList();
        var (rate, channels) = SequentialAudioSource.ChooseFormat(sources);
        var source = new SequentialAudioSource(sources, rate, channels);

        // Write somewhere local first, then move into place. Writing straight
        // into an iCloud/OneDrive folder would start uploading a half-built file.
        var work = options.WorkFolder ?? AppPaths.WorkFolder;
        Directory.CreateDirectory(work);
        var temp = Path.Combine(work, $"casebound-{Guid.NewGuid():N}.m4b.partial");

        double estimatedTotal = Math.Max(1, parts.Sum(p => p.EstimatedDuration.TotalSeconds));
        long lastReport = -1;
        int currentPart = 0;
        var chapterStarts = new List<(long startMs, string title)>();

        try
        {
            using (var encoder = new AacEncoder(rate, channels, options.BitrateKbps))
            using (var writer = new Mp4Writer(temp, rate, channels, encoder.AudioSpecificConfig))
            {
                long framesFed = 0;
                FrameSink sink = frame => writer.WriteFrame(frame);

                source.Run(
                    (index, startFrame) =>
                    {
                        currentPart = index;
                        chapterStarts.Add((startFrame * 1000 / rate, parts[index].ChapterTitle));
                    },
                    pcm =>
                    {
                        encoder.Write(pcm, sink);
                        framesFed += pcm.Length / channels;
                        // Throttle here, not in the UI: one callback per buffer
                        // would be hundreds of thousands of UI-thread hops.
                        long now = clock.ElapsedMilliseconds;
                        if (progress != null && now - lastReport >= 200)
                        {
                            lastReport = now;
                            double encodedSec = framesFed / (double)rate;
                            progress.Report(new ConversionProgress(currentPart, parts.Count, parts[currentPart].Source.DisplayName,
                                Math.Min(0.99, encodedSec / estimatedTotal), TimeSpan.FromSeconds(encodedSec), clock.Elapsed));
                        }
                    },
                    cancel);

                cancel.ThrowIfCancelled();
                encoder.Drain(sink);
                writer.Finish(chapterStarts, metadata);
            }

            cancel.ThrowIfCancelled();
            var info = AudiobookInspector.Inspect(temp);
            Verify(info, parts.Count, chapterStarts.Select(c => c.title).ToList());

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(temp, outputPath, overwrite: true);
            progress?.Report(new ConversionProgress(parts.Count - 1, parts.Count, "", 1.0, info.Duration, clock.Elapsed));
            return new ConversionResult(outputPath, info with { Path = outputPath }, clock.Elapsed, rate, channels);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static void Verify(AudiobookInfo info, int expectedChapters, IReadOnlyList<string> titles)
    {
        if (info.Chapters.Count != expectedChapters)
        {
            // A zero-length part legitimately loses its mark; anything else is a bug.
            if (info.Chapters.Count == 0 || info.Chapters.Count > expectedChapters)
                throw new InvalidDataException($"Verification failed: wrote {expectedChapters} chapters, read back {info.Chapters.Count}.");
        }
        if (info.MediaKind != 2) throw new InvalidDataException("Verification failed: stik (media kind) is not 2 = audiobook.");
        if (info.Duration <= TimeSpan.Zero) throw new InvalidDataException("Verification failed: zero duration.");
        if (info.Chapters.Count == expectedChapters)
            for (int i = 0; i < titles.Count; i++)
                if (!string.IsNullOrWhiteSpace(titles[i]) && info.Chapters[i].Title != titles[i].Trim())
                    throw new InvalidDataException($"Verification failed: chapter {i + 1} title read back as \"{info.Chapters[i].Title}\", expected \"{titles[i]}\".");
    }
}
