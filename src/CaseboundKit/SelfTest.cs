using System.Diagnostics;

namespace Casebound.Kit;

/// <summary>
/// Engine self-test, shared by `abtool selftest` and `Casebound.exe --selftest`.
///
///  1. Media Foundation + the AAC encoder come up.
///  2. Three real WAV files (one 48 kHz mono, so the resample/remix path runs)
///     → one .m4b with UTF-8 chapter titles, metadata and cover.
///  3. Read back: 3 chapters at the right boundaries, titles intact, stik = 2.
///  4. Feed that .m4b back in as input — proves Windows' own MP4 parser accepts
///     what the muxer writes (the same parser the player uses).
///  5. Print where the iCloud container was (or wasn't) found.
/// </summary>
public static class SelfTest
{
    public static bool Run(TextWriter log)
    {
        var dir = Path.Combine(Path.GetTempPath(), "casebound-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        bool ok = true;
        void Pass(string m) => log.WriteLine("  PASS  " + m);
        void Fail(string m) { log.WriteLine("  FAIL  " + m); ok = false; }

        log.WriteLine($"Casebound self-test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.WriteLine($"  OS {Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        try
        {
            // 1
            using (var enc = new AacEncoder(44100, 2, 96)) { }
            Pass("AAC encoder accepted 44.1 kHz stereo 96 kbps");

            // 2
            var specs = new (double freq, double sec, int rate, int ch, string title)[]
            {
                (440, 1.5, 44100, 2, "Opening — Prélude"),
                (660, 2.5, 44100, 2, "Chapter 1"),
                (880, 3.5, 48000, 1, "終わり 🎧"),
            };
            var parts = new List<ConversionPart>();
            for (int i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                var wav = Path.Combine(dir, $"{i + 1:00} tone.wav");
                ToneGenerator.WriteWav(wav, s.freq, s.sec, s.rate, s.ch);
                parts.Add(new ConversionPart(new FileAudioPart(wav, s.rate, s.ch), s.title, TimeSpan.FromSeconds(s.sec)));
            }
            var meta = new BookMetadata { Title = "Self-Test Böok", Author = "Casebound", Narrator = "Tone Generator", Cover = TinyPng() };
            var output = Path.Combine(dir, "selftest.m4b");
            var result = M4BBuilder.Build(parts, meta, output, new ConversionOptions { WorkFolder = dir });
            Pass($"converted 3 WAVs → {new FileInfo(output).Length:N0} bytes in {result.Elapsed.TotalSeconds:0.00}s ({result.SampleRate} Hz, {result.Channels} ch)");

            // 3
            var info = AudiobookInspector.Inspect(output);
            if (info.Chapters.Count == 3) Pass($"3 chapters read back (from {info.ChapterSource})");
            else Fail($"expected 3 chapters, read {info.Chapters.Count}");
            double[] expectedStarts = { 0, 1.5, 4.0 };
            for (int i = 0; i < Math.Min(3, info.Chapters.Count); i++)
            {
                double got = info.Chapters[i].Start.TotalSeconds;
                if (Math.Abs(got - expectedStarts[i]) <= 0.05) Pass($"chapter {i + 1} starts at {got:0.000}s");
                else Fail($"chapter {i + 1} starts at {got:0.000}s, expected {expectedStarts[i]:0.000}s");
                if (info.Chapters[i].Title == specs[i].title) Pass($"chapter {i + 1} title intact: {info.Chapters[i].Title}");
                else Fail($"chapter {i + 1} title \"{info.Chapters[i].Title}\" ≠ \"{specs[i].title}\"");
            }
            if (Math.Abs(info.Duration.TotalSeconds - 7.5) <= 0.1) Pass($"duration {info.Duration.TotalSeconds:0.000}s");
            else Fail($"duration {info.Duration.TotalSeconds:0.000}s, expected 7.5s");
            if (info.MediaKind == 2) Pass("stik = 2 (audiobook)"); else Fail($"stik = {info.MediaKind?.ToString() ?? "missing"}");
            if (info.Title == meta.Title && info.Author == meta.Author && info.Narrator == meta.Narrator) Pass("title / author / narrator present");
            else Fail($"metadata read back as {info.Title} / {info.Author} / {info.Narrator}");
            if (info.Cover is { Length: > 0 }) Pass("cover art present"); else Fail("cover art missing");

            // 4
            var round = Path.Combine(dir, "roundtrip.m4b");
            var again = M4BBuilder.Build(
                new[] { new ConversionPart(new FileAudioPart(output), "Whole book", info.Duration) },
                meta with { Title = "Round Trip" }, round, new ConversionOptions { WorkFolder = dir });
            if (Math.Abs(again.Verified.Duration.TotalSeconds - info.Duration.TotalSeconds) <= 0.2)
                Pass($"decoded its own .m4b back in ({again.Verified.Duration.TotalSeconds:0.000}s)");
            else Fail($"round trip duration {again.Verified.Duration.TotalSeconds:0.000}s vs {info.Duration.TotalSeconds:0.000}s");

            // 5. positions sync: two "devices" sharing one library folder; newest wins
            var lib = Path.Combine(dir, "Audiobooks");
            Directory.CreateDirectory(lib);
            var book = Path.Combine(lib, "Some Book.m4b");
            File.WriteAllBytes(book, new byte[1234]);
            var phone = PlaybackPositions.CreateForTest(Path.Combine(dir, "phone.json"), "iPhone");
            var pc = PlaybackPositions.CreateForTest(Path.Combine(dir, "pc.json"), "PC");
            phone.SyncFolder = lib; pc.SyncFolder = lib;
            pc.Set(book, TimeSpan.FromSeconds(100)); pc.Save(flushSync: true);
            Thread.Sleep(1100);                                   // timestamps are whole seconds
            phone.Set(book, TimeSpan.FromSeconds(4321.5)); phone.Save(flushSync: true);
            pc.RefreshRemote(force: true);
            var merged = pc.Get(book);
            if (merged != null && Math.Abs(merged.Seconds - 4321.5) < 0.01) Pass("positions sync: newer position from another device wins");
            else Fail($"positions sync: got {merged?.Seconds.ToString() ?? "nothing"}, expected 4321.5");
            var files = Directory.GetFiles(Path.Combine(lib, PlaybackPositions.SyncFolderName), "positions-*.json");
            if (files.Length == 2) Pass($"positions sync: one file per device ({string.Join(", ", files.Select(f => Path.GetFileName(f)))})");
            else Fail($"positions sync: expected 2 device files, found {files.Length}");
            if (PlaybackPositions.KeyFor("Cafe\u0301 Book.M4B", 1) != PlaybackPositions.KeyFor("caf\u00e9 book.m4b", 1) || PlaybackPositions.KeyFor(book) != "some book.m4b|1234")
                Fail($"positions key rule: got {PlaybackPositions.KeyFor(book)}");
            else Pass("positions key: NFC, lower-cased file name | size");
        }
        catch (Exception e)
        {
            Fail(e.GetType().Name + ": " + e.Message);
            log.WriteLine(e.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        // 5
        var containers = CloudLibrary.DetectICloudContainers();
        foreach (var r in CloudLibrary.ICloudDriveRoots().Where(r => r.Exists))
            log.WriteLine($"  info  iCloud Drive: {r.Path} ({r.Source})");
        if (OperatingSystem.IsWindows() && !CloudLibrary.ICloudDriveInstalled) log.WriteLine("  info  iCloud Drive folder not found — iCloud for Windows not installed or not signed in");
        else if (OperatingSystem.IsWindows() && containers.Count == 0) log.WriteLine($"  info  iCloud Drive found, but no {CloudLibrary.ContainerIdentifier} container folder in it yet");
        foreach (var c in containers) log.WriteLine("  info  iCloud container: " + c);
        log.WriteLine("  info  local library: " + AppPaths.LocalLibrary);

        log.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FAILED");
        return ok;
    }

    /// <summary>Scale test: many synthetic parts (no files), the shape that hung the Mac's composition path.</summary>
    public static bool RunScale(TextWriter log, int count, double secondsEach)
    {
        var dir = Path.Combine(Path.GetTempPath(), "casebound-scaletest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var parts = Enumerable.Range(0, count)
                .Select(i => new ConversionPart(new TonePart(220 + (i % 12) * 40, secondsEach), $"Chapter {i + 1}", TimeSpan.FromSeconds(secondsEach)))
                .ToList();
            var output = Path.Combine(dir, "scale.m4b");
            var clock = Stopwatch.StartNew();
            var lastLine = TimeSpan.Zero;
            var progress = new SyncProgress<ConversionProgress>(p =>
            {
                if (clock.Elapsed - lastLine < TimeSpan.FromSeconds(5)) return;
                lastLine = clock.Elapsed;
                log.WriteLine($"  … part {p.PartIndex + 1}/{p.PartCount}, {p.Fraction:P0}");
            });
            var result = M4BBuilder.Build(parts, new BookMetadata { Title = "Scale Test" }, output,
                new ConversionOptions { WorkFolder = dir }, progress);

            var info = result.Verified;
            var expected = TimeSpan.FromSeconds(count * secondsEach);
            log.WriteLine($"  {count} parts, {info.Duration:hh\\:mm\\:ss}, {info.Chapters.Count} chapters read back, " +
                          $"{new FileInfo(output).Length / 1048576.0:0.0} MB — {result.Elapsed.TotalSeconds:0}s, ~{result.SpeedFactor:0}x realtime");
            bool ok = info.Chapters.Count == count && Math.Abs((info.Duration - expected).TotalSeconds) < 1;
            log.WriteLine(ok ? "SCALETEST OK" : "SCALETEST FAILED");
            return ok;
        }
        catch (Exception e)
        {
            log.WriteLine("SCALETEST FAILED: " + e);
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // 1×1 amber PNG
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGO42SUHAAPBAYJzPGQAAAAAAElFTkSuQmCC");
}

/// <summary>IProgress that invokes synchronously on the reporting thread (Progress&lt;T&gt; posts to the thread pool from a console).</summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
