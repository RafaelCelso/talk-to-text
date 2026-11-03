using TalkToText.Abstractions;

namespace TalkToText.App.Views;

/// <summary>
/// The one recording the app is playing, shared by every control that shows it: the bar on
/// its transcript, the floating mini player on other pages, and the pill at the bottom of the
/// screen while the window is minimised.
/// </summary>
/// <remarks>
/// <para>
/// Playback belongs to the app, not to a page. Leaving the transcript no longer stops the
/// audio; the controls simply move to the mini player. Only one file plays at a time: playing
/// another closes the first.
/// </para>
/// <para>
/// Controls never keep a player of their own — they ask the hub each frame — so none can be
/// left holding a closed one. That once crashed the app when a page came back on screen.
/// </para>
/// </remarks>
public sealed class PlaybackHub : IDisposable
{
    private readonly Func<string, IAudioPlayer?> _open;
    private readonly Dictionary<string, int> _inline = new(StringComparer.OrdinalIgnoreCase);
    private float _volume = 1;
    private double _speed = 1;

    /// <summary>A hub that opens files with <paramref name="open"/>.</summary>
    public PlaybackHub(Func<string, IAudioPlayer?> open) => _open = open;

    /// <summary>The file being played, or null.</summary>
    public string? Path { get; private set; }

    /// <summary>The title shown on the mini players.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The transcript the file belongs to, to go back to it.</summary>
    public Guid? TranscriptId { get; private set; }

    /// <summary>The player of <see cref="Path"/>.</summary>
    public IAudioPlayer? Player { get; private set; }

    /// <summary>Playing, or paused part-way: something the user would want controls for.</summary>
    public bool IsActive => Player is { } player && (player.IsPlaying || player.Position > TimeSpan.Zero);

    /// <summary>Whether the transcript of <see cref="Path"/> is on screen with its own bar.</summary>
    public bool IsShownInline => Path is not null && _inline.TryGetValue(Path, out var count) && count > 0;

    /// <summary>Shared by every player, so all controls agree.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (Player is not null) Player.Volume = value;
        }
    }

    /// <summary>Shared by every player, so all controls agree.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _speed = value;
            if (Player is not null) Player.Speed = value;
        }
    }

    /// <summary>The file, the title or the playing state changed. UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The player of <paramref name="path"/> if it is the one playing, else null.</summary>
    public IAudioPlayer? PlayerFor(string path) =>
        Path is not null && string.Equals(Path, path, StringComparison.OrdinalIgnoreCase) ? Player : null;

    /// <summary>
    /// The player of <paramref name="path"/>, opening it — and closing whatever played before —
    /// if it is not the current one. Null when the file cannot be opened.
    /// </summary>
    public IAudioPlayer? Acquire(string path, string title, Guid? transcriptId)
    {
        if (PlayerFor(path) is { } current) return current;

        Release();
        var player = _open(path);
        if (player is null) return null;

        player.Volume = _volume;
        player.Speed = _speed;
        player.Ended += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Notify);
        player.Failed += (_, error) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DiagnosticLog.Write($"audio playback failed: {path}: {error.Message}");
            Notify();
        });

        Player = player;
        Path = path;
        Title = title;
        TranscriptId = transcriptId;
        Notify();
        return player;
    }

    /// <summary>Stops and closes the file; the mini players disappear.</summary>
    public void Close()
    {
        Release();
        Notify();
    }

    /// <summary>A transcript's own bar came on screen (true) or left it (false).</summary>
    public void SetShownInline(string path, bool shown)
    {
        _inline.TryGetValue(path, out var count);
        _inline[path] = Math.Max(0, count + (shown ? 1 : -1));
        Notify();
    }

    /// <summary>Tells the mini players to update.</summary>
    public void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void Dispose() => Release();

    private void Release()
    {
        Player?.Dispose();
        Player = null;
        Path = null;
        TranscriptId = null;
        Title = string.Empty;
    }
}
