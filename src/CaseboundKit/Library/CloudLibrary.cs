using System.Runtime.InteropServices;
using System.Text.Json;

namespace Casebound.Kit;

/// <summary>
/// Where Casebound keeps its own files: settings, positions, logs, conversion work. Never a synced folder.
///
/// Unpackaged (dev builds, abtool): %LOCALAPPDATA%\Casebound.
/// Packaged (the Store MSIX): the app calls <see cref="UseDataRoot"/> with
/// ApplicationData.Current.LocalFolder (…\Packages\&lt;family name&gt;\LocalState) before anything reads
/// settings. A packaged app's writes under AppData are otherwise virtualized: new files land in
/// …\Packages\&lt;family&gt;\LocalCache\Local\Casebound while existing ones are edited in place (and on
/// 1809 everything is copy-on-write), so logs and settings would end up split across two places.
/// </summary>
public static class AppPaths
{
    private static string? dataRoot;

    /// <summary>%LOCALAPPDATA%\Casebound: the unpackaged location.</summary>
    public static string UnpackagedRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Casebound");

    /// <summary>The data folder in use (created on first use).</summary>
    public static string Root
    {
        get
        {
            var p = dataRoot ?? UnpackagedRoot;
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public static string WorkFolder => Path.Combine(Root, "work");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string PositionsFile => Path.Combine(Root, "positions.json");

    /// <summary>
    /// Switches the data folder. Call once at startup, before anything reads settings.
    /// With <paramref name="importUnpackaged"/>, a folder without settings yet takes the unpackaged
    /// install's preferences and local positions once, but not its DeviceId: each install is its own
    /// device in the sync folder, so two installs on one PC never write the same positions file.
    /// </summary>
    public static void UseDataRoot(string folder, bool importUnpackaged = false)
    {
        Directory.CreateDirectory(folder);
        if (importUnpackaged) ImportUnpackaged(folder);
        dataRoot = folder;
    }

    private static void ImportUnpackaged(string to)
    {
        var from = UnpackagedRoot;
        var settingsTo = Path.Combine(to, "settings.json");
        if (File.Exists(settingsTo) || string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var positionsFrom = Path.Combine(from, "positions.json");
            var positionsTo = Path.Combine(to, "positions.json");
            if (File.Exists(positionsFrom) && !File.Exists(positionsTo)) File.Copy(positionsFrom, positionsTo);
            var settingsFrom = Path.Combine(from, "settings.json");
            if (File.Exists(settingsFrom) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFrom)) is { } imported)
            {
                imported.DeviceId = null;   // a new id is generated on load: this install is a new device
                AppSettings.AtomicWrite(settingsTo, JsonSerializer.Serialize(imported, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception) { /* nothing to import, or unreadable: start fresh */ }
    }

    /// <summary>True when this process runs with MSIX package identity (the Store build).</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) return false;
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;   // 122 (buffer too small) = packaged
        }
        catch (Exception) { return false; }
    }

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    /// <summary>
    /// LocalState folders of installed Casebound packages (Store or sideloaded), newest settings first,
    /// so abtool, which runs unpackaged, can look at what the packaged app sees.
    /// </summary>
    public static List<string> PackagedDataRoots()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            return Directory.EnumerateDirectories(packages, "*Casebound*")
                .Select(d => Path.Combine(d, "LocalState"))
                .Where(d => File.Exists(Path.Combine(d, "settings.json")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "settings.json")))
                .ToList();
        }
        catch (Exception) { return new List<string>(); }
    }

    /// <summary>A local library folder that always exists: Music\Casebound.</summary>
    public static string LocalLibrary
    {
        get
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Casebound");
            Directory.CreateDirectory(p);
            return p;
        }
    }
}

/// <summary>
/// Finds and watches the folders books live in.
///
/// The iCloud Drive container is the one the Apple apps sync through. Its
/// identifier is iCloud.com.headbytes.bindery — deliberately never renamed (see
/// NOTES.md) — and iCloud for Windows exposes it under the iCloud Drive folder.
/// Depending on the iCloud for Windows version the container folder shows up by
/// its identifier (iCloud~com~headbytes~bindery) or by its display name, and that
/// display name may still be a pre-Casebound one (Apple caches it — the Mac
/// build's gotcha 20), so every known name is checked.
/// </summary>
public static class CloudLibrary
{
    public const string ContainerIdentifier = "iCloud.com.headbytes.bindery";
    public static readonly string[] BookExtensions = { ".m4b", ".m4a" };

    /// <summary>
    /// Where books live in iCloud Drive for every Casebound app (Windows, iOS, macOS):
    /// iCloud Drive/Documents/Audiobooks. New books are saved here; the first entry
    /// DetectICloudContainers returns is this folder unless the user picked another.
    /// </summary>
    public const string SharedLibraryRelativePath = @"Documents\Audiobooks";

    private static readonly string[] ContainerFolderNames =
        { "iCloud~com~headbytes~bindery", "Casebound", "Bindwave", "Bindery" };

    /// <summary>
    /// Where iCloud Drive actually is. iCloud for Windows (the Store version) registers
    /// its folder with Windows as a Cloud Files sync root, so the real location — even
    /// after "Change" in iCloud's settings moved it to another drive — is recorded under
    /// HKLM\...\Explorer\SyncRootManager\iCloudDrive!…\UserSyncRoots (value name = the
    /// user's SID, data = the folder). The default %USERPROFILE% locations come after.
    /// </summary>
    public static List<ICloudRoot> ICloudDriveRoots(AppSettings? settings = null)
    {
        var roots = new List<ICloudRoot>();
        foreach (var p in SyncRootManagerPaths()) roots.Add(new ICloudRoot(p, "registered sync root"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(new ICloudRoot(Path.Combine(home, "iCloudDrive"), "default location"));
        roots.Add(new ICloudRoot(Path.Combine(home, "iCloud Drive"), "default location (older iCloud)"));
        return roots.DistinctBy(r => r.Path.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> SyncRootManagerPaths()
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            using var manager = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (manager == null) return result;
            foreach (var name in manager.GetSubKeyNames())
            {
                // iCloud Drive's id starts "iCloudDrive"; skip iCloud Photos and other providers.
                if (!name.Contains("iCloud", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("Photo", StringComparison.OrdinalIgnoreCase)) continue;
                using var userRoots = manager.OpenSubKey(name + @"\UserSyncRoots");
                if (userRoots == null) continue;
                var values = userRoots.GetValueNames();
                // Prefer this user's entry; fall back to any entry on the machine.
                foreach (var v in values.OrderByDescending(v => sid != null && v.StartsWith(sid, StringComparison.OrdinalIgnoreCase)))
                {
                    if (userRoots.GetValue(v) is string path && Directory.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                        result.Add(path);
                }
            }
        }
        catch (Exception) { /* registry unreadable: fall back to defaults and the manual setting */ }
        return result;
    }

    /// <summary>
    /// The one folder positions sync through and new books are saved to — the same
    /// folder the Apple apps use: iCloud Drive\Documents\Audiobooks. A folder chosen with
    /// "Set iCloud Drive folder…" wins, resolved the way the Mac/iOS picker resolves it:
    /// "Audiobooks" is used as is, "Documents" means Documents\Audiobooks, an iCloud Drive
    /// root means root\Documents\Audiobooks, anything else is used as is.
    /// Created on first use. Null when there's no iCloud Drive and nothing chosen.
    /// </summary>
    public static string? SharedLibraryFolder(AppSettings? settings = null)
    {
        settings ??= AppSettings.Load();
        string? target = null;
        if (!string.IsNullOrWhiteSpace(settings.ICloudFolder) && Directory.Exists(settings.ICloudFolder))
            target = ResolveChosen(settings.ICloudFolder);
        else if (ICloudDriveRoots(settings).FirstOrDefault(r => r.Exists) is { } root)
            target = Path.Combine(root.Path, SharedLibraryRelativePath);
        if (target == null) return null;
        try { Directory.CreateDirectory(target); return target; }
        catch (Exception) { return null; }   /* iCloud not signed in / read-only */
    }

    private static string ResolveChosen(string chosen)
    {
        // Linux has no iCloud Drive: the user picks the synced folder itself (Nextcloud,
        // Dropbox, Syncthing, rclone…) and it is used exactly as chosen.
        if (!OperatingSystem.IsWindows()) return chosen.TrimEnd('/');
        var trimmed = chosen.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (name.Equals("Audiobooks", StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (name.Equals("Documents", StringComparison.OrdinalIgnoreCase)) return Path.Combine(trimmed, "Audiobooks");
        bool isRoot = ICloudDriveRoots().Any(r => string.Equals(r.Path.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase))
                      || (Directory.Exists(Path.Combine(trimmed, "Documents")) && ContainersIn(trimmed).Count > 0);
        return isRoot ? Path.Combine(trimmed, SharedLibraryRelativePath) : trimmed;
    }

    /// <summary>
    /// The shared library folder first, then any legacy app-container folders that exist
    /// (still listed so books already there show up; never used for positions).
    /// </summary>
    public static List<string> DetectICloudContainers(AppSettings? settings = null)
    {
        settings ??= AppSettings.Load();
        var found = new List<string>();
        if (SharedLibraryFolder(settings) is { } shared) found.Add(shared);

        if (!string.IsNullOrWhiteSpace(settings.ICloudFolder) && Directory.Exists(settings.ICloudFolder))
            found.AddRange(ContainersIn(settings.ICloudFolder));
        foreach (var root in ICloudDriveRoots(settings).Where(r => r.Exists))
            found.AddRange(ContainersIn(root.Path));
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Container folders directly inside an iCloud Drive root, matched case-insensitively.</summary>
    private static List<string> ContainersIn(string root)
    {
        var result = new List<string>();
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception) { return result; }
        foreach (var wanted in ContainerFolderNames)
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                bool match = name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                             || (wanted.StartsWith("iCloud~") && name.StartsWith("iCloud~com~headbytes", StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
                var docs = Path.Combine(d, "Documents");
                result.Add(Directory.Exists(docs) ? docs : d);
            }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool ICloudDriveInstalled => ICloudDriveRoots().Any(r => r.Exists);

    /// <summary>All library folders: iCloud container(s), Music\Casebound, then user-added folders.</summary>
    public static List<LibraryFolder> Folders(AppSettings settings)
    {
        var list = new List<LibraryFolder>();
        var shared = SharedLibraryFolder(settings);
        foreach (var c in DetectICloudContainers(settings))
            list.Add(new LibraryFolder(c, LibraryFolderKind.ICloud,
                IsSharedLibrary: shared != null && string.Equals(c, shared, StringComparison.OrdinalIgnoreCase)));
        list.Add(new LibraryFolder(AppPaths.LocalLibrary, LibraryFolderKind.Local));
        foreach (var f in settings.ExtraLibraryFolders.Where(Directory.Exists))
            list.Add(new LibraryFolder(f, LibraryFolderKind.UserAdded));
        return list.DistinctBy(f => Path.GetFullPath(f.Path).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Lists books without opening them — so a cloud-only file isn't downloaded just to be listed.
    /// The shared library folder is scanned at the TOP LEVEL ONLY (spec decision 2: the key is
    /// the file name alone, so same-named books in two subfolders would share one position);
    /// other folders are still searched a few levels deep. A book that appears in more than one
    /// iCloud folder (shared + old container) is listed once, preferring the shared folder.
    /// </summary>
    public static List<LibraryEntry> Scan(IEnumerable<LibraryFolder> folders)
    {
        var entries = new List<LibraryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cloudKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders.OrderByDescending(f => f.IsSharedLibrary))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder.Path, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = !folder.IsSharedLibrary,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 4,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                });
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                if (!BookExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                if (Path.GetFileName(file).StartsWith('.')) continue;   // same rule as the Swift scanner
                if (!seen.Add(file)) continue;
                try
                {
                    var fi = new FileInfo(file);
                    if (folder.Kind == LibraryFolderKind.ICloud && !cloudKeys.Add(PlaybackPositions.KeyFor(fi.Name, fi.Length))) continue;
                    entries.Add(new LibraryEntry(file, folder.Kind, fi.Length, fi.LastWriteTimeUtc, IsCloudOnly(fi.Attributes)));
                }
                catch (IOException) { }
            }
        }
        return entries;
    }

    /// <summary>Placeholder files (iCloud/OneDrive "files on demand") that would download on open.</summary>
    public static bool IsCloudOnly(FileAttributes a)
    {
        const int RecallOnOpen = 0x00040000, RecallOnDataAccess = 0x00400000;
        int v = (int)a;
        return (v & (RecallOnOpen | RecallOnDataAccess)) != 0 || a.HasFlag(FileAttributes.Offline);
    }

    /// <summary>Watches the folders; raises <paramref name="onChange"/> at most once per second of quiet.</summary>
    public static IDisposable Watch(IEnumerable<LibraryFolder> folders, Action onChange)
    {
        var watchers = new List<FileSystemWatcher>();
        var timer = new Timer(_ => onChange(), null, Timeout.Infinite, Timeout.Infinite);
        void Poke(object? s, EventArgs e) => timer.Change(1000, Timeout.Infinite);
        foreach (var f in folders)
        {
            try
            {
                var w = new FileSystemWatcher(f.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes
                };
                w.Created += Poke; w.Deleted += Poke; w.Renamed += Poke; w.Changed += Poke;
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception) { /* a folder that can't be watched still gets rescanned manually */ }
        }
        return new Disposer(() => { foreach (var w in watchers) w.Dispose(); timer.Dispose(); });
    }

    private sealed class Disposer(Action a) : IDisposable { public void Dispose() => a(); }
}

public enum LibraryFolderKind { ICloud, Local, UserAdded }

public sealed record ICloudRoot(string Path, string Source)
{
    public bool Exists => Directory.Exists(Path);
}

public sealed record LibraryFolder(string Path, LibraryFolderKind Kind, bool IsSharedLibrary = false);

public sealed record LibraryEntry(string Path, LibraryFolderKind Kind, long Size, DateTime ModifiedUtc, bool IsCloudOnly)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>Small JSON settings file in the data folder (<see cref="AppPaths.Root"/>).</summary>
public sealed class AppSettings
{
    public List<string> ExtraLibraryFolders { get; set; } = new();
    public int BitrateKbps { get; set; } = 96;
    public string? LastOutputFolder { get; set; }
    public bool SaveToICloud { get; set; } = true;
    public double PlaybackRate { get; set; } = 1.0;
    public int SkipBackSeconds { get; set; } = 15;
    public int SkipForwardSeconds { get; set; } = 30;
    public string? LastBookPath { get; set; }
    /// <summary>Chosen with "Set iCloud Drive folder…": an iCloud Drive root or the Casebound folder itself.</summary>
    public string? ICloudFolder { get; set; }
    /// <summary>Books hidden from the library (full paths). The files are untouched.</summary>
    public List<string> HiddenBooks { get; set; } = new();
    /// <summary>Random id for this install, used to name this device's positions file.</summary>
    public string? DeviceId { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception) { }
        return new AppSettings();
    }

    public void Save()
    {
        try { AtomicWrite(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json)); }
        catch (Exception) { }
    }

    internal static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
