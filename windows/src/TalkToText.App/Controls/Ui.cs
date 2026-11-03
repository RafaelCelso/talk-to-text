using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Controls;

/// <summary>
/// Stroke icons from Lucide (ISC licence), as path data on a 24 × 24 grid.
/// </summary>
public static class Icons
{
    /// <summary>Microphone.</summary>
    public const string Mic = "M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z M19 10v2a7 7 0 0 1-14 0v-2 M12 19v3";

    /// <summary>Upload: an arrow rising out of a tray.</summary>
    public const string Upload = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M17 8l-5-5-5 5 M12 3v12";

    /// <summary>Download: an arrow falling into a tray.</summary>
    public const string Download = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3";

    /// <summary>An audio file: a page with a folded corner and level bars.</summary>
    public const string AudioFile = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M8 13v4 M12 11v8 M16 14v2";

    /// <summary>Play.</summary>
    public const string Play = "M7 4.5v15a1 1 0 0 0 1.5.86l12.5-7.5a1 1 0 0 0 0-1.72L8.5 3.64A1 1 0 0 0 7 4.5z";

    /// <summary>Pause.</summary>
    public const string Pause = "M7 4h3v16H7z M14 4h3v16h-3z";

    /// <summary>Start again: an arrow turning back.</summary>
    public const string Restart = "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8 M3 3v5h5";

    /// <summary>Speaker with sound.</summary>
    public const string Volume = "M11 5 6 9H2v6h4l5 4V5z M15.54 8.46a5 5 0 0 1 0 7.07 M19.07 4.93a10 10 0 0 1 0 14.14";

    /// <summary>Speaker, muted.</summary>
    public const string VolumeOff = "M11 5 6 9H2v6h4l5 4V5z M22 9l-6 6 M16 9l6 6";

    /// <summary>A gear: settings for one thing, as opposed to the app's settings.</summary>
    public const string Gear = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0z";

    /// <summary>Collapse: a chevron pointing down.</summary>
    public const string Collapse = "M6 9l6 6 6-6";

    /// <summary>Open in full: arrows to opposite corners.</summary>
    public const string Expand = "M15 3h6v6 M9 21H3v-6 M21 3l-7 7 M3 21l7-7";

    /// <summary>Two speech balloons: a conversation with the assistant.</summary>
    public const string Messages = "M14 9a2 2 0 0 1-2 2H6l-4 4V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2z M18 9h2a2 2 0 0 1 2 2v11l-4-4h-6a2 2 0 0 1-2-2v-1";

    /// <summary>Close.</summary>
    public const string Close = "M18 6 6 18 M6 6l12 12";

    /// <summary>Stop.</summary>
    public const string Stop = "M7 7h10v10H7z";

    /// <summary>Home.</summary>
    public const string Home = "M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1Z";

    /// <summary>Book.</summary>
    public const string Book = "M2 4h6a4 4 0 0 1 4 4v13a3 3 0 0 0-3-3H2Z M22 4h-6a4 4 0 0 0-4 4v13a3 3 0 0 1 3-3h7Z";

    /// <summary>Sliders — settings.</summary>
    public const string Settings = "M20 7h-9 M14 17H5 M20 17a3 3 0 1 1-6 0a3 3 0 1 1 6 0 M10 7a3 3 0 1 1-6 0a3 3 0 1 1 6 0";

    /// <summary>Copy.</summary>
    public const string Copy = "M8 8h12v12H8Z M16 8V4H4v12h4";

    /// <summary>Check mark.</summary>
    public const string Check = "M20 6 9 17l-5-5";

    /// <summary>Trash.</summary>
    public const string Trash = "M3 6h18 M8 6V4h8v2 M19 6l-1 14H6L5 6";

    /// <summary>Plus.</summary>
    public const string Plus = "M12 5v14 M5 12h14";

    /// <summary>Export: an arrow down onto a line.</summary>
    public const string Export = "M12 3v12 M7 10l5 5 5-5 M5 21h14";

    /// <summary>Search.</summary>
    public const string Search = "M11 4a7 7 0 1 1 0 14a7 7 0 1 1 0-14 M21 21l-4.3-4.3";

    /// <summary>Pencil.</summary>
    public const string Pencil = "M4 20h4L19 9l-4-4L4 16Z";

    /// <summary>Folder.</summary>
    public const string Folder = "M3 6a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1Z";

    /// <summary>Chat bubble — meetings.</summary>
    public const string Chat = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2Z";

    /// <summary>Sparkles — the summary.</summary>
    public const string Sparkles = "M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9Z M19 3v4 M21 5h-4 M5 17v4 M7 19H3";

    /// <summary>Audio lines — the brand mark.</summary>
    public const string Brand = "M2 10v3 M6 6v11 M10 3v18 M14 8v7 M18 5v13 M22 10v3";

    /// <summary>An icon at <paramref name="size"/> px in <paramref name="brush"/>.</summary>
    public static Control Make(string data, IBrush? brush = null, double size = Tokens.Size.Icon) => new Viewbox
    {
        Width = size,
        Height = size,
        Child = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stroke = brush ?? Tokens.Brushes.Foreground,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24,
        },
    };
}

/// <summary>Factories for the app's building blocks. Every value comes from <see cref="Tokens"/>.</summary>
public static class Ui
{
    /// <summary>Text in the app face.</summary>
    public static TextBlock Text(
        string text, double size = Tokens.Fonts.Body, IBrush? brush = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontFamily = Tokens.Fonts.Sans,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush ?? Tokens.Brushes.Foreground,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The app's own icon — the one in the taskbar and Start menu — at <paramref name="size"/>.
    /// The picture has a transparent margin of about a tenth on each side, as app icons do.
    /// </summary>
    public static Image AppIcon(double size) => new()
    {
        Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TalkToText/Assets/app-icon.png"))),
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A page title with an optional muted subtitle.</summary>
    public static StackPanel PageHeader(string title, string? subtitle = null)
    {
        var header = new StackPanel { Spacing = Tokens.Space.Tight, Margin = new Thickness(0, 0, 0, Tokens.Space.Wide) };
        header.Children.Add(Text(title, Tokens.Fonts.Title, weight: FontWeight.SemiBold));
        if (subtitle is not null) header.Children.Add(Text(subtitle, Tokens.Fonts.Body, Tokens.Brushes.Muted));
        return header;
    }

    /// <summary>A section heading inside a page.</summary>
    public static TextBlock SectionTitle(string text)
    {
        var title = Text(text, Tokens.Fonts.Heading, weight: FontWeight.SemiBold);
        title.Margin = new Thickness(0, 0, 0, Tokens.Space.Snug);
        return title;
    }

    /// <summary>A glass card: frosted fill, a rim lit along the top, and a soft lift.</summary>
    public static Border Card(Control content, double padding = Tokens.Space.Roomy) => new()
    {
        Background = Tokens.Brushes.GlassFill,
        BorderBrush = Tokens.Brushes.GlassRim,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Tokens.Radius.Card),
        BoxShadow = Tokens.Elevation.Glass,
        Padding = new Thickness(padding),
        Child = content,
    };

    /// <summary>The brand-blue button.</summary>
    public static Button Primary(string label, string? icon = null) => Button(label, icon, primary: true);

    /// <summary>A quiet glass button.</summary>
    public static Button Secondary(string label, string? icon = null) => Button(label, icon, primary: false);

    /// <summary>A borderless icon-only button for row actions.</summary>
    public static Button IconButton(string icon, string tooltip, IBrush? brush = null)
    {
        var button = new Button
        {
            Content = Icons.Make(icon, brush ?? Tokens.Brushes.Muted, Tokens.Size.Icon - 2),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            Padding = new Thickness(Tokens.Space.Snug - 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, tooltip);

        // Icon-only: without a name, screen readers and UI Automation see "Viewbox".
        Avalonia.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static Button Button(string label, string? icon, bool primary)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug };
        var foreground = primary ? new SolidColorBrush(Tokens.Colors.OnPrimary) : Tokens.Brushes.Foreground;
        if (icon is not null) content.Children.Add(Icons.Make(icon, foreground, Tokens.Size.Icon - 2));
        content.Children.Add(Text(label, Tokens.Fonts.Body, foreground, FontWeight.Medium));

        var button = new Button
        {
            Content = content,
            Height = Tokens.Size.Control,
            Padding = new Thickness(Tokens.Space.Roomy - 2, 0),
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = primary ? Tokens.Brushes.PrimaryGlass : Tokens.Brushes.GlassFillRaised,
            BorderBrush = primary ? Tokens.Brushes.PrimaryRim : Tokens.Brushes.GlassRimSubtle,
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        button.Classes.Add(primary ? "primary" : "secondary");
        return button;
    }

    /// <summary>A search field with a leading icon.</summary>
    public static TextBox SearchBox(string placeholder) => new()
    {
        Watermark = placeholder,
        FontFamily = Tokens.Fonts.Sans,
        FontSize = Tokens.Fonts.Body,
        Height = Tokens.Size.Control,
        CornerRadius = new CornerRadius(Tokens.Radius.Control),
        VerticalContentAlignment = VerticalAlignment.Center,
        InnerLeftContent = new Border
        {
            Padding = new Thickness(Tokens.Space.Snug + 2, 0, 0, 0),
            Child = Icons.Make(Icons.Search, Tokens.Brushes.Muted, Tokens.Size.Icon - 2),
        },
    };

    /// <summary>A single-line text field.</summary>
    public static TextBox Field(string placeholder, string text = "") => new()
    {
        Text = text,
        Watermark = placeholder,
        FontFamily = Tokens.Fonts.Sans,
        FontSize = Tokens.Fonts.Body,
        Height = Tokens.Size.Control,
        CornerRadius = new CornerRadius(Tokens.Radius.Control),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>A label above a control.</summary>
    public static StackPanel Labelled(string label, Control content) => new()
    {
        Spacing = Tokens.Space.Tight + 2,
        Children = { Text(label, Tokens.Fonts.Label, Tokens.Brushes.Muted, FontWeight.Medium), content },
    };

    /// <summary>A row with text on the left and a control on the right — a settings line.</summary>
    public static Grid SettingRow(string title, string? detail, Control control)
    {
        var text = new StackPanel { Spacing = Tokens.Space.Hair, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title, Tokens.Fonts.Body, weight: FontWeight.Medium));
        if (detail is not null) text.Children.Add(Text(detail, Tokens.Fonts.Label, Tokens.Brushes.Muted));

        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(Tokens.Space.Roomy, 0, 0, 0);
        Grid.SetColumn(control, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { text, control },
        };
    }

    /// <summary>A hairline between rows.</summary>
    public static Border Divider() => new()
    {
        Height = 1,
        Background = Tokens.Brushes.GlassHairline,
        Margin = new Thickness(0, Tokens.Space.Base),
    };

    /// <summary>A small coloured status dot.</summary>
    public static Avalonia.Controls.Shapes.Ellipse Dot(Color color, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The keys of <paramref name="shortcut"/>, drawn as key caps joined by "+".</summary>
    public static StackPanel KeyCaps(Shortcut shortcut, double size = Tokens.Fonts.Label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Tight + 2 };
        var keys = shortcut.KeyList;

        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) row.Children.Add(Text("+", size, Tokens.Brushes.Muted));
            row.Children.Add(KeyCap(Keys.Name(keys[i]), size));
        }

        return row;
    }

    /// <summary>One key cap.</summary>
    public static Border KeyCap(string label, double size = Tokens.Fonts.Label) => new()
    {
        Background = Tokens.Brushes.GlassFillRaised,
        BorderBrush = Tokens.Brushes.GlassRim,
        BorderThickness = new Thickness(1, 1, 1, 2),
        CornerRadius = new CornerRadius(Tokens.Radius.Small),
        Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Hair),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(label, size, weight: FontWeight.Medium),
    };

    /// <summary>Centred "nothing here yet" copy.</summary>
    public static StackPanel EmptyState(string icon, string title, string detail) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = Tokens.Space.Snug,
        Margin = new Thickness(0, Tokens.Space.Page),
        Children =
        {
            new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                Background = Tokens.Brushes.GlassFillRaised,
                BorderBrush = Tokens.Brushes.GlassRim,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = Icons.Make(icon, Tokens.Brushes.Muted, 20),
                },
            },
            Centered(Text(title, Tokens.Fonts.Heading, weight: FontWeight.Medium)),
            Centered(Text(detail, Tokens.Fonts.Body, Tokens.Brushes.Muted)),
        },
    };

    private static TextBlock Centered(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        return text;
    }

    /// <summary>
    /// Text the user may want to copy — a dictation, a message, a summary, an answer: selectable
    /// with the mouse, copied with Ctrl+C or from its right-click menu.
    /// </summary>
    /// <remarks>
    /// Only for content. Labels, buttons and headings stay plain <see cref="Text"/>: a selectable
    /// block takes the pointer, and inside a button it would swallow the click. The highlight
    /// defaults to the primary colour; pass another for text on a primary-tinted bubble, where
    /// that one would not show.
    /// </remarks>
    public static SelectableTextBlock Selectable(
        string text, double size = Tokens.Fonts.Body, IBrush? brush = null,
        FontWeight weight = FontWeight.Normal, IBrush? selection = null)
    {
        var block = new SelectableTextBlock
        {
            Text = text,
            FontFamily = Tokens.Fonts.Sans,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush ?? Tokens.Brushes.Foreground,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            SelectionBrush = selection ?? Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.45),
        };

        // Copies the selection, or the whole text when nothing is selected — right-click then
        // "Copiar" is the quick way to take one message.
        var copy = new MenuItem { Header = "Copiar", InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.C, Avalonia.Input.KeyModifiers.Control) };
        copy.Click += async (_, _) =>
        {
            var selected = block.SelectedText;
            var clipboard = TopLevel.GetTopLevel(block)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(selected.Length > 0 ? selected : block.Text).ConfigureAwait(true);
        };
        var all = new MenuItem { Header = "Selecionar tudo" };
        all.Click += (_, _) => block.SelectAll();
        block.ContextMenu = new ContextMenu { Items = { copy, all } };

        return block;
    }

    /// <summary>
    /// Renders the small Markdown subset the summary model writes: <c>##</c> headings,
    /// <c>-</c> bullets and paragraphs. Bold markers are dropped rather than shown.
    /// </summary>
    /// <remarks>
    /// Headings are bold white capitals. Each section's bullets are dots of the next colour along
    /// <see cref="Tokens.Colors.Spectrum"/>, so the four sections of a summary read apart at a
    /// glance.
    /// </remarks>
    public static StackPanel Markdown(string markdown)
    {
        var panel = new StackPanel { Spacing = Tokens.Space.Tight + 2 };
        var lines = markdown.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var sections = Math.Max(1, lines.Count(l => l.TrimStart().StartsWith('#')));
        var section = -1;
        var accent = Tokens.Colors.Spectrum[0];

        foreach (var raw in lines)
        {
            var line = raw.Trim().Replace("**", string.Empty, StringComparison.Ordinal);
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                section++;
                accent = Tokens.Colors.SpectrumAt(sections == 1 ? 0 : (double)section / (sections - 1));
                var heading = Selectable(line.TrimStart('#').Trim().ToUpperInvariant(), Tokens.Fonts.Body, Brushes.White, FontWeight.Bold);
                heading.LetterSpacing = 0.8;
                heading.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : Tokens.Space.Base, 0, Tokens.Space.Hair);
                panel.Children.Add(heading);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                var bullet = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                bullet.Children.Add(new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                    Background = new SolidColorBrush(accent),
                    BoxShadow = BoxShadows.Parse($"0 0 6 0 #{accent.R:X2}{accent.G:X2}{accent.B:X2}"),
                    VerticalAlignment = VerticalAlignment.Top,
                    // Centred on the first line of a 13 px body.
                    Margin = new Thickness(Tokens.Space.Hair, 7, 0, 0),
                });
                var body = Selectable(line[2..].Trim(), Tokens.Fonts.Body, Tokens.Brushes.Foreground);
                body.LineHeight = 19;
                body.Margin = new Thickness(Tokens.Space.Base - Tokens.Space.Hair, 0, 0, 0);
                Grid.SetColumn(body, 1);
                bullet.Children.Add(body);
                panel.Children.Add(bullet);
            }
            else
            {
                var paragraph = Selectable(line, Tokens.Fonts.Body);
                paragraph.LineHeight = 19;
                panel.Children.Add(paragraph);
            }
        }

        return panel;
    }

    /// <summary>Docks a control and returns it, so it reads inline in a Children list.</summary>
    public static Control Docked(Control control, Dock side)
    {
        DockPanel.SetDock(control, side);
        return control;
    }
}

/// <summary>
/// Live bars driven by the microphone level: the newest reading enters on the right.
/// </summary>
/// <remarks>
/// <para>
/// Each bar takes its colour from <see cref="Tokens.Colors.Spectrum"/> by position, and is
/// lit like a light source: the louder it is, the more opaque, the whiter its core and the
/// wider the halo around it. Silence reads as dim embers; speech flares.
/// </para>
/// <para>
/// Draws itself rather than animating child controls, so a 30 fps refresh costs one render
/// pass and no layout. The halo deliberately paints outside the bounds.
/// </para>
/// </remarks>
public sealed class Waveform : Control
{
    private const double Gap = 2.5;

    private readonly double[] _levels = new double[Tokens.Size.WaveformBars];

    /// <summary>Pushes the latest level, 0…1. Speech RMS rarely exceeds 0.2, so it is scaled up.</summary>
    public void Push(double level)
    {
        Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
        _levels[^1] = Math.Clamp(Math.Sqrt(Math.Max(0, level)) * 3.2, 0, 1);
        InvalidateVisual();
    }

    /// <summary>Drops every bar back to rest.</summary>
    public void Clear()
    {
        Array.Clear(_levels);
        InvalidateVisual();
    }

    /// <summary>The current bar heights, 0…1. Exposed for tests.</summary>
    public IReadOnlyList<double> Levels => _levels;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var count = _levels.Length;
        var width = Math.Max(1, (Bounds.Width - (Gap * (count - 1))) / count);
        var mid = Bounds.Height / 2;
        var rest = Math.Min(3, Bounds.Height);

        for (var i = 0; i < count; i++)
        {
            var level = _levels[i];
            var color = Tokens.Colors.SpectrumAt(count == 1 ? 0 : (double)i / (count - 1));
            var height = Math.Max(rest, level * Bounds.Height);
            var rect = new Rect(i * (width + Gap), mid - (height / 2), width, height);

            // Halo: two soft, wider layers whose strength follows the level.
            if (level > 0.04)
            {
                for (var ring = 2; ring >= 1; ring--)
                {
                    var spread = ring * (1.5 + (level * 2.5));
                    var halo = rect.Inflate(new Thickness(spread, spread * 0.8));
                    var alpha = level * (ring == 1 ? 0.28 : 0.12);
                    context.DrawRectangle(
                        new SolidColorBrush(color, alpha), null, new RoundedRect(halo, (width / 2) + spread));
                }
            }

            context.DrawRectangle(BarFill(color, level), null, new RoundedRect(rect, width / 2));
        }
    }

    /// <summary>
    /// The bar itself: the spectrum colour at the ends, a whiter core in the middle, more
    /// opaque and brighter the louder it is.
    /// </summary>
    internal static LinearGradientBrush BarFill(Color color, double level)
    {
        var core = Tokens.Colors.Mix(color, Avalonia.Media.Colors.White, 0.15 + (level * 0.45));
        var edge = Color.FromArgb((byte)(255 * (0.45 + (level * 0.45))), color.R, color.G, color.B);
        var centre = Color.FromArgb((byte)(255 * (0.55 + (level * 0.45))), core.R, core.G, core.B);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(edge, 0),
                new GradientStop(centre, 0.5),
                new GradientStop(edge, 1),
            },
        };
    }
}

/// <summary>
/// The glass layers shared by the floating windows: a translucent tint over the system blur,
/// a top-down sheen, and a rim that catches the light along the top edge.
/// </summary>
public static class Glass
{
    /// <summary>Tint opacity over a real blur: enough to read text on any wallpaper.</summary>
    public const double Tint = 0.5;

    /// <summary>Tint opacity when the system gives no blur (transparency effects off).</summary>
    public const double SolidTint = 0.94;

    /// <summary>The tint layer, kept separate so its opacity can follow the system's blur.</summary>
    public static Border TintLayer(CornerRadius radius) =>
        new() { CornerRadius = radius, Background = new SolidColorBrush(Tokens.Colors.Overlay, Tint) };

    /// <summary>Stacks tint, sheen, rim and <paramref name="content"/>.</summary>
    public static Grid Panel(Border tint, Control content, CornerRadius radius) => new()
    {
        Children =
        {
            tint,
            new Border { CornerRadius = radius, Background = WhiteGradient((0.13, 0), (0.04, 0.5), (0, 1)) },
            new Border
            {
                CornerRadius = radius,
                BorderThickness = new Thickness(1),
                BorderBrush = WhiteGradient((0.34, 0), (0.1, 0.55), (0.06, 1)),
            },
            content,
        },
    };

    /// <summary>Whether <paramref name="window"/> actually got a blurred backdrop.</summary>
    public static bool IsBlurred(Window window) =>
        window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
        || window.ActualTransparencyLevel == WindowTransparencyLevel.Blur;

    private static LinearGradientBrush WhiteGradient(params (double Alpha, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        };

        foreach (var (alpha, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * alpha), 255, 255, 255), offset));
        }

        return brush;
    }
}
