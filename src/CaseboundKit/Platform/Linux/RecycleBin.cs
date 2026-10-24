namespace Casebound.Kit;

/// <summary>Deletes by moving to the freedesktop.org Trash (~/.local/share/Trash), never a hard delete.</summary>
public static class RecycleBin
{
    public static void Send(string path)
    {
        if (!File.Exists(path)) return;
        var full = Path.GetFullPath(path);
        // Inside Flatpak XDG_DATA_HOME points into the sandbox (~/.var/app/...); use the host's Trash.
        bool flatpak = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"));
        var dataHome = Environment.GetEnvironmentVariable(flatpak ? "HOST_XDG_DATA_HOME" : "XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome)) dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var trash = Path.Combine(dataHome, "Trash");
        var files = Path.Combine(trash, "files"); var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files); Directory.CreateDirectory(info);
        var baseName = Path.GetFileName(full); var name = baseName; int n = 1;
        while (File.Exists(Path.Combine(files, name)) || File.Exists(Path.Combine(info, name + ".trashinfo")))
            name = $"{Path.GetFileNameWithoutExtension(baseName)}.{n++}{Path.GetExtension(baseName)}";
        File.WriteAllText(Path.Combine(info, name + ".trashinfo"),
            $"[Trash Info]\nPath={Uri.EscapeDataString(full).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        try { File.Move(full, Path.Combine(files, name)); }
        catch (Exception e)
        {
            try { File.Delete(Path.Combine(info, name + ".trashinfo")); } catch { }
            throw new IOException($"Couldn't move {baseName} to the Trash: {e.Message}", e);
        }
    }
}
