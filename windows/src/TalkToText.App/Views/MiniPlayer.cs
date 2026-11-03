using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TalkToText.App.Controls;
using TalkToText.App.Design;

namespace TalkToText.App.Views;

/// <summary>
/// The recording that is playing, with its controls, wherever its transcript is not on screen:
/// a floating card in the window, or the pill at the bottom of the screen.
/// </summary>
/// <remarks>
/// A title that leads back to the transcript, a close button that stops the audio, and the
/// same <see cref="AudioPlayerBar"/> the transcript has — rebuilt when another file starts.
/// </remarks>
public sealed class MiniPlayer : UserControl
{
    private readonly PlaybackHub _hub;
    private readonly PlayerLayout _layout;
    private readonly ContentControl _bar = new();
    private readonly TextBlock _title;
    private string? _shownPath;

    /// <summary>Controls for whatever <paramref name="hub"/> plays.</summary>
    public MiniPlayer(PlaybackHub hub, PlayerLayout layout)
    {
        _hub = hub;
        _layout = layout;

        _title = Ui.Text(string.Empty, Tokens.Fonts.Label, weight: FontWeight.SemiBold);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.TextWrapping = TextWrapping.NoWrap;

        var open = new Button
        {
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Snug,
                Children = { Icons.Make(Icons.AudioFile, Tokens.Brushes.Primary, Tokens.Size.Icon - 2), _title },
            },
        };
        ToolTip.SetTip(open, "Abrir a transcrição");
        AutomationProperties.SetName(open, "Abrir a transcrição");
        open.Click += (_, _) => { if (_hub.TranscriptId is { } id) OpenRequested?.Invoke(this, id); };

        var close = Ui.IconButton(Icons.Close, "Fechar o player");
        close.Click += (_, _) => _hub.Close();

        if (layout == PlayerLayout.Pill)
        {
            // One row: title, controls, close.
            open.MaxWidth = 150;
            open.Margin = new Thickness(0, 0, Tokens.Space.Snug, 0);
            Grid.SetColumn(_bar, 1);
            Grid.SetColumn(close, 2);
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                VerticalAlignment = VerticalAlignment.Center,
                Children = { open, _bar, close },
            };
        }
        else
        {
            Grid.SetColumn(close, 1);
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { open, close } };
            Content = new StackPanel { Spacing = Tokens.Space.Snug, Children = { header, _bar } };
        }

        AttachedToVisualTree += (_, _) =>
        {
            _hub.Changed += OnHubChanged;
            Rebuild();
        };
        DetachedFromVisualTree += (_, _) => _hub.Changed -= OnHubChanged;
        Rebuild();
    }

    /// <summary>The user asked to go to the transcript with this id.</summary>
    public event EventHandler<Guid>? OpenRequested;

    private void OnHubChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        _title.Text = _hub.Title;
        if (_hub.Path == _shownPath) return;

        _shownPath = _hub.Path;
        _bar.Content = _hub.Path is { } path
            ? new AudioPlayerBar(_hub, path, _hub.Title, _hub.TranscriptId, _layout, inline: false)
            : null;
    }
}
