using Avalonia.Controls;
using Avalonia.Interactivity;
using Casebound.Kit;
using Casebound.Models;

namespace Casebound.Views;

public partial class ChapterNamingDialog : Window
{
    private readonly ConverterModel model;
    private List<string>? explicitTitles;
    private bool ready;
    public IReadOnlyList<string>? Result { get; private set; }

    public ChapterNamingDialog() : this(new ConverterModel()) { }

    public ChapterNamingDialog(ConverterModel model)
    {
        this.model = model;
        InitializeComponent();
        FrontBox.TextChanged += (_, _) => Changed();
        PatternBox.TextChanged += (_, _) => Changed();
        BackBox.TextChanged += (_, _) => Changed();
        StartBox.ValueChanged += (_, _) => Changed();
        StyleBox.SelectionChanged += (_, _) => Changed();
        ready = true;
        Refresh();
    }

    private List<string> Compute()
    {
        if (explicitTitles != null) return explicitTitles;
        static List<string> Lines(string? s) =>
            (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var scheme = new ChapterNamingScheme
        {
            FrontMatter = Lines(FrontBox.Text),
            Pattern = PatternBox.Text ?? "Chapter {n}",
            StartNumber = (int)(StartBox.Value ?? 1),
            Style = StyleBox.SelectedIndex == 1 ? ChapterNumberStyle.Words : ChapterNumberStyle.Digits,
            BackMatter = Lines(BackBox.Text)
        };
        return ChapterNaming.Apply(model.Parts.Count, scheme);
    }

    private void Refresh()
    {
        if (!ready) return;
        var titles = Compute();
        PreviewHeader.Text = $"Preview — {titles.Count} chapters";
        PreviewList.ItemsSource = titles.Select((t, i) => $"{i + 1}.  {t}").ToList();
    }

    private void Changed() { explicitTitles = null; Refresh(); }
    private void FromTags_Click(object? s, RoutedEventArgs e) { explicitTitles = model.TitlesFromTags(); Refresh(); }
    private void FromFiles_Click(object? s, RoutedEventArgs e) { explicitTitles = model.TitlesFromFilenames(); Refresh(); }
    private void Apply_Click(object? s, RoutedEventArgs e) { Result = Compute(); Close(); }
    private void Cancel_Click(object? s, RoutedEventArgs e) => Close();
}
