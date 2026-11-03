using System.Net;
using System.Text;
using TalkToText.Abstractions;
using TalkToText.Core;
using Shouldly;
using Xunit;

namespace TalkToText.CoreTests;

/// <summary>The cloud providers: which models are offered, what is sent, and what comes back.</summary>
public sealed class CloudAiTests
{
    [Fact]
    public void OpenAI_offers_only_chat_models_newest_first()
    {
        const string json = """
            {"data":[
              {"id":"gpt-4o-mini","created":100},
              {"id":"gpt-4.1","created":300},
              {"id":"text-embedding-3-small","created":400},
              {"id":"gpt-4o-realtime-preview","created":500},
              {"id":"whisper-1","created":50},
              {"id":"o3-mini","created":200},
              {"id":"o1-pro","created":600},
              {"id":"gpt-image-1","created":700},
              {"id":"gpt-3.5-turbo-instruct","created":10},
              {"id":"omni-moderation-latest","created":800}
            ]}
            """;

        CloudAi.ParseModels(AiProvider.OpenAI, json).Select(m => m.Id).ShouldBe(["gpt-4.1", "o3-mini", "gpt-4o-mini"]);
    }

    [Fact]
    public void Anthropic_keeps_its_order_and_names()
    {
        const string json = """
            {"data":[
              {"type":"model","id":"claude-sonnet-4-20250514","display_name":"Claude Sonnet 4"},
              {"type":"model","id":"claude-3-5-haiku-20241022","display_name":"Claude Haiku 3.5"}
            ],"has_more":false}
            """;

        var models = CloudAi.ParseModels(AiProvider.Anthropic, json);

        models.Select(m => m.Id).ShouldBe(["claude-sonnet-4-20250514", "claude-3-5-haiku-20241022"]);
        models[0].Name.ShouldBe("Claude Sonnet 4 (claude-sonnet-4-20250514)");
    }

    [Fact]
    public void Gemini_offers_models_that_generate_text()
    {
        const string json = """
            {"models":[
              {"name":"models/gemini-2.0-flash","displayName":"Gemini 2.0 Flash","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/text-embedding-004","displayName":"Embedding","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-2.5-flash-preview-tts","displayName":"TTS","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemma-3-4b-it","displayName":"Gemma","supportedGenerationMethods":["generateContent"]}
            ]}
            """;

        CloudAi.ParseModels(AiProvider.Gemini, json).Select(m => m.Id).ShouldBe(["gemini-2.0-flash"]);
    }

    [Fact]
    public void Mistral_offers_models_that_chat()
    {
        const string json = """
            {"data":[
              {"id":"mistral-small-latest","created":5,"capabilities":{"completion_chat":true}},
              {"id":"mistral-embed","created":4,"capabilities":{"completion_chat":false}},
              {"id":"mistral-small-latest","created":5,"capabilities":{"completion_chat":true}}
            ]}
            """;

        CloudAi.ParseModels(AiProvider.Mistral, json).Select(m => m.Id).ShouldBe(["mistral-small-latest"]);
    }

    [Theory]
    [InlineData(AiProvider.OpenAI, """{"choices":[{"delta":{"content":"Olá"}}]}""", "Olá")]
    [InlineData(AiProvider.Grok, """{"choices":[{"delta":{"role":"assistant"}}]}""", "")]
    [InlineData(AiProvider.Anthropic, """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Olá"}}""", "Olá")]
    [InlineData(AiProvider.Anthropic, """{"type":"message_start","message":{}}""", "")]
    [InlineData(AiProvider.Gemini, """{"candidates":[{"content":{"parts":[{"text":"pensando","thought":true},{"text":"Olá"}]}}]}""", "Olá")]
    public void Each_provider_streams_its_own_way(AiProvider provider, string data, string text) =>
        CloudAi.ParseChunk(provider, data).ShouldBe(text);

    [Fact]
    public void An_error_in_the_middle_of_a_stream_is_reported()
    {
        var error = Should.Throw<HttpRequestException>(() =>
            CloudAi.ParseChunk(AiProvider.Anthropic, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));
        error.Message.ShouldBe("Overloaded");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}""")]
    public void A_bad_key_reads_as_one_whatever_the_provider(HttpStatusCode status, string body)
    {
        var (message, reported) = CloudAi.Refusal(status, body);

        reported.ShouldBe(HttpStatusCode.Unauthorized);
        message.ShouldBe("Chave de API inválida ou sem permissão.");
    }

    [Fact]
    public void Other_refusals_keep_the_providers_reason() =>
        CloudAi.Refusal(HttpStatusCode.TooManyRequests, """{"error":{"message":"You exceeded your current quota"}}""").Message
            .ShouldBe("Limite de uso atingido ou créditos da conta esgotados: You exceeded your current quota");

    [Fact]
    public void OpenAI_is_asked_with_max_completion_tokens_and_the_others_with_max_tokens()
    {
        var (path, body) = CloudAi.CompletionRequest(AiProvider.OpenAI, "gpt-4o-mini", "sys", "oi", 100);
        path.ShouldBe("chat/completions");
        Encoding.UTF8.GetString(body).ShouldBe(
            """{"model":"gpt-4o-mini","stream":true,"max_completion_tokens":100,"messages":[{"role":"system","content":"sys"},{"role":"user","content":"oi"}]}""");

        Encoding.UTF8.GetString(CloudAi.CompletionRequest(AiProvider.DeepSeek, "deepseek-chat", "sys", "oi", 100).Body)
            .ShouldContain("\"max_tokens\":100");
    }

    [Fact]
    public void Anthropic_and_Gemini_have_their_own_request_shapes()
    {
        var (anthropicPath, anthropic) = CloudAi.CompletionRequest(AiProvider.Anthropic, "claude-x", "sys", "oi", 100);
        anthropicPath.ShouldBe("messages");
        Encoding.UTF8.GetString(anthropic).ShouldBe(
            """{"model":"claude-x","max_tokens":100,"system":"sys","stream":true,"messages":[{"role":"user","content":"oi"}]}""");

        var (geminiPath, gemini) = CloudAi.CompletionRequest(AiProvider.Gemini, "gemini-2.0-flash", "sys", "oi", 100);
        geminiPath.ShouldBe("models/gemini-2.0-flash:streamGenerateContent?alt=sse");
        Encoding.UTF8.GetString(gemini).ShouldBe(
            """{"systemInstruction":{"parts":[{"text":"sys"}]},"contents":[{"role":"user","parts":[{"text":"oi"}]}],"generationConfig":{"maxOutputTokens":100}}""");
    }

    [Fact]
    public async Task A_key_is_checked_by_listing_its_models_with_the_providers_header()
    {
        using var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, """{"data":[{"id":"claude-x","display_name":"Claude X"}]}"""));
        using var http = new HttpClient(handler);

        var models = await CloudAi.ListModelsAsync(http, AiProvider.Anthropic, "sk-ant-test", CancellationToken.None);

        models.Single().Id.ShouldBe("claude-x");
        handler.Requests.Single().RequestUri!.ToString().ShouldBe("https://api.anthropic.com/v1/models?limit=1000");
        handler.Requests.Single().Headers.GetValues("x-api-key").Single().ShouldBe("sk-ant-test");
    }

    [Fact]
    public async Task A_rejected_key_fails_with_401()
    {
        using var handler = new FakeHandler(_ => Reply(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key"}}"""));
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<HttpRequestException>(() => CloudAi.ListModelsAsync(http, AiProvider.OpenAI, "sk-wrong", CancellationToken.None));

        error.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        handler.Requests.Single().Headers.Authorization!.ToString().ShouldBe("Bearer sk-wrong");
    }

    [Fact]
    public async Task The_cloud_model_streams_the_answer_piece_by_piece()
    {
        const string events = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n"
                              + "data: {\"choices\":[{\"delta\":{\"content\":\"Bom \"}}]}\n\n"
                              + ": keep-alive\n\n"
                              + "data: {\"choices\":[{\"delta\":{\"content\":\"dia.\"}}]}\n\n"
                              + "data: [DONE]\n\n";
        using var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, events));
        using var http = new HttpClient(handler);
        using var model = new CloudLanguageModel(http, AiProvider.Grok, "xai-key", "grok-3");

        var pieces = new List<string>();
        await foreach (var piece in model.CompleteAsync("sys", "oi", 50, CancellationToken.None)) pieces.Add(piece);

        pieces.ShouldBe(["Bom ", "dia."]);
        handler.Requests.Single().RequestUri!.ToString().ShouldBe("https://api.x.ai/v1/chat/completions");
        handler.Bodies.Single().ShouldContain("\"model\":\"grok-3\"");
    }

    [Fact]
    public void Keys_are_stored_encrypted_and_can_be_forgotten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ttt-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ApiKeyStore(directory, new ReversingProtector());
            store.Set(AiProvider.OpenAI, "sk-secret");

            var file = Directory.GetFiles(directory).Single();
            File.ReadAllText(file).ShouldNotContain("sk-secret");
            new ApiKeyStore(directory, new ReversingProtector()).Get(AiProvider.OpenAI).ShouldBe("sk-secret");
            store.Get(AiProvider.Gemini).ShouldBeNull();

            store.Set(AiProvider.OpenAI, null);
            new ApiKeyStore(directory, new ReversingProtector()).Get(AiProvider.OpenAI).ShouldBeNull();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Without_encryption_a_key_is_only_kept_in_memory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ttt-keys-" + Guid.NewGuid().ToString("N"));
        var store = new ApiKeyStore(directory, protector: null);

        store.Set(AiProvider.OpenAI, "sk-secret");

        store.Get(AiProvider.OpenAI).ShouldBe("sk-secret");
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public void Settings_remember_the_chosen_ai()
    {
        var path = Path.Combine(Path.GetTempPath(), "ttt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new AppSettings(path).Update(new SettingsData { AiProvider = AiProvider.Gemini, AiModel = "gemini-2.0-flash" });

            var loaded = new AppSettings(path).Data;
            loaded.AiProvider.ShouldBe(AiProvider.Gemini);
            loaded.AiModel.ShouldBe("gemini-2.0-flash");
            File.ReadAllText(path).ShouldContain("\"Gemini\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return reply(request);
        }
    }

    /// <summary>Stands in for DPAPI: anything but plain text on disk.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Reverse().Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();
    }
}
