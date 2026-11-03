using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// Past transcriptions, grouped by day: searchable, each copyable and deletable.
/// </summary>
/// <remarks>
/// Rows show which dictionary corrections fired. Without that the dictionary is invisible and
/// there is no way to tell a rule that works from one that never matches.
/// </remarks>
public sealed class TranscriptionsView : UserControl
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    private readonly TranscriptStore _store;
    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly TextBlock _count;

    /// <summary>Builds the view over <paramref name="store"/>.</summary>
    public TranscriptionsView(TranscriptStore store)
    {
        _store = store;

        _search = Ui.SearchBox("Buscar transcrições");
        _search.TextChanged += (_, _) => Refresh();

        _list = new StackPanel { Spacing = Tokens.Space.Snug, Margin = new Thickness(0, 0, Tokens.Space.Snug, Tokens.Space.Page) };
        _count = Ui.Text(string.Empty, Tokens.Fonts.Label, Tokens.Brushes.Muted);

        var clear = Ui.Secondary("Limpar tudo", Icons.Trash);
        clear.Click += (_, _) => { _store.Clear(); Refresh(); };

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
        };
        toolbar.Children.Add(_search);
        _count.Margin = new Thickness(Tokens.Space.Base, 0);
        Grid.SetColumn(_count, 1);
        toolbar.Children.Add(_count);
        Grid.SetColumn(clear, 2);
        toolbar.Children.Add(clear);

        Content = new DockPanel
        {
            Children =
            {
                Ui.Docked(toolbar, Dock.Top),
                new ScrollViewer { Content = _list },
            },
        };

        // Posted, not called: the engine adds finished dictations from a background thread,
        // and touching a control there throws — out through the engine's Completed event,
        // which then skips typing the text.
        _store.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var records = _store.Search(_search.Text ?? string.Empty);
        var total = _store.Records.Count;

        _list.Children.Clear();
        _count.Text = total == 1 ? "1 transcrição" : $"{total} transcrições";

        if (records.Count == 0)
        {
            _list.Children.Add(total == 0
                ? Ui.EmptyState(Icons.Mic, "Nenhuma transcrição ainda", "Segure o atalho e fale — o texto aparece aqui também.")
                : Ui.EmptyState(Icons.Search, "Nada encontrado", "Tente outra busca."));
            return;
        }

        foreach (var day in records.GroupBy(r => r.At.ToLocalTime().Date))
        {
            var heading = Ui.Text(DayLabel(day.Key), Tokens.Fonts.Label, Tokens.Brushes.Muted, FontWeight.Medium);
            heading.Margin = new Thickness(Tokens.Space.Hair, _list.Children.Count == 0 ? 0 : Tokens.Space.Base, 0, 0);
            _list.Children.Add(heading);

            foreach (var record in day) _list.Children.Add(BuildRow(record));
        }
    }

    private static string DayLabel(DateTime date)
    {
        var today = DateTime.Now.Date;
        if (date == today) return "Hoje";
        if (date == today.AddDays(-1)) return "Ontem";
        return date.ToString("d 'de' MMMM", Portuguese);
    }

    private Border BuildRow(TranscriptRecord record)
    {
        var copy = Ui.IconButton(Icons.Copy, "Copiar");
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(record.Text).ConfigureAwait(true);

            copy.Content = Icons.Make(Icons.Check, new SolidColorBrush(Tokens.Colors.Success), Tokens.Size.Icon - 2);
            await Task.Delay(TimeSpan.FromSeconds(1.4)).ConfigureAwait(true);
            copy.Content = Icons.Make(Icons.Copy, Tokens.Brushes.Muted, Tokens.Size.Icon - 2);
        };

        var delete = Ui.IconButton(Icons.Trash, "Excluir");
        delete.Click += (_, _) => _store.Remove(record.Id);

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                Ui.Text(record.At.ToLocalTime().ToString("HH:mm", Portuguese), Tokens.Fonts.Caption, Tokens.Brushes.Muted),
                Ui.Text("·", Tokens.Fonts.Caption, Tokens.Brushes.Muted),
                Ui.Text(
                    record.AudioSeconds.ToString("0.0", Portuguese) + " s de áudio",
                    Tokens.Fonts.Caption, Tokens.Brushes.Muted),
            },
        };

        var body = new StackPanel { Spacing = Tokens.Space.Snug };
        body.Children.Add(Ui.Selectable(record.Text, Tokens.Fonts.Body));
        if (record.Corrections is { Count: > 0 } corrections) body.Children.Add(BuildCorrectionBadges(corrections));
        body.Children.Add(meta);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Hair,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Tokens.Space.Snug, -Tokens.Space.Tight, -Tokens.Space.Tight, 0),
            Children = { copy, delete },
        };
        Grid.SetColumn(actions, 1);

        return Ui.Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { body, actions },
        }, Tokens.Space.Base + 2);
    }

    /// <summary>Shows that the dictionary fired, and on what.</summary>
    private static WrapPanel BuildCorrectionBadges(IReadOnlyList<Dictionary.AppliedCorrection> corrections)
    {
        var row = new WrapPanel { ItemSpacing = Tokens.Space.Tight + 2, LineSpacing = Tokens.Space.Tight };

        foreach (var correction in corrections)
        {
            var label = correction.Count > 1
                ? $"{correction.From} → {correction.To} ×{correction.Count}"
                : $"{correction.From} → {correction.To}";

            row.Children.Add(new Border
            {
                Background = Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.14),
                CornerRadius = new CornerRadius(Tokens.Radius.Small),
                Padding = new Thickness(Tokens.Space.Snug - 2, Tokens.Space.Hair),
                Child = Ui.Text(label, Tokens.Fonts.Caption, Tokens.Brushes.Primary, FontWeight.Medium),
            });
        }

        return row;
    }
}
