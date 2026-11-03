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
              {"id":"omni-moderation-latest","created":800},
              {"id":"computer-use-preview","created":900},
              {"id":"gpt-5-codex","created":950}
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
              {"name":"models/gemma-3-4b-it","displayName":"Gemma","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.5-transcribe","displayName":"Transcribe","supportedGenerationMethods":["generateContent"]}
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
              {"id":"mistral-small-latest","created":5,"capabilities":{"completion_chat":true}},
              {"id":"open-mistral-7b","created":3,"capabilities":{"completion_chat":true},"deprecation":"2025-03-30T12:00:00Z"},
              {"id":"mistral-large-latest","created":2,"capabilities":{"completion_chat":true},"deprecation":"2999-01-01T00:00:00Z"},
              {"id":"voxtral-mini-transcribe-2507","created":1,"capabilities":{"completion_chat":true}}
            ]}
            """;

        // A deprecation date in the past means the model no longer answers; one in the future
        // is only a notice.
        CloudAi.ParseModels(AiProvider.Mistral, json).Select(m => m.Id).ShouldBe(["mistral-small-latest", "mistral-large-latest"]);
    }

    [Fact]
    public void Grok_offers_only_text_models()
    {
        const string json = """
            {"data":[
              {"id":"grok-4","created":3},
              {"id":"grok-2-image-1212","created":2},
              {"id":"grok-imagine-video","created":4},
              {"id":"grok-3-mini","created":1}
            ]}
            """;

        CloudAi.ParseModels(AiProvider.Grok, json).Select(m => m.Id).ShouldBe(["grok-4", "grok-3-mini"]);
    }

    [Fact]
    public void DeepSeek_offers_its_chat_models()
    {
        const string json = """{"object":"list","data":[{"id":"deepseek-chat","object":"model"},{"id":"deepseek-reasoner","object":"model"}]}""";

        CloudAi.ParseModels(AiProvider.DeepSeek, json).Select(m => m.Id).ShouldBe(["deepseek-chat", "deepseek-reasoner"]);
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
    public void Other_refusals_say_what_to_do_then_keep_the_providers_reason() =>
        CloudAi.Refusal(HttpStatusCode.TooManyRequests, """{"error":{"message":"You exceeded your current quota"}}""").Message
            .ShouldBe("A conta do provedor está sem créditos ou passou do limite — confira o saldo no site dele. You exceeded your current quota");

    /// <summary>The refusals each provider actually sends, and what the user is told.</summary>
    [Theory]
    // No credits: xAI says it with 403 and Anthropic with 400 — neither is a bad key.
    [InlineData(HttpStatusCode.Forbidden, """{"code":"forbidden","error":"Your team doesn't have any credits yet."}""", "A conta do provedor está sem créditos")]
    [InlineData(HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""", "A conta do provedor está sem créditos")]
    [InlineData(HttpStatusCode.PaymentRequired, """{"error":{"message":"Insufficient Balance","type":"unknown_error"}}""", "A conta do provedor está sem créditos")]
    // A model that is gone or unknown: Mistral and DeepSeek answer 400, Gemini 400 or 404.
    [InlineData(HttpStatusCode.NotFound, """{"error":{"message":"The model `gpt-9` does not exist","code":"model_not_found"}}""", "Este modelo não está disponível")]
    [InlineData(HttpStatusCode.BadRequest, """{"object":"error","message":"Invalid model: mistral-tiny","type":"invalid_model"}""", "Este modelo não está disponível")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Model Not Exist","type":"invalid_request_error"}}""", "Este modelo não está disponível")]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"This model models/gemini-2.5-flash is no longer available to new users.","status":"NOT_FOUND"}}""", "Este modelo não está disponível")]
    // A model that cannot take this request.
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"Developer instruction is not enabled for this model","status":"INVALID_ARGUMENT"}}""", "Este modelo não aceita este tipo de pedido")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"This model is only supported in v1/responses and not in v1/chat/completions."}}""", "Este modelo não aceita este tipo de pedido")]
    // Everything else that happens.
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"User location is not supported for the API use.","status":"FAILED_PRECONDITION"}}""", "O provedor não atende a sua região")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"unsupported_country_region_territory","message":"Country, region, or territory not supported"}}""", "O provedor não atende a sua região")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"This model's maximum context length is 128000 tokens.","code":"context_length_exceeded"}}""", "O texto é longo demais")]
    [InlineData((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", "O provedor está sobrecarregado")]
    [InlineData(HttpStatusCode.InternalServerError, "oops", "O provedor teve um erro interno")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota).","status":"RESOURCE_EXHAUSTED"}}""", "Limite de uso atingido")]
    public void Each_refusal_says_what_happened(HttpStatusCode status, string body, string start) =>
        CloudAi.Refusal(status, body).Message.ShouldStartWith(start);

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

    private const string HighDemand = """{"error":{"code":503,"message":"This model is currently experiencing high demand.","status":"UNAVAILABLE"}}""";

    [Fact]
    public async Task An_overloaded_provider_is_asked_again()
    {
        var calls = 0;
        using var handler = new FakeHandler(_ => ++calls < 3
            ? Reply(HttpStatusCode.ServiceUnavailable, HighDemand)
            : Reply(HttpStatusCode.OK, "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hello\"}]}}]}\n\n"));
        using var http = new HttpClient(handler);
        using var model = new CloudLanguageModel(http, AiProvider.Gemini, "key", "gemini-3.8-flash") { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        var pieces = new List<string>();
        await foreach (var piece in model.CompleteAsync("sys", "oi", 50, CancellationToken.None)) pieces.Add(piece);

        pieces.ShouldBe(["Hello"]);
        handler.Bodies.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Asking_again_gives_up_and_says_why()
    {
        using var handler = new FakeHandler(_ => Reply(HttpStatusCode.ServiceUnavailable, HighDemand));
        using var http = new HttpClient(handler);
        using var model = new CloudLanguageModel(http, AiProvider.Gemini, "key", "gemini-3.8-flash") { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        var error = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in model.CompleteAsync("sys", "oi", 50, CancellationToken.None)) { }
        });

        error.Message.ShouldStartWith("O provedor está sobrecarregado");
        handler.Requests.Count.ShouldBe(3); // the first try and two more
    }

    [Fact]
    public async Task A_refusal_is_not_asked_again()
    {
        using var handler = new FakeHandler(_ => Reply(HttpStatusCode.NotFound, """{"error":{"message":"model not found","code":"model_not_found"}}"""));
        using var http = new HttpClient(handler);
        using var model = new CloudLanguageModel(http, AiProvider.OpenAI, "key", "gpt-9") { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in model.CompleteAsync("sys", "oi", 50, CancellationToken.None)) { }
        });

        handler.Requests.Count.ShouldBe(1);
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
