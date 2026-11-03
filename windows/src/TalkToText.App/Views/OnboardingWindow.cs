using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;
using TalkToText.Speech;

namespace TalkToText.App.Views;

/// <summary>
/// The first-run setup, shown once after installing: your name, how to dictate, the shortcut,
/// the local AI download and the AI instruction — every step skippable.
/// </summary>
/// <remarks>
/// <para>
/// Each "Continuar" saves that step at once, so leaving half-way keeps what was set. Skipping
/// a step leaves the default; skipping the whole setup, or closing the window, marks it done
/// all the same — it never comes back uninvited (Settings can open it again).
/// </para>
/// <para>
/// The AI download can be started here and keeps going in the background while the user moves
/// on, or even after the window closes.
/// </para>
/// </remarks>
public sealed class OnboardingWindow : Window
{
    private const int Steps = 6;

    private readonly Composition? _composition;
    private readonly ContentControl _page = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stepLabel = Ui.Text(string.Empty, Tokens.Fonts.Caption, Tokens.Brushes.Muted);
    private readonly Button _back = Ui.Secondary("Voltar");
    private readonly Button _skip = Ui.Secondary("Pular");
    private readonly Button _next = Ui.Primary("Continuar");
    private readonly Border _footer;

    private readonly TextBox _name;
    private readonly TextBox _instruction;
    private ActivationMode _mode;
    private readonly List<(ActivationMode Mode, Button Card)> _modeCards = [];

    private readonly ProgressBar _downloadBar = new() { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, IsVisible = false };
    private readonly TextBlock _downloadStatus = Ui.Text(string.Empty, Tokens.Fonts.Label, Tokens.Brushes.Muted);
    private readonly Button _download = Ui.Primary("Baixar agora (cerca de 2 GB)", Icons.Upload);
    private CancellationTokenSource? _downloading;
    private readonly StackPanel _localAi = new() { Spacing = Tokens.Space.Base };
    private AiChoicePicker? _aiChoice;

    private readonly TextBlock _testHint = Ui.Text(string.Empty, Tokens.Fonts.Body, Tokens.Brushes.Muted);
    private readonly TextBlock _testStatus = Ui.Text(string.Empty, Tokens.Fonts.Body);
    private readonly Border _testLevel = new() { Width = 0, Height = 8, CornerRadius = new CornerRadius(Tokens.Radius.Pill), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Avalonia.Threading.DispatcherTimer _testTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string? _heard;

    private readonly Dictionary<int, Control> _pages = [];
    private int _step;

    /// <summary>The setup over <paramref name="composition"/>'s settings.</summary>
    public OnboardingWindow(Composition? composition)
    {
        _composition = composition;
        var data = Data;
        _mode = data.Mode;

        Title = "Bem-vindo ao Talk-to-Text";
        Width = 640;
        Height = 580;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Tokens.Colors.Surface);

        _name = Ui.Field("Seu nome", data.DisplayName ?? string.Empty);
        _name.Width = 360;
        _name.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(_name, "Seu nome");

        _instruction = new TextBox
        {
            Text = data.AiInstruction ?? string.Empty,
            Watermark = "Descreva quem você é, do que tratam as gravações e o que a IA deve destacar no resumo.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 130,
            MaxHeight = 200,
            FontFamily = Tokens.Fonts.Sans,
            FontSize = Tokens.Fonts.Body,
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(_instruction, "Instrução para a IA");

        _download.Click += async (_, _) => await DownloadAsync().ConfigureAwait(true);

        _testLevel.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Tokens.Colors.SpectrumAt(0), 0),
                new GradientStop(Tokens.Colors.SpectrumAt(1), 1),
            },
        };
        _testStatus.TextWrapping = TextWrapping.Wrap;
        _testTimer.Tick += (_, _) => ShowTest();
        if (composition?.Engine is { } engine) engine.Completed += OnDictated;

        _back.Click += (_, _) => Go(_step - 1);
        _skip.Click += (_, _) => Go(_step + 1);
        _next.Click += (_, _) =>
        {
            Save();
            Go(_step + 1);
        };
        AutomationProperties.SetName(_back, "Voltar");
        AutomationProperties.SetName(_skip, "Pular");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _skip, _next },
        };
        Grid.SetColumn(buttons, 1);

        _footer = new Border
        {
            Padding = new Thickness(Tokens.Space.Page, Tokens.Space.Roomy),
            BorderBrush = Tokens.Brushes.GlassHairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _back, buttons } },
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Wide, Tokens.Space.Page, 0),
            Children = { _dots },
        };
        Grid.SetColumn(_stepLabel, 1);
        header.Children.Add(_stepLabel);

        _page.Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Wide, Tokens.Space.Page, Tokens.Space.Roomy);

        Content = new DockPanel
        {
            Children =
            {
                Ui.Docked(header, Dock.Top),
                Ui.Docked(_footer, Dock.Bottom),
                _page,
            },
        };

        Go(0);
    }

    /// <summary>The step showing: 0 is the welcome, 1–6 the steps. Exposed for tests.</summary>
    public int Step => _step;

    private SettingsData Data => _composition?.Settings.Data ?? new SettingsData();

    private void Update(SettingsData data) => _composition?.Settings.Update(data);

    /// <summary>Goes to <paramref name="step"/>; past the last one, finishes.</summary>
    public void Go(int step)
    {
        if (step > Steps)
        {
            Finish();
            return;
        }

        _step = Math.Max(0, step);

        // Each page is built once and kept: the name, instruction and download controls are
        // single instances, and putting one in a second page while the first still holds it
        // throws — going back used to crash the app.
        if (!_pages.TryGetValue(_step, out var page))
        {
            page = _step switch
            {
                0 => Welcome(),
                1 => NamePage(),
                2 => ModePage(),
                3 => ShortcutPage(),
                4 => TestPage(),
                5 => AiPage(),
                _ => InstructionPage(),
            };
            _pages[_step] = page;
        }

        if (_step == 5) ShowAiState();
        if (_step == 4) StartTest();
        else _testTimer.Stop();
        _page.Content = page;

        _footer.IsVisible = _step > 0;
        _back.IsVisible = _step > 1;
        SetLabel(_next, _step == Steps ? "Concluir" : "Continuar");
        AutomationProperties.SetName(_next, _step == Steps ? "Concluir" : "Continuar");

        _dots.Children.Clear();
        _dots.IsVisible = _step > 0;
        _stepLabel.Text = _step > 0 ? $"Passo {_step} de {Steps} · opcional" : string.Empty;
        for (var i = 1; i <= Steps; i++)
        {
            _dots.Children.Add(new Border
            {
                Width = i == _step ? 22 : 8,
                Height = 8,
                CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                Background = i <= _step
                    ? new SolidColorBrush(Tokens.Colors.SpectrumAt((i - 1) / (double)(Steps - 1)))
                    : Tokens.Brushes.Tint(Colors.White, 0.14),
            });
        }
    }

    /// <summary>Keeps what the step on screen sets.</summary>
    private void Save()
    {
        switch (_step)
        {
            case 1:
                var name = (_name.Text ?? string.Empty).Trim();
                Update(Data with { DisplayName = name.Length == 0 ? null : name });
                break;
            case 2:
                Update(Data with { Mode = _mode });
                break;
            case 6:
                var text = (_instruction.Text ?? string.Empty).Trim();
                Update(Data with { AiInstruction = text.Length == 0 ? null : text });
                break;
        }
    }

    private void Finish()
    {
        Update(Data with { OnboardingDone = true });
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _testTimer.Stop();
        if (_composition?.Engine is { } engine) engine.Completed -= OnDictated;

        // Closed from the title bar counts as skipping the rest: it does not come back.
        if (!Data.OnboardingDone) Update(Data with { OnboardingDone = true });
        base.OnClosed(e);
    }

    private StackPanel Welcome()
    {
        var start = Ui.Primary("Começar");
        start.Click += (_, _) => Go(1);
        AutomationProperties.SetName(start, "Começar");

        var skipAll = Ui.Secondary("Pular configuração");
        skipAll.Click += (_, _) => Finish();
        AutomationProperties.SetName(skipAll, "Pular configuração");

        var title = Ui.Text("Bem-vindo ao Talk-to-Text", Tokens.Fonts.Title + 4, weight: FontWeight.SemiBold);
        var lead = Ui.Text(
            "Fale e o texto aparece onde o cursor estiver, transcreva reuniões e gravações e peça resumos à IA — tudo no seu computador.",
            Tokens.Fonts.Body + 1, Tokens.Brushes.Muted);
        var hint = Ui.Text(
            "Em 6 passos rápidos você escolhe seu nome, o jeito de ditar e o atalho, testa o microfone e prepara a IA. Todos são opcionais e dá para mudar depois em Configurações.",
            Tokens.Fonts.Body, Tokens.Brushes.Muted);

        foreach (var t in new[] { title, lead, hint })
        {
            t.TextAlignment = TextAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.MaxWidth = 480;
        }

        return new StackPanel
        {
            Spacing = Tokens.Space.Roomy,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, Tokens.Space.Page, 0, 0),
            Children =
            {
                new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = Ui.AppIcon(96) },
                title,
                lead,
                hint,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Base,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, Tokens.Space.Base, 0, 0),
                    Children = { skipAll, start },
                },
            },
        };
    }

    private StackPanel NamePage() => Page(
        Icons.Chat,
        "Como você quer ser chamado?",
        "Seu nome aparece nas suas falas, nas transcrições de reuniões. Sem nome, elas aparecem como \"Você\".",
        _name);

    private StackPanel ModePage()
    {
        _modeCards.Clear();
        var cards = new StackPanel { Spacing = Tokens.Space.Snug };
        cards.Children.Add(ModeCard(ActivationMode.Hold, "Segurar para falar", "Mantenha as teclas pressionadas enquanto fala; ao soltar, o texto é escrito."));
        cards.Children.Add(ModeCard(ActivationMode.Toggle, "Pressionar para iniciar e parar", "Um toque começa a gravar, outro termina. Bom para falas longas."));
        ShowMode();

        return Page(Icons.Mic, "Como você prefere ditar?", "Dá para trocar a qualquer momento em Configurações.", cards);
    }

    private Button ModeCard(ActivationMode mode, string title, string detail)
    {
        var card = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(Tokens.Space.Roomy, Tokens.Space.Base),
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Tag = (title, detail),
        };
        card.Classes.Add("nav");
        AutomationProperties.SetName(card, title);
        card.Click += (_, _) =>
        {
            _mode = mode;
            ShowMode();
        };
        _modeCards.Add((mode, card));
        return card;
    }

    private void ShowMode()
    {
        foreach (var (mode, card) in _modeCards)
        {
            var selected = mode == _mode;
            var (title, detail) = ((string, string))card.Tag!;
            card.Background = selected ? Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.16) : Tokens.Brushes.GlassFill;
            card.BorderBrush = selected ? Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.7) : Tokens.Brushes.GlassRimSubtle;
            var check = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(Tokens.Radius.Pill),
                BorderThickness = new Thickness(selected ? 5 : 1.5),
                BorderBrush = selected ? Tokens.Brushes.Primary : Tokens.Brushes.Muted,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, Tokens.Space.Base, 0),
            };
            var text = new StackPanel
            {
                Spacing = Tokens.Space.Hair,
                Children =
                {
                    Ui.Text(title, Tokens.Fonts.Body, weight: FontWeight.SemiBold),
                    Ui.Text(detail, Tokens.Fonts.Label, Tokens.Brushes.Muted),
                },
            };
            Grid.SetColumn(text, 1);
            card.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { check, text } };
        }
    }

    private StackPanel ShortcutPage()
    {
        var picker = new ShortcutPicker(_composition) { HorizontalAlignment = HorizontalAlignment.Left };
        return Page(
            Icons.Settings,
            "Qual atalho inicia a gravação?",
            "Até duas teclas — o padrão é Ctrl + Win. Clique no atalho abaixo e pressione a nova combinação; Esc cancela. Ele é salvo na hora.",
            picker);
    }

    /// <summary>A try of the real thing: the user's own shortcut, a level meter, and what was heard.</summary>
    private StackPanel TestPage()
    {
        var track = new Border
        {
            Height = 8,
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            Background = Tokens.Brushes.Tint(Colors.White, 0.1),
            Child = _testLevel,
        };
        _testHint.TextWrapping = TextWrapping.Wrap;

        return Page(
            Icons.Mic,
            "Teste o microfone",
            "Fale uma frase usando o atalho. A barra mostra o volume do microfone e a frase aparece abaixo.",
            new StackPanel { Spacing = Tokens.Space.Base, Children = { _testHint, track, _testStatus } });
    }

    private void StartTest()
    {
        var data = Data;
        _testHint.Text = _composition?.Engine is null
            ? "O ditado não está disponível neste computador."
            : data.Mode == ActivationMode.Hold
                ? $"Segure {data.Shortcut} enquanto fala e solte no fim."
                : $"Pressione {data.Shortcut} para começar a gravar e de novo para terminar.";
        _testTimer.Start();
        ShowTest();
    }

    private void ShowTest()
    {
        if (_composition?.Engine is not { } engine)
        {
            _testStatus.Text = string.Empty;
            return;
        }

        _testLevel.Width = engine.State == DictationState.Recording ? 360 * Math.Min(1, engine.Level * 5) : 0;
        _testStatus.Text = engine.State switch
        {
            DictationState.Recording => "Gravando…",
            DictationState.Transcribing => ActivityText.Describe(_composition?.Engine?.Activity),
            _ when _heard is not null => $"Ouvimos: “{_heard}”. Funcionou! Em qualquer programa, o texto é escrito onde o cursor estiver.",
            _ => "Aguardando você falar…",
        };
    }

    private void OnDictated(object? sender, DictationResult result) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _heard = result.Text;
            ShowTest();
        });

    /// <summary>
    /// The local AI — downloaded here, or later on first use — or a cloud one with the user's
    /// API key. Scrolls: the cloud choice adds a provider, a key and a model list.
    /// </summary>
    private ScrollViewer AiPage()
    {
        _aiChoice = new AiChoicePicker(_composition);
        _aiChoice.SourceChanged += (_, _) => ShowAiState();
        _localAi.Children.AddRange([_download, _downloadBar, _downloadStatus]);

        return new ScrollViewer
        {
            Content = Page(
                Icons.Sparkles,
                "Escolha a IA",
                "Ela escreve os resumos de reuniões e gravações, responde no Chat e executa o modo comando. Dá para trocar depois em Configurações.",
                new StackPanel
                {
                    Spacing = Tokens.Space.Roomy,
                    Children = { _aiChoice, _localAi },
                }),
        };
    }

    /// <summary>The AI choice on the AI step. Exposed for headless tests.</summary>
    public AiChoicePicker? AiChoice => _aiChoice;

    /// <summary>Whether the AI is there, downloading or still to get — read each time the step shows.</summary>
    private void ShowAiState()
    {
        // The download is only for the local AI.
        _localAi.IsVisible = _aiChoice?.CloudSelected != true;
        if (_downloading is not null) return;

        var installed = SummaryModel.IsInstalled();
        _download.IsVisible = !installed;
        _downloadStatus.Text = installed
            ? "✓ A IA já está instalada."
            : "Se pular, ela é baixada na primeira vez que você gerar um resumo ou usar o Chat.";
    }

    private StackPanel InstructionPage() => Page(
        Icons.Sparkles,
        "Uma instrução para a IA",
        "Diga quem você é e do que tratam suas gravações — a IA segue isso ao resumir arquivos de áudio, usando os termos certos e destacando o que importa. Dá para mudar depois em Configurações.",
        _instruction);

    /// <summary>Downloads the AI with progress; keeps going if the user moves on.</summary>
    private async Task DownloadAsync()
    {
        if (_downloading is not null || SummaryModel.IsInstalled()) return;

        var cancel = new CancellationTokenSource();
        _downloading = cancel;
        _download.IsVisible = false;
        _downloadBar.IsVisible = true;
        _downloadStatus.Text = "Baixando… você pode continuar a configuração enquanto isso.";

        try
        {
            await MeetingCoordinator.DownloadFileAsync(
                SummaryModel.Url, SummaryModel.PathOnDisk, SummaryModel.Size,
                new Progress<long>(bytes =>
                {
                    var fraction = (double)bytes / SummaryModel.Size;
                    _downloadBar.Value = fraction;
                    _downloadStatus.Text = $"Baixando… {fraction:P0} de cerca de 2 GB. Você pode continuar enquanto isso.";
                }),
                cancel.Token).ConfigureAwait(true);

            _downloadStatus.Text = "✓ A IA foi instalada.";
            DiagnosticLog.Write("summary model downloaded from the first-run setup");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
        {
            DiagnosticLog.Write($"summary model download failed: {e.Message}");
            _downloadStatus.Text = "Não foi possível baixar agora. Tudo bem: ela é baixada na primeira vez que você usar um recurso de IA.";
            _download.IsVisible = true;
        }
        finally
        {
            _downloadBar.IsVisible = false;
            cancel.Dispose();
            _downloading = null;
        }
    }

    /// <summary>A step: icon chip, title, explanation, then its control.</summary>
    private static StackPanel Page(string icon, string title, string detail, Control body) => new()
    {
        Spacing = Tokens.Space.Base,
        Children =
        {
            new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(12),
                Background = Tokens.Brushes.PrimaryGlass,
                BorderBrush = Tokens.Brushes.PrimaryRim,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = Icons.Make(icon, new SolidColorBrush(Tokens.Colors.OnPrimary), 20),
                },
            },
            Ui.Text(title, Tokens.Fonts.Title, weight: FontWeight.SemiBold),
            Ui.Text(detail, Tokens.Fonts.Body, Tokens.Brushes.Muted),
            new Border { Margin = new Thickness(0, Tokens.Space.Snug, 0, 0), Child = body },
        },
    };

    private static void SetLabel(Button button, string label) =>
        button.Content = Ui.Text(label, Tokens.Fonts.Body, button.Classes.Contains("primary") ? new SolidColorBrush(Tokens.Colors.OnPrimary) : Tokens.Brushes.Foreground, FontWeight.Medium);
}
