using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Dictum.Services;

/// <summary>
/// Optional LLM pass over the transcript: punctuation, casing and obvious
/// slips of the tongue. Off by default — when enabled, the recognised text is
/// sent to the configured router, so it is an explicit privacy decision.
///
/// Works with any OpenAI-compatible gateway: OpenRouter, OrcaRouter or a
/// custom endpoint.
/// </summary>
public sealed class AiCleanup
{
    private const string SystemPrompt =
        "Ты редактор расшифровки голосовой диктовки на русском языке. " +
        "Расставь знаки препинания и заглавные буквы, убери слова-паразиты и явные оговорки. " +
        "Не добавляй новую информацию, не меняй смысл, не переводи текст и не пересказывай его. " +
        "Сохраняй английские термины и названия в их обычном написании. " +
        "Верни только исправленный текст, без пояснений и кавычек.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;

    public AiCleanup(string apiKey, string model, string endpoint)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "anthropic/claude-haiku-4.5" : model;
        _endpoint = string.IsNullOrWhiteSpace(endpoint)
            ? "https://openrouter.ai/api/v1/chat/completions"
            : endpoint;
    }

    public static string DefaultEndpoint(string provider) => provider switch
    {
        "orcarouter" => "https://api.orcarouter.ai/v1/chat/completions",
        "teamorouter" => "https://api.teamorouter.com/v1/chat/completions",
        _ => "https://openrouter.ai/api/v1/chat/completions"
    };

    public static string CredentialTarget(string provider) => provider switch
    {
        "orcarouter" => "Dictum/orcarouter",
        "teamorouter" => "Dictum/teamorouter",
        _ => "Dictum/openrouter"
    };

    public static string? ResolveKey(string provider)
    {
        var environmentVariable = provider switch
        {
            "orcarouter" => Environment.GetEnvironmentVariable("ORCAROUTER_API_KEY"),
            "teamorouter" => Environment.GetEnvironmentVariable("TEAMOROUTER_API_KEY"),
            _ => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
        };

        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            return environmentVariable.Trim();
        }

        var generic = Environment.GetEnvironmentVariable("DICTUM_AI_API_KEY");
        if (!string.IsNullOrWhiteSpace(generic))
        {
            return generic.Trim();
        }

        return CredentialManager.Read(CredentialTarget(provider));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<string?> CleanAsync(string text, int timeoutSeconds)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!_apiKey.All(ch => ch >= 32 && ch < 127))
        {
            Log.Write("ai: API key contains non-ASCII characters, skipping");
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 30)));

            var payload = new
            {
                model = _model,
                temperature = 0.2,
                max_tokens = 800,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = text }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/ivanpetrv2026/dictum");
            request.Headers.TryAddWithoutValidation("X-Title", "Dictum");
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                Log.Write($"ai: HTTP {(int)response.StatusCode} from {_endpoint}: {Trim(errorBody)}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        }
        catch (Exception ex)
        {
            Log.Write("ai failed: " + ex.Message);
            return null;
        }
    }

    private static string Trim(string value) =>
        value.Length <= 300 ? value : value[..300];
}
