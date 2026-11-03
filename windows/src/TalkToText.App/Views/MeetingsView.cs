using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// Meetings: the list on the left, the selected one as a chat on the right.
/// </summary>
public sealed class MeetingsView : UserControl
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    private readonly MeetingStore _store;
    private readonly MeetingCoordinator? _coordinator;
    private readonly StackPanel _list = new() { Spacing = Tokens.Space.Tight };
    private readonly ContentControl _detail = new();
    private readonly Button _startStop;
    private readonly TextBox _search = Ui.SearchBox("Buscar nas reuniões");
    private readonly DispatcherTimer _listRefresh;
    private Guid? _selected;

    /// <summary>Builds the page.</summary>
    public MeetingsView(MeetingStore store, MeetingCoordinator? coordinator)
    {
        _store = store;
        _coordinator = coordinator;

        _startStop = Ui.Primary("Transcrever agora", Icons.Mic);
        _startStop.Click += async (_, _) => await ToggleAsync().ConfigureAwait(true);
        _startStop.HorizontalAlignment = HorizontalAlignment.Stretch;
        _startStop.HorizontalContentAlignment = HorizontalAlignment.Center;

        var left = new DockPanel
        {
            Width = 268,
            Margin = new Thickness(0, 0, Tokens.Space.Roomy, 0),
            Children =
            {
                Ui.Docked(new StackPanel
                {
                    Spacing = Tokens.Space.Base,
                    Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
                    Children = { Ui.PageHeader("Reuniões"), _startStop, _search },
                }, Dock.Top),
                new ScrollViewer { Content = _list },
            },
        };

        Content = new DockPanel
        {
            Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Page - 4, Tokens.Space.Page, Tokens.Space.Roomy),
            Children = { Ui.Docked(left, Dock.Left), _detail },
        };

        // Debounced: a live meeting saves after every message, and re-reading every
        // transcript from disk that often would be wasted work.
        _listRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _listRefresh.Tick += (_, _) => { _listRefresh.Stop(); RefreshList(); };
        _store.Changed += (_, _) => Dispatcher.UIThread.Post(() => { if (!_listRefresh.IsEnabled) _listRefresh.Start(); });

        if (_coordinator is not null) _coordinator.SessionChanged += (_, _) => OnSessionChanged();
        Avalonia.Automation.AutomationProperties.SetName(_search, "Buscar nas reuniões");
        _search.TextChanged += (_, _) => RefreshList();

        RefreshList();
        OnSessionChanged();
    }

    private void OnSessionChanged()
    {
        var live = _coordinator?.Session;
        _startStop.IsVisible = _coordinator?.IsAvailable ?? false;
        _startStop.Content = ButtonContent(live is null ? "Transcrever agora" : "Parar transcrição", live is null ? Icons.Mic : Icons.Stop);
        _startStop.Classes.Set("recording", live is not null);

        if (live is not null) _selected = live.Transcript.Id;
        RefreshList();
        ShowSelected();
    }

    private async Task ToggleAsync()
    {
        if (_coordinator is null) return;

        if (_coordinator.Session is not null)
        {
            await _coordinator.StopAsync().ConfigureAwait(true);
            return;
        }

        if (!Speech.MeetingModels.IsInstalled())
        {
            _startStop.IsEnabled = false;
            _startStop.Content = ButtonContent("Baixando modelos…", Icons.Mic);
            try
            {
                await MeetingCoordinator.DownloadModelsAsync(null, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                DiagnosticLog.Write($"meeting model download failed: {e.Message}");
            }
            finally
            {
                _startStop.IsEnabled = true;
            }
        }

        if (!_coordinator.StartTranscription()) OnSessionChanged();
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        var meetings = _store.All();

        if (meetings.Count == 0)
        {
            _list.Children.Add(Ui.Text(
                "Quando você entrar numa reunião no Teams, Meet ou Zoom, o Talk-to-Text pergunta se deve transcrever.",
                Tokens.Fonts.Label, Tokens.Brushes.Muted));
            return;
        }

        _selected ??= meetings[0].Id;
        var liveId = _coordinator?.Session?.Transcript.Id;

        var query = (_search.Text ?? string.Empty).Trim();
        var shown = 0;
        foreach (var meeting in meetings)
        {
            var snippet = TranscriptSearch.Find(meeting, query);
            if (snippet is null) continue;
            _list.Children.Add(ListItem(meeting, meeting.Id == liveId, snippet));
            shown++;
        }

        if (shown == 0) _list.Children.Add(Ui.Text($"Nenhuma reunião menciona \"{query}\".", Tokens.Fonts.Label, Tokens.Brushes.Muted));
    }

    /// <summary>The line under a search result: where the words were said.</summary>
    internal static TextBlock Snippet(string text)
    {
        var line = Ui.Text(text, Tokens.Fonts.Caption, Tokens.Brushes.Muted);
        line.TextWrapping = TextWrapping.Wrap;
        line.MaxLines = 2;
        line.TextTrimming = TextTrimming.CharacterEllipsis;
        line.FontStyle = FontStyle.Italic;
        return line;
    }

    private Button ListItem(MeetingTranscript meeting, bool live, string snippet)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug };
        if (live) title.Children.Add(Ui.Dot(Tokens.Colors.Recording, 7));
        var name = Ui.Text(meeting.Title, Tokens.Fonts.Body, weight: FontWeight.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.MaxWidth = 210;
        title.Children.Add(name);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            Background = meeting.Id == _selected ? Tokens.Brushes.GlassFillRaised : Avalonia.Media.Brushes.Transparent,
            BorderBrush = meeting.Id == _selected ? Tokens.Brushes.GlassRimSubtle : Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = new StackPanel
            {
                Spacing = Tokens.Space.Hair,
                Children =
                {
                    title,
                    Ui.Text($"{meeting.App} · {meeting.StartedAt.ToLocalTime().ToString("d MMM, HH:mm", Portuguese)} · {Duration(meeting)}",
                        Tokens.Fonts.Caption, Tokens.Brushes.Muted),
                },
            },
        };
        if (snippet.Length > 0) ((StackPanel)button.Content).Children.Add(Snippet(snippet));
        button.Classes.Add("nav");
        button.Click += (_, _) =>
        {
            _selected = meeting.Id;
            RefreshList();
            ShowSelected();
        };
        return button;
    }

    private void ShowSelected()
    {
        if (_coordinator?.Session is { } live && live.Transcript.Id == _selected)
        {
            _detail.Content = new ChatView(live, null, _store, _coordinator);
            return;
        }

        var meeting = _store.All().FirstOrDefault(m => m.Id == _selected);
        _detail.Content = meeting is null
            ? Ui.EmptyState(Icons.Mic, "Nenhuma reunião ainda", "As transcrições de reunião aparecem aqui, como uma conversa.")
            : new ChatView(null, meeting, _store, _coordinator);
    }

    internal static string Duration(MeetingTranscript meeting)
    {
        var length = (meeting.EndedAt ?? DateTimeOffset.Now) - meeting.StartedAt;
        return length.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours}h{length.Minutes:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)length.TotalMinutes)} min");
    }

    private static StackPanel ButtonContent(string label, string icon) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = Tokens.Space.Snug,
        Children =
        {
            Icons.Make(icon, new SolidColorBrush(Tokens.Colors.OnPrimary), Tokens.Size.Icon - 2),
            Ui.Text(label, Tokens.Fonts.Body, new SolidColorBrush(Tokens.Colors.OnPrimary), FontWeight.Medium),
        },
    };
}

/// <summary>
/// One meeting as a chat: bubbles grouped by speaker, the user on the right, names that can
/// be clicked to rename.
/// </summary>
public sealed class ChatView : UserControl
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Consecutive messages closer together than this share one name header.</summary>
    private static readonly TimeSpan GroupWithin = TimeSpan.FromMinutes(2);

    private readonly MeetingSession? _live;
    private readonly MeetingStore _store;
    private readonly MeetingCoordinator? _coordinator;
    private readonly Func<string?>? _instruction;
    private readonly PlaybackHub? _playback;
    private readonly ContentControl _playerHost = new();
    // By position in the list, not by object: saving a summary reloads the transcript from
    // disk, and the reloaded messages are new objects the bubbles on screen do not know.
    private readonly Dictionary<int, Border> _bubbles = [];
    private int _playing = -1;
    private AudioPlayerBar? _player;
    private MeetingTranscript _transcript;
    private readonly StackPanel _messages = new() { Spacing = Tokens.Space.Tight };
    private readonly ContentControl _summary = new();
    private string? _summaryError;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _subtitle;

    /// <summary>Shows a live session, or a saved transcript.</summary>
    /// <param name="live">A meeting being transcribed now, or null.</param>
    /// <param name="saved">A saved transcript, when <paramref name="live"/> is null.</param>
    /// <param name="store">Where <paramref name="saved"/> lives.</param>
    /// <param name="coordinator">Writes summaries; null hides them.</param>
    /// <param name="instruction">The user's context for the AI, read when a summary starts.</param>
    /// <param name="playback">The app's audio playback; null shows no player.</param>
    public ChatView(
        MeetingSession? live,
        MeetingTranscript? saved,
        MeetingStore store,
        MeetingCoordinator? coordinator = null,
        Func<string?>? instruction = null,
        PlaybackHub? playback = null)
    {
        _playback = playback;
        _live = live;
        _store = store;
        _coordinator = coordinator;
        _instruction = instruction;
        _transcript = live?.Snapshot() ?? saved ?? new MeetingTranscript();

        _subtitle = Ui.Text(string.Empty, Tokens.Fonts.Label, Tokens.Brushes.Muted);

        var copy = Ui.IconButton(Icons.Copy, "Copiar a conversa");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_transcript.ToPlainText()).ConfigureAwait(true);
            }
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Hair, Children = { copy } };
        if (live is null)
        {
            var export = Ui.IconButton(Icons.Export, "Exportar");
            ToolTip.SetTip(export, "Exportar para PDF, Word, Markdown ou texto");
            var formats = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            formats.Content = FormatMenu(formats);
            export.Click += (_, _) => formats.ShowAt(export);
            actions.Children.Add(export);

            var delete = Ui.IconButton(Icons.Trash, saved?.SourceFile is null ? "Excluir reunião" : "Excluir transcrição");
            delete.Click += (_, _) => _store.Delete(_transcript.Id);
            actions.Children.Add(delete);
        }

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
            Children =
            {
                new StackPanel
                {
                    Spacing = Tokens.Space.Hair,
                    Children = { Ui.Text(_transcript.Title, Tokens.Fonts.Heading, weight: FontWeight.SemiBold), _subtitle },
                },
            },
        };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        // The summary sits above the conversation, inside the same scroll.
        _scroll = new ScrollViewer
        {
            Content = new StackPanel { Spacing = Tokens.Space.Base, Children = { _summary, _messages } },
            Padding = new Thickness(0, 0, Tokens.Space.Snug, 0),
        };

        var body = new DockPanel { Children = { Ui.Docked(header, Dock.Top) } };

        // A transcribed recording can be listened to, beside its text.
        if (live is null && _transcript.SourceFile is not null && playback is not null)
        {
            _playerHost.Margin = new Thickness(0, 0, 0, Tokens.Space.Base);
            body.Children.Add(Ui.Docked(_playerHost, Dock.Top));
            ShowPlayer();
        }

        body.Children.Add(_scroll);
        Content = Ui.Card(body, Tokens.Space.Roomy);

        if (live is not null)
        {
            live.MessageAdded += (_, _) => Dispatcher.UIThread.Post(Reload);
            live.SpeakersChanged += (_, _) => Dispatcher.UIThread.Post(Reload);
        }

        if (coordinator is not null && live is null)
        {
            coordinator.SummaryChanged += OnSummaryChanged;
            DetachedFromVisualTree += (_, _) => coordinator.SummaryChanged -= OnSummaryChanged;
        }

        Render();
        RenderSummary();
    }

    /// <summary>The formats a transcript exports to: extension, name, what it is good for.</summary>
    private static readonly (string Extension, string Name, string Detail)[] Formats =
    [
        ("pdf", "PDF", "Com o visual do app, pronto para enviar"),
        ("docx", "Word", "Para editar no Word ou no Google Docs"),
        ("md", "Markdown", "Para Notion, Obsidian e afins"),
        ("txt", "Texto", "Só o texto, abre em qualquer lugar"),
    ];

    /// <summary>The export button's menu: the format first, then where to save it.</summary>
    private StackPanel FormatMenu(Flyout flyout)
    {
        var menu = new StackPanel { Width = 260, Spacing = Tokens.Space.Hair };
        var caption = Ui.Text("Exportar como", Tokens.Fonts.Caption, Tokens.Brushes.Muted, FontWeight.Medium);
        caption.Margin = new Thickness(Tokens.Space.Snug, Tokens.Space.Tight, 0, Tokens.Space.Tight);
        menu.Children.Add(caption);

        foreach (var (extension, name, detail) in Formats)
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
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        Ui.Text(name, Tokens.Fonts.Body, Tokens.Brushes.Foreground, FontWeight.SemiBold),
                        Ui.Text(detail, Tokens.Fonts.Caption, Tokens.Brushes.Muted),
                    },
                },
            };
            item.Classes.Add("nav");
            Avalonia.Automation.AutomationProperties.SetName(item, $"Exportar como {name}");
            item.Click += async (_, _) =>
            {
                flyout.Hide();
                await ExportAsync(extension, name).ConfigureAwait(true);
            };
            menu.Children.Add(item);
        }

        return menu;
    }

    /// <summary>Saves the transcript, with its summary, as <paramref name="extension"/>, where the user picks.</summary>
    private async Task ExportAsync(string extension, string name)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = $"Exportar transcrição como {name}",
            SuggestedFileName = TranscriptExport.FileName(_transcript),
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType(name) { Patterns = [$"*.{extension}"] }],
        }).ConfigureAwait(true);
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            stream.SetLength(0);
            // What was picked in the menu decides, whatever the name typed ends in.
            if (extension == "pdf")
            {
                TranscriptPdf.Write(_transcript, stream);
            }
            else if (extension == "docx")
            {
                TranscriptExport.WriteDocx(_transcript, stream);
            }
            else
            {
                var text = extension == "md" ? TranscriptExport.ToMarkdown(_transcript) : TranscriptExport.ToText(_transcript);
                await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
                await writer.WriteAsync(text).ConfigureAwait(true);
            }

            DiagnosticLog.Write($"exported a transcript as {extension}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"export failed: {e.Message}");
        }
        finally
        {
            file.Dispose();
        }
    }

    private void OnSummaryChanged(object? sender, EventArgs e)
    {
        // When the job for this meeting has just finished, the saved file has the summary.
        if (_coordinator?.Summary is null && _transcript.Summary is null)
        {
            _transcript = _store.All().FirstOrDefault(m => m.Id == _transcript.Id) ?? _transcript;
        }

        RenderSummary();
    }

    /// <summary>
    /// The summary area: nothing during a live meeting; otherwise a button to write one, the
    /// one being written, or the finished one. Never generated without the user asking.
    /// </summary>
    private void RenderSummary()
    {
        if (_live is not null || _coordinator is null || _transcript.Messages.Count == 0)
        {
            _summary.Content = null;
            return;
        }

        var job = _coordinator.Summary;
        if (job is not null && job.MeetingId == _transcript.Id)
        {
            var cancel = Ui.Secondary("Interromper", Icons.Stop);
            cancel.Click += (_, _) => _coordinator.CancelSummary();

            var text = job.CurrentText();
            var body = new StackPanel { Spacing = Tokens.Space.Snug };
            body.Children.Add(SummaryHeader("Gerando o resumo", job.Status, cancel, spinner: true));
            if (text.Length > 0) body.Children.Add(Ui.Markdown(text));
            _summary.Content = SummaryCard(body);
            return;
        }

        if (_transcript.Summary is { Length: > 0 } summary)
        {
            var copy = Ui.IconButton(Icons.Copy, "Copiar o resumo");
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(summary).ConfigureAwait(true);
            };

            var again = Ui.IconButton(Icons.Sparkles, "Gerar de novo");
            again.Click += async (_, _) => await GenerateAsync().ConfigureAwait(true);
            again.IsEnabled = job is null;

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Hair, Children = { copy, again } };
            _summary.Content = SummaryCard(new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children = { SummaryHeader(_transcript.SourceFile is null ? "Resumo da reunião" : "Resumo da gravação", Generated(_transcript.SummarizedAt), actions, spinner: false), Ui.Markdown(summary) },
            });
            return;
        }

        var generate = Ui.Primary(_transcript.SourceFile is null ? "Gerar resumo" : "Resumir com IA", Icons.Sparkles);
        generate.IsEnabled = job is null;
        generate.Click += async (_, _) => await GenerateAsync().ConfigureAwait(true);

        var hint = Speech.SummaryModel.IsInstalled()
            ? "Escrito por um modelo local: nada sai do seu computador. Leva alguns minutos."
            : "Escrito por um modelo local: nada sai do seu computador. Na primeira vez baixa 2 GB.";
        if (!string.IsNullOrWhiteSpace(_instruction?.Invoke())) hint += " Segue a sua instrução das Configurações.";

        var row = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children =
            {
                // A grid, not a horizontal StackPanel, so the hint wraps beside the button
                // instead of running off the edge of a narrow card.
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Children = { generate, Hint(hint) },
                },
            },
        };

        if (job is not null) row.Children.Add(Ui.Text("Outro resumo está sendo gerado.", Tokens.Fonts.Caption, Tokens.Brushes.Muted));
        if (_summaryError is not null) row.Children.Add(Ui.Text(_summaryError, Tokens.Fonts.Label, Tokens.Brushes.Destructive));
        _summary.Content = row;

        static TextBlock Hint(string text)
        {
            var hint = Ui.Text(text, Tokens.Fonts.Label, Tokens.Brushes.Muted);
            hint.Margin = new Thickness(Tokens.Space.Base, 0, 0, 0);
            Grid.SetColumn(hint, 1);
            return hint;
        }
    }

    /// <summary>"Gerado no seu computador · 1 de nov., 14:05".</summary>
    private static string Generated(DateTimeOffset? at) => at is { } when
        ? $"Gerado no seu computador · {when.ToLocalTime().ToString("d 'de' MMM, HH:mm", Portuguese)}"
        : "Gerado no seu computador";

    private async Task GenerateAsync()
    {
        if (_coordinator is null) return;
        _summaryError = null;
        _summaryError = await _coordinator.SummarizeAsync(_store, _transcript.Id, _instruction?.Invoke()).ConfigureAwait(true);
        _transcript = _store.All().FirstOrDefault(m => m.Id == _transcript.Id) ?? _transcript;
        RenderSummary();
    }

    /// <summary>
    /// Icon chip, title over a muted subtitle, actions on the right — then a hairline that
    /// fades out at both ends.
    /// </summary>
    private static StackPanel SummaryHeader(string title, string? subtitle, Control trailing, bool spinner)
    {
        var chip = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            Background = Blend((Tokens.Colors.Spectrum[1], 0.55, 0), (Tokens.Colors.Spectrum[3], 0.4, 1)),
            BorderBrush = WhiteFade(Vertical, (0.35, 0), (0.05, 1)),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 4 14 -4 #A08B5CF6"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Icons.Make(Icons.Sparkles, Brushes.White, Tokens.Size.Icon),
        };

        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text(title, Tokens.Fonts.Heading, weight: FontWeight.SemiBold));
        if (subtitle is not null) titles.Children.Add(Ui.Text(subtitle, Tokens.Fonts.Caption, Tokens.Brushes.Muted));
        if (spinner) titles.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, Height = 2, MinHeight = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, Tokens.Space.Tight, 0, 0) });

        // A grid, not a horizontal StackPanel: that gives the titles unlimited width, and the
        // subtitle then ran underneath the action buttons instead of wrapping.
        titles.Margin = new Thickness(Tokens.Space.Base, 0, Tokens.Space.Snug, 0);
        trailing.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(trailing, 2);

        return new StackPanel
        {
            Spacing = Tokens.Space.Roomy,
            Children =
            {
                new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { chip, titles, trailing } },
                new Border { Height = 1, Background = WhiteFade(Horizontal, (0, 0), (0.16, 0.2), (0.16, 0.8), (0, 1)) },
            },
        };
    }

    /// <summary>
    /// Frosted glass: a translucent tint with a faint white frost, a diagonal sheen, and a rim
    /// brightest along the top edge where the light falls.
    /// </summary>
    private static Border SummaryCard(Control content)
    {
        var radius = new CornerRadius(14);

        return new Border
        {
            CornerRadius = radius,
            BoxShadow = BoxShadows.Parse("0 16 40 -16 #C0000000"),
            Margin = new Thickness(0, 0, 0, Tokens.Space.Tight),
            Child = new Border
            {
                CornerRadius = radius,
                ClipToBounds = true,
                Child = new Grid
                {
                    Children =
                    {
                        new Border { Background = new SolidColorBrush(Tokens.Colors.Overlay, 0.72) },
                        new Border { Background = new SolidColorBrush(Colors.White, 0.03) },
                        new Border { Background = WhiteFade(Diagonal, (0.09, 0), (0.025, 0.45), (0, 1)) },
                        new Border
                        {
                            CornerRadius = radius,
                            BorderThickness = new Thickness(1),
                            BorderBrush = WhiteFade(Vertical, (0.32, 0), (0.08, 0.45), (0.04, 1)),
                        },
                        new Border { Padding = new Thickness(Tokens.Space.Wide - 2, Tokens.Space.Roomy + 2), Child = content },
                    },
                },
            },
        };
    }

    private static readonly (RelativePoint Start, RelativePoint End) Vertical =
        (new RelativePoint(0.5, 0, RelativeUnit.Relative), new RelativePoint(0.5, 1, RelativeUnit.Relative));

    private static readonly (RelativePoint Start, RelativePoint End) Horizontal =
        (new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative));

    private static readonly (RelativePoint Start, RelativePoint End) Diagonal =
        (new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative));

    /// <summary>White at each (opacity, offset) along <paramref name="direction"/>.</summary>
    private static LinearGradientBrush WhiteFade((RelativePoint Start, RelativePoint End) direction, params (double Alpha, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = direction.Start, EndPoint = direction.End };
        foreach (var (alpha, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * alpha), 255, 255, 255), offset));
        }

        return brush;
    }

    /// <summary>A top-left to bottom-right blend of (colour, opacity, offset) stops.</summary>
    private static LinearGradientBrush Blend(params (Color Color, double Alpha, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative) };
        foreach (var (color, alpha, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B), offset));
        }

        return brush;
    }

    private void Reload()
    {
        if (_live is null) return;
        _transcript = _live.Snapshot();
        Render();
    }

    private void Render()
    {
        // Follow new messages only if the user was already at the bottom, so reading back
        // during a live meeting is not interrupted.
        var atBottom = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 24;

        _subtitle.Text = _live is not null
            ? $"Ao vivo · {_transcript.App} · {_transcript.Speakers.Count} pessoas"
            : $"{_transcript.App} · {_transcript.StartedAt.ToLocalTime().ToString("dddd, d 'de' MMMM, HH:mm", Portuguese)} · {MeetingsView.Duration(_transcript)}";

        _messages.Children.Clear();

        // Rebuilt bubbles: the next position update highlights the current one again.
        _bubbles.Clear();
        _playing = -1;

        if (_transcript.Messages.Count == 0)
        {
            _messages.Children.Add(Ui.EmptyState(
                Icons.Mic,
                _live is not null ? "Ouvindo…" : "Nada foi dito",
                _live is not null ? "As falas aparecem aqui alguns segundos depois de cada pessoa terminar." : "Esta reunião não tem falas transcritas."));
            return;
        }

        MeetingMessage? previous = null;
        foreach (var message in _transcript.Messages)
        {
            var grouped = previous is not null
                && previous.SpeakerId == message.SpeakerId
                && message.At - previous.At < GroupWithin;

            _messages.Children.Add(Bubble(message, showHeader: !grouped));
            previous = message;
        }

        if (_live is not null && (atBottom || _transcript.Messages.Count <= 1))
        {
            Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private Control Bubble(MeetingMessage message, bool showHeader)
    {
        var speaker = _transcript.Speaker(message.SpeakerId) ?? new MeetingSpeaker { Name = "?" };
        var mine = speaker.IsLocal;

        var bubble = new Border
        {
            MaxWidth = 520,
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
            CornerRadius = mine
                ? new CornerRadius(Tokens.Radius.Shell, showHeader ? Tokens.Radius.Small : Tokens.Radius.Shell, Tokens.Radius.Shell, Tokens.Radius.Shell)
                : new CornerRadius(showHeader ? Tokens.Radius.Small : Tokens.Radius.Shell, Tokens.Radius.Shell, Tokens.Radius.Shell, Tokens.Radius.Shell),
            Background = mine ? Tokens.Brushes.PrimaryGlass : Tokens.Brushes.GlassFillRaised,
            BorderBrush = mine ? Tokens.Brushes.PrimaryRim : Tokens.Brushes.GlassRimSubtle,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = Ui.Text(message.Text, Tokens.Fonts.Body, mine ? new SolidColorBrush(Tokens.Colors.OnPrimary) : null),
        };
        _bubbles[_transcript.Messages.IndexOf(message)] = bubble;

        var column = new StackPanel
        {
            Spacing = Tokens.Space.Tight,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        if (showHeader)
        {
            var name = new Button
            {
                Padding = new Thickness(0),
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Content = Ui.Text(speaker.Name, Tokens.Fonts.Label, new SolidColorBrush(ColorFor(speaker)), FontWeight.SemiBold),
            };
            ToolTip.SetTip(name, "Renomear");
            name.Click += (_, _) => ShowRename(name, speaker);

            column.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Snug,
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, Tokens.Space.Snug, 0, 0),
                Children =
                {
                    name,
                    Timestamp(message),
                },
            });
        }

        column.Children.Add(bubble);

        if (mine) return column;

        // Others get an avatar beside their first bubble; the rest line up under it.
        var avatar = showHeader ? Avatar(speaker) : new Border { Width = 32 };
        avatar.VerticalAlignment = VerticalAlignment.Bottom;

        // A grid, not a horizontal StackPanel: that offers the bubble unlimited width, so
        // long messages from other people never wrapped and ran off the card.
        column.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);
        Grid.SetColumn(column, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { avatar, column },
        };
    }

    /// <summary>
    /// The player for the recording, or — when the file has moved, or the transcript is from
    /// before paths were kept — a way to point at it again.
    /// </summary>
    private void ShowPlayer()
    {
        var source = _transcript.SourceFile!;
        if (File.Exists(source))
        {
            _player = new AudioPlayerBar(_playback!, source, _transcript.Title, _transcript.Id);
            _player.PositionChanged += (_, e) => FollowPlayback(e.Position, e.Playing);
            _playerHost.Content = _player;
            return;
        }

        var locate = Ui.Secondary("Localizar arquivo", Icons.Folder);
        locate.Click += async (_, _) => await LocateAsync().ConfigureAwait(true);

        var note = Ui.Text(
            $"Para ouvir, aponte onde está \"{Path.GetFileName(source)}\".",
            Tokens.Fonts.Label, Tokens.Brushes.Muted);
        note.Margin = new Thickness(Tokens.Space.Base, 0, 0, 0);
        Grid.SetColumn(note, 1);
        _playerHost.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { locate, note } };
    }

    private async Task LocateAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var name = Path.GetFileName(_transcript.SourceFile ?? string.Empty);
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = $"Onde está {name}?",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Áudio") { Patterns = ["*.mp3", "*.wav", "*.m4a", "*.aac", "*.wma"] }],
        }).ConfigureAwait(true);

        if (files.Count == 0 || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(files[0]) is not { } path) return;

        _transcript.SourceFile = path;
        _store.Save(_transcript);
        ShowPlayer();

        // The timestamps become links now there is something to play.
        Render();
    }

    /// <summary>
    /// Lights up the message being heard and, while playing, scrolls it into view — the text
    /// follows the audio like karaoke. Paused, the highlight stays but the view is left alone,
    /// so reading elsewhere is not interrupted.
    /// </summary>
    public void FollowPlayback(TimeSpan position, bool playing)
    {
        var at = _transcript.StartedAt + position;
        var current = -1;
        for (var i = 0; i < _transcript.Messages.Count && _transcript.Messages[i].At <= at; i++) current = i;

        // Before the first word, nothing is being said yet.
        if (position <= TimeSpan.Zero && !playing) current = -1;
        if (current == _playing) return;

        if (_bubbles.TryGetValue(_playing, out var old)) Highlight(old, _transcript.Messages[_playing], on: false);
        _playing = current;
        if (!_bubbles.TryGetValue(current, out var bubble)) return;

        Highlight(bubble, _transcript.Messages[current], on: true);
        // With room above and below, so the line being heard is not pinned to the very edge.
        if (playing) bubble.BringIntoView(new Rect(0, -120, bubble.Bounds.Width, bubble.Bounds.Height + 240));
    }

    private void Highlight(Border bubble, MeetingMessage message, bool on)
    {
        var mine = _transcript.Speaker(message.SpeakerId)?.IsLocal == true;
        if (on)
        {
            bubble.Background = mine ? Tokens.Brushes.PrimaryGlass : Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.2);
            bubble.BorderBrush = mine ? Brushes.White : Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.85);
            bubble.BoxShadow = BoxShadows.Parse("0 0 14 0 #804577E9");
        }
        else
        {
            bubble.Background = mine ? Tokens.Brushes.PrimaryGlass : Tokens.Brushes.GlassFillRaised;
            bubble.BorderBrush = mine ? Tokens.Brushes.PrimaryRim : Tokens.Brushes.GlassRimSubtle;
            bubble.BoxShadow = default;
        }
    }

    /// <summary>The message being played now, if any. Exposed for tests.</summary>
    public MeetingMessage? PlayingMessage => _playing >= 0 && _playing < _transcript.Messages.Count ? _transcript.Messages[_playing] : null;

    /// <summary>
    /// When it was said. For a recording with a player, a link: clicking plays from there.
    /// </summary>
    private Control Timestamp(MeetingMessage message)
    {
        var text = Ui.Text(_transcript.TimeOf(message), Tokens.Fonts.Caption, Tokens.Brushes.Muted);
        if (_player is null) return text;

        text.Foreground = Tokens.Brushes.Primary;
        var link = new Button
        {
            Padding = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Content = text,
        };
        ToolTip.SetTip(link, "Ouvir deste ponto");
        var at = message.At - _transcript.StartedAt;
        link.Click += (_, _) => _player.PlayFrom(at < TimeSpan.Zero ? TimeSpan.Zero : at);
        return link;
    }

    private static Border Avatar(MeetingSpeaker speaker) => new()
    {
        Width = 32,
        Height = 32,
        CornerRadius = new CornerRadius(Tokens.Radius.Pill),
        Background = new SolidColorBrush(ColorFor(speaker), 0.22),
        Child = new TextBlock
        {
            Text = Initials(speaker.Name),
            FontFamily = Tokens.Fonts.Sans,
            FontSize = Tokens.Fonts.Label,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(ColorFor(speaker)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>A stable colour per person, from the waveform spectrum.</summary>
    public static Color ColorFor(MeetingSpeaker speaker) => speaker.IsLocal
        ? Tokens.Colors.Primary
        : Tokens.Colors.SpectrumAt(((speaker.Id * 0.37) % 1 + 1) % 1);

    /// <summary>"Ana Silva" → "AS"; "Participante 2" → "P2".</summary>
    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1) return words[0][..1].ToUpper(CultureInfo.CurrentCulture);
        return string.Concat(words[0][..1], words[^1][..1]).ToUpper(CultureInfo.CurrentCulture);
    }

    private void ShowRename(Control anchor, MeetingSpeaker speaker)
    {
        var field = Ui.Field("Nome", speaker.Name);
        var remember = new CheckBox
        {
            Content = Ui.Text("Lembrar esta voz nas próximas reuniões", Tokens.Fonts.Label),
            IsChecked = true,
            IsVisible = !speaker.IsLocal && (_live is not null || (_coordinator is not null && speaker.Voice is { Length: > 0 })),
        };

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var save = Ui.Primary("Salvar");
        save.Click += (_, _) =>
        {
            var name = (field.Text ?? string.Empty).Trim();
            if (name.Length > 0) Rename(speaker, name, remember.IsChecked == true);
            flyout.Hide();
        };

        flyout.Content = new StackPanel
        {
            Width = 280,
            Spacing = Tokens.Space.Snug,
            Children = { Ui.Text("Renomear", Tokens.Fonts.Body, weight: FontWeight.SemiBold), field, remember, save },
        };
        flyout.ShowAt(anchor);
    }

    private void Rename(MeetingSpeaker speaker, string name, bool remember)
    {
        if (_live is not null)
        {
            _live.Rename(speaker.Id, name, remember);
            return;
        }

        // A saved meeting or recording: rename in the file, and remember the voice print it kept.
        if (_transcript.Speaker(speaker.Id) is { } target)
        {
            target.Name = name;
            target.IsNamed = true;
            if (remember && !target.IsLocal && target.Voice is { Length: > 0 } voice && _coordinator is not null)
            {
                _coordinator.Voices.Remember(name, voice, Math.Max(1, target.VoiceCount));
            }

            _store.Save(_transcript);
            Render();
        }
    }
}
