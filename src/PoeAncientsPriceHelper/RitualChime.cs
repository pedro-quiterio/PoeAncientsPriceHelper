using System.Windows.Media;
using System.Windows.Threading;

namespace PoeAncientsPriceHelper;

// Plays the ritual "full" chime. Uses WPF MediaPlayer (already in the app's dependency set) so a custom
// chime can be a .wav OR an .mp3 — SoundPlayer would be WAV-only. MediaPlayer must be created and driven
// on a thread that owns a Dispatcher, so this runs its own background STA thread with a message loop
// (mirroring the overlay managers), keeping playback off the WPF UI thread and independent of it.
//
// The chime source is resolved fresh on every Play(): the user-selected file when it exists, otherwise
// the bundled default (assets\ritual_chime.wav next to the exe). A missing/invalid custom file therefore
// falls back silently to the bundled chime — the failure is logged once for a --debug session, never
// surfaced as an error.
internal sealed class RitualChime : IDisposable
{
    private readonly Func<string?> _customPath;
    private readonly Action<string>? _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Dispatcher? _dispatcher;
    private MediaPlayer? _player;
    private bool _loggedFallback;

    public RitualChime(Func<string?> customPath, Action<string>? log = null)
    {
        _customPath = customPath;
        _log = log;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "RitualChime-STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void ThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _player = new MediaPlayer();
        _ready.Set();
        Dispatcher.Run();
    }

    public void Play()
    {
        if (!_ready.Wait(TimeSpan.FromSeconds(1)) || _dispatcher is null) return;
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                var uri = ResolveChimeUri();
                if (uri is null) { _log?.Invoke("ritual chime: no playable file (bundled default missing)"); return; }
                // Re-Open each time so replaying the same clip restarts it cleanly.
                _player!.Open(uri);
                _player.Position = TimeSpan.Zero;
                _player.Play();
            }
            catch (Exception ex) { _log?.Invoke($"ritual chime play failed: {ex.Message}"); }
        });
    }

    // The custom file when it's set and exists, else the bundled default. Returns null only when even the
    // bundled chime is missing (shouldn't happen in a real build).
    private Uri? ResolveChimeUri()
    {
        var custom = _customPath();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (File.Exists(custom)) return new Uri(Path.GetFullPath(custom));
            if (!_loggedFallback)
            {
                _log?.Invoke($"ritual chime: custom file '{custom}' not found — using the bundled default");
                _loggedFallback = true;
            }
        }
        var bundled = BundledChimePath;
        return File.Exists(bundled) ? new Uri(bundled) : null;
    }

    public static string BundledChimePath =>
        Path.Combine(AppContext.BaseDirectory, "assets", "ritual_chime.wav");

    public void Dispose()
    {
        try { _dispatcher?.InvokeShutdown(); } catch { /* already gone */ }
        _ready.Dispose();
    }
}
