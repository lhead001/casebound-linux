using Avalonia.Platform.Storage;

namespace Casebound.Models;

/// <summary>File pickers via Avalonia's StorageProvider (xdg-desktop-portal when available, so they work inside Flatpak).</summary>
public static class Pickers
{
    private static IStorageProvider Sp => App.Window!.StorageProvider;

    private static async Task<IStorageFolder?> Start(string? path)
        => path != null && Directory.Exists(path) ? await Sp.TryGetFolderFromPathAsync(path) : null;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static async Task<IReadOnlyList<string>> OpenFilesAsync(IEnumerable<string> extensions)
    {
        var files = await Sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add audio files", AllowMultiple = true, SuggestedStartLocation = await Start(Home),
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = extensions.Select(e => "*" + e).ToList() }, FilePickerFileTypes.All }
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> OpenImageAsync()
    {
        var files = await Sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose cover", FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> FolderAsync(string title)
    {
        var f = await Sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = await Start(Home) });
        return f.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> SaveM4bAsync(string suggestedName)
    {
        var f = await Sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save audiobook", SuggestedFileName = suggestedName + ".m4b", DefaultExtension = "m4b",
            FileTypeChoices = new[] { new FilePickerFileType("Audiobook") { Patterns = new[] { "*.m4b" } } }
        });
        return f?.TryGetLocalPath();
    }
}
