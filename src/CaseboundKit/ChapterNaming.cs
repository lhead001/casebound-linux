namespace Casebound.Kit;

public enum ChapterNumberStyle { Digits, Words }

/// <summary>Front matter → numbered run → back matter.</summary>
public sealed record ChapterNamingScheme
{
    public IReadOnlyList<string> FrontMatter { get; init; } = Array.Empty<string>();
    public string Pattern { get; init; } = "Chapter {n}";
    public int StartNumber { get; init; } = 1;
    public ChapterNumberStyle Style { get; init; } = ChapterNumberStyle.Digits;
    public IReadOnlyList<string> BackMatter { get; init; } = Array.Empty<string>();
}

public static class ChapterNaming
{
    /// <summary>
    /// Names <paramref name="count"/> chapters. The first parts take the front-matter
    /// names ("Opening Credits", "Prologue"), the last take the back-matter names
    /// ("Epilogue", "End Credits"), and everything between is the numbered run.
    /// If front + back exceed the count, front matter wins.
    /// </summary>
    public static List<string> Apply(int count, ChapterNamingScheme scheme)
    {
        var front = scheme.FrontMatter.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var back = scheme.BackMatter.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        int f = Math.Min(front.Count, count);
        int b = Math.Min(back.Count, count - f);
        var result = new List<string>(count);
        for (int i = 0; i < f; i++) result.Add(front[i]);
        int run = count - f - b;
        for (int i = 0; i < run; i++) result.Add(Format(scheme.Pattern, scheme.StartNumber + i, scheme.Style));
        for (int i = back.Count - b; i < back.Count; i++) result.Add(back[i]);
        return result;
    }

    public static string Format(string pattern, int n, ChapterNumberStyle style)
    {
        var number = style == ChapterNumberStyle.Words ? ToWords(n) : n.ToString();
        if (string.IsNullOrWhiteSpace(pattern)) return number;
        return pattern.Contains("{n}") ? pattern.Replace("{n}", number) : $"{pattern.TrimEnd()} {number}";
    }

    private static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    };
    private static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    public static string ToWords(int n)
    {
        if (n < 0) return "Minus " + ToWords(-n);
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 == 0 ? "" : "-" + Ones[n % 10]);
        if (n < 1000) return Ones[n / 100] + " Hundred" + (n % 100 == 0 ? "" : " " + ToWords(n % 100));
        return n.ToString();
    }
}
