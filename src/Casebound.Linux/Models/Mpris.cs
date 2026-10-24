using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Tmds.DBus;

[assembly: InternalsVisibleTo(Tmds.DBus.Connection.DynamicAssemblyName)]

namespace Casebound.Models;

[DBusInterface("org.mpris.MediaPlayer2")]
public interface IMediaPlayer2 : IDBusObject
{
    Task RaiseAsync();
    Task QuitAsync();
    Task<object> GetAsync(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

[DBusInterface("org.mpris.MediaPlayer2.Player")]
public interface IMprisPlayer : IDBusObject
{
    Task NextAsync();
    Task PreviousAsync();
    Task PauseAsync();
    Task PlayPauseAsync();
    Task StopAsync();
    Task PlayAsync();
    Task SeekAsync(long offsetUs);
    Task SetPositionAsync(ObjectPath trackId, long positionUs);
    Task OpenUriAsync(string uri);
    Task<IDisposable> WatchSeekedAsync(Action<long> handler, Action<Exception>? onError = null);
    Task<object> GetAsync(string prop);
    Task<IDictionary<string, object>> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

/// <summary>
/// MPRIS 2 server (org.mpris.MediaPlayer2.casebound) so desktop media controls, keyboard media keys
/// (via GNOME/KDE), headset buttons and playerctl work. Next/Previous move between chapters, like
/// the Windows media overlay and the Apple lock screen.
/// </summary>
public sealed class Mpris : IMediaPlayer2, IMprisPlayer
{
    public static readonly ObjectPath Path = new("/org/mpris/MediaPlayer2");
    public ObjectPath ObjectPath => Path;
    private readonly AudiobookPlayer p;
    private Connection? conn;
    private event Action<PropertyChanges>? rootChanged, playerChanged;
    private event Action<long>? seeked;

    public Mpris(AudiobookPlayer player)
    {
        p = player;
        p.StateChanged += () => Notify();
        p.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AudiobookPlayer.Rate)) Notify(); };
        p.Seeked += () => seeked?.Invoke(Us(p.Position));
    }

    public async Task<bool> StartAsync()
    {
        try
        {
            var address = Address.Session;
            if (string.IsNullOrEmpty(address)) return false;
            conn = new Connection(address);
            await conn.ConnectAsync();
            await conn.RegisterObjectAsync(this);
            await conn.RegisterServiceAsync("org.mpris.MediaPlayer2.casebound");
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("MPRIS unavailable: " + e.Message);
            return false;
        }
    }

    private static long Us(double s) => (long)(s * 1_000_000);
    private static Task Ui(Action a) { Dispatcher.UIThread.Post(a); return Task.CompletedTask; }

    private void Notify()
    {
        var all = PlayerProps();
        playerChanged?.Invoke(PropertyChanges.ForProperty("PlaybackStatus", all["PlaybackStatus"]));
        playerChanged?.Invoke(PropertyChanges.ForProperty("Metadata", all["Metadata"]));
        playerChanged?.Invoke(PropertyChanges.ForProperty("Rate", all["Rate"]));
    }

    // ---- org.mpris.MediaPlayer2 ----
    Task IMediaPlayer2.RaiseAsync() => Ui(() => { App.Window?.Show(); App.Window?.Activate(); });
    Task IMediaPlayer2.QuitAsync() => Ui(() => App.Window?.Close());
    private static Dictionary<string, object> RootProps() => new()
    {
        ["CanQuit"] = true, ["CanRaise"] = true, ["HasTrackList"] = false, ["Identity"] = "Casebound",
        ["DesktopEntry"] = "io.github.lhead001.Casebound",
        ["SupportedUriSchemes"] = new[] { "file" }, ["SupportedMimeTypes"] = new[] { "audio/mp4", "audio/x-m4b" }
    };
    Task<object> IMediaPlayer2.GetAsync(string prop) => Task.FromResult(RootProps()[prop]);
    Task<IDictionary<string, object>> IMediaPlayer2.GetAllAsync() => Task.FromResult<IDictionary<string, object>>(RootProps());
    Task IMediaPlayer2.SetAsync(string prop, object val) => Task.CompletedTask;
    Task<IDisposable> IMediaPlayer2.WatchPropertiesAsync(Action<PropertyChanges> h) { rootChanged += h; return Task.FromResult<IDisposable>(new Unsub(() => rootChanged -= h)); }

    // ---- org.mpris.MediaPlayer2.Player ----
    public Task NextAsync() => Ui(p.NextChapter);
    public Task PreviousAsync() => Ui(p.PreviousChapter);
    public Task PauseAsync() => Ui(p.Pause);
    public Task PlayPauseAsync() => Ui(p.TogglePlay);
    public Task StopAsync() => Ui(p.Pause);
    public Task PlayAsync() => Ui(p.Play);
    public Task SeekAsync(long offsetUs) => Ui(() => p.Seek(p.Position + offsetUs / 1_000_000.0));
    public Task SetPositionAsync(ObjectPath trackId, long positionUs) => Ui(() => p.Seek(positionUs / 1_000_000.0));
    public Task OpenUriAsync(string uri) => Task.CompletedTask;
    public Task<IDisposable> WatchSeekedAsync(Action<long> handler, Action<Exception>? onError = null)
    { seeked += handler; return Task.FromResult<IDisposable>(new Unsub(() => seeked -= handler)); }

    private Dictionary<string, object> PlayerProps()
    {
        var meta = new Dictionary<string, object> { ["mpris:trackid"] = new ObjectPath("/com/headbytes/Casebound/track/" + (p.HasBook ? "current" : "none")) };
        if (p.Book != null)
        {
            meta["xesam:title"] = string.IsNullOrEmpty(p.ChapterTitle) ? p.Title : $"{p.ChapterTitle}";
            meta["xesam:album"] = p.Title;
            if (!string.IsNullOrEmpty(p.Author)) meta["xesam:artist"] = new[] { p.Author };
            meta["mpris:length"] = Us(p.Duration);
            meta["xesam:url"] = new Uri(p.Book.Path).AbsoluteUri;
        }
        return new()
        {
            ["PlaybackStatus"] = p.IsPlaying ? "Playing" : p.HasBook ? "Paused" : "Stopped",
            ["Rate"] = p.Rate, ["MinimumRate"] = 0.75, ["MaximumRate"] = 3.0, ["Volume"] = 1.0,
            ["Metadata"] = meta, ["Position"] = Us(p.Position),
            ["CanGoNext"] = p.HasBook, ["CanGoPrevious"] = p.HasBook, ["CanPlay"] = p.HasBook, ["CanPause"] = p.HasBook,
            ["CanSeek"] = p.HasBook, ["CanControl"] = true, ["LoopStatus"] = "None", ["Shuffle"] = false
        };
    }
    Task<object> IMprisPlayer.GetAsync(string prop) => Dispatcher.UIThread.InvokeAsync(() => PlayerProps()[prop]).GetTask();
    Task<IDictionary<string, object>> IMprisPlayer.GetAllAsync() => Dispatcher.UIThread.InvokeAsync(() => (IDictionary<string, object>)PlayerProps()).GetTask();
    Task IMprisPlayer.SetAsync(string prop, object val)
    {
        if (prop == "Rate" && val is double r) return Ui(() => p.SetRate(Math.Clamp(r, 0.75, 3.0)));
        return Task.CompletedTask;
    }
    Task<IDisposable> IMprisPlayer.WatchPropertiesAsync(Action<PropertyChanges> h) { playerChanged += h; return Task.FromResult<IDisposable>(new Unsub(() => playerChanged -= h)); }

    public void Dispose() => conn?.Dispose();
    private sealed class Unsub(Action a) : IDisposable { public void Dispose() => a(); }
}
