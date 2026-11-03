using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>The pages the sidebar switches between.</summary>
public enum Page
{
    /// <summary>Dictate and see past transcriptions.</summary>
    Home,

    /// <summary>Meeting transcripts, as chats.</summary>
    Meetings,

    /// <summary>Transcribe and summarise a recorded audio file.</summary>
    AudioFiles,

    /// <summary>Ask questions about meetings and recordings.</summary>
    Chat,

    /// <summary>Words it should spell a particular way.</summary>
    Dictionary,

    /// <summary>Shortcut, behaviour, model.</summary>
    Settings,
}

/// <summary>
/// The main window: a sidebar on the window backdrop, and an inset rounded container holding
/// the selected page — OpenWhispr's control-panel layout.
/// </summary>
/// <remarks>
/// Built in code rather than XAML, deliberately: every value comes from <see cref="Tokens"/>,
/// and in C# a stray literal is visible in review.
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly Composition? _composition;
    private readonly MeetingCoordinator? _meetings;
    private readonly Dictionary<Page, Button> _nav = [];
    private readonly ContentControl _pageHost = new();
    private readonly HomeView _home;
    private readonly TextBlock _shortcutHint;
    private readonly DispatcherTimer _poll;

    private Control? _dictionary;
    private Control? _meetingsView;
    private Control? _audioFiles;
    private Control? _chat;
    private readonly Border _floating;
    private Control? _settings;

    /// <summary>Builds a window with no engine behind it. Used by headless tests.</summary>
    public MainWindow() : this(null) { }

    /// <summary>Builds the window over <paramref name="composition"/>.</summary>
    public MainWindow(Composition? composition, MeetingCoordinator? meetings = null)
    {
        _composition = composition;
        _meetings = meetings;

        Title = "Talk-to-Text";
        MinWidth = 760;
        MinHeight = 540;
        Width = 960;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Glass for real behind the sidebar: Windows blurs the desktop under the window, and a
        // dark tint keeps text readable on any wallpaper. With transparency effects off the
        // system gives no blur, and the tint turns opaque.
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica];
        UpdateBackdrop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty) UpdateBackdrop();
        };

        _home = new HomeView(composition);
        _shortcutHint = Ui.Text(string.Empty, Tokens.Fonts.Caption, Tokens.Brushes.Muted);

        Playback = new PlaybackHub(PlatformFactory.CreateAudioPlayer);

        // The floating mini player: the recording keeps playing on other pages, and its
        // controls float over them, bottom-right, until its transcript is back on screen.
        var mini = new MiniPlayer(Playback, PlayerLayout.Full);
        mini.OpenRequested += (_, id) => OpenAudioTranscript(id);
        _floating = new Border
        {
            Width = 360,
            Margin = new Thickness(0, 0, Tokens.Space.Wide, Tokens.Space.Wide),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Tokens.Colors.Overlay, 0.94),
            BorderBrush = Tokens.Brushes.GlassRim,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.Radius.Shell),
            BoxShadow = Tokens.Elevation.Overlay,
            Padding = new Thickness(Tokens.Space.Base),
            Child = mini,
            IsVisible = false,
        };
        Playback.Changed += (_, _) => UpdateFloating();

        Content = new Grid
        {
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        Ui.Docked(BuildSidebar(), Dock.Left),
                        new Border
                        {
                            Margin = new Thickness(0, Tokens.Space.Snug, Tokens.Space.Snug, Tokens.Space.Snug),
                            Background = Tokens.Brushes.ShellGlass,
                            BorderBrush = Tokens.Brushes.GlassRim,
                            BorderThickness = new Thickness(1),
                            BoxShadow = Tokens.Elevation.Glass,
                            CornerRadius = new CornerRadius(Tokens.Radius.Shell),
                            ClipToBounds = true,
                            Child = _pageHost,
                        },
                    },
                },
                _floating,
            },
        };

        ShowPage(Page.Home);

        // Polled rather than pushed: the engine raises Changed on a background thread at
        // buffer rate, far more often than a display needs.
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tokens.Motion.Frame };
        _poll.Tick += (_, _) => _home.Sync();
        _poll.Start();

        if (_composition is not null)
        {
            _composition.Settings.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateShortcutHint);
            _composition.Engine?.Start();
        }

        UpdateShortcutHint();
    }

    /// <summary>The recording being played, shared by every player in the app.</summary>
    public PlaybackHub Playback { get; }

    /// <summary>Whether the floating mini player is showing. Exposed for tests.</summary>
    public bool IsFloatingPlayerVisible => _floating.IsVisible;

    /// <summary>Goes to the audio page with the transcript <paramref name="id"/> open.</summary>
    public void OpenAudioTranscript(Guid id)
    {
        ShowPage(Page.AudioFiles);
        if (_audioFiles is AudioFilesView view) view.Select(id);
    }

    /// <summary>The mini player shows while something plays and its transcript is not on screen.</summary>
    private void UpdateFloating() => _floating.IsVisible = Playback.IsActive && !Playback.IsShownInline;

    /// <summary>The page currently shown.</summary>
    public Page CurrentPage { get; private set; }

    /// <summary>The home page. Exposed for headless tests.</summary>
    public HomeView Home => _home;

    /// <summary>Switches page.</summary>
    public void ShowPage(Page page)
    {
        CurrentPage = page;
        foreach (var (key, button) in _nav) button.Classes.Set("selected", key == page);

        // Built once and kept: rebuilding would drop search text and scroll position.
        _pageHost.Content = page switch
        {
            Page.Dictionary => _dictionary ??= _composition is null
                ? Ui.EmptyState(Icons.Book, "Dicionário vazio", "Adicione palavras que ele erra.")
                : new DictionaryView(_composition.Dictionary),
            Page.Settings => _settings ??= new SettingsView(_composition),
            Page.AudioFiles => _audioFiles ??= new AudioFilesView(_composition, _meetings, Playback),
            Page.Chat => _chat ??= new ChatPage(_composition, _meetings),
            Page.Meetings => _meetingsView ??= _composition is null
                ? Ui.EmptyState(Icons.Chat, "Nenhuma reunião ainda", "As transcrições de reunião aparecem aqui.")
                : new MeetingsView(_composition.Meetings, _meetings),
            _ => _home,
        };
    }

    /// <summary>Starts or stops recording, as the microphone button does. For headless tests.</summary>
    public void ToggleRecording() => _home.ToggleRecording();

    private DockPanel BuildSidebar()
    {
        var nav = new StackPanel { Spacing = Tokens.Space.Hair };
        nav.Children.Add(NavItem(Page.Home, Icons.Home, "Início"));
        nav.Children.Add(NavItem(Page.Meetings, Icons.Chat, "Reuniões"));
        nav.Children.Add(NavItem(Page.AudioFiles, Icons.AudioFile, "Transcrever áudio"));
        nav.Children.Add(NavItem(Page.Chat, Icons.Messages, "Chat"));
        nav.Children.Add(NavItem(Page.Dictionary, Icons.Book, "Dicionário"));
        nav.Children.Add(NavItem(Page.Settings, Icons.Settings, "Configurações"));

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug - 2,
            Margin = new Thickness(Tokens.Space.Snug - 3, Tokens.Space.Tight - 3, 0, Tokens.Space.Wide - 3),
            Children =
            {
                // The real app icon, as in the taskbar: 34 px shows about 28 of artwork.
                Ui.AppIcon(34),
                Ui.Text("Talk-to-Text", Tokens.Fonts.Heading, weight: FontWeight.SemiBold),
            },
        };

        var footer = new StackPanel
        {
            Spacing = Tokens.Space.Tight,
            Margin = new Thickness(Tokens.Space.Snug, 0, Tokens.Space.Snug, Tokens.Space.Tight),
            Children = { ModelStatus(), _shortcutHint },
        };

        return new DockPanel
        {
            Width = Tokens.Size.Sidebar,
            Margin = new Thickness(Tokens.Space.Base, Tokens.Space.Roomy, Tokens.Space.Base, Tokens.Space.Roomy),
            Children =
            {
                Ui.Docked(brand, Dock.Top),
                Ui.Docked(footer, Dock.Bottom),
                nav,
            },
        };
    }

    private Button NavItem(Page page, string icon, string label)
    {
        var button = new Button
        {
            Height = Tokens.Size.NavItem,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            Padding = new Thickness(Tokens.Space.Snug + 2, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Snug + 2,
                Children =
                {
                    Icons.Make(icon, Tokens.Brushes.Muted),
                    Ui.Text(label, Tokens.Fonts.Body, weight: FontWeight.Medium),
                },
            },
        };
        button.Classes.Add("nav");
        button.Click += (_, _) => ShowPage(page);
        _nav[page] = button;
        return button;
    }

    /// <summary>The window tint: translucent over a real blur, opaque without one.</summary>
    private void UpdateBackdrop()
    {
        var blurred = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                      || ActualTransparencyLevel == WindowTransparencyLevel.Mica;
        Background = new SolidColorBrush(Tokens.Colors.Window, blurred ? 0.78 : 1);
    }

    private StackPanel ModelStatus()
    {
        var ready = _composition?.IsModelInstalled ?? false;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                Ui.Dot(ready ? Tokens.Colors.Success : Tokens.Colors.Warning, 7),
                Ui.Text(ready ? "Modelo pronto" : "Modelo não instalado", Tokens.Fonts.Caption, Tokens.Brushes.Muted),
            },
        };
    }

    private void UpdateShortcutHint()
    {
        var data = _composition?.Settings.Data ?? new SettingsData();
        var verb = data.Mode == ActivationMode.Hold ? "Segure" : "Pressione";
        _shortcutHint.Text = $"{verb} {data.Shortcut} para ditar";
        _home.UpdateShortcut(data);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The close button hides the window rather than closing it: the app keeps running in
    /// the tray, and Avalonia cannot re-show a closed window — "Abrir" from the tray menu
    /// crashed the app after the window had once been closed. Only quitting really closes it.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        base.OnClosed(e);
    }
}
