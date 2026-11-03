using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using TalkToText.App.Views;
using TalkToText.Core;
using Shouldly;

namespace TalkToText.AppTests;

/// <summary>Choosing between the local AI and a cloud one with an API key.</summary>
public sealed class AiChoicePickerTests
{
    [AvaloniaFact]
    public void Starts_on_the_local_ai()
    {
        var picker = new AiChoicePicker(null);

        picker.CloudSelected.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task A_valid_key_offers_the_providers_models()
    {
        using var handler = new Replies(HttpStatusCode.OK, """{"data":[{"id":"gpt-4o-mini","created":1},{"id":"gpt-4.1","created":2}]}""");
        using var http = new HttpClient(handler);
        var picker = new AiChoicePicker(null, http);
        var changed = 0;
        picker.SourceChanged += (_, _) => changed++;

        picker.Choose(cloud: true);
        picker.ChooseProvider(AiProvider.OpenAI);
        await picker.CheckKeyAsync("sk-test");

        changed.ShouldBe(1);
        picker.ModelIds.ShouldBe(["gpt-4.1", "gpt-4o-mini"]);
        picker.StatusText.ShouldBe("✓ Chave válida. Escolha um dos 2 modelos abaixo.");

        picker.ChooseModel("gpt-4o-mini");
        picker.StatusText.ShouldBe("✓ Em uso: OpenAI (GPT) · gpt-4o-mini");
    }

    [AvaloniaFact]
    public async Task A_rejected_key_says_so_and_offers_nothing()
    {
        using var handler = new Replies(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}""");
        using var http = new HttpClient(handler);
        var picker = new AiChoicePicker(null, http);

        picker.Choose(cloud: true);
        picker.ChooseProvider(AiProvider.Anthropic);
        await picker.CheckKeyAsync("sk-ant-wrong");

        picker.StatusText.ShouldBe("✗ Chave de API inválida ou sem permissão.");
        picker.ModelIds.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void The_setup_hides_the_download_when_the_cloud_is_chosen()
    {
        var setup = new OnboardingWindow(null);
        setup.Show();
        setup.Go(5);

        setup.AiChoice.ShouldNotBeNull();
        setup.AiChoice.Choose(cloud: true);
        setup.AiChoice.CloudSelected.ShouldBeTrue();
        setup.Close();
    }

    private sealed class Replies(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
