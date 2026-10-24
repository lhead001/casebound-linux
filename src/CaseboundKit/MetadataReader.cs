using System.Text.RegularExpressions;

namespace Casebound.Kit;

/// <summary>Book-level metadata written into the M4B.</summary>
public sealed record BookMetadata
{
    public string Title { get; init; } = "Untitled";
    public string? Author { get; init; }
    public string? Narrator { get; init; }
    public string? Year { get; init; }
    public string? Description { get; init; }
    public byte[]? Cover { get; init; }
}

/// <summary>What we know about one input file before converting it.</summary>
public sealed record PartInfo(
    string Path,
    TimeSpan Duration,
    string? Title,
    int? Track,
    int? Disc,
    string? Album,
    string? Artist,
    string? AlbumArtist,
    string? Composer,
    string? Year,
    int SampleRate,
    int Channels,
    bool HasPicture)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Duration / title / track number, book metadata, natural ordering, smart
/// chapter titles and filename titles — the same responsibilities as the Mac's
/// MetadataReader, backed by TagLib# instead of AVAsset metadata.
/// </summary>
public static partial class MetadataReader
{
    public static readonly string[] AudioExtensions =
        { ".mp3", ".m4a", ".m4b", ".mp4", ".aac", ".wav", ".flac", ".wma", ".ogg", ".opus", ".aiff", ".aif" };

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    public static PartInfo ReadPart(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            var tag = f.Tag;
            return new PartInfo(
                path,
                f.Properties?.Duration ?? TimeSpan.Zero,
                Clean(tag.Title),
                tag.Track > 0 ? (int)tag.Track : null,
                tag.Disc > 0 ? (int)tag.Disc : null,
                Clean(tag.Album),
                Clean(tag.FirstPerformer),
                Clean(tag.FirstAlbumArtist),
                Clean(tag.FirstComposer),
                tag.Year > 0 ? tag.Year.ToString() : null,
                f.Properties?.AudioSampleRate ?? 0,
                f.Properties?.AudioChannels ?? 0,
                tag.Pictures is { Length: > 0 });
        }
        catch (Exception)
        {
            // Unreadable tags are not fatal — Media Foundation may still decode it.
            return new PartInfo(path, TimeSpan.Zero, null, null, null, null, null, null, null, null, 0, 0, false);
        }
    }

    public static (int rate, int channels) ProbeFormat(string path)
    {
        var p = ReadPart(path);
        return (p.SampleRate > 0 ? p.SampleRate : 44100, p.Channels > 0 ? p.Channels : 2);
    }

    /// <summary>First embedded picture, or a cover.jpg/folder.jpg beside the files.</summary>
    public static byte[]? ReadCover(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            var pic = f.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? f.Tag.Pictures.FirstOrDefault();
            if (pic?.Data?.Data is { Length: > 0 } bytes) return bytes;
        }
        catch (Exception) { }
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir == null) return null;
        foreach (var name in new[] { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png", "front.jpg" })
        {
            var candidate = System.IO.Path.Combine(dir, name);
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
        }
        return null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------- ordering ----------

    /// <summary>
    /// Disc/track order when every file has a unique track number; otherwise
    /// natural filename order ("Part 2" before "Part 10").
    /// </summary>
    public static List<PartInfo> NaturalOrder(IEnumerable<PartInfo> parts)
    {
        var list = parts.ToList();
        bool tracksUsable = list.Count > 1
            && list.All(p => p.Track.HasValue)
            && list.Select(p => (p.Disc ?? 1, p.Track!.Value)).Distinct().Count() == list.Count;
        if (tracksUsable)
            return list.OrderBy(p => p.Disc ?? 1).ThenBy(p => p.Track).ThenBy(p => p.FileName, NaturalComparer.Instance).ToList();
        return list.OrderBy(p => p.Path, NaturalComparer.Instance).ToList();
    }

    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        public int Compare(string? a, string? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a[si..i].TrimStart('0');
                    var nb = b[sj..j].TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    // ---------- book guess ----------

    public static BookMetadata GuessBook(IReadOnlyList<PartInfo> parts)
    {
        string? MostCommon(Func<PartInfo, string?> f) => parts.Select(f).Where(s => s != null)
            .GroupBy(s => s!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        // Albums split per disc ("… CD 1", "… Disc 2") collapse to the book title.
        var title = MostCommon(p => p.Album == null ? null : CleanBookTitle(p.Album));
        if (title == null && parts.Count > 0)
        {
            // Fall back to the folder name, which is usually the book title.
            var dir = System.IO.Path.GetDirectoryName(parts[0].Path);
            title = dir != null ? System.IO.Path.GetFileName(dir) : null;
        }
        var author = MostCommon(p => p.AlbumArtist) ?? MostCommon(p => p.Artist);
        var narrator = MostCommon(p => p.Composer);
        // "J.K. Rowling (read by Stephen Fry)" → author + narrator
        if (author != null && ReadBy().Match(author) is { Success: true } m)
        {
            author = m.Groups[1].Value.Trim().TrimEnd(',', ';', '-').Trim();
            narrator ??= m.Groups[2].Value.Trim();
        }
        if (narrator != null && narrator == author) narrator = null;
        byte[]? cover = null;
        foreach (var p in parts.Where(p => p.HasPicture).Take(3))
            if ((cover = ReadCover(p.Path)) != null) break;
        cover ??= parts.Count > 0 ? ReadCover(parts[0].Path) : null;
        return new BookMetadata
        {
            Title = title ?? "Untitled",
            Author = author,
            Narrator = narrator,
            Year = MostCommon(p => p.Year),
            Cover = cover
        };
    }

    // ---------- chapter titles ----------

    /// <summary>
    /// Tag titles when they're real (present, distinct, not just the book title or
    /// "Track 07"); otherwise titles from filenames; otherwise "Chapter n".
    /// </summary>
    public static List<string> SmartChapterTitles(IReadOnlyList<PartInfo> parts, string? bookTitle = null)
    {
        int n = parts.Count;
        if (n == 0) return new List<string>();
        // PROVISIONAL (Linux, unverified vs Mac): per file, prefer its title tag when present, else its file name.
        var fromTags = parts.Select(p => p.Title == null ? null : StripNoise(p.Title, bookTitle ?? p.Album)).ToList();
        var fromFiles = StripCommonSuffix(FilenameTitles(parts, bookTitle));
        var titles = fromTags.Select((t, i) => string.IsNullOrWhiteSpace(t) ? fromFiles[i] : t!.Trim()).ToList();
        if (titles.All(t => !string.IsNullOrWhiteSpace(t))) return titles;

        return Enumerable.Range(1, n).Select(i => $"Chapter {i}").ToList();
    }

    /// <summary>Titles derived from filenames: common prefix/suffix stripped, leading numbering removed.</summary>
    public static List<string> FilenameTitles(IReadOnlyList<PartInfo> parts, string? bookTitle = null)
    {
        var names = parts.Select(p => System.IO.Path.GetFileNameWithoutExtension(p.Path).Replace('_', ' ').Trim()).ToList();
        if (names.Count > 1)
        {
            // Strip a common prefix only at a word boundary, and never all of a name.
            int prefix = CommonPrefix(names);
            while (prefix > 0 && names.Any(s => s.Length > prefix && char.IsLetterOrDigit(s[prefix - 1]) && char.IsLetterOrDigit(s[prefix]))) prefix--;
            // Keep digits that belong to the numbering: back the prefix up to the start of a digit run.
            while (prefix > 0 && names.Any(s => prefix < s.Length && char.IsDigit(s[prefix]) && char.IsDigit(s[prefix - 1]))) prefix--;
            if (names.All(s => s.Length > prefix)) names = names.Select(s => s[prefix..]).ToList();
        }
        return names.Select((s, i) =>
        {
            var t = StripNoise(s, bookTitle);
            return string.IsNullOrWhiteSpace(t) ? $"Chapter {i + 1}" : t;
        }).ToList();
    }

    public static string CleanBookTitle(string album)
    {
        var t = DiscSuffix().Replace(album.Trim(), "").Trim();
        return t.Length > 0 ? t : album.Trim();
    }

    /// <summary>
    /// Drops a trailing word every title shares ("Aragog 01", "Fudge 01" → "Aragog", "Fudge"),
    /// the leftover of per-disc track numbering in tags.
    /// </summary>
    public static List<string> StripCommonSuffix(List<string> titles)
    {
        if (titles.Count < 2) return titles;
        var result = titles;
        while (true)
        {
            var lastWords = result.Select(t => { int i = t.LastIndexOf(' '); return i > 0 ? t[(i + 1)..] : null; }).ToList();
            var w = lastWords[0];
            if (w == null || lastWords.Any(x => !string.Equals(x, w, StringComparison.OrdinalIgnoreCase))) return result;
            var trimmed = result.Select(t => t[..t.LastIndexOf(' ')].TrimEnd(' ', '-', '_', '.', '–', '—', ':')).ToList();
            if (trimmed.Any(string.IsNullOrWhiteSpace) || trimmed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != titles.Count) return result;
            result = trimmed;
        }
    }

    private static int CommonPrefix(List<string> names)
    {
        int len = names.Min(s => s.Length);
        for (int i = 0; i < len; i++)
            if (names.Any(s => char.ToUpperInvariant(s[i]) != char.ToUpperInvariant(names[0][i]))) return i;
        return len;
    }

    private static string StripNoise(string s, string? bookTitle)
    {
        var t = s.Trim();
        if (!string.IsNullOrWhiteSpace(bookTitle) && t.StartsWith(bookTitle, StringComparison.OrdinalIgnoreCase) && t.Length > bookTitle.Length)
            t = t[bookTitle.Length..];
        t = LeadingNumbering().Replace(t, "");
        t = t.Trim(' ', '-', '_', '.', '–', '—', ':');
        return t;
    }

    private static bool IsGeneric(string? t) =>
        t == null || GenericTitle().IsMatch(t);

    // "01 - ", "01. ", "1-03 ", "(01) ", "[02] "
    [GeneratedRegex(@"^\s*[\(\[]?\d{1,4}(?:[-.]\d{1,4})?[\)\]]?(?:\s*[-–—._:)]\s*|\s+)(?=\S)")]
    private static partial Regex LeadingNumbering();

    // "… CD", "… CD 1", "… (Disc 2)", "… - Disk 3 of 8"
    [GeneratedRegex(@"[\s\-–—,:]*[\(\[]?\s*\b(?:cd\s*\d*|(?:disc|disk)\s*\d+)(?:\s*(?:of|/)\s*\d+)?\s*[\)\]]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscSuffix();

    // "Author (read by Narrator)", "Author, narrated by Narrator"
    [GeneratedRegex(@"^(.+?)\s*(?:[\(\[]|,|-)\s*(?:read|narrated|performed)\s+by\s+(.+?)\s*[\)\]]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadBy();

    [GeneratedRegex(@"^(?:track|part|disc|cd|file|chapter)?\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTitle();
}
