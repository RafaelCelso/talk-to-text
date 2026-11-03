using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// The home page: a card to dictate from, and the history below it.
/// </summary>
public sealed class HomeView : UserControl
{
    private readonly Composition? _composition;
    private readonly Button _mic;
    private readonly TextBlock _status;
    private readonly TextBlock _timer;
    private readonly Waveform _waveform;
    private readonly ContentControl _keys = new();
    private readonly TextBlock _verb;
    private DateTimeOffset? _fakeStartedAt;

    /// <summary>Builds the page over <paramref name="composition"/>, or with nothing behind it.</summary>
    public HomeView(Composition? composition)
    {
        _composition = composition;

        _mic = new Button
        {
            Width = Tokens.Size.MicButton,
            Height = Tokens.Size.MicButton,
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            Background = Tokens.Brushes.PrimaryGlass,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = Icons.Make(Icons.Mic, new SolidColorBrush(Tokens.Colors.OnPrimary), 26),
        };
        _mic.Classes.Add("primary");
        _mic.Click += (_, _) => ToggleRecording();
        ToolTip.SetTip(_mic, "Gravar / parar");

        _status = Ui.Text("Pronto para ditar", Tokens.Fonts.Heading, weight: FontWeight.SemiBold);
        _timer = Ui.Text(string.Empty, Tokens.Fonts.Body, Tokens.Brushes.Muted);
        _timer.FontFamily = Tokens.Fonts.Mono;
        _waveform = new Waveform
        {
            Width = 120,
            Height = 22,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _verb = Ui.Text("Segure", Tokens.Fonts.Body, Tokens.Brushes.Muted);

        // Wraps rather than clipping when the window is narrow.
        var shortcutLine = new WrapPanel
        {
            ItemSpacing = Tokens.Space.Snug,
            LineSpacing = Tokens.Space.Tight,
            Children = { _verb, _keys, Ui.Text("em qualquer aplicativo para ditar.", Tokens.Fonts.Body, Tokens.Brushes.Muted) },
        };

        var statusLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            Children = { _status, _waveform, _timer },
        };

        // A grid, not a horizontal stack: a stack gives its children unlimited width, so the
        // shortcut line would clip instead of wrapping.
        var text = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Tokens.Space.Wide - 4, 0, 0, 0),
            Children = { statusLine, shortcutLine },
        };
        Grid.SetColumn(text, 1);

        var hero = Ui.Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { _mic, text },
        }, Tokens.Space.Wide - 4);

        var page = new DockPanel { Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Page - 4, Tokens.Space.Page, 0) };
        page.Children.Add(Ui.Docked(Ui.PageHeader("Início", "Fale e o texto aparece onde o cursor estiver."), Dock.Top));
        page.Children.Add(Ui.Docked(hero, Dock.Top));

        // Off Windows there is no engine to give a model to, so nothing to download.
        if (composition is { IsPlatformAvailable: true })
        {
            ModelBanner = new SpeechModelBanner(composition);
            page.Children.Add(Ui.Docked(ModelBanner, Dock.Top));
        }

        var historyTitle = Ui.SectionTitle("Histórico");
        historyTitle.Margin = new Thickness(0, Tokens.Space.Page - 4, 0, Tokens.Space.Base);
        page.Children.Add(Ui.Docked(historyTitle, Dock.Top));

        page.Children.Add(composition is null
            ? Ui.EmptyState(Icons.Mic, "Nenhuma transcrição ainda", "Segure o atalho e fale.")
            : new TranscriptionsView(composition.Transcripts));

        Content = page;
        UpdateShortcut(composition?.Settings.Data ?? new SettingsData());
    }

    /// <summary>Whether recording is on. Exposed for headless tests.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>The waveform. Exposed for headless tests.</summary>
    public Waveform Waveform => _waveform;

    /// <summary>The speech-model notice, when the platform layer is there. Exposed for headless tests.</summary>
    public SpeechModelBanner? ModelBanner { get; }

    /// <summary>The status line. Exposed for headless tests.</summary>
    public string StatusText => _status.Text ?? string.Empty;

    /// <summary>Shows the configured shortcut.</summary>
    public void UpdateShortcut(SettingsData data)
    {
        _keys.Content = Ui.KeyCaps(data.Shortcut);
        _verb.Text = data.Mode == ActivationMode.Hold ? "Segure" : "Pressione";
    }

    /// <summary>Starts or stops recording, as the microphone button does.</summary>
    public void ToggleRecording()
    {
        // With no engine — a headless test — the page still toggles so its states can be
        // exercised.
        if (_composition?.Engine is not { } engine)
        {
            _fakeStartedAt = _fakeStartedAt is null ? DateTimeOffset.Now : null;
            Show(_fakeStartedAt is null ? DictationState.Idle : DictationState.Recording, 0, _fakeStartedAt);
            return;
        }

        engine.TogglePushToTalk();
        Sync();
    }

    /// <summary>Pulls state from the engine. Called by the window's frame timer.</summary>
    public void Sync()
    {
        if (_composition?.Engine is not { } engine)
        {
            if (_fakeStartedAt is not null) Show(DictationState.Recording, 0, _fakeStartedAt);
            return;
        }

        Show(engine.State, engine.Level, engine.RecordingStartedAt);
    }

    private void Show(DictationState state, double level, DateTimeOffset? startedAt)
    {
        var wasRecording = IsRecording;
        IsRecording = state == DictationState.Recording;

        // This runs every frame; only rebuild the icon when the state actually flips.
        if (wasRecording != IsRecording || _mic.Content is null)
        {
            _mic.Classes.Set("recording", IsRecording);
            _mic.Content = Icons.Make(IsRecording ? Icons.Stop : Icons.Mic, new SolidColorBrush(Tokens.Colors.OnPrimary), 26);
        }

        _status.Text = state switch
        {
            DictationState.Recording => "Gravando",
            DictationState.Transcribing => "Transcrevendo…",
            _ => "Pronto para ditar",
        };

        _waveform.IsVisible = IsRecording;
        if (IsRecording) _waveform.Push(level);
        else _waveform.Clear();

        if (IsRecording && startedAt is { } start)
        {
            var elapsed = DateTimeOffset.Now - start;
            _timer.Text = string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes:0}:{elapsed.Seconds:00}");
        }
        else
        {
            _timer.Text = string.Empty;
        }
    }
}
