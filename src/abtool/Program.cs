using Casebound.Kit;

// abtool — headless Casebound: convert, inspect, selftest, scaletest, locations.
// Exit code 0 = success, 1 = failure, 2 = usage.

Console.OutputEncoding = System.Text.Encoding.UTF8;

// --data <folder>: read settings/positions from that data folder. Without it, `locations` and
// `positions` look at the installed Store (MSIX) app's LocalState if there is one, since abtool
// itself runs unpackaged and would otherwise see only %LOCALAPPDATA%\Casebound.
string? dataArg = null;
{
    int d = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
    if (d >= 0)
    {
        if (d + 1 >= args.Length) return Usage();
        dataArg = args[d + 1];
        args = args.Where((_, i) => i != d && i != d + 1).ToArray();
    }
}
if (args.Length == 0) return Usage();

void UseAppDataFolder()
{
    var packaged = AppPaths.PackagedDataRoots();
    if (dataArg != null) AppPaths.UseDataRoot(Path.GetFullPath(dataArg));
    else if (packaged.Count > 0) AppPaths.UseDataRoot(packaged[0]);
    string source = dataArg != null ? "--data" : packaged.Count > 0 ? "installed Store/MSIX app" : "unpackaged";
    Console.WriteLine($"Data folder:  {AppPaths.Root}  ({source})");
    foreach (var other in packaged.Where(p => !string.Equals(p, AppPaths.Root, StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"Also found:   {other}  (use --data to pick it)");
    if (packaged.Count > 0 || dataArg != null) Console.WriteLine($"Unpackaged:   {AppPaths.UnpackagedRoot}");
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "selftest":
            return SelfTest.Run(Console.Out) ? 0 : 1;

        case "scaletest":
        {
            int count = args.Length > 1 ? int.Parse(args[1]) : 95;
            double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 580;
            Console.WriteLine($"Scale test: {count} parts × {seconds:0.#} s");
            return SelfTest.RunScale(Console.Out, count, seconds) ? 0 : 1;
        }

        case "inspect":
        {
            if (args.Length < 2) return Usage();
            var info = AudiobookInspector.Inspect(args[1]);
            Console.WriteLine($"File:      {info.Path}");
            Console.WriteLine($"Title:     {info.Title}");
            Console.WriteLine($"Author:    {info.Author}");
            Console.WriteLine($"Narrator:  {info.Narrator}");
            Console.WriteLine($"Duration:  {Fmt(info.Duration)}");
            Console.WriteLine($"Audio:     {info.SampleRate} Hz, {info.Channels} ch");
            Console.WriteLine($"stik:      {info.MediaKind?.ToString() ?? "-"}");
            Console.WriteLine($"Cover:     {(info.Cover is { Length: > 0 } c ? $"{c.Length:N0} bytes" : "none")}");
            Console.WriteLine($"Chapters:  {info.Chapters.Count} (from {info.ChapterSource})");
            foreach (var ch in info.Chapters)
                Console.WriteLine($"  {ch.Index + 1,3}  {Fmt(ch.Start)}  {ch.Title}");
            return 0;
        }

        case "locations":
        {
            UseAppDataFolder();
            Console.WriteLine($"Container id: {CloudLibrary.ContainerIdentifier}");
            var settings = AppSettings.Load();
            foreach (var r in CloudLibrary.ICloudDriveRoots(settings))
                Console.WriteLine($"iCloud Drive root {(r.Exists ? "found  " : "missing")}: {r.Path}  ({r.Source})");
            if (settings.ICloudFolder != null) Console.WriteLine($"Chosen iCloud folder: {settings.ICloudFolder}");
            var containers = CloudLibrary.DetectICloudContainers(settings);
            Console.WriteLine(containers.Count == 0 ? "Casebound container: not found" : $"Casebound container: {containers[0]}");
            foreach (var r in CloudLibrary.ICloudDriveRoots(settings).Where(r => r.Exists))
                foreach (var d in Directory.EnumerateDirectories(r.Path).Take(40))
                    Console.WriteLine($"   {r.Path} contains: {Path.GetFileName(d)}");
            foreach (var f in CloudLibrary.Folders(settings))
                Console.WriteLine($"{f.Kind,-10} {f.Path}");
            return 0;
        }

        case "positions":
        {
            UseAppDataFolder();
            var cfg = AppSettings.Load();
            var positions = PlaybackPositions.Load(cfg);
            positions.SyncFolder = CloudLibrary.SharedLibraryFolder(cfg);
            positions.RefreshRemote(force: true);
            Console.WriteLine($"This device's file: {positions.DeviceFileName}");
            Console.WriteLine($"Sync folder:        {(positions.SyncFolder is { } sf ? Path.Combine(sf, PlaybackPositions.SyncFolderName) : "none (local only)")}");
            foreach (var f in positions.RemoteFiles()) Console.WriteLine($"Other device:       {f}");
            if (positions.SyncDirectory is { } sd && Directory.Exists(sd))
            {
                Console.WriteLine($"Folder attributes:  {File.GetAttributes(sd)}");
                Console.WriteLine($"Hide .casebound:    {PlaybackPositions.EnsureHiddenFolder(sd)}");
                Thread.Sleep(3000);
                Console.WriteLine($"3 s later:          {File.GetAttributes(sd)}");
            }
            foreach (var e in CloudLibrary.Scan(CloudLibrary.Folders(cfg)))
            {
                var p = e.IsCloudOnly ? null : positions.Get(e.Path);
                var where = p == null ? "not started" : p.Finished ? "finished" : $"{Fmt(TimeSpan.FromSeconds(p.Seconds))} (saved {p.UpdatedUtc.ToLocalTime():g})";
                Console.WriteLine($"  {e.FileName,-50} {where}");
                Console.WriteLine($"      key: {PlaybackPositions.KeyFor(e.FileName, e.Size)}");
            }
            return 0;
        }

        case "convert":
            return RunConvert(args.Skip(1).ToArray());

        default:
            return Usage();
    }
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

static int RunConvert(string[] a)
{
    string? output = null, title = null, author = null, narrator = null, coverPath = null, titles = "smart";
    int bitrate = 96;
    var inputs = new List<string>();
    for (int i = 0; i < a.Length; i++)
    {
        string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value");
        switch (a[i])
        {
            case "-o": case "--output": output = Next(); break;
            case "--title": title = Next(); break;
            case "--author": author = Next(); break;
            case "--narrator": narrator = Next(); break;
            case "--cover": coverPath = Next(); break;
            case "--bitrate": bitrate = int.Parse(Next()); break;
            case "--titles": titles = Next(); break;
            default:
                if (Directory.Exists(a[i]))
                    inputs.AddRange(Directory.EnumerateFiles(a[i]).Where(MetadataReader.IsAudioFile));
                else inputs.Add(a[i]);
                break;
        }
    }
    if (inputs.Count == 0) return Usage();

    var parts = MetadataReader.NaturalOrder(inputs.Select(MetadataReader.ReadPart));
    var guess = MetadataReader.GuessBook(parts);
    var book = guess with
    {
        Title = title ?? guess.Title,
        Author = author ?? guess.Author,
        Narrator = narrator ?? guess.Narrator,
        Cover = coverPath != null ? File.ReadAllBytes(coverPath) : guess.Cover
    };
    var chapterTitles = titles switch
    {
        "filenames" => MetadataReader.FilenameTitles(parts, book.Title),
        "numbered" => ChapterNaming.Apply(parts.Count, new ChapterNamingScheme()),
        _ => MetadataReader.SmartChapterTitles(parts, book.Title)
    };
    output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(parts[0].Path))!, Sanitize(book.Title) + ".m4b");

    Console.WriteLine($"{book.Title} — {book.Author ?? "unknown author"}: {parts.Count} parts → {output}");
    var conversion = parts.Select((p, i) => new ConversionPart(new FileAudioPart(p.Path, p.SampleRate, p.Channels), chapterTitles[i], p.Duration)).ToList();
    var cancel = new ConversionCancellation();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); Console.Error.WriteLine("cancelling…"); };
    int lastPct = -1;
    var progress = new SyncProgress<ConversionProgress>(p =>
    {
        int pct = (int)(p.Fraction * 100);
        if (pct == lastPct) return;
        lastPct = pct;
        Console.Write($"\r  {pct,3}%  part {p.PartIndex + 1}/{p.PartCount}  {p.PartName,-50}");
    });
    var result = M4BBuilder.Build(conversion, book, output, new ConversionOptions { BitrateKbps = bitrate }, progress, cancel);
    Console.WriteLine();
    Console.WriteLine($"Done: {Fmt(result.Verified.Duration)}, {result.Verified.Chapters.Count} chapters verified, " +
                      $"{new FileInfo(output).Length / 1048576.0:0.0} MB in {result.Elapsed.TotalSeconds:0}s (~{result.SpeedFactor:0}x realtime)");
    return 0;
}

static string Sanitize(string s)
{
    foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
    return s.Trim().TrimEnd('.');
}

static string Fmt(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

static int Usage()
{
    Console.Error.WriteLine("""
        abtool — Casebound, headless

          abtool convert <files or folders…> [-o out.m4b] [--title T] [--author A] [--narrator N]
                         [--cover image] [--bitrate 96|128|160|192] [--titles smart|filenames|numbered]
          abtool inspect <book.m4b>
          abtool selftest
          abtool scaletest [count=95] [seconds=580]
          abtool locations [--data <folder>]
          abtool positions [--data <folder>]

        locations/positions read the installed Store (MSIX) app's data folder if there is one,
        else %LOCALAPPDATA%\Casebound; --data picks another.
        """);
    return 2;
}
