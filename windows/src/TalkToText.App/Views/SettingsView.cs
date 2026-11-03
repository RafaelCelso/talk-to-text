using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TalkToText.Abstractions;
using TalkToText.App.Controls;
using TalkToText.App.Design;
using TalkToText.Core;
using TalkToText.Speech;

namespace TalkToText.App.Views;

/// <summary>
/// Settings: the shortcut and how it behaves, what happens to finished text, and the model.
/// </summary>
public sealed class SettingsView : UserControl
{
    private readonly Composition? _composition;
    private readonly ShortcutPicker _shortcut;
    private readonly RadioButton _hold;
    private readonly RadioButton _toggle;

    /// <summary>Builds the page over <paramref name="composition"/>, or with nothing behind it.</summary>
    public SettingsView(Composition? composition)
    {
        _composition = composition;
        var data = Data;

        _shortcut = new ShortcutPicker(composition);

        _hold = Radio("Segurar para falar", "Grava enquanto as teclas estão pressionadas.", data.Mode == ActivationMode.Hold);
        _toggle = Radio("Pressionar para iniciar e parar", "Um toque começa a gravar; outro toque termina.", data.Mode == ActivationMode.Toggle);
        _hold.IsCheckedChanged += (_, _) => { if (_hold.IsChecked == true) Save(Data with { Mode = ActivationMode.Hold }); };
        _toggle.IsCheckedChanged += (_, _) => { if (_toggle.IsChecked == true) Save(Data with { Mode = ActivationMode.Toggle }); };

        var shortcutCard = Ui.Card(new StackPanel
        {
            Children =
            {
                Ui.SettingRow(
                    "Atalho de gravação",
                    "Até duas teclas — por exemplo Ctrl + Win. Clique para trocar; Esc cancela.",
                    _shortcut),
                Ui.Divider(),
                Ui.Text("Modo", Tokens.Fonts.Body, weight: FontWeight.Medium),
                new StackPanel { Spacing = Tokens.Space.Snug, Margin = new Thickness(0, Tokens.Space.Snug, 0, 0), Children = { _hold, _toggle } },
            },
        });

        var behaviourCard = Ui.Card(new StackPanel
        {
            Children =
            {
                Ui.SettingRow(
                    "Digitar no aplicativo em foco",
                    "Cola o texto onde o cursor estiver assim que a transcrição termina.",
                    Switch(data.InjectText, v => Save(Data with { InjectText = v }))),
                Ui.Divider(),
                Ui.SettingRow(
                    "Indicador de gravação",
                    "Mostra uma pílula na parte de baixo da tela enquanto grava e transcreve.",
                    Switch(data.ShowOverlay, v => Save(Data with { ShowOverlay = v }))),
                Ui.Divider(),
                Ui.SettingRow(
                    "Manter histórico",
                    "Guarda as transcrições na página Início, só neste computador.",
                    Switch(data.KeepHistory, v => Save(Data with { KeepHistory = v }))),
                Ui.Divider(),
                Ui.SettingRow(
                    "Remover hesitações",
                    "Tira \"éé\", \"hã\", \"hum\" e palavras repetidas sem querer, como \"que que\". Instantâneo.",
                    Switch(data.RemoveHesitations, v => Save(Data with { RemoveHesitations = v }))),
                Ui.Divider(),
                Ui.SettingRow(
                    "Revisar com IA",
                    "A IA local corrige pontuação, acentos e concordância antes de colar. Leva alguns segundos a mais por ditado.",
                    Switch(data.PolishWithAi, v => Save(Data with { PolishWithAi = v }))),
            },
        });

        var commandCard = Ui.Card(new StackPanel
        {
            Children =
            {
                Ui.SettingRow(
                    "Modo comando",
                    "Selecione um texto, segure o atalho abaixo e diga o que fazer: \"deixa mais formal\", \"traduz para inglês\", "
                    + "\"resume em três tópicos\". A IA reescreve e o resultado substitui a seleção. Sem nada selecionado, "
                    + "ela escreve o que você pedir. Usa a IA local.",
                    Switch(data.CommandMode, v => Save(Data with { CommandMode = v }))),
                Ui.Divider(),
                Ui.SettingRow(
                    "Atalho do comando",
                    "Diferente do atalho de gravação. O padrão é Shift + Win.",
                    new ShortcutPicker(composition, d => d.CommandShortcut, (d, s) => d with { CommandShortcut = s }, "Atalho do comando")),
            },
        });

        var meetingsCard = BuildMeetingsCard(data);
        var aiCard = BuildAiCard(data);

        var openFolder = Ui.Secondary("Abrir pasta", Icons.Folder);
        openFolder.Click += (_, _) => OpenFolder(DataFolder.Path);

        var version = typeof(SettingsView).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "—";

        var aboutCard = Ui.Card(new StackPanel
        {
            Children =
            {
                BuildModelRow(),
                Ui.Divider(),
                Ui.SettingRow(
                    "Dados e registro",
                    "Configurações, histórico, dicionário e o arquivo talktotext.log.",
                    openFolder),
                Ui.Divider(),
                Ui.SettingRow(
                    "Configuração inicial",
                    "Rever os passos da primeira abertura: nome, modo, atalho e IA.",
                    BuildOnboardingButton(composition)),
                Ui.Divider(),
                Ui.SettingRow("Versão", null, Ui.Text(version, Tokens.Fonts.Body, Tokens.Brushes.Muted)),
            },
        });

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                // Full width, like every other page: a column capped at 720 px left half the
                // window empty on a wide screen.
                Margin = new Thickness(Tokens.Space.Page, Tokens.Space.Page - 4, Tokens.Space.Page, Tokens.Space.Page),
                Children =
                {
                    Ui.PageHeader("Configurações"),
                    Ui.SectionTitle("Atalho"),
                    shortcutCard,
                    Spacer(),
                    Ui.SectionTitle("Comportamento"),
                    behaviourCard,
                    Spacer(),
                    Ui.SectionTitle("Modo comando"),
                    commandCard,
                    Spacer(),
                    Ui.SectionTitle("Reuniões"),
                    meetingsCard,
                    Spacer(),
                    Ui.SectionTitle("IA local"),
                    aiCard,
                    Spacer(),
                    Ui.SectionTitle("Modelo e aplicativo"),
                    aboutCard,
                },
            },
        };

    }

    /// <summary>Whether the page is waiting for a new shortcut. Exposed for headless tests.</summary>
    public bool IsRecordingShortcut => _shortcut.IsRecording;

    private SettingsData Data => _composition?.Settings.Data ?? new SettingsData();

    /// <summary>The instruction field on the AI card. Exposed for headless tests.</summary>
    public TextBox? AiInstructionField { get; private set; }

    /// <summary>
    /// The user's standing instruction for the AI: who they are and what the recordings are
    /// about, so summaries of audio files use the right vocabulary and pick out what matters.
    /// </summary>
    private Border BuildAiCard(SettingsData data)
    {
        var field = new TextBox
        {
            Text = data.AiInstruction ?? string.Empty,
            Watermark = "Descreva quem você é, do que tratam as gravações e o que a IA deve destacar no resumo.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 260,
            FontFamily = Tokens.Fonts.Sans,
            FontSize = Tokens.Fonts.Body,
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        AiInstructionField = field;

        var save = Ui.Primary("Salvar instrução");
        var status = Ui.Text(string.Empty, Tokens.Fonts.Label, Tokens.Brushes.Muted);
        save.IsEnabled = false;

        string Current() => (field.Text ?? string.Empty).Trim();
        string Saved() => (Data.AiInstruction ?? string.Empty).Trim();

        field.TextChanged += (_, _) =>
        {
            save.IsEnabled = Current() != Saved();
            status.Text = save.IsEnabled ? "Alterações não salvas" : string.Empty;
        };
        save.Click += (_, _) =>
        {
            var text = Current();
            Save(Data with { AiInstruction = text.Length == 0 ? null : text });
            save.IsEnabled = false;
            status.Text = "Instrução salva";
        };

        return Ui.Card(new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children =
            {
                Ui.Text("Instrução para a IA", Tokens.Fonts.Body, weight: FontWeight.Medium),
                Ui.Text(
                    "Diga quem você é e do que tratam as gravações. A IA segue esta instrução ao resumir "
                    + "arquivos de áudio, para o resumo usar os termos certos e destacar o que importa. "
                    + "A transcrição em si não muda: para nomes e termos, use o Dicionário.",
                    Tokens.Fonts.Label, Tokens.Brushes.Muted),
                field,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Base,
                    Children = { save, status },
                },
                Ui.Divider(),
                Ui.SettingRow(
                    "Usar a placa de vídeo",
                    "Deixa os resumos e o Chat mais rápidos quando o computador tem uma. "
                    + "A mudança vale na próxima vez que o app abrir.",
                    Switch(data.UseGpuForAi, v => Save(Data with { UseGpuForAi = v }))),
            },
        });
    }

    private void Save(SettingsData data) => _composition?.Settings.Update(data);

    private Button BuildOnboardingButton(Composition? composition)
    {
        var button = Ui.Secondary("Rever");
        Avalonia.Automation.AutomationProperties.SetName(button, "Rever configuração inicial");
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
                await new OnboardingWindow(composition).ShowDialog(owner).ConfigureAwait(true);
        };
        return button;
    }

    private Grid BuildModelRow()
    {
        var located = Data.ModelDirectory ?? ParakeetTranscriber.Locate();
        var found = located is not null && ParakeetTranscriber.IsComplete(located);

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                Ui.Dot(found ? Tokens.Colors.Success : Tokens.Colors.Warning),
                Ui.Text(found ? "Pronto" : "Não instalado", Tokens.Fonts.Body, Tokens.Brushes.Muted),
            },
        };

        return Ui.SettingRow(
            "Modelo de voz — Parakeet",
            found
                ? located
                : "Sem modelo o Talk-to-Text não transcreve. Reinstale o aplicativo para baixá-lo.",
            status);
    }

    private Border BuildMeetingsCard(SettingsData data)
    {
        var name = Ui.Field("Você", data.DisplayName ?? string.Empty);
        name.Width = 200;
        name.LostFocus += (_, _) =>
        {
            var value = (name.Text ?? string.Empty).Trim();
            Save(Data with { DisplayName = value.Length == 0 ? null : value });
        };

        var modelsStatus = Ui.Text(
            Speech.MeetingModels.IsInstalled() ? "Instalados" : "Não instalados (27 MB)",
            Tokens.Fonts.Body, Tokens.Brushes.Muted);

        var download = Ui.Secondary("Baixar");
        download.IsVisible = !Speech.MeetingModels.IsInstalled();
        download.Click += async (_, _) =>
        {
            download.IsEnabled = false;
            try
            {
                var progress = new Progress<double>(p => modelsStatus.Text = $"Baixando… {p:P0}");
                await MeetingCoordinator.DownloadModelsAsync(progress, CancellationToken.None).ConfigureAwait(true);
                modelsStatus.Text = "Instalados";
                download.IsVisible = false;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                modelsStatus.Text = "Falhou — verifique a internet";
                download.IsEnabled = true;
            }
        };

        return Ui.Card(new StackPanel
        {
            Children =
            {
                Ui.SettingRow(
                    "Detectar reuniões",
                    "Quando você entra numa chamada no Teams, Meet, Zoom, Slack ou Discord, pergunta se deve transcrever.",
                    Switch(data.DetectMeetings, v => Save(Data with { DetectMeetings = v }))),
                Ui.Divider(),
                Ui.SettingRow(
                    "Transcrição flutuante",
                    "Com o app minimizado, mostra a conversa da reunião numa janela no canto inferior direito da tela. Dá para recolher até virar só o ícone.",
                    Switch(data.ShowLiveTranscriptWindow, v => Save(Data with { ShowLiveTranscriptWindow = v }))),
                Ui.Divider(),
                Ui.SettingRow("Seu nome nas transcrições", "Como suas falas aparecem no chat da reunião.", name),
                Ui.Divider(),
                Ui.SettingRow(
                    "Modelos de reunião",
                    "Separação de falas e reconhecimento de vozes. Funcionam offline.",
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug, Children = { modelsStatus, download } }),
            },
        });
    }

    private static RadioButton Radio(string title, string detail, bool isChecked) => new()
    {
        GroupName = "activation-mode",
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

    private static ToggleSwitch Switch(bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = null, OffContent = null, MinWidth = 0 };
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked ?? false);
        return toggle;
    }

    private static Border Spacer() => new() { Height = Tokens.Space.Wide };

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Opening Explorer is a convenience; failing to must never take the app down.
        }
    }
}
