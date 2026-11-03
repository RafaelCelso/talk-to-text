using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Media;

namespace TalkToText.App.Design;

/// <summary>
/// The design system, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on OpenWhispr's open-source (MIT) theme: charcoal surfaces stepped in small
/// lightness increments, one brand blue, tight 6–12 px rounding and soft "glass" edges. The
/// app is dark-only, so every value here is the dark one.
/// </para>
/// <para>
/// <b>Views must not contain literal values.</b> If a control needs a number that isn't here,
/// add the token rather than inlining it.
/// </para>
/// <para>
/// <b>Blue means "this is the action" and red means "recording".</b> Neither is used for
/// decoration.
/// </para>
/// </remarks>
public static class Tokens
{
    /// <summary>Colours.</summary>
    public static class Colors
    {
        /// <summary>The window backdrop behind the sidebar and the content container.</summary>
        public static Color Window => Rgb(0x131416);

        /// <summary>The inset content container.</summary>
        public static Color Surface => Rgb(0x1A1B1E);

        /// <summary>One step up: cards and list rows.</summary>
        public static Color Card => Rgb(0x222327);

        /// <summary>Two steps up: hovered rows, inputs, key caps.</summary>
        public static Color Raised => Rgb(0x2C2D32);

        /// <summary>Default border.</summary>
        public static Color Border => Rgb(0x34353A);

        /// <summary>Hairlines between rows and around the container.</summary>
        public static Color BorderSubtle => Rgb(0x2A2B30);

        /// <summary>Primary text.</summary>
        public static Color Foreground => Rgb(0xE8E8E8);

        /// <summary>Secondary text: captions, timestamps, hints.</summary>
        public static Color Muted => Rgb(0x8F9096);

        /// <summary>The brand blue.</summary>
        public static Color Primary => Rgb(0x4577E9);

        /// <summary>Top of the brand glass gradient.</summary>
        public static Color PrimaryTop => Rgb(0x5588F0);

        /// <summary>Bottom of the brand glass gradient.</summary>
        public static Color PrimaryBottom => Rgb(0x3466E2);

        /// <summary>Text on blue.</summary>
        public static Color OnPrimary => Rgb(0xFFFFFF);

        /// <summary>Recording, and nothing else.</summary>
        public static Color Recording => Rgb(0xEF4444);

        /// <summary>Destructive actions.</summary>
        public static Color Destructive => Rgb(0xE5484D);

        /// <summary>Healthy status.</summary>
        public static Color Success => Rgb(0x3FB950);

        /// <summary>Needs attention.</summary>
        public static Color Warning => Rgb(0xE3A23B);

        /// <summary>The overlay pill's tint over the blurred backdrop.</summary>
        public static Color Overlay => Rgb(0x16171B);

        /// <summary>
        /// The waveform's colours, left to right: sky, the brand blue, violet, pink. The blue and
        /// violet are OpenWhispr's primary and agent accents.
        /// </summary>
        public static IReadOnlyList<Color> Spectrum { get; } =
            [Rgb(0x38BDF8), Rgb(0x4577E9), Rgb(0x8B5CF6), Rgb(0xEC4899)];

        /// <summary>The colour at <paramref name="t"/> (0…1) along <see cref="Spectrum"/>.</summary>
        public static Color SpectrumAt(double t)
        {
            var stops = Spectrum;
            var scaled = Math.Clamp(t, 0, 1) * (stops.Count - 1);
            var i = Math.Min((int)scaled, stops.Count - 2);
            return Mix(stops[i], stops[i + 1], scaled - i);
        }

        /// <summary>Linear blend from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));

        private static Color Rgb(uint hex) => Color.FromRgb(
            (byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));
    }

    /// <summary>Brushes for the colours above.</summary>
    public static class Brushes
    {
        /// <summary><see cref="Colors.Window"/>.</summary>
        public static IBrush Window => new SolidColorBrush(Colors.Window);

        /// <summary><see cref="Colors.Surface"/>.</summary>
        public static IBrush Surface => new SolidColorBrush(Colors.Surface);

        /// <summary><see cref="Colors.Card"/>.</summary>
        public static IBrush Card => new SolidColorBrush(Colors.Card);

        /// <summary><see cref="Colors.Raised"/>.</summary>
        public static IBrush Raised => new SolidColorBrush(Colors.Raised);

        /// <summary><see cref="Colors.Border"/>.</summary>
        public static IBrush Border => new SolidColorBrush(Colors.Border);

        /// <summary><see cref="Colors.BorderSubtle"/>.</summary>
        public static IBrush BorderSubtle => new SolidColorBrush(Colors.BorderSubtle);

        /// <summary><see cref="Colors.Foreground"/>.</summary>
        public static IBrush Foreground => new SolidColorBrush(Colors.Foreground);

        /// <summary><see cref="Colors.Muted"/>.</summary>
        public static IBrush Muted => new SolidColorBrush(Colors.Muted);

        /// <summary><see cref="Colors.Primary"/>.</summary>
        public static IBrush Primary => new SolidColorBrush(Colors.Primary);

        /// <summary><see cref="Colors.Recording"/>.</summary>
        public static IBrush Recording => new SolidColorBrush(Colors.Recording);

        /// <summary><see cref="Colors.Destructive"/>.</summary>
        public static IBrush Destructive => new SolidColorBrush(Colors.Destructive);

        /// <summary>The brand "glass" fill: a short vertical blue gradient.</summary>
        public static IBrush PrimaryGlass => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.PrimaryTop, 0),
                new GradientStop(Colors.PrimaryBottom, 1),
            },
        };

        /// <summary><paramref name="color"/> at <paramref name="opacity"/>.</summary>
        public static IBrush Tint(Color color, double opacity) => new SolidColorBrush(color, opacity);

        // Glass. Every surface is translucent white over the dark shell rather than a grey of
        // its own, lit from the top-left: a diagonal frost for the fill, and a rim brightest
        // along the top edge. The meeting summary card set the look; these carry it everywhere.

        /// <summary>The frosted fill of a card, row or quiet button.</summary>
        public static IBrush GlassFill => WhiteGradient(Diagonal, (0.075, 0), (0.035, 0.5), (0.02, 1));

        /// <summary>A brighter frost: hovered and selected rows, inputs.</summary>
        public static IBrush GlassFillRaised => WhiteGradient(Diagonal, (0.12, 0), (0.065, 0.5), (0.04, 1));

        /// <summary>The rim of a glass surface: light catching the top edge.</summary>
        public static IBrush GlassRim => WhiteGradient(Vertical, (0.24, 0), (0.08, 0.5), (0.05, 1));

        /// <summary>A fainter rim, for small controls and rows.</summary>
        public static IBrush GlassRimSubtle => WhiteGradient(Vertical, (0.16, 0), (0.06, 0.5), (0.04, 1));

        /// <summary>A hairline between rows that fades out at both ends.</summary>
        public static IBrush GlassHairline => WhiteGradient(Horizontal, (0.02, 0), (0.11, 0.15), (0.11, 0.85), (0.02, 1));

        /// <summary>The rim on the brand-blue buttons: a bright top edge over the blue.</summary>
        public static IBrush PrimaryRim => WhiteGradient(Vertical, (0.4, 0), (0.08, 0.5), (0, 1));

        /// <summary>The page container: dark enough to read on, with a faint frost on top.</summary>
        public static IBrush ShellGlass => new LinearGradientBrush
        {
            StartPoint = Diagonal.Start,
            EndPoint = Diagonal.End,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xD0, 0x24, 0x25, 0x2A), 0),
                new GradientStop(Color.FromArgb(0xD8, 0x1A, 0x1B, 0x1E), 0.5),
                new GradientStop(Color.FromArgb(0xE0, 0x17, 0x18, 0x1B), 1),
            },
        };

        private static readonly (RelativePoint Start, RelativePoint End) Vertical =
            (new RelativePoint(0.5, 0, RelativeUnit.Relative), new RelativePoint(0.5, 1, RelativeUnit.Relative));

        private static readonly (RelativePoint Start, RelativePoint End) Horizontal =
            (new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative));

        private static readonly (RelativePoint Start, RelativePoint End) Diagonal =
            (new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative));

        private static LinearGradientBrush WhiteGradient((RelativePoint Start, RelativePoint End) direction, params (double Alpha, double Offset)[] stops)
        {
            var brush = new LinearGradientBrush { StartPoint = direction.Start, EndPoint = direction.End };
            foreach (var (alpha, offset) in stops)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * alpha), 255, 255, 255), offset));
            }

            return brush;
        }
    }

    /// <summary>Type.</summary>
    public static class Fonts
    {
        /// <summary>
        /// Inter, bundled with the app (Avalonia.Fonts.Inter), with Segoe UI behind it.
        /// OpenWhispr's own face is licensed; Inter is the nearest open equivalent.
        /// </summary>
        public static FontFamily Sans { get; } = new("fonts:Inter#Inter, Segoe UI, sans-serif");

        /// <summary>Timers. Monospaced so digits don't shift as they tick.</summary>
        public static FontFamily Mono { get; } = new("Cascadia Mono, Consolas, monospace");

        /// <summary>Small captions and timestamps.</summary>
        public const double Caption = 11;

        /// <summary>Labels and secondary text.</summary>
        public const double Label = 12;

        /// <summary>Body text.</summary>
        public const double Body = 13;

        /// <summary>Section headings.</summary>
        public const double Heading = 15;

        /// <summary>Page titles.</summary>
        public const double Title = 20;
    }

    /// <summary>Spacing, on a 4 px grid.</summary>
    public static class Space
    {
        /// <summary>2 px.</summary>
        public const double Hair = 2;

        /// <summary>4 px.</summary>
        public const double Tight = 4;

        /// <summary>8 px.</summary>
        public const double Snug = 8;

        /// <summary>12 px.</summary>
        public const double Base = 12;

        /// <summary>16 px.</summary>
        public const double Roomy = 16;

        /// <summary>24 px.</summary>
        public const double Wide = 24;

        /// <summary>32 px.</summary>
        public const double Page = 32;
    }

    /// <summary>Corner radii — OpenWhispr's "ultra-tight" rounding.</summary>
    public static class Radius
    {
        /// <summary>Badges and key caps.</summary>
        public const double Small = 4;

        /// <summary>Buttons and inputs.</summary>
        public const double Control = 6;

        /// <summary>Cards and rows.</summary>
        public const double Card = 8;

        /// <summary>The inset content container.</summary>
        public const double Shell = 12;

        /// <summary>Fully round: the recording pill and circular buttons.</summary>
        public const double Pill = 999;
    }

    /// <summary>Fixed sizes.</summary>
    public static class Size
    {
        /// <summary>Sidebar width.</summary>
        public const double Sidebar = 216;

        /// <summary>Standard control height.</summary>
        public const double Control = 34;

        /// <summary>Nav item height.</summary>
        public const double NavItem = 36;

        /// <summary>Inline icons.</summary>
        public const double Icon = 16;

        /// <summary>The big microphone button.</summary>
        public const double MicButton = 64;

        /// <summary>The recording pill.</summary>
        public const double PillWidth = 200;

        /// <summary>The recording pill.</summary>
        public const double PillHeight = 44;

        /// <summary>Gap between the pill and the bottom of the work area.</summary>
        public const double PillBottomOffset = 48;

        /// <summary>Waveform bars in the pill.</summary>
        public const int WaveformBars = 18;
    }

    /// <summary>Shadows.</summary>
    public static class Elevation
    {
        /// <summary>The faint glass rim on cards.</summary>
        public static BoxShadows Card { get; } = BoxShadows.Parse("0 1 3 0 #33000000");

        /// <summary>A glass card floating over the shell.</summary>
        public static BoxShadows Glass { get; } = BoxShadows.Parse("0 12 30 -14 #B0000000");

        /// <summary>The overlay pill: a deep, soft lift.</summary>
        public static BoxShadows Overlay { get; } = BoxShadows.Parse("0 12 32 -8 #A0000000");

        /// <summary>The brand button's coloured glow.</summary>
        public static BoxShadows Primary { get; } = BoxShadows.Parse("0 8 20 -8 #803466E2");
    }

    /// <summary>Motion.</summary>
    public static class Motion
    {
        /// <summary>OpenWhispr's standard curve: fast start, long settle. Never ease-in.</summary>
        public static Easing Standard { get; } = new SplineEasing(0.2, 0, 0, 1);

        /// <summary>Enter and exit of the overlay pill.</summary>
        public static TimeSpan Overlay { get; } = TimeSpan.FromMilliseconds(280);

        /// <summary>Hover and selection changes.</summary>
        public static TimeSpan Quick { get; } = TimeSpan.FromMilliseconds(150);

        /// <summary>Distance the pill travels on entry.</summary>
        public const double OverlayRise = 16;

        /// <summary>Waveform refresh interval.</summary>
        public static TimeSpan Frame { get; } = TimeSpan.FromMilliseconds(33);
    }
}
