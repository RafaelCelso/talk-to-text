using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.Abstractions;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// The recording shortcut as key caps; click, press the new keys, and it is saved. Used by
/// Settings and by the first-run setup.
/// </summary>
/// <remarks>
/// While listening, dictation is suspended — pressing the current shortcut, the natural first
/// thing to try, must not start a recording — and Win and Alt are masked so a recorded Win key
/// does not open the Start menu. Leaving the screen mid-recording always lets go of both.
/// </remarks>
public sealed class ShortcutPicker : UserControl
{
    private readonly Composition? _composition;
    private readonly Func<SettingsData, Shortcut> _read;
    private readonly Func<SettingsData, Shortcut, SettingsData> _write;
    private readonly Button _button;
    private ShortcutRecorder _recorder = new();
    private bool _recording;

    /// <summary>A picker for the recording shortcut, over <paramref name="composition"/>'s settings and keyboard hook.</summary>
    public ShortcutPicker(Composition? composition)
        : this(composition, d => d.Shortcut, (d, s) => d with { Shortcut = s }, "Atalho de gravação")
    {
    }

    /// <summary>A picker for whichever shortcut <paramref name="read"/> and <paramref name="write"/> reach.</summary>
    public ShortcutPicker(
        Composition? composition,
        Func<SettingsData, Shortcut> read,
        Func<SettingsData, Shortcut, SettingsData> write,
        string name)
    {
        _composition = composition;
        _read = read;
        _write = write;

        _button = new Button
        {
            MinWidth = 150,
            Height = Tokens.Size.Control + 4,
            Padding = new Thickness(Tokens.Space.Base, 0),
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            Background = Tokens.Brushes.GlassFillRaised,
            BorderBrush = Tokens.Brushes.GlassRimSubtle,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            IsEnabled = composition?.Hotkey is not null,
        };
        _button.Classes.Add("secondary");
        _button.Click += (_, _) => { if (_recording) StopRecording(); else StartRecording(); };
        ToolTip.SetTip(_button, "Clique e pressione a nova combinação");
        AutomationProperties.SetName(_button, name);
        Content = _button;

        if (composition is not null) composition.Settings.Changed += (_, _) => Dispatcher.UIThread.Post(Show);
        Show();
    }

    /// <summary>Whether it is waiting for keys. Exposed for tests.</summary>
    public bool IsRecording => _recording;

    private SettingsData Data => _composition?.Settings.Data ?? new SettingsData();

    private void StartRecording()
    {
        if (_composition?.Hotkey is not { } hotkey) return;

        _recording = true;

        // Fresh each time: a recording abandoned mid-press must not leave keys "held".
        _recorder = new ShortcutRecorder();
        _recorder.Completed += (_, shortcut) =>
        {
            _composition.Settings.Update(_write(Data, shortcut));
            StopRecording();
        };
        _recorder.Cancelled += (_, _) => StopRecording();

        if (_composition.Engine is { } engine) engine.IsSuspended = true;
        hotkey.KeyChanged += OnKey;

        _button.BorderBrush = Tokens.Brushes.Primary;
        _button.Content = Ui.Text("Pressione as teclas…", Tokens.Fonts.Body, Tokens.Brushes.Primary, FontWeight.Medium);
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;

        if (_composition?.Hotkey is { } hotkey) hotkey.KeyChanged -= OnKey;
        if (_composition?.Engine is { } engine) engine.IsSuspended = false;

        _button.BorderBrush = Tokens.Brushes.GlassRimSubtle;
        Show();
    }

    /// <summary>Raised on the hook thread; the recorder and the UI live on the UI thread.</summary>
    private void OnKey(object? sender, KeyTransition transition)
    {
        // Masked here, on the hook thread and while the key is still down: the engine is
        // suspended, so nothing else stops a recorded Win key from opening the Start menu.
        if (transition.IsDown && (Shortcut.Matches(Keys.Windows, transition.VirtualKey)
                                  || Shortcut.Matches(Keys.Alt, transition.VirtualKey)))
        {
            _composition?.Hotkey?.MaskModifierRelease();
        }

        Dispatcher.UIThread.Post(() => { if (_recording) _recorder.Handle(transition); });
    }

    private void Show()
    {
        if (_recording) return;
        _button.Content = Ui.KeyCaps(_read(Data));
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Never leave dictation suspended because the picker went away mid-recording.
        StopRecording();
        base.OnDetachedFromVisualTree(e);
    }
}
