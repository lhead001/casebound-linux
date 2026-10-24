using System.Diagnostics;
using Casebound.Kit;
using Xunit;

public class RemotePolicyTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    [Fact] public void OlderRemoteIgnored() => Assert.Equal(RemoteDecision.Ignore, RemotePositionPolicy.Decide(100, T0, 500, T0.AddSeconds(-1), false));
    [Fact] public void TinyDifferenceIgnored() => Assert.Equal(RemoteDecision.Ignore, RemotePositionPolicy.Decide(100, T0, 101, T0.AddMinutes(1), false));
    [Fact] public void PausedAheadJumps() => Assert.Equal(RemoteDecision.Jump, RemotePositionPolicy.Decide(100, T0, 900, T0.AddMinutes(1), false));
    [Fact] public void PausedSmallRewindJumps() => Assert.Equal(RemoteDecision.Jump, RemotePositionPolicy.Decide(100, T0, 40, T0.AddMinutes(1), false));
    [Fact] public void PausedBigRewindOffers() => Assert.Equal(RemoteDecision.Offer, RemotePositionPolicy.Decide(100, T0, 39, T0.AddMinutes(1), false));
    [Fact] public void PlayingOffers() => Assert.Equal(RemoteDecision.Offer, RemotePositionPolicy.Decide(100, T0, 900, T0.AddMinutes(1), true));
}

public class ListingTests
{
    static LibraryEntry E(string p) => new(p, LibraryFolderKind.Local, 1, DateTime.UtcNow, false);
    [Fact] public void SplitsHidden()
    {
        var (v, r) = LibraryListing.Split(new[] { E("/a.m4b"), E("/b.m4b"), E("/c.m4b") }, new[] { "/b.m4b" });
        Assert.Equal(new[] { "/a.m4b", "/c.m4b" }, v.Select(x => x.Path));
        Assert.Equal("/b.m4b", Assert.Single(r).Path);
    }
    [Fact] public void ContinueListeningOrderAndFilter()
    {
        var t = DateTime.UtcNow;
        var pos = new Dictionary<string, PlaybackPositions.Entry>
        {
            ["a"] = new() { Seconds = 10, UpdatedUtc = t.AddHours(-2) },
            ["b"] = new() { Seconds = 10, UpdatedUtc = t },
            ["c"] = new() { Seconds = 10, UpdatedUtc = t, Finished = true },
            ["d"] = new() { Seconds = 0, UpdatedUtc = t },
        };
        var r = LibraryListing.ContinueListening(new[] { "a", "b", "c", "d", "e" }, k => pos.GetValueOrDefault(k));
        Assert.Equal(new[] { "b", "a" }, r);
    }
}

public class EncoderTests
{
    [Theory, InlineData(44100, 2, 0x12, 0x10), InlineData(48000, 1, 0x11, 0x88), InlineData(22050, 2, 0x13, 0x90)]
    public void AscBytes(int rate, int ch, byte b0, byte b1) => Assert.Equal(new[] { b0, b1 }, AacEncoder.BuildAsc(rate, ch));

    [Fact] public void EndToEndM4b()
    {
        var dir = Directory.CreateTempSubdirectory("cbtest").FullName;
        for (int i = 1; i <= 3; i++) Run("ffmpeg", $"-loglevel error -f lavfi -i sine=frequency={200 * i}:duration=5 -metadata title=\"Ch {i}\" -metadata track={i} {dir}/{i}.mp3");
        var abtool = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/abtool"));
        var outp = Path.Combine(dir, "out.m4b");
        Run("dotnet", $"run --project {abtool} -c Release -- convert {dir} -o {outp} --title \"Test Book\" --author \"Ann Author\" --narrator \"Nat\" --bitrate 64");
        var probe = Run("ffprobe", $"-v error -show_chapters -show_entries format_tags=title,artist,composer:stream=codec_name -of compact {outp}");
        Assert.Equal(3, probe.Split('\n').Count(l => l.StartsWith("chapter|")));
        Assert.Contains("codec_name=aac", probe);
        Assert.Contains("tag:title=Test Book", probe);
        Assert.Contains("tag:artist=Ann Author", probe);
        Assert.Contains("tag:composer=Nat", probe);
    }

    static string Run(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{exe} failed: {e}");
        return o.Result;
    }
}
