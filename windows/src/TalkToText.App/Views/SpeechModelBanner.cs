using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TalkToText.App.Controls;
using TalkToText.App.Design;

namespace TalkToText.App.Views;

/// <summary>
/// The Home page's notice when the speech model is missing or English-only, with the button
/// that downloads it and the progress while it does.
/// </summary>
/// <remarks>
/// Follows <see cref="SpeechModelDownload"/>, so a download started anywhere shows here, and the
/// banner disappears once the model is in place.
/// </remarks>
public sealed class SpeechModelBanner : UserControl
{
    private readonly Composition _composition;
    private readonly TextBlock _message = Ui.Text(string.Empty, Tokens.Fonts.Body);
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6 };
    private readonly Button _download = Ui.Primary("Baixar modelo de voz", Icons.Download);
    private readonly Button _cancel = Ui.Secondary("Cancelar");

    /// <summary>Builds the banner for <paramref name="composition"/>.</summary>
    public SpeechModelBanner(Composition composition)
    {
        _composition = composition;

        _download.Click += async (_, _) => await SpeechModelDownload.StartAsync(_composition).ConfigureAwait(true);
        _cancel.Click += (_, _) => SpeechModelDownload.Cancel();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children = { _download, _cancel },
        };

        var card = Ui.Card(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                Ui.Dot(Tokens.Colors.Warning),
                At(new StackPanel
                {
                    Spacing = Tokens.Space.Base,
                    Margin = new Thickness(Tokens.Space.Base, 0, 0, 0),
                    Children = { _message, _bar, buttons },
                }, 1),
            },
        }, Tokens.Space.Base);
        card.Margin = new Thickness(0, Tokens.Space.Base, 0, 0);
        Content = card;

        Render();
    }

    /// <summary>What the banner says now. Exposed for headless tests.</summary>
    public string Message => _message.Text ?? string.Empty;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SpeechModelDownload.Changed += OnChanged;
        Render();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SpeechModelDownload.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        var running = SpeechModelDownload.IsRunning;
        IsVisible = running || _composition.NeedsSpeechModel;
        if (!IsVisible) return;

        _bar.IsVisible = running;
        _bar.Value = SpeechModelDownload.Fraction;
        _download.IsVisible = !running;
        _cancel.IsVisible = running;

        var megabytes = Speech.SpeechModel.TotalSize / 1_000_000;
        _message.Text = running
            ? $"Baixando o modelo de voz… {SpeechModelDownload.Fraction:P0} de {megabytes} MB. Você pode continuar usando o app."
            : SpeechModelDownload.Error
              ?? (_composition.IsModelInstalled
                  ? $"O modelo de voz instalado só reconhece inglês. Baixe o Parakeet v3 ({megabytes} MB) para ditar em português."
                  : $"Falta o modelo de voz para transcrever. Ele é baixado uma única vez ({megabytes} MB) e depois tudo funciona offline.");
    }

    private static T At<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
