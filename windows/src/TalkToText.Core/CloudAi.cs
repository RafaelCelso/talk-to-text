using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TalkToText.Abstractions;

namespace TalkToText.Core;

/// <summary>Where the AI runs: on this computer, or at a provider reached with the user's API key.</summary>
public enum AiProvider
{
    /// <summary>Qwen 2.5 3B on this computer. Nothing leaves it.</summary>
    Local,

    /// <summary>OpenAI — GPT.</summary>
    OpenAI,

    /// <summary>Anthropic — Claude.</summary>
    Anthropic,

    /// <summary>Google — Gemini.</summary>
    Gemini,

    /// <summary>xAI — Grok.</summary>
    Grok,

    /// <summary>Mistral AI.</summary>
    Mistral,

    /// <summary>DeepSeek.</summary>
    DeepSeek,
}

/// <summary>A cloud provider as the settings show it.</summary>
/// <param name="Provider">Which one.</param>
/// <param name="Name">Its name — "OpenAI (GPT)".</param>
/// <param name="KeysPage">Where the user creates a key, without the scheme — as it is shown.</param>
public sealed record CloudProviderInfo(AiProvider Provider, string Name, string KeysPage)
{
    /// <summary>The page, to open in the browser.</summary>
    public Uri KeysUri => new("https://" + KeysPage);
}

/// <summary>One model a provider offers this key.</summary>
/// <param name="Id">What the API calls it — "gpt-4o-mini".</param>
/// <param name="Name">What to show — "Claude Sonnet 4" — or the id when there is no nicer name.</param>
public sealed record CloudModelInfo(string Id, string Name);

/// <summary>
/// The cloud providers: checking a key by listing its models, and talking to the chosen one.
/// </summary>
/// <remarks>
/// <para>
/// Plain HTTP, no SDKs: four request shapes cover all of them. OpenAI, xAI, Mistral and
/// DeepSeek share OpenAI's <c>chat/completions</c>; Anthropic and Gemini have their own.
/// </para>
/// <para>
/// Listing the models doubles as validating the key: it is the cheapest authenticated call
/// every provider has, and it costs nothing.
/// </para>
/// </remarks>
public static class CloudAi
{
    /// <summary>Shared by every request; answers stream, so it waits long.</summary>
    public static HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>The providers, in the order the settings list them.</summary>
    public static IReadOnlyList<CloudProviderInfo> Providers { get; } =
    [
        new(AiProvider.OpenAI, "OpenAI (GPT)", "platform.openai.com/api-keys"),
        new(AiProvider.Anthropic, "Anthropic (Claude)", "console.anthropic.com/settings/keys"),
        new(AiProvider.Gemini, "Google (Gemini)", "aistudio.google.com/apikey"),
        new(AiProvider.Grok, "xAI (Grok)", "console.x.ai"),
        new(AiProvider.Mistral, "Mistral AI", "console.mistral.ai/api-keys"),
        new(AiProvider.DeepSeek, "DeepSeek", "platform.deepseek.com/api_keys"),
    ];

    /// <summary>The settings entry for <paramref name="provider"/>.</summary>
    public static CloudProviderInfo Info(AiProvider provider) => Providers.First(p => p.Provider == provider);

    private static string BaseUrl(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "https://api.openai.com/v1/",
        AiProvider.Anthropic => "https://api.anthropic.com/v1/",
        AiProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/",
        AiProvider.Grok => "https://api.x.ai/v1/",
        AiProvider.Mistral => "https://api.mistral.ai/v1/",
        AiProvider.DeepSeek => "https://api.deepseek.com/v1/",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "not a cloud provider"),
    };

    /// <summary>
    /// The chat models <paramref name="key"/> can use, newest first where the provider says.
    /// Fails with an <see cref="HttpRequestException"/> whose status is 401 for a bad key.
    /// </summary>
    /// <param name="http">Where requests go; a test passes a fake.</param>
    /// <param name="provider">Whose key it is.</param>
    /// <param name="key">The API key.</param>
    /// <param name="cancellationToken">Gives up.</param>
    public static async Task<IReadOnlyList<CloudModelInfo>> ListModelsAsync(
        HttpClient http, AiProvider provider, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var path = provider switch
        {
            AiProvider.Gemini => "models?pageSize=1000",
            AiProvider.Anthropic => "models?limit=1000",
            _ => "models",
        };

        using var request = Request(HttpMethod.Get, provider, key, path);
        using var response = await SendAsync(http, request, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var models = ParseModels(provider, json);
        return models.Count > 0
            ? models
            : throw new HttpRequestException("A chave é válida, mas o provedor não ofereceu nenhum modelo de texto para ela.");
    }

    /// <summary>The chat models in a provider's model list — no embeddings, images or speech.</summary>
    public static IReadOnlyList<CloudModelInfo> ParseModels(AiProvider provider, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (provider == AiProvider.Gemini)
        {
            if (!root.TryGetProperty("models", out var list)) return [];
            return list.EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var methods)
                            && methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                .Select(m => new CloudModelInfo(
                    (Text(m, "name") ?? string.Empty).Replace("models/", string.Empty, StringComparison.Ordinal),
                    Text(m, "displayName") ?? string.Empty))
                .Where(m => m.Id.StartsWith("gemini", StringComparison.Ordinal) && !Excluded(m.Id, "embedding", "tts", "image", "live", "audio", "robotics", "computer-use"))
                .Select(m => m with { Name = m.Name.Length == 0 ? m.Id : $"{m.Name} ({m.Id})" })
                .ToList();
        }

        if (!root.TryGetProperty("data", out var data)) return [];

        // Anthropic lists newest first already, with names; the others give a creation time.
        var models = data.EnumerateArray()
            .Where(m => Text(m, "id") is { Length: > 0 } id && IsChatModel(provider, id, m))
            .Select(m => (
                Model: new CloudModelInfo(Text(m, "id")!, Text(m, "display_name") is { } name ? $"{name} ({Text(m, "id")})" : Text(m, "id")!),
                Created: m.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds) ? seconds : 0))
            .DistinctBy(m => m.Model.Id)
            .ToList();

        return provider == AiProvider.Anthropic
            ? models.Select(m => m.Model).ToList()
            : models.OrderByDescending(m => m.Created).ThenBy(m => m.Model.Id, StringComparer.Ordinal).Select(m => m.Model).ToList();
    }

    private static bool IsChatModel(AiProvider provider, string id, JsonElement model) => provider switch
    {
        // /v1/models also lists speech, images, embeddings and models only the Responses API serves.
        AiProvider.OpenAI => (id.StartsWith("gpt-", StringComparison.Ordinal) || id.StartsWith("chatgpt-", StringComparison.Ordinal)
                              || (id.Length > 1 && id[0] == 'o' && char.IsAsciiDigit(id[1])))
                             && !Excluded(id, "audio", "realtime", "tts", "transcribe", "image", "search", "embedding", "instruct", "codex", "deep-research", "diarize")
                             && !id.EndsWith("-pro", StringComparison.Ordinal) && !id.Contains("-pro-", StringComparison.Ordinal),
        AiProvider.Mistral => (!model.TryGetProperty("capabilities", out var capabilities)
                               || !capabilities.TryGetProperty("completion_chat", out var chat) || chat.ValueKind == JsonValueKind.True)
                              && !Excluded(id, "embed", "ocr", "moderation", "transcribe"),
        AiProvider.Grok => !Excluded(id, "image", "imagine", "video"),
        _ => true,
    };

    private static bool Excluded(string id, params string[] words) => words.Any(w => id.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A request to <paramref name="path"/>, authenticated the way <paramref name="provider"/> wants.</summary>
    internal static HttpRequestMessage Request(HttpMethod method, AiProvider provider, string key, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUrl(provider) + path));
        switch (provider)
        {
            case AiProvider.Anthropic:
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case AiProvider.Gemini:
                request.Headers.Add("x-goog-api-key", key);
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                break;
        }

        return request;
    }

    /// <summary>Sends <paramref name="request"/>; a refusal becomes a message the user can act on.</summary>
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, bool stream, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(
                request,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new HttpRequestException("Sem conexão com o provedor de IA. Verifique a internet.", e);
        }

        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var (message, status) = Refusal(response.StatusCode, body);
            throw new HttpRequestException(message, null, status);
        }
    }

    /// <summary>
    /// What a refusal means for the user. Gemini answers a bad key with 400 rather than 401,
    /// so that is reported as 401 too.
    /// </summary>
    public static (string Message, HttpStatusCode Status) Refusal(HttpStatusCode status, string body)
    {
        var detail = ErrorDetail(body);
        var badKey = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                     || (status == HttpStatusCode.BadRequest && (body.Contains("API_KEY_INVALID", StringComparison.Ordinal)
                                                                 || body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase)));
        if (badKey) return ("Chave de API inválida ou sem permissão.", HttpStatusCode.Unauthorized);

        var message = status switch
        {
            HttpStatusCode.TooManyRequests => "Limite de uso atingido ou créditos da conta esgotados",
            HttpStatusCode.PaymentRequired => "A conta do provedor está sem créditos",
            HttpStatusCode.NotFound => "Modelo não encontrado — escolha outro em Configurações",
            _ => $"O provedor recusou o pedido ({(int)status})",
        };
        return (detail is null ? message + "." : $"{message}: {detail}", status);
    }

    private static string? ErrorDetail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var error = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            var text = error.TryGetProperty("error", out var inner)
                ? inner.ValueKind == JsonValueKind.String ? inner.GetString() : Text(inner, "message")
                : Text(error, "message") ?? Text(error, "detail");
            return text is { Length: > 0 } ? (text.Length > 200 ? text[..200] + "…" : text) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text in one streamed event; empty for events that carry none.</summary>
    /// <exception cref="HttpRequestException">The provider reported an error mid-stream.</exception>
    public static string ParseChunk(AiProvider provider, string data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        if (Text(root, "type") == "error" || (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object))
        {
            throw new HttpRequestException(ErrorDetail(data) ?? "O provedor de IA interrompeu a resposta.");
        }

        switch (provider)
        {
            case AiProvider.Anthropic:
                return Text(root, "type") == "content_block_delta" && root.TryGetProperty("delta", out var delta) && Text(delta, "type") == "text_delta"
                    ? Text(delta, "text") ?? string.Empty
                    : string.Empty;

            case AiProvider.Gemini:
                if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) return string.Empty;
                if (!candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts)) return string.Empty;

                // Thinking models send their reasoning as parts marked "thought": not the answer.
                var text = new StringBuilder();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                    text.Append(Text(part, "text"));
                }

                return text.ToString();

            default:
                return root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                       && choices[0].TryGetProperty("delta", out var change)
                    ? Text(change, "content") ?? string.Empty
                    : string.Empty;
        }
    }

    /// <summary>The body of a streamed completion request.</summary>
    public static (string Path, byte[] Body) CompletionRequest(AiProvider provider, string model, string system, string user, int maxTokens)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            switch (provider)
            {
                case AiProvider.Anthropic:
                    json.WriteString("model", model);
                    json.WriteNumber("max_tokens", maxTokens);
                    json.WriteString("system", system);
                    json.WriteBoolean("stream", true);
                    json.WriteStartArray("messages");
                    Message(json, "user", user);
                    json.WriteEndArray();
                    break;

                case AiProvider.Gemini:
                    json.WriteStartObject("systemInstruction");
                    Parts(json, system);
                    json.WriteEndObject();
                    json.WriteStartArray("contents");
                    json.WriteStartObject();
                    json.WriteString("role", "user");
                    Parts(json, user);
                    json.WriteEndObject();
                    json.WriteEndArray();
                    json.WriteStartObject("generationConfig");
                    json.WriteNumber("maxOutputTokens", maxTokens);
                    json.WriteEndObject();
                    break;

                default:
                    json.WriteString("model", model);
                    json.WriteBoolean("stream", true);

                    // OpenAI's reasoning models refuse max_tokens and any temperature; the
                    // others take max_tokens, and their defaults are fine.
                    json.WriteNumber(provider == AiProvider.OpenAI ? "max_completion_tokens" : "max_tokens", maxTokens);
                    json.WriteStartArray("messages");
                    Message(json, "system", system);
                    Message(json, "user", user);
                    json.WriteEndArray();
                    break;
            }

            json.WriteEndObject();
        }

        var path = provider switch
        {
            AiProvider.Anthropic => "messages",
            AiProvider.Gemini => $"models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse",
            _ => "chat/completions",
        };
        return (path, buffer.ToArray());

        static void Message(Utf8JsonWriter json, string role, string content)
        {
            json.WriteStartObject();
            json.WriteString("role", role);
            json.WriteString("content", content);
            json.WriteEndObject();
        }

        static void Parts(Utf8JsonWriter json, string text)
        {
            json.WriteStartArray("parts");
            json.WriteStartObject();
            json.WriteString("text", text);
            json.WriteEndObject();
            json.WriteEndArray();
        }
    }
}

/// <summary>A cloud model, through the same interface as the local one.</summary>
/// <param name="http">Where requests go.</param>
/// <param name="provider">Whose model.</param>
/// <param name="key">The user's API key.</param>
/// <param name="model">The model id the user chose.</param>
public sealed class CloudLanguageModel(HttpClient http, AiProvider provider, string key, string model) : ILanguageModel
{
    /// <summary>
    /// Room to ask for: reasoning models spend part of it thinking, and an unused allowance
    /// costs nothing. 4,096 is the most the oldest models still listed accept.
    /// </summary>
    private const int MinimumAnswerTokens = 4096;

    /// <summary>The provider.</summary>
    public AiProvider Provider => provider;

    /// <summary>The model id.</summary>
    public string Model => model;

    /// <inheritdoc />
    /// <remarks>
    /// Every current model takes far more, but a long meeting is still cut into parts at
    /// this size: one request per part stays quick, and no provider's limit is reached.
    /// </remarks>
    public int ContextTokens => 32_000;

    /// <inheritdoc />
    /// <remarks>An estimate: about three characters per token in Portuguese, on the safe side.</remarks>
    public int CountTokens(string text) => ((text?.Length ?? 0) / 3) + 1;

    /// <inheritdoc />
    public async IAsyncEnumerable<string> CompleteAsync(
        string system, string user, int maxTokens, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (path, body) = CloudAi.CompletionRequest(provider, model, system, user, Math.Max(maxTokens, MinimumAnswerTokens));
        using var request = CloudAi.Request(HttpMethod.Post, provider, key, path);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await CloudAi.SendAsync(http, request, stream: true, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            var piece = CloudAi.ParseChunk(provider, data);
            if (piece.Length > 0) yield return piece;
        }
    }

    /// <inheritdoc />
    /// <remarks>Nothing to release: the HTTP client is shared.</remarks>
    public void Dispose()
    {
    }
}

/// <summary>
/// The cloud AI's API keys, one per provider, encrypted on disk — never in settings.json,
/// which people copy and share when asking for help.
/// </summary>
/// <param name="directory">Where the encrypted keys live.</param>
/// <param name="protector">
/// Encrypts them; null where there is none (off Windows), and keys are then kept in memory
/// only, for this run.
/// </param>
public sealed class ApiKeyStore(string directory, ISecretProtector? protector)
{
    private readonly Dictionary<AiProvider, string?> _cache = [];
    private readonly Lock _gate = new();

    /// <summary>The default location.</summary>
    public static string DefaultDirectory => Path.Combine(DataFolder.Path, "keys");

    /// <summary>The key saved for <paramref name="provider"/>, or null.</summary>
    public string? Get(AiProvider provider)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(provider, out var cached)) return cached;

            string? key = null;
            try
            {
                var file = FileFor(provider);
                if (protector is not null && File.Exists(file))
                {
                    key = Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(file)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Unreadable — another user's, or a copied profile: as if there were none.
            }

            _cache[provider] = key;
            return key;
        }
    }

    /// <summary>Saves <paramref name="key"/> for <paramref name="provider"/>; null forgets it.</summary>
    public void Set(AiProvider provider, string? key)
    {
        lock (_gate)
        {
            _cache[provider] = key;
            if (protector is null) return;

            var file = FileFor(provider);
            if (key is null)
            {
                File.Delete(file);
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(file, protector.Protect(Encoding.UTF8.GetBytes(key)));
        }
    }

    private string FileFor(AiProvider provider) => Path.Combine(directory, $"{provider.ToString().ToLowerInvariant()}.key");
}
