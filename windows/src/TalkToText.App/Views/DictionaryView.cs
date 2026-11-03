using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;
using TalkToText.Dictionary;

namespace TalkToText.App.Views;

/// <summary>
/// The dictionary: add, edit, enable, delete, search.
/// </summary>
/// <remarks>
/// Both entry kinds live in one list rather than separate tabs — they are two shapes of the
/// same idea, and you want to see everything you have taught it at once.
/// </remarks>
public sealed class DictionaryView : UserControl
{
    private readonly DictionaryFile _file;
    private readonly TextBox _search;
    private readonly StackPanel _list;

    /// <summary>Builds the view over <paramref name="file"/>.</summary>
    public DictionaryView(DictionaryFile file)
    {
        _file = file;

        _search = Ui.SearchBox("Buscar no dicionário");
        _search.TextChanged += (_, _) => Refresh();

        var add = Ui.Primary("Adicionar", Icons.Plus);
        add.Click += (_, _) => ShowEditor(null);

        var open = Ui.Secondary("Abrir arquivo", Icons.Folder);
        open.Click += (_, _) => OpenInEditor(_file.FilePath);

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
        };
        toolbar.Children.Add(_search);
        open.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);
        Grid.SetColumn(open, 1);
        toolbar.Children.Add(open);
        add.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);
        Grid.SetColumn(add, 2);
        toolbar.Children.Add(add);

        _list = new StackPanel { Spacing = Tokens.Space.Snug, Margin = new Thickness(0, 0, Tokens.Space.Snug, Tokens.Space.Page) };

        Content = new DockPanel
        {
            Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Page - 4, Tokens.Space.Page, 0),
            Children =
            {
                Ui.Docked(Ui.PageHeader(
                    "Dicionário",
                    "Termos que ele deve reconhecer e correções do tipo \"quando ouvir X, escreva Y\"."), Dock.Top),
                Ui.Docked(toolbar, Dock.Top),
                new ScrollViewer { Content = _list },
            },
        };

        _file.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var entries = _file.Search(_search.Text ?? string.Empty);

        _list.Children.Clear();

        if (entries.Count == 0)
        {
            _list.Children.Add(_file.Entries.Count == 0
                ? Ui.EmptyState(Icons.Book, "Dicionário vazio", "Adicione nomes e palavras que ele costuma errar.")
                : Ui.EmptyState(Icons.Search, "Nada encontrado", "Tente outra busca."));
            return;
        }

        foreach (var entry in entries) _list.Children.Add(BuildRow(entry));
    }

    private Border BuildRow(DictionaryEntry entry)
    {
        var enabled = new ToggleSwitch
        {
            IsChecked = entry.IsEnabled,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabled.IsCheckedChanged += (_, _) => _file.Update(entry with { IsEnabled = enabled.IsChecked ?? false });
        ToolTip.SetTip(enabled, "Ativar / desativar");

        var edit = Ui.IconButton(Icons.Pencil, "Editar");
        edit.Click += (_, _) => ShowEditor(entry);

        var delete = Ui.IconButton(Icons.Trash, "Excluir");
        delete.Click += (_, _) => _file.Remove(entry.Id);

        var badge = new Border
        {
            Background = entry.Kind == EntryKind.Correction
                ? Tokens.Brushes.Tint(Tokens.Colors.Primary, 0.14)
                : Tokens.Brushes.GlassFillRaised,
            CornerRadius = new CornerRadius(Tokens.Radius.Small),
            Padding = new Thickness(Tokens.Space.Snug - 2, Tokens.Space.Hair),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Text(
                entry.Kind == EntryKind.Correction ? "Correção" : "Termo",
                Tokens.Fonts.Caption,
                entry.Kind == EntryKind.Correction ? Tokens.Brushes.Primary : Tokens.Brushes.Muted,
                FontWeight.Medium),
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                badge,
                Ui.Text(
                    entry.Kind == EntryKind.Correction ? $"{entry.Hear}  →  {entry.Write}" : entry.Write,
                    Tokens.Fonts.Body,
                    weight: FontWeight.Medium),
            },
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            Children = { enabled, edit, delete },
        };
        Grid.SetColumn(right, 1);

        var card = Ui.Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { left, right },
        }, Tokens.Space.Snug + 2);
        card.Padding = new Thickness(Tokens.Space.Roomy - 2, Tokens.Space.Snug - 2);
        card.Opacity = entry.IsEnabled ? 1 : 0.55;
        return card;
    }

    private void ShowEditor(DictionaryEntry? entry)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var editor = new DictionaryEditorWindow(entry);
        editor.Saved += (_, saved) =>
        {
            if (entry is null) _file.Add(saved); else _file.Update(saved);
        };

        _ = editor.ShowDialog(owner);
    }

    /// <summary>Opens the dictionary in the user's default text editor.</summary>
    private static void OpenInEditor(string path)
    {
        try
        {
            // The file must exist before the shell will open it — a brand-new install has
            // never saved one.
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Opening an editor is a convenience; failing to must never take the app down.
        }
    }
}

/// <summary>
/// Add or edit one dictionary entry, with the false-positive warning shown live.
/// </summary>
public sealed class DictionaryEditorWindow : Window
{
    private readonly Button _termKind;
    private readonly Button _correctionKind;
    private readonly TextBox _hear;
    private readonly TextBox _write;
    private readonly StackPanel _hearField;
    private readonly TextBlock _writeLabel;
    private readonly StackPanel _warnings;
    private readonly Button _save;
    private readonly Guid _id;
    private readonly bool _wasEnabled;

    private EntryKind _kind;

    /// <summary>Raised when the user saves.</summary>
    public event EventHandler<DictionaryEntry>? Saved;

    /// <summary>Creates the editor for a new or existing entry.</summary>
    public DictionaryEditorWindow(DictionaryEntry? entry)
    {
        _id = entry?.Id ?? Guid.NewGuid();
        _wasEnabled = entry?.IsEnabled ?? true;
        _kind = entry?.Kind ?? EntryKind.Term;

        Title = entry is null ? "Nova entrada" : "Editar entrada";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = Tokens.Brushes.Surface;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _termKind = Ui.Secondary("Termo");
        _correctionKind = Ui.Secondary("Correção");
        _termKind.Click += (_, _) => SetKind(EntryKind.Term);
        _correctionKind.Click += (_, _) => SetKind(EntryKind.Correction);

        _hear = Ui.Field("cloud code", entry?.Hear ?? string.Empty);
        _write = Ui.Field("Claude Code", entry?.Write ?? string.Empty);
        _hear.TextChanged += (_, _) => Revalidate();
        _write.TextChanged += (_, _) => Revalidate();

        _hearField = Ui.Labelled("Quando ouvir", _hear);
        _writeLabel = Ui.Text("Palavra ou frase", Tokens.Fonts.Label, Tokens.Brushes.Muted, FontWeight.Medium);
        _warnings = new StackPanel { Spacing = Tokens.Space.Snug };

        var cancel = Ui.Secondary("Cancelar");
        cancel.Click += (_, _) => Close();

        _save = Ui.Primary("Salvar");
        _save.Click += (_, _) =>
        {
            if (!IsValid) return;
            Saved?.Invoke(this, Draft);
            Close();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(Tokens.Space.Wide),
            Spacing = Tokens.Space.Roomy,
            Children =
            {
                Ui.Text(Title, Tokens.Fonts.Title, weight: FontWeight.SemiBold),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    Children = { _termKind, _correctionKind },
                },
                _hearField,
                new StackPanel { Spacing = Tokens.Space.Tight + 2, Children = { _writeLabel, _write } },
                _warnings,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _save },
                },
            },
        };

        SetKind(_kind);
    }

    private DictionaryEntry Draft => new()
    {
        Id = _id,
        Kind = _kind,
        Write = (_write.Text ?? string.Empty).Trim(),
        Hear = _kind == EntryKind.Correction ? (_hear.Text ?? string.Empty).Trim() : string.Empty,
        IsEnabled = _wasEnabled,
    };

    private bool IsValid =>
        Draft.Write.Length > 0 && (_kind == EntryKind.Term || Draft.Hear.Length > 0);

    private void SetKind(EntryKind kind)
    {
        _kind = kind;

        // The chosen kind wears the primary style; the other stays quiet.
        _termKind.Classes.Set("primary", kind == EntryKind.Term);
        _termKind.Background = kind == EntryKind.Term ? Tokens.Brushes.PrimaryGlass : Tokens.Brushes.GlassFillRaised;
        _correctionKind.Classes.Set("primary", kind == EntryKind.Correction);
        _correctionKind.Background = kind == EntryKind.Correction ? Tokens.Brushes.PrimaryGlass : Tokens.Brushes.GlassFillRaised;

        _hearField.IsVisible = kind == EntryKind.Correction;
        _writeLabel.Text = kind == EntryKind.Correction ? "Escrever" : "Palavra ou frase";

        Revalidate();
    }

    private void Revalidate()
    {
        _warnings.Children.Clear();

        foreach (var warning in DictionaryWarning.Check(Draft))
        {
            _warnings.Children.Add(new Border
            {
                Background = Tokens.Brushes.Tint(Tokens.Colors.Warning, 0.1),
                BorderBrush = Tokens.Brushes.Tint(Tokens.Colors.Warning, 0.35),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Tokens.Radius.Control),
                Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    Children =
                    {
                        Ui.Dot(Tokens.Colors.Warning, 7),
                        Ui.Text(warning.Message, Tokens.Fonts.Label),
                    },
                },
            });
        }

        _save.IsEnabled = IsValid;
    }
}
