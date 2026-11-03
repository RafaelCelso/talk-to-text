using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;
using TalkToText.Speech;

namespace TalkToText.App.Views;

/// <summary>
/// Ask questions about meetings and recorded audio — "what did we decide in the last meeting?",
/// "which next steps are mine?" — answered by the local model from the transcripts.
/// </summary>
/// <remarks>
/// The question can cover everything or one transcript. Each answer streams in as it is
/// written, can be interrupted, and names the meetings it drew on. The model is loaded for each
/// question and released after, as for summaries: it holds about 2.5 GB.
/// </remarks>
public sealed class ChatPage : UserControl
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly string[] MeetingSuggestions =
    [
        "O que foi decidido na última reunião?",
        "Quais próximos passos ficaram comigo?",
        "Quais prazos foram combinados?",
    ];

    private static readonly string[] RecordingSuggestions =
    [
        "Resuma a gravação mais recente.",
        "Quais números, datas ou lotes foram citados?",
        "Qual foi o motivo do contato?",
    ];

    private readonly Composition? _composition;
    private readonly MeetingCoordinator? _coordinator;
    private readonly ComboBox _scope;
    private readonly Button _meetingsTab;
    private readonly Button _recordingsTab;
    private bool _recordings;
    private readonly StackPanel _messages = new() { Spacing = Tokens.Space.Base };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly TextBlock _status;
    private readonly ChatStore? _chats;
    private readonly StackPanel _historyList = new() { Spacing = Tokens.Space.Hair };
    private ChatConversation? _conversation;
    private bool _opening;
    private CancellationTokenSource? _running;

    /// <summary>Builds the page.</summary>
    public ChatPage(Composition? composition, MeetingCoordinator? coordinator)
    {
        _composition = composition;
        _coordinator = coordinator;
        _chats = composition?.Chats;

        // Takes the room left between the tabs and the button, up to a comfortable width; a fixed
        // width pushed "Nova conversa" off the edge of a normal-sized window.
        _scope = new ComboBox { MinWidth = 160, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_scope, "Onde procurar");

        var clear = Ui.Secondary("Nova conversa", Icons.Restart);
        AutomationProperties.SetName(clear, "Nova conversa");
        clear.Click += (_, _) =>
        {
            if (_running is not null) return;
            StartNew();
        };

        // Two tabs, meetings and recordings, each with its own "all, or this one" selector.
        _meetingsTab = Tab("Reuniões", Icons.Chat);
        _recordingsTab = Tab("Gravações", Icons.AudioFile);
        _meetingsTab.Click += (_, _) => SelectTab(recordings: false);
        _recordingsTab.Click += (_, _) => SelectTab(recordings: true);

        var tabs = new Border
        {
            Background = Tokens.Brushes.GlassFill,
            BorderBrush = Tokens.Brushes.GlassRimSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.Radius.Control + 2),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { _meetingsTab, _recordingsTab } },
        };

        _scope.Margin = new Thickness(Tokens.Space.Base, 0, Tokens.Space.Base, 0);
        _scope.VerticalAlignment = VerticalAlignment.Center;
        clear.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_scope, 1);
        Grid.SetColumn(clear, 3);
        var scopeRow = new Grid
        {
            // The selector's column takes nearly all the free room, up to 460; anything beyond
            // goes to the nearly weightless filler, keeping the button on the right.
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = 460 + (Tokens.Space.Base * 2) },
                new ColumnDefinition(0.001, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
            Children = { tabs, _scope, clear },
        };

        _scroll = new ScrollViewer { Content = _messages, Padding = new Thickness(0, 0, Tokens.Space.Snug, 0) };

        _input = new TextBox
        {
            Watermark = "Pergunte sobre suas reuniões e gravações…",
            FontFamily = Tokens.Fonts.Sans,
            FontSize = Tokens.Fonts.Body,
            MinHeight = Tokens.Size.Control + 6,
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(_input, "Pergunta");
        // Enter sends. Caught on the way down, before the text box can use the key itself.
        _input.AddHandler(
            KeyDownEvent,
            async (_, e) =>
            {
                if (e.Key != Key.Enter || _running is not null) return;
                e.Handled = true;
                await AskAsync(_input.Text ?? string.Empty).ConfigureAwait(true);
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // As tall as the text box beside it, and growing with it: the default control height
        // left the button shorter than the box and out of line with it.
        _send = Ui.Primary("Perguntar", Icons.Sparkles);
        AutomationProperties.SetName(_send, "Perguntar");
        _send.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);
        _send.Height = double.NaN;
        _send.MinHeight = _input.MinHeight;
        _send.VerticalAlignment = VerticalAlignment.Stretch;
        _send.Click += async (_, _) =>
        {
            if (_running is { } running)
            {
                running.Cancel();
                return;
            }

            await AskAsync(_input.Text ?? string.Empty).ConfigureAwait(true);
        };
        Grid.SetColumn(_send, 1);

        _status = Ui.Text(string.Empty, Tokens.Fonts.Caption, Tokens.Brushes.Muted);
        _status.Margin = new Thickness(Tokens.Space.Tight, Tokens.Space.Tight, 0, 0);

        var composer = new StackPanel
        {
            Margin = new Thickness(0, Tokens.Space.Base, 0, 0),
            Children =
            {
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _input, _send } },
                _status,
            },
        };

        // The history: this tab's conversations, newest first, beside the open one.
        var history = new Border
        {
            Width = 248,
            Margin = new Thickness(0, 0, Tokens.Space.Roomy, 0),
            Background = Tokens.Brushes.GlassFill,
            BorderBrush = Tokens.Brushes.GlassRim,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            Padding = new Thickness(Tokens.Space.Snug),
            Child = new DockPanel
            {
                Children =
                {
                    Ui.Docked(new Border
                    {
                        Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Tight, 0, Tokens.Space.Snug),
                        Child = Ui.Text("Conversas", Tokens.Fonts.Label, Tokens.Brushes.Muted, FontWeight.SemiBold),
                    }, Dock.Top),
                    new ScrollViewer { Content = _historyList },
                },
            },
        };

        var conversation = new DockPanel
        {
            Children =
            {
                Ui.Docked(composer, Dock.Bottom),
                Ui.Card(_scroll, Tokens.Space.Roomy),
            },
        };

        Content = new DockPanel
        {
            Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Page - 4, Tokens.Space.Page, Tokens.Space.Roomy),
            Children =
            {
                Ui.Docked(Ui.PageHeader("Chat", "Pergunte sobre suas reuniões e gravações. A IA roda no seu computador."), Dock.Top),
                Ui.Docked(scopeRow, Dock.Top),
                Ui.Docked(history, Dock.Left),
                conversation,
            },
        };

        // Picking another meeting or recording starts a new conversation about it.
        _scope.SelectionChanged += (_, _) =>
        {
            if (_opening || _running is not null || _conversation is null) return;
            StartNew();
        };

        // The list of transcripts changes as meetings end and files are transcribed.
        AttachedToVisualTree += (_, _) => RefreshScope();
        SelectTab(recordings: false);
        ShowEmpty();
    }

    /// <summary>Whether an answer is being written. Exposed for tests.</summary>
    public bool IsAnswering => _running is not null;

    /// <summary>The choices in the selector: "all", then each transcript of the tab. Exposed for tests.</summary>
    public int ScopeCount => _scope.ItemCount;

    /// <summary>The selector's first choice, "Todas as reuniões" or "Todas as gravações". Exposed for tests.</summary>
    public string ScopeAllLabel => (_scope.Items.Count > 0 ? (_scope.Items[0] as ComboBoxItem)?.Content as string : null) ?? string.Empty;

    /// <summary>Shows the meetings tab, or the recordings tab, and its selector.</summary>
    public void SelectTab(bool recordings)
    {
        if (_running is not null) return;
        _recordings = recordings;
        StyleTab(_meetingsTab, !recordings);
        StyleTab(_recordingsTab, recordings);
        RefreshScope(keepSelection: false);
        StartNew();
    }

    /// <summary>The conversations listed in the history. Exposed for tests.</summary>
    public int HistoryCount => _historyList.Children.OfType<Grid>().Count();

    /// <summary>An empty conversation, nothing selected in the history.</summary>
    private void StartNew()
    {
        _conversation = null;
        ShowEmpty();
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        _historyList.Children.Clear();
        var all = _chats?.All(_recordings) ?? [];
        if (all.Count == 0)
        {
            var hint = Ui.Text(
                _recordings ? "As conversas sobre gravações aparecem aqui." : "As conversas sobre reuniões aparecem aqui.",
                Tokens.Fonts.Label, Tokens.Brushes.Muted);
            hint.Margin = new Thickness(Tokens.Space.Snug, 0);
            _historyList.Children.Add(hint);
            return;
        }

        foreach (var c in all) _historyList.Children.Add(HistoryItem(c));
    }

    /// <summary>
    /// A conversation in the history: its first question, what it was about — "Todas as
    /// reuniões" or the meeting or recording — and when, with a button to delete it.
    /// </summary>
    private Grid HistoryItem(ChatConversation c)
    {
        var selected = c.Id == _conversation?.Id;

        var title = Ui.Text(c.Title, Tokens.Fonts.Body, weight: selected ? FontWeight.SemiBold : FontWeight.Medium);
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;

        var scope = Ui.Text(c.ScopeTitle, Tokens.Fonts.Caption, selected ? Tokens.Brushes.Primary : Tokens.Brushes.Muted, FontWeight.Medium);
        scope.TextTrimming = TextTrimming.CharacterEllipsis;
        scope.TextWrapping = TextWrapping.NoWrap;
        scope.Margin = new Thickness(Tokens.Space.Tight + 1, 0, 0, 0);
        Grid.SetColumn(scope, 1);

        var about = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                Icons.Make(c.AboutRecordings ? Icons.AudioFile : Icons.Chat, selected ? Tokens.Brushes.Primary : Tokens.Brushes.Muted, 12),
                scope,
            },
        };

        var open = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(Tokens.Space.Snug + 2, Tokens.Space.Snug, 30, Tokens.Space.Snug),
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            Background = selected ? Tokens.Brushes.GlassFillRaised : Brushes.Transparent,
            BorderBrush = selected ? Tokens.Brushes.GlassRimSubtle : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    title,
                    about,
                    Ui.Text(c.UpdatedAt.ToLocalTime().ToString("d MMM, HH:mm", Portuguese), Tokens.Fonts.Caption, Tokens.Brushes.Muted),
                },
            },
        };
        open.Classes.Add("nav");
        AutomationProperties.SetName(open, c.Title);
        open.Click += (_, _) => Open(c);

        var delete = Ui.IconButton(Icons.Trash, "Excluir conversa");
        delete.HorizontalAlignment = HorizontalAlignment.Right;
        delete.VerticalAlignment = VerticalAlignment.Top;
        delete.Margin = new Thickness(0, Tokens.Space.Tight, Tokens.Space.Tight, 0);
        delete.Click += (_, _) =>
        {
            if (_running is not null) return;
            _chats?.Delete(c.Id);
            if (_conversation?.Id == c.Id) StartNew();
            else RefreshHistory();
        };

        return new Grid { Children = { open, delete } };
    }

    /// <summary>Reopens <paramref name="c"/>: its scope in the selector, its questions and answers.</summary>
    private void Open(ChatConversation c)
    {
        if (_running is not null) return;

        _opening = true;
        _conversation = c;
        _scope.SelectedItem = _scope.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as Guid?) == c.ScopeId) ?? _scope.Items[0];
        _opening = false;

        _messages.Children.Clear();
        foreach (var e in c.Exchanges)
        {
            _messages.Children.Add(UserBubble(e.Question));
            var sources = Ui.Text(e.Sources is null ? string.Empty : "Fontes: " + e.Sources, Tokens.Fonts.Caption, Tokens.Brushes.Muted);
            sources.IsVisible = e.Sources is not null;
            _messages.Children.Add(AnswerBubble(new ContentControl { Content = Ui.Markdown(e.Answer) }, sources));
        }

        RefreshHistory();
        ScrollToEnd();
    }

    /// <summary>The transcripts of the open tab, newest first.</summary>
    private IReadOnlyList<MeetingTranscript> OfTab() =>
        _composition is null
            ? []
            : [.. (_recordings ? _composition.AudioFiles : _composition.Meetings).All().OrderByDescending(t => t.StartedAt)];

    private void RefreshScope(bool keepSelection = true)
    {
        // Refilling the selector is not the user picking something else: the open conversation stays.
        _opening = true;
        var selected = keepSelection ? (_scope.SelectedItem as ComboBoxItem)?.Tag as Guid? : null;
        _scope.Items.Clear();
        _scope.Items.Add(new ComboBoxItem { Content = _recordings ? "Todas as gravações" : "Todas as reuniões", Tag = null });

        foreach (var t in OfTab())
        {
            _scope.Items.Add(new ComboBoxItem
            {
                Content = $"{t.Title} · {t.StartedAt.ToLocalTime().ToString("d MMM, HH:mm", Portuguese)}",
                Tag = t.Id,
            });
        }

        _scope.SelectedItem = _scope.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as Guid?) == selected) ?? _scope.Items[0];
        AutomationProperties.SetName(_scope, _recordings ? "Qual gravação" : "Qual reunião");
        _opening = false;
    }

    private static Button Tab(string label, string icon)
    {
        var tab = new Button
        {
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Tight + 2),
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = (label, icon),
        };
        tab.Classes.Add("nav");
        AutomationProperties.SetName(tab, label);
        return tab;
    }

    /// <summary>The open tab is lifted glass with a bright label; the other stays quiet.</summary>
    private static void StyleTab(Button tab, bool selected)
    {
        var (label, icon) = ((string, string))tab.Tag!;
        tab.Background = selected ? Tokens.Brushes.GlassFillRaised : Brushes.Transparent;
        tab.BorderBrush = selected ? Tokens.Brushes.GlassRim : Brushes.Transparent;
        var brush = selected ? Tokens.Brushes.Foreground : Tokens.Brushes.Muted;
        tab.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                Icons.Make(icon, selected ? Tokens.Brushes.Primary : Tokens.Brushes.Muted, Tokens.Size.Icon - 2),
                Ui.Text(label, Tokens.Fonts.Body, brush, selected ? FontWeight.SemiBold : FontWeight.Medium),
            },
        };
    }

    private void ShowEmpty()
    {
        _messages.Children.Clear();
        var suggestions = new StackPanel { Spacing = Tokens.Space.Snug, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var text in _recordings ? RecordingSuggestions : MeetingSuggestions)
        {
            var chip = Ui.Secondary(text);
            chip.HorizontalAlignment = HorizontalAlignment.Center;
            chip.Click += async (_, _) => await AskAsync(text).ConfigureAwait(true);
            suggestions.Children.Add(chip);
        }

        _messages.Children.Add(new StackPanel
        {
            Spacing = Tokens.Space.Base,
            Children =
            {
                Ui.EmptyState(Icons.Messages, "Pergunte o que quiser", "Decisões, prazos, quem disse o quê — a resposta vem das suas transcrições."),
                suggestions,
            },
        });
    }

    /// <summary>Asks <paramref name="question"/> and streams the answer into the chat.</summary>
    public async Task AskAsync(string question)
    {
        question = question.Trim();
        if (question.Length == 0 || _running is not null || _composition is null) return;

        var scopeId = (_scope.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        var transcripts = OfTab().Where(t => scopeId is null || t.Id == scopeId).ToList();

        // The first question opens a conversation about what the selector shows now.
        if (_conversation is null)
        {
            _messages.Children.Clear();
            var now = DateTimeOffset.Now;
            _conversation = new ChatConversation
            {
                AboutRecordings = _recordings,
                ScopeId = scopeId,
                ScopeTitle = scopeId is null
                    ? (_recordings ? "Todas as gravações" : "Todas as reuniões")
                    : transcripts.FirstOrDefault()?.Title ?? string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        var conversation = _conversation;
        _input.Text = string.Empty;
        _messages.Children.Add(UserBubble(question));

        // Until the first word arrives: dots, skeleton lines and the step it is on.
        var thinking = new ThinkingIndicator();
        var answerBody = new ContentControl { Content = thinking };
        var sources = Ui.Text(string.Empty, Tokens.Fonts.Caption, Tokens.Brushes.Muted);
        sources.IsVisible = false;
        _messages.Children.Add(AnswerBubble(answerBody, sources));
        ScrollToEnd();

        if (_coordinator?.Summary is not null)
        {
            answerBody.Content = Ui.Text("Um resumo está sendo gerado agora; pergunte de novo quando ele terminar.", Tokens.Fonts.Body, Tokens.Brushes.Muted);
            return;
        }

        var running = new CancellationTokenSource();
        _running = running;
        SetBusy(true);
        var answer = new System.Text.StringBuilder();
        string? sourceList = null;

        try
        {
            if (!SummaryModel.IsInstalled())
            {
                thinking.SetStep("Baixando a IA (2 GB, só na primeira vez)…");
                await MeetingCoordinator.DownloadFileAsync(
                    SummaryModel.Url, SummaryModel.PathOnDisk, SummaryModel.Size,
                    new Progress<long>(b => thinking.SetStep($"Baixando a IA… {(double)b / SummaryModel.Size:P0}")),
                    running.Token).ConfigureAwait(true);
            }

            thinking.SetStep("Preparando a IA…");

            // The model dictation keeps loaded, when there is one: no load wait between messages.
            using var lease = _composition.LocalAi is { } shared ? await shared.AcquireAsync(running.Token).ConfigureAwait(true) : null;
            using var own = lease is null ? await Task.Run(() => new QwenLanguageModel(), running.Token).ConfigureAwait(true) : null;
            var assistant = new TranscriptAssistant(lease?.Model ?? own!);
            thinking.SetStep("Procurando nas suas transcrições…");

            var found = new Progress<AssistantFound>(f => thinking.SetStep(
                f.Passages == 0
                    ? "Pensando…"
                    : $"Lendo {f.Passages} {(f.Passages == 1 ? "trecho" : "trechos")} de {f.Transcripts} {(f.Transcripts == 1 ? "reunião ou gravação" : "reuniões e gravações")}…",
                reading: true));

            await Task.Run(async () =>
            {
                await foreach (var piece in assistant.AskAsync(question, transcripts, conversation.Turns, running.Token, found).ConfigureAwait(false))
                {
                    lock (answer) answer.Append(piece);
                    Dispatcher.UIThread.Post(() =>
                    {
                        string text;
                        lock (answer) text = answer.ToString();

                        // A caret at the end while it is still being written.
                        answerBody.Content = Ui.Markdown(text.TrimEnd() + " ▍");
                        ScrollToEnd();
                    });
                }
            }, CancellationToken.None).ConfigureAwait(true);

            if (assistant.Sources.Count > 0)
            {
                sourceList = string.Join(" · ", assistant.Sources.Take(4).Select(t =>
                    $"{t.Title} ({t.StartedAt.ToLocalTime().ToString("d MMM", Portuguese)})"));
                sources.Text = "Fontes: " + sourceList;
                sources.IsVisible = true;
            }

            _status.Text = string.Empty;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Interrompido.";
        }
        catch (Exception e)
        {
            DiagnosticLog.Write($"chat failed: {e}");
            _status.Text = $"Não foi possível responder: {e.Message}";
        }
        finally
        {
            running.Dispose();
            _running = null;
            SetBusy(false);
        }

        string final;
        lock (answer) final = answer.ToString().Trim();
        answerBody.Content = final.Length > 0 ? Ui.Markdown(final) : Ui.Text("Sem resposta.", Tokens.Fonts.Body, Tokens.Brushes.Muted);
        if (final.Length > 0)
        {
            // Saved after every answer, so the history survives closing the app.
            conversation.Exchanges.Add(new ChatExchange { Question = question, Answer = final, Sources = sourceList });
            conversation.UpdatedAt = DateTimeOffset.Now;
            _chats?.Save(conversation);
            RefreshHistory();
        }

        ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        _input.IsEnabled = !busy;
        _scope.IsEnabled = !busy;
        _meetingsTab.IsEnabled = !busy;
        _recordingsTab.IsEnabled = !busy;
        _send.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                Icons.Make(busy ? Icons.Stop : Icons.Sparkles, new SolidColorBrush(Tokens.Colors.OnPrimary), Tokens.Size.Icon - 2),
                Ui.Text(busy ? "Interromper" : "Perguntar", Tokens.Fonts.Body, new SolidColorBrush(Tokens.Colors.OnPrimary), FontWeight.Medium),
            },
        };
        AutomationProperties.SetName(_send, busy ? "Interromper" : "Perguntar");
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);

    private static Border UserBubble(string text) => new()
    {
        MaxWidth = 560,
        HorizontalAlignment = HorizontalAlignment.Right,
        Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
        CornerRadius = new CornerRadius(Tokens.Radius.Shell, Tokens.Radius.Small, Tokens.Radius.Shell, Tokens.Radius.Shell),
        Background = Tokens.Brushes.PrimaryGlass,
        BorderBrush = Tokens.Brushes.PrimaryRim,
        BorderThickness = new Thickness(1),
        Child = Ui.Selectable(text, Tokens.Fonts.Body, new SolidColorBrush(Tokens.Colors.OnPrimary), selection: Tokens.Brushes.Tint(Colors.White, 0.35)),
    };

    private static Grid AnswerBubble(Control body, TextBlock sources)
    {
        var avatar = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(10),
            Background = Tokens.Brushes.Tint(Tokens.Colors.Spectrum[2], 0.25),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = Icons.Make(Icons.Sparkles, new SolidColorBrush(Tokens.Colors.Spectrum[2]), Tokens.Size.Icon - 2),
            },
        };

        var bubble = new Border
        {
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug + 2),
            CornerRadius = new CornerRadius(Tokens.Radius.Small, Tokens.Radius.Shell, Tokens.Radius.Shell, Tokens.Radius.Shell),
            Background = Tokens.Brushes.GlassFillRaised,
            BorderBrush = Tokens.Brushes.GlassRimSubtle,
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = Tokens.Space.Snug, Children = { body, sources } },
        };
        Grid.SetColumn(bubble, 1);
        bubble.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);

        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { avatar, bubble } };
    }
}
