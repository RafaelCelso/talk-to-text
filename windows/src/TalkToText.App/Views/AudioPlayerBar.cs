using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.Abstractions;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>How a player is laid out.</summary>
public enum PlayerLayout
{
    /// <summary>Bars on top, controls below: on a transcript and in the floating mini player.</summary>
    Full,

    /// <summary>Everything on one row: the pill at the bottom of the screen.</summary>
    Pill,
}

/// <summary>
/// Controls for one recording: the recording drawn as bars that double as the seek bar,
/// restart, play/pause, stop, volume, and the speed behind a gear.
/// </summary>
/// <remarks>
/// It plays through the app's <see cref="PlaybackHub"/> and owns nothing: the audio carries on
/// when the bar leaves the screen, and several bars — the transcript's, the floating one, the
/// pill — can show the same playback at once. Until the user first plays or seeks, the file is
/// not opened at all, so a recording merely looked at never interrupts another one playing.
/// </remarks>
public sealed class AudioPlayerBar : UserControl
{
    private static readonly double[] Speeds = [1, 1.5, 2];

    private readonly PlaybackHub _hub;
    private readonly string _path;
    private readonly string _title;
    private readonly Guid? _transcriptId;
    private readonly bool _inline;
    private readonly Button _playPause;
    private readonly PlaybackTimeline _timeline = new();
    private readonly TextBlock _time;
    private readonly Button _mute;
    private readonly Slider? _volume;
    private readonly Button _gear;
    private readonly Flyout _speedMenu;
    private readonly List<(double Speed, Button Button)> _speedItems = [];
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _detached = true;
    private float _volumeBeforeMute = 1;
    private TimeSpan _fileDuration;

    // The device reports position a buffer at a time; between reports the playhead is
    // carried forward by the clock so it glides instead of stepping.
    private TimeSpan _reported;
    private double _reportedAt;

    /// <summary>Controls for the recording at <paramref name="path"/>.</summary>
    /// <param name="hub">The app's playback.</param>
    /// <param name="path">The audio file.</param>
    /// <param name="title">Shown on the mini players while it plays.</param>
    /// <param name="transcriptId">The transcript it belongs to.</param>
    /// <param name="layout">Two rows, or one.</param>
    /// <param name="inline">
    /// True for the bar on the transcript itself: while it is on screen the floating player
    /// stays away.
    /// </param>
    public AudioPlayerBar(PlaybackHub hub, string path, string title, Guid? transcriptId, PlayerLayout layout = PlayerLayout.Full, bool inline = true)
    {
        _hub = hub;
        _path = path;
        _title = title;
        _transcriptId = transcriptId;
        _inline = inline;

        var restart = Control(Icons.Restart, "Recomeçar");
        restart.Click += (_, _) =>
        {
            if (Acquire() is not { } player) return;
            player.Seek(TimeSpan.Zero);
            player.Play();
            Changed();
        };

        _playPause = Control(Icons.Play, "Reproduzir", large: true);
        _playPause.Click += (_, _) => TogglePlay();

        var stop = Control(Icons.Stop, "Parar");
        stop.Click += (_, _) =>
        {
            Live?.StopAndRewind();
            Changed();
        };

        _time = Ui.Text(string.Empty, Tokens.Fonts.Label, Tokens.Brushes.Muted);
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.TextWrapping = TextWrapping.NoWrap;

        ToolTip.SetTip(_timeline, "Clique ou arraste para ir a outro ponto");
        AutomationProperties.SetName(_timeline, "Posição");
        _timeline.SeekRequested += (_, fraction) =>
        {
            if (Acquire() is not { } player) return;
            player.Seek(TimeSpan.FromSeconds(fraction * player.Duration.TotalSeconds));
            Changed();
        };

        _mute = Control(Icons.Volume, "Sem som");
        _mute.Click += (_, _) => ToggleMute();

        _speedMenu = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = SpeedMenu() };
        _gear = Control(Icons.Gear, "Velocidade de reprodução");
        _gear.Click += (_, _) => _speedMenu.ShowAt(_gear);

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { restart, _playPause, stop },
        };

        if (layout == PlayerLayout.Pill)
        {
            // One row: transport, the bars, the time, mute and the gear.
            _timeline.Height = 26;
            _timeline.Margin = new Thickness(Tokens.Space.Base, 0);
            _time.Margin = new Thickness(0, 0, Tokens.Space.Snug, 0);
            Grid.SetColumn(_timeline, 1);
            Grid.SetColumn(_time, 2);
            Grid.SetColumn(_mute, 3);
            Grid.SetColumn(_gear, 4);
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
                Children = { transport, _timeline, _time, _mute, _gear },
            };
        }
        else
        {
            _volume = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = hub.Volume,
                MinWidth = 40,
                MinHeight = 0,
                Margin = new Thickness(Tokens.Space.Tight, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTip.SetTip(_volume, "Volume");
            AutomationProperties.SetName(_volume, "Volume");
            _volume.ValueChanged += (_, e) =>
            {
                if (Math.Abs(_hub.Volume - e.NewValue) < 0.001) return;
                _hub.Volume = (float)e.NewValue;
                Changed();
            };

            _time.Margin = new Thickness(Tokens.Space.Base, 0, 0, 0);
            Grid.SetColumn(_time, 1);
            _mute.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);
            _gear.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);
            Grid.SetColumn(_mute, 1);
            Grid.SetColumn(_volume, 2);
            Grid.SetColumn(_gear, 3);

            Content = new Border
            {
                Background = Tokens.Brushes.GlassFill,
                BorderBrush = Tokens.Brushes.GlassRim,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Tokens.Radius.Card),
                Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
                Child = new StackPanel
                {
                    Spacing = Tokens.Space.Snug,
                    Children =
                    {
                        new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _timeline, _time } },
                        new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
                            Children = { transport, _mute, _volume, _gear },
                        },
                    },
                },
            };
        }

        // A frame timer, as the recording pill uses: the bars need ~30 fps while playing.
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = Tokens.Motion.Frame };
        _tick.Tick += (_, _) => Sync();
        AttachedToVisualTree += (_, _) =>
        {
            _detached = false;
            _hub.Changed += OnHubChanged;
            if (_inline) _hub.SetShownInline(_path, true);
            _tick.Start();
            Sync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            // Leaving the screen does not stop the audio: the floating player takes over.
            _detached = true;
            _tick.Stop();
            _hub.Changed -= OnHubChanged;
            if (_inline) _hub.SetShownInline(_path, false);
        };

        _ = LoadEnvelopeAsync(path);
        ShowSpeed();
        Sync();
    }

    /// <summary>
    /// The position shown, every frame while this file plays, with whether it is playing.
    /// UI thread.
    /// </summary>
    public event EventHandler<(TimeSpan Position, bool Playing)>? PositionChanged;

    /// <summary>Whether this bar's file is playing. Exposed for tests and the timestamps.</summary>
    public bool IsPlaying => Live?.IsPlaying ?? false;

    /// <summary>The speed choices in the gear menu. Exposed for tests.</summary>
    public IReadOnlyList<Button> SpeedItems => _speedItems.Select(s => s.Button).ToList();

    /// <summary>The bars. Exposed for tests.</summary>
    public PlaybackTimeline Timeline => _timeline;

    /// <summary>The player, if this bar's file is the one the app is playing.</summary>
    private IAudioPlayer? Live => _hub.PlayerFor(_path);

    /// <summary>Jumps to <paramref name="position"/> and plays from there — a message's timestamp.</summary>
    public void PlayFrom(TimeSpan position)
    {
        if (Acquire() is not { } player) return;
        player.Seek(position);
        player.Play();
        Changed();
    }

    private IAudioPlayer? Acquire()
    {
        var player = _hub.Acquire(_path, _title, _transcriptId);
        if (player is null) ToolTip.SetTip(_playPause, "Não foi possível abrir o arquivo de áudio");
        return player;
    }

    /// <summary>After any action: every control showing this playback updates.</summary>
    private void Changed()
    {
        Sync();
        _hub.Notify();
    }

    private void OnHubChanged(object? sender, EventArgs e)
    {
        if (_volume is not null && Math.Abs(_volume.Value - _hub.Volume) > 0.001) _volume.Value = _hub.Volume;
        ShowVolumeIcon();
        ShowSpeed();
        Sync();
    }

    private async Task LoadEnvelopeAsync(string path)
    {
        if (PlatformFactory.CreateAudioFileDecoder() is not { } decoder) return;

        try
        {
            // A few seconds even for an hour of audio; if the bar has gone by then, the
            // result is simply dropped.
            var (levels, duration) = await Task.Run(() =>
            {
                using var audio = decoder.Open(path);
                var length = audio.Duration;
                return (AudioEnvelope.Compute(audio, 600), length);
            }).ConfigureAwait(true);

            _fileDuration = duration;
            _timeline.Envelope = levels;
            if (!_detached) Sync();
        }
        catch (Exception e)
        {
            // The bars stay even; playback does not depend on them.
            DiagnosticLog.Write($"audio envelope failed: {path}: {e.Message}");
        }
    }

    private void TogglePlay()
    {
        if (Live is { IsPlaying: true } playing) playing.Pause();
        else Acquire()?.Play();
        Changed();
    }

    private void ToggleMute()
    {
        if (_hub.Volume > 0)
        {
            _volumeBeforeMute = _hub.Volume;
            _hub.Volume = 0;
        }
        else
        {
            _hub.Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : 1;
        }

        if (_volume is not null) _volume.Value = _hub.Volume;
        ShowVolumeIcon();
        _hub.Notify();
    }

    private void Sync()
    {
        var player = Live;
        var playing = player?.IsPlaying ?? false;
        var now = _clock.Elapsed.TotalSeconds;
        var reported = player?.Position ?? TimeSpan.Zero;
        if (reported != _reported)
        {
            _reported = reported;
            _reportedAt = now;
        }

        var shown = playing
            ? _reported + TimeSpan.FromSeconds(Math.Min(0.3, now - _reportedAt) * _hub.Speed)
            : _reported;
        var duration = player?.Duration ?? _fileDuration;
        if (shown > duration) shown = duration;

        _timeline.Fraction = duration > TimeSpan.Zero ? shown / duration : 0;
        _timeline.IsPlaying = playing;
        _timeline.Phase = now;
        _timeline.InvalidateVisual();

        _time.Text = $"{Clock(shown)} / {Clock(duration)}";
        if (player is not null) PositionChanged?.Invoke(this, (shown, playing));

        var label = playing ? "Pausar" : "Reproduzir";
        if (!Equals(AutomationProperties.GetName(_playPause), label))
        {
            _playPause.Content = Icons.Make(playing ? Icons.Pause : Icons.Play, new SolidColorBrush(Tokens.Colors.OnPrimary), Tokens.Size.Icon);
            ToolTip.SetTip(_playPause, label);
            AutomationProperties.SetName(_playPause, label);
        }
    }

    private void ShowVolumeIcon() =>
        _mute.Content = Icons.Make(_hub.Volume <= 0 ? Icons.VolumeOff : Icons.Volume, Tokens.Brushes.Muted, Tokens.Size.Icon - 2);

    /// <summary>The gear's drop-down: a caption, then one row per speed with a check on the current one.</summary>
    private StackPanel SpeedMenu()
    {
        var menu = new StackPanel { Width = 168, Spacing = Tokens.Space.Hair };
        var caption = Ui.Text("Velocidade de reprodução", Tokens.Fonts.Caption, Tokens.Brushes.Muted, FontWeight.Medium);
        caption.Margin = new Thickness(Tokens.Space.Snug, Tokens.Space.Tight, 0, Tokens.Space.Tight);
        menu.Children.Add(caption);

        foreach (var speed in Speeds)
        {
            var item = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Tight + 2),
                CornerRadius = new CornerRadius(Tokens.Radius.Control),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            item.Classes.Add("nav");
            AutomationProperties.SetName(item, $"Velocidade {Label(speed)}");
            item.Click += (_, _) =>
            {
                _hub.Speed = speed;
                ShowSpeed();
                _speedMenu.Hide();
                _hub.Notify();
            };

            _speedItems.Add((speed, item));
            menu.Children.Add(item);
        }

        return menu;
    }

    private void ShowSpeed()
    {
        foreach (var (speed, item) in _speedItems)
        {
            var selected = Math.Abs(speed - _hub.Speed) < 0.01;
            var check = selected ? Icons.Make(Icons.Check, Tokens.Brushes.Primary, Tokens.Size.Icon - 2) : new Border { Width = Tokens.Size.Icon - 2 };
            var label = Ui.Text(speed == 1 ? "1x (normal)" : Label(speed), Tokens.Fonts.Body, selected ? Tokens.Brushes.Foreground : Tokens.Brushes.Muted, selected ? FontWeight.SemiBold : FontWeight.Normal);
            item.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug, Children = { check, label } };
        }

        // The gear lights up when not at normal speed, so a changed speed is never a surprise.
        var changed = Math.Abs(_hub.Speed - 1) > 0.01;
        _gear.Content = Icons.Make(Icons.Gear, changed ? Tokens.Brushes.Primary : Tokens.Brushes.Muted, Tokens.Size.Icon - 2);
        ToolTip.SetTip(_gear, $"Velocidade de reprodução: {Label(_hub.Speed)}");
    }

    private static Button Control(string icon, string tooltip, bool large = false)
    {
        var size = large ? 36.0 : 30.0;
        var button = new Button
        {
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            Background = large ? Tokens.Brushes.PrimaryGlass : Brushes.Transparent,
            BorderBrush = large ? Tokens.Brushes.PrimaryRim : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = Icons.Make(icon, large ? new SolidColorBrush(Tokens.Colors.OnPrimary) : Tokens.Brushes.Muted, large ? Tokens.Size.Icon : Tokens.Size.Icon - 2),
        };
        if (large) button.Classes.Add("primary");
        ToolTip.SetTip(button, tooltip);

        // Icon-only: without a name, screen readers and UI Automation see "Viewbox".
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    /// <summary>"1x", "1,5x", "2x".</summary>
    private static string Label(double speed) => speed.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + "x";

    /// <summary>"01:05", or "1:02:10" past an hour.</summary>
    internal static string Clock(TimeSpan time) =>
        time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
}
