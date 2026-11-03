using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;

namespace TalkToText.App.Views;

/// <summary>
/// Which AI to use: the local one, or a cloud provider with the user's API key — the key
/// checked by listing its models, and the model picked from that list. Shared by Settings and
/// the first-run setup.
/// </summary>
/// <remarks>
/// Nothing switches to the cloud until a model is picked: a provider chosen and a key typed
/// but never confirmed leave the AI as it was, rather than half-configured and failing.
/// </remarks>
public sealed class AiChoicePicker : UserControl
{
    private static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(20);

    private readonly Composition? _composition;
    private readonly HttpClient _http;
    private readonly RadioButton _local;
    private readonly RadioButton _cloud;
    private readonly StackPanel _cloudPanel;
    private readonly ComboBox _provider = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _models = new() { MinWidth = 320, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, PlaceholderText = "Escolha o modelo" };
    private readonly Control _modelsRow;
    private readonly TextBox _key;
    private readonly Button _validate;
    private readonly HyperlinkButton _keysLink = new() { Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status;
    private CancellationTokenSource? _checking;
    private bool _filling;

    /// <summary>Builds the picker over <paramref name="composition"/>'s settings and keys.</summary>
    /// <param name="composition">Where the choice is saved; null in a test with nothing behind it.</param>
    /// <param name="http">Where key checks go; a test passes a fake.</param>
    public AiChoicePicker(Composition? composition, HttpClient? http = null)
    {
        _composition = composition;
        _http = http ?? CloudAi.Http;
        var data = Data;
        var group = "ai-source-" + Guid.NewGuid().ToString("N");

        _local = Radio(
            group,
            "IA local, no seu computador",
            "Gratuita e privada: nada sai do computador. Baixa cerca de 2 GB e é mais lenta.",
            data.AiProvider == AiProvider.Local);
        _cloud = Radio(
            group,
            "IA na nuvem, com a sua chave de API",
            "OpenAI, Claude, Gemini, Grok e outras: mais rápidas e capazes. O texto é enviado ao provedor, e o uso é cobrado na sua conta dele.",
            data.AiProvider != AiProvider.Local);

        _filling = true;
        foreach (var info in CloudAi.Providers)
        {
            _provider.Items.Add(new ComboBoxItem { Content = info.Name, Tag = info.Provider });
        }

        _provider.SelectedItem = _provider.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (AiProvider)i.Tag! == data.AiProvider)
                                 ?? _provider.Items[0];
        _filling = false;
        AutomationProperties.SetName(_provider, "Provedor de IA");
        AutomationProperties.SetName(_models, "Modelo de IA");

        _key = Ui.Field("Cole aqui a sua chave de API");
        _key.PasswordChar = '•';
        _key.MinWidth = 320;
        AutomationProperties.SetName(_key, "Chave de API");

        _validate = Ui.Primary("Validar chave");
        AutomationProperties.SetName(_keysLink, "Abrir a página de chaves do provedor");
        var keyHint = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Ui.Text("Crie uma chave em ", Tokens.Fonts.Caption, Tokens.Brushes.Muted), _keysLink },
        };
        _status = Ui.Text(string.Empty, Tokens.Fonts.Label, Tokens.Brushes.Muted);
        _status.TextWrapping = TextWrapping.Wrap;

        var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        _validate.Margin = new Thickness(Tokens.Space.Snug, 0, 0, 0);
        Grid.SetColumn(_validate, 1);
        keyRow.Children.Add(_key);
        keyRow.Children.Add(_validate);

        _modelsRow = Ui.Labelled("Modelo", _models);
        _modelsRow.IsVisible = false;

        _cloudPanel = new StackPanel
        {
            Spacing = Tokens.Space.Base,
            Margin = new Thickness(Tokens.Space.Wide + 4, Tokens.Space.Tight, 0, 0),
            Children =
            {
                Ui.Labelled("Provedor", _provider),
                new StackPanel { Spacing = Tokens.Space.Tight, Children = { Ui.Labelled("Chave de API", keyRow), keyHint } },
                _status,
                _modelsRow,
            },
        };

        _local.IsCheckedChanged += (_, _) =>
        {
            if (_local.IsChecked != true) return;
            Save(Data with { AiProvider = AiProvider.Local });
            ShowPanel();
        };
        _cloud.IsCheckedChanged += (_, _) =>
        {
            ShowPanel();
            if (_cloud.IsChecked == true) LoadProvider();
        };
        _provider.SelectionChanged += (_, _) => { if (!_filling) LoadProvider(); };
        _validate.Click += async (_, _) => await CheckAsync().ConfigureAwait(true);
        _key.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await CheckAsync().ConfigureAwait(true);
        };
        _models.SelectionChanged += (_, _) => { if (!_filling) UseSelectedModel(); };

        Content = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children = { _local, _cloud, _cloudPanel },
        };

        ShowPanel();
        if (_cloud.IsChecked == true) LoadProvider();
    }

    /// <summary>Raised when the user switches between the local AI and the cloud.</summary>
    public event EventHandler? SourceChanged;

    /// <summary>Whether the cloud option is selected — the first-run setup hides the download then.</summary>
    public bool CloudSelected => _cloud.IsChecked == true;

    /// <summary>The status line under the key. Exposed for headless tests.</summary>
    public string StatusText => _status.Text ?? string.Empty;

    /// <summary>Where the link under the key leads. Exposed for headless tests.</summary>
    public Uri? KeysLink => _keysLink.NavigateUri;

    /// <summary>The models offered after the key was checked. Exposed for headless tests.</summary>
    public IReadOnlyList<string> ModelIds => _models.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag!).ToList();

    /// <summary>Selects the local AI or the cloud, as the user would. For headless tests.</summary>
    public void Choose(bool cloud)
    {
        if (cloud) _cloud.IsChecked = true;
        else _local.IsChecked = true;
    }

    /// <summary>Picks <paramref name="provider"/>. For headless tests.</summary>
    public void ChooseProvider(AiProvider provider) =>
        _provider.SelectedItem = _provider.Items.OfType<ComboBoxItem>().First(i => (AiProvider)i.Tag! == provider);

    /// <summary>Types <paramref name="key"/> and checks it, as the button does. For headless tests.</summary>
    public Task CheckKeyAsync(string key)
    {
        _key.Text = key;
        return CheckAsync();
    }

    /// <summary>Picks the model <paramref name="id"/> from the list. For headless tests.</summary>
    public void ChooseModel(string id) =>
        _models.SelectedItem = _models.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag! == id);

    private SettingsData Data => _composition?.Settings.Data ?? new SettingsData();

    private AiProvider SelectedProvider => (AiProvider)((ComboBoxItem)_provider.SelectedItem!).Tag!;

    private void Save(SettingsData data) => _composition?.Settings.Update(data);

    private void ShowPanel()
    {
        _cloudPanel.IsVisible = _cloud.IsChecked == true;
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the chosen provider: where to get a key, and the saved key checked again for its models.</summary>
    private void LoadProvider()
    {
        var provider = SelectedProvider;
        // A link: the system opens it in the default browser — a new tab when it is already open.
        var info = CloudAi.Info(provider);
        _keysLink.NavigateUri = info.KeysUri;
        _keysLink.Content = new TextBlock
        {
            Text = info.KeysPage,
            FontSize = Tokens.Fonts.Caption,
            Foreground = Tokens.Brushes.Primary,
            TextDecorations = TextDecorations.Underline,
        };
        ToolTip.SetTip(_keysLink, $"Abrir {info.KeysUri} no navegador");
        ClearModels();

        var saved = _composition?.ApiKeys.Get(provider);
        _key.Text = saved ?? string.Empty;
        if (saved is null)
        {
            _checking?.Cancel();
            _status.Text = "Cole a chave e clique em Validar chave.";
            return;
        }

        _ = CheckAsync();
    }

    private void ClearModels()
    {
        _filling = true;
        _models.Items.Clear();
        _models.SelectedItem = null;
        _filling = false;
        _modelsRow.IsVisible = false;
    }

    /// <summary>Checks the key by listing its models; on success saves it and offers the list.</summary>
    private async Task CheckAsync()
    {
        var provider = SelectedProvider;
        var key = (_key.Text ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            _status.Text = "Cole a chave primeiro.";
            return;
        }

        if (_checking is not null) await _checking.CancelAsync().ConfigureAwait(true);
        using var checking = new CancellationTokenSource(CheckLimit);
        _checking = checking;
        _validate.IsEnabled = false;
        _status.Text = "Verificando a chave…";
        ClearModels();

        try
        {
            var models = await CloudAi.ListModelsAsync(_http, provider, key, checking.Token).ConfigureAwait(true);
            if (_checking != checking) return;

            _composition?.ApiKeys.Set(provider, key);
            FillModels(provider, models);
        }
        catch (HttpRequestException e)
        {
            if (_checking != checking) return;
            _status.Text = $"✗ {e.Message}";
            DiagnosticLog.Write($"cloud AI key check failed ({provider}): {(e.StatusCode is { } status ? (int)status : 0)} {e.Message}");

            // A key the provider rejects is not kept: the AI would only fail later.
            if (e.StatusCode == HttpStatusCode.Unauthorized && _composition?.ApiKeys.Get(provider) == key) _composition.ApiKeys.Set(provider, null);
        }
        catch (OperationCanceledException)
        {
            if (_checking == checking) _status.Text = "✗ O provedor demorou demais para responder. Tente de novo.";
        }
        finally
        {
            if (_checking == checking)
            {
                _checking = null;
                _validate.IsEnabled = true;
            }
        }
    }

    private void FillModels(AiProvider provider, IReadOnlyList<CloudModelInfo> models)
    {
        _filling = true;
        foreach (var model in models)
        {
            _models.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
        }

        var data = Data;
        _models.SelectedItem = data.AiProvider == provider
            ? _models.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == data.AiModel)
            : null;
        _filling = false;
        _modelsRow.IsVisible = true;

        _status.Text = _models.SelectedItem is null
            ? $"✓ Chave válida. Escolha um dos {models.Count} modelos abaixo."
            : InUse(provider, data.AiModel!);
    }

    private void UseSelectedModel()
    {
        if (_models.SelectedItem is not ComboBoxItem { Tag: string id }) return;

        var provider = SelectedProvider;
        Save(Data with { AiProvider = provider, AiModel = id });
        _status.Text = InUse(provider, id);
        DiagnosticLog.Write($"AI switched to {provider} {id}");
    }

    private static string InUse(AiProvider provider, string model) => $"✓ Em uso: {CloudAi.Info(provider).Name} · {model}";

    private static RadioButton Radio(string group, string title, string detail, bool isChecked) => new()
    {
        GroupName = group,
        IsChecked = isChecked,
        Content = new StackPanel
        {
            Spacing = Tokens.Space.Hair,
            Children =
            {
                Ui.Text(title, Tokens.Fonts.Body),
                Ui.Text(detail, Tokens.Fonts.Label, Tokens.Brushes.Muted),
            },
        },
    };
}
