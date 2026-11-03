using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TalkToText.Abstractions;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;
using TalkToText.Speech;

namespace TalkToText.App.Views;

/// <summary>
/// Transcribe a recorded audio file — an MP3 of a call, a WAV from a recorder — and, if the
/// user asks, summarise it with the local AI.
/// </summary>
/// <remarks>
/// Nothing starts on its own: attaching a file only shows it, with a Transcribe button, and the
/// summary is a further button on the finished transcript. Both can be interrupted; an
/// interrupted transcription keeps what it had, saved as "(parcial)".
/// </remarks>
public sealed class AudioFilesView : UserControl
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    private readonly Composition? _composition;
    private readonly MeetingCoordinator? _coordinator;
    private readonly MeetingStore _store;
    private readonly IAudioFileDecoder? _decoder;
    private readonly PlaybackHub _playback;
    private readonly StackPanel _list = new() { Spacing = Tokens.Space.Tight };
    private readonly TextBox _search = Ui.SearchBox("Buscar nas gravações");
    private readonly ContentControl _detail = new();
    private readonly Border _dropZone;
    private readonly Rectangle _dropRim;

    private AttachedFile? _attached;
    private Running? _running;
    private string? _error;
    private Guid? _selected;

    /// <summary>Builds the page.</summary>
    public AudioFilesView(Composition? composition, MeetingCoordinator? coordinator, PlaybackHub? playback = null)
    {
        _playback = playback ?? new PlaybackHub(PlatformFactory.CreateAudioPlayer);
        _composition = composition;
        _coordinator = coordinator;
        _store = composition?.AudioFiles ?? new MeetingStore(MeetingStore.AudioFilesDirectory);
        _decoder = PlatformFactory.CreateAudioFileDecoder();

        (_dropZone, _dropRim) = BuildDropZone();

        var left = new DockPanel
        {
            Width = 268,
            Margin = new Thickness(0, 0, Tokens.Space.Roomy, 0),
            Children =
            {
                Ui.Docked(new StackPanel
                {
                    Spacing = Tokens.Space.Base,
                    Children =
                    {
                        Ui.PageHeader("Transcrever áudio", "Tudo é processado no seu computador."),
                        _dropZone,
                        Ui.Text("Transcrições", Tokens.Fonts.Label, Tokens.Brushes.Muted, FontWeight.Medium),
                        _search,
                    },
                }, Dock.Top),
                new ScrollViewer { Content = _list, Margin = new Thickness(0, Tokens.Space.Snug, 0, 0) },
            },
        };

        Content = new DockPanel
        {
            Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Page - 4, Tokens.Space.Page, Tokens.Space.Roomy),
            Children = { Ui.Docked(left, Dock.Left), _detail },
        };

        // The open transcript is rebuilt only when it was deleted: renaming someone or saving
        // a summary also saves, and rebuilding then would stop the audio mid-sentence.
        _store.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var gone = _selected is { } id && _store.All().All(m => m.Id != id);
            if (gone) _selected = null;
            RefreshList();
            if (gone && _attached is null && _running is null) ShowDetail();
        });

        Avalonia.Automation.AutomationProperties.SetName(_search, "Buscar nas gravações");
        _search.TextChanged += (_, _) => RefreshList();

        RefreshList();
        ShowDetail();
    }

    /// <summary>Opens the transcript <paramref name="id"/> — from the mini player's title.</summary>
    public void Select(Guid id)
    {
        if (_running is not null) return;
        _selected = id;
        _attached = null;
        _error = null;
        RefreshList();
        ShowDetail();
    }

    /// <summary>Whether a transcription is running. Exposed for headless tests.</summary>
    public bool IsTranscribing => _running is not null;

    /// <summary>
    /// Attaches <paramref name="path"/>, showing it with a Transcribe button. Does not start.
    /// Public for headless tests and for drag and drop.
    /// </summary>
    public void Attach(string path)
    {
        if (_running is not null) return;
        _error = null;

        if (_decoder is null)
        {
            _error = "Ler arquivos de áudio só funciona no Windows.";
            ShowDetail();
            return;
        }

        try
        {
            using var audio = _decoder.Open(path);
            _attached = new AttachedFile(path, new FileInfo(path).Length, audio.Duration);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Write($"audio file could not be opened: {path}: {e.Message}");
            _attached = null;
            _error = $"Não foi possível ler \"{System.IO.Path.GetFileName(path)}\". Use um arquivo MP3 ou WAV.";
        }

        ShowDetail();
    }

    private (Border Zone, Rectangle Rim) BuildDropZone()
    {
        var chip = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            Background = Tokens.Brushes.GlassFillRaised,
            BorderBrush = Tokens.Brushes.GlassRim,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = Icons.Make(Icons.Upload, Tokens.Brushes.Foreground, 18),
            },
        };

        var text = new StackPanel
        {
            Spacing = Tokens.Space.Hair,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                Centered(Ui.Text("Arraste um arquivo de áudio", Tokens.Fonts.Body, weight: FontWeight.Medium)),
                Centered(Ui.Text("ou clique para escolher", Tokens.Fonts.Label, Tokens.Brushes.Muted)),
                Centered(Ui.Text("MP3 ou WAV (também M4A, AAC, WMA)", Tokens.Fonts.Caption, Tokens.Brushes.Muted)),
            },
        };

        // A dashed rim says "drop here" better than a solid one; Border cannot dash.
        var rim = new Rectangle
        {
            RadiusX = Tokens.Radius.Card,
            RadiusY = Tokens.Radius.Card,
            Stroke = new SolidColorBrush(Colors.White, 0.22),
            StrokeThickness = 1.5,
            StrokeDashArray = [5, 4],
            IsHitTestVisible = false,
        };

        var zone = new Border
        {
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            Background = Tokens.Brushes.GlassFill,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid
            {
                Children =
                {
                    rim,
                    new StackPanel
                    {
                        Spacing = Tokens.Space.Snug,
                        Margin = new Thickness(Tokens.Space.Roomy, Tokens.Space.Wide),
                        Children = { chip, text },
                    },
                },
            },
        };

        DragDrop.SetAllowDrop(zone, true);
        zone.AddHandler(DragDrop.DragEnterEvent, (_, e) => Highlight(e, true));
        zone.AddHandler(DragDrop.DragOverEvent, (_, e) => Highlight(e, true));
        zone.AddHandler(DragDrop.DragLeaveEvent, (_, e) => Highlight(e, false));
        zone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Highlight(e, false);
            if (DroppedPath(e) is { } path) Attach(path);
        });
        zone.PointerPressed += async (_, _) => await PickAsync().ConfigureAwait(true);

        return (zone, rim);
    }

    private void Highlight(DragEventArgs e, bool on)
    {
        var accepted = on && _running is null && DroppedPath(e) is not null;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        _dropRim.Stroke = accepted ? Tokens.Brushes.Primary : new SolidColorBrush(Colors.White, 0.22);
        _dropZone.Background = accepted ? Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.1) : Tokens.Brushes.GlassFill;
    }

#pragma warning disable CS0618 // DragEventArgs.Data: the replacement API is not in Avalonia 11.3.
    private static string? DroppedPath(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
#pragma warning restore CS0618

    private async Task PickAsync()
    {
        if (_running is not null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var extensions = _decoder?.Extensions ?? [".mp3", ".wav"];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolha um arquivo de áudio",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Áudio") { Patterns = [.. extensions.Select(x => "*" + x)] },
            ],
        }).ConfigureAwait(true);

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) Attach(path);
    }

    private void ShowDetail()
    {
        _dropZone.IsEnabled = _running is null;
        _dropZone.Opacity = _running is null ? 1 : 0.5;

        if (_running is { } running)
        {
            _detail.Content = RunningCard(running);
            return;
        }

        if (_attached is { } file)
        {
            _detail.Content = AttachedCard(file);
            return;
        }

        var saved = _selected is { } id ? _store.All().FirstOrDefault(m => m.Id == id) : null;
        if (saved is not null)
        {
            _detail.Content = new ChatView(null, saved, _store, _coordinator, () => _composition?.Settings.Data.AiInstruction, _playback);
            return;
        }

        var empty = Ui.EmptyState(Icons.AudioFile, "Nenhum arquivo", "Anexe uma gravação para transcrever e, se quiser, resumir com IA.");
        _detail.Content = _error is null
            ? Ui.Card(empty)
            : Ui.Card(new StackPanel { Children = { empty, Centered(Ui.Text(_error, Tokens.Fonts.Label, Tokens.Brushes.Destructive)) } });
    }

    private Border AttachedCard(AttachedFile file)
    {
        var transcribe = Ui.Primary("Transcrever", Icons.Mic);
        transcribe.Click += async (_, _) => await TranscribeAsync(file).ConfigureAwait(true);
        transcribe.IsEnabled = _composition?.Transcriber is not null;

        var remove = Ui.IconButton(Icons.Close, "Remover arquivo");
        remove.Click += (_, _) =>
        {
            _attached = null;
            _error = null;
            ShowDetail();
        };

        var body = new StackPanel
        {
            Spacing = Tokens.Space.Roomy,
            Children =
            {
                FileRow(file, remove),
                Ui.Text(
                    "Cada voz diferente vira uma pessoa na conversa; você pode renomeá-las depois. "
                    + "O dicionário corrige nomes e termos.",
                    Tokens.Fonts.Label, Tokens.Brushes.Muted),
                transcribe,
            },
        };

        if (_composition?.Transcriber is null)
        {
            body.Children.Add(Ui.Text("O modelo de voz não está instalado.", Tokens.Fonts.Label, Tokens.Brushes.Destructive));
        }

        if (_error is not null) body.Children.Add(Ui.Text(_error, Tokens.Fonts.Label, Tokens.Brushes.Destructive));
        return Detail(body);
    }

    private static Border RunningCard(Running running)
    {
        var stop = Ui.Secondary("Interromper", Icons.Stop);
        stop.Click += (_, _) =>
        {
            running.Status.Text = "Interrompendo…";
            running.Cancellation.Cancel();
        };

        return Detail(new StackPanel
        {
            Spacing = Tokens.Space.Roomy,
            Children =
            {
                FileRow(running.File, null),
                new StackPanel
                {
                    Spacing = Tokens.Space.Snug,
                    Children = { running.Status, running.Bar, running.Detail },
                },
                running.Preview,
                stop,
            },
        });
    }

    /// <summary>Icon, name, then "4,2 MB · 12:34".</summary>
    private static Grid FileRow(AttachedFile file, Control? trailing)
    {
        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            Background = Tokens.Brushes.PrimaryGlass,
            BorderBrush = Tokens.Brushes.PrimaryRim,
            BorderThickness = new Thickness(1),
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = Icons.Make(Icons.AudioFile, new SolidColorBrush(Tokens.Colors.OnPrimary), 18),
            },
        };

        var name = Ui.Text(System.IO.Path.GetFileName(file.Path), Tokens.Fonts.Heading, weight: FontWeight.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;

        var titles = new StackPanel
        {
            Spacing = Tokens.Space.Hair,
            Margin = new Thickness(Tokens.Space.Base, 0, Tokens.Space.Snug, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, Ui.Text($"{Size(file.Bytes)} · {Clock(file.Duration)}", Tokens.Fonts.Label, Tokens.Brushes.Muted) },
        };
        Grid.SetColumn(titles, 1);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { icon, titles } };
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }

        return row;
    }

    private static Border Detail(Control content) => Ui.Card(new StackPanel
    {
        MaxWidth = 560,
        HorizontalAlignment = HorizontalAlignment.Left,
        Children = { content },
    }, Tokens.Space.Wide);

    private async Task TranscribeAsync(AttachedFile file)
    {
        if (_running is not null || _composition?.Transcriber is not { } transcriber || _decoder is null) return;

        var running = new Running(file);
        _running = running;
        _attached = null;
        _error = null;
        ShowDetail();

        var transcript = AudioFileTranscriber.NewTranscript(file.Path, file.Duration, DateTimeOffset.Now);
        var token = running.Cancellation.Token;
        var stopped = false;

        try
        {
            if (!MeetingModels.IsInstalled())
            {
                running.Status.Text = "Baixando os modelos de voz (27 MB)…";
                await MeetingCoordinator.DownloadModelsAsync(
                    new Progress<double>(p => running.Bar.Value = p * 100), token).ConfigureAwait(true);
            }

            running.Status.Text = "Transcrevendo…";
            var progress = new Progress<FileTranscriptionProgress>(p =>
            {
                running.Bar.Value = p.Fraction * 100;
                running.Status.Text = running.Cancellation.IsCancellationRequested ? "Interrompendo…" : $"Transcrevendo… {p.Fraction:P0}";
                running.Detail.Text = $"{Clock(p.Done)} de {Clock(p.Total)} · {p.Messages} {(p.Messages == 1 ? "fala" : "falas")}";
                lock (transcript)
                {
                    if (transcript.Messages.Count > 0) running.Preview.Text = $"“{transcript.Messages[^1].Text}”";
                }
            });

            var dictionary = _composition.Dictionary.Entries.ToList();
            var voices = _composition.Voices.All;

            await Task.Run(async () =>
            {
                using var audio = _decoder.Open(file.Path);
                using var segmenter = new SileroSpeechSegmenter();
                using var embedder = new WeSpeakerEmbedder();
                await new AudioFileTranscriber(transcriber, segmenter, embedder, voices, dictionary)
                    .TranscribeAsync(audio, transcript, progress, token).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(true);

            DiagnosticLog.Write($"audio file transcribed: {file.Path} ({transcript.Messages.Count} messages)");
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            DiagnosticLog.Write($"audio file transcription interrupted: {file.Path} ({transcript.Messages.Count} messages)");
        }
        catch (Exception e)
        {
            DiagnosticLog.Write($"audio file transcription failed: {file.Path}: {e}");
            _error = $"A transcrição falhou: {e.Message}";
        }
        finally
        {
            running.Cancellation.Dispose();
            _running = null;
        }

        if (transcript.Messages.Count > 0)
        {
            if (stopped) transcript.Title += " (parcial)";
            _store.Save(transcript);
            _selected = transcript.Id;
        }
        else
        {
            // Nothing to keep: show the file again, so it can be retried.
            _attached = file;
            _error ??= stopped ? "Interrompido antes de qualquer fala ser transcrita." : "Nenhuma fala encontrada neste arquivo.";
        }

        RefreshList();
        ShowDetail();
    }

    private void RefreshList()
    {
        _list.Children.Clear();
        var all = _store.All();

        if (all.Count == 0)
        {
            _list.Children.Add(Ui.Text("As gravações que você transcrever aparecem aqui.", Tokens.Fonts.Label, Tokens.Brushes.Muted));
            return;
        }

        var query = (_search.Text ?? string.Empty).Trim();
        var shown = 0;
        foreach (var item in all)
        {
            var snippet = TranscriptSearch.Find(item, query);
            if (snippet is null) continue;
            _list.Children.Add(ListItem(item, snippet));
            shown++;
        }

        if (shown == 0) _list.Children.Add(Ui.Text($"Nenhuma gravação menciona \"{query}\".", Tokens.Fonts.Label, Tokens.Brushes.Muted));
    }

    private Button ListItem(MeetingTranscript item, string snippet)
    {
        var selected = item.Id == _selected && _attached is null && _running is null;

        var name = Ui.Text(item.Title, Tokens.Fonts.Body, weight: FontWeight.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;

        var details = $"{item.StartedAt.ToLocalTime().ToString("d MMM, HH:mm", Portuguese)} · {MeetingsView.Duration(item)}";
        if (item.Summary is not null) details += " · resumo";

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            Background = selected ? Tokens.Brushes.GlassFillRaised : Brushes.Transparent,
            BorderBrush = selected ? Tokens.Brushes.GlassRimSubtle : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel
            {
                Spacing = Tokens.Space.Hair,
                Children = { name, Ui.Text(details, Tokens.Fonts.Caption, Tokens.Brushes.Muted) },
            },
        };
        if (snippet.Length > 0) ((StackPanel)button.Content).Children.Add(MeetingsView.Snippet(snippet));
        button.Classes.Add("nav");
        button.Click += (_, _) =>
        {
            if (_running is not null) return;
            _selected = item.Id;
            _attached = null;
            _error = null;
            RefreshList();
            ShowDetail();
        };
        return button;
    }

    private static TextBlock Centered(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        return text;
    }

    /// <summary>"12:34", or "1:02:10" past an hour.</summary>
    private static string Clock(TimeSpan time) =>
        time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>"850 KB", "4,2 MB".</summary>
    private static string Size(long bytes) => bytes < 1024 * 1024
        ? string.Create(Portuguese, $"{Math.Max(1, bytes / 1024)} KB")
        : string.Create(Portuguese, $"{bytes / (1024.0 * 1024):0.0} MB");

    /// <summary>A file the user attached but has not transcribed yet.</summary>
    private sealed record AttachedFile(string Path, long Bytes, TimeSpan Duration);

    /// <summary>A transcription in progress and the controls that show it.</summary>
    private sealed class Running(AttachedFile file)
    {
        public AttachedFile File { get; } = file;

        public CancellationTokenSource Cancellation { get; } = new();

        public TextBlock Status { get; } = Ui.Text("Preparando…", Tokens.Fonts.Body, weight: FontWeight.Medium);

        public ProgressBar Bar { get; } = new() { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4 };

        public TextBlock Detail { get; } = Ui.Text(string.Empty, Tokens.Fonts.Label, Tokens.Brushes.Muted);

        public TextBlock Preview { get; } = Ui.Text(string.Empty, Tokens.Fonts.Body, Tokens.Brushes.Muted);
    }
}
