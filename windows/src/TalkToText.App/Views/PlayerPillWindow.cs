using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TalkToText.App.Controls;
using TalkToText.App.Design;

namespace TalkToText.App.Views;

/// <summary>
/// The player as a glass pill at the bottom centre of the screen — where the recording pill
/// appears — while the app is minimised or closed to the tray and audio is playing.
/// </summary>
/// <remarks>
/// Like the recording pill it never takes focus, so using it does not pull the user out of the
/// app they are in; its buttons still take clicks. When dictation starts, the recording pill
/// needs that spot, so this one moves up above it.
/// </remarks>
public sealed class PlayerPillWindow : Window
{
    private const double PillWidth = 560;
    private const double PillHeight = 56;
    private const double Gap = 10;

    private readonly Border _tint;
    private bool _styled;

    /// <summary>A pill over <paramref name="hub"/>.</summary>
    public PlayerPillWindow(PlaybackHub hub)
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Width = PillWidth;
        Height = PillHeight;
        Title = "Talk-to-Text — reprodução";

        var radius = new CornerRadius(PillHeight / 2);
        _tint = Glass.TintLayer(radius);
        Player = new MiniPlayer(hub, PlayerLayout.Pill) { Margin = new Thickness(Tokens.Space.Roomy + 2, 0, Tokens.Space.Snug, 0) };
        Content = Glass.Panel(_tint, Player, radius);
    }

    /// <summary>The controls inside.</summary>
    public MiniPlayer Player { get; }

    /// <summary>Shows the pill at the bottom centre, or above the recording pill when that is up.</summary>
    public void ShowAtBottom(bool recordingPillVisible)
    {
        if (Screens?.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var width = (int)(PillWidth * scale);
            var height = (int)(PillHeight * scale);
            var lift = recordingPillVisible ? (int)((Tokens.Size.PillHeight + Gap) * scale) : 0;
            Position = new PixelPoint(
                area.X + ((area.Width - width) / 2),
                area.Y + area.Height - height - (int)(Tokens.Size.PillBottomOffset * scale) - lift);
        }

        if (IsVisible) return;
        Show();

        if (!_styled && TryGetPlatformHandle() is { } handle)
        {
            PlatformFactory.MakeOverlay(handle.Handle);
            var scale = Screens?.Primary?.Scaling ?? 1;
            var w = (int)Math.Round(PillWidth * scale);
            var h = (int)Math.Round(PillHeight * scale);
            PlatformFactory.SetRoundedRegion(handle.Handle, 0, 0, w, h, h / 2);
            _styled = true;
        }

        var glass = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                    || ActualTransparencyLevel == WindowTransparencyLevel.Blur;
        _tint.Background = new SolidColorBrush(Tokens.Colors.Overlay, glass ? Glass.Tint : Glass.SolidTint);
    }
}
