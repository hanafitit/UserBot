using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestApp.Configuration;

namespace TestApp.Services;

/// <summary>
/// OpenAI-совместимый клиент: POST /chat/completions с системным промптом копирайтера.
/// </summary>
public sealed class AiTextService : IAiTextService
{
    private const string SystemPrompt =
        "Ты — профессиональный копирайтер. Твоя задача — переписать (сделать рерайт) предложенный текст объявления, " +
        "сохранив весь его смысл, ключевые слова, контакты и ссылки, но изменив структуру предложений и синонимы, " +
        "чтобы текст стал уникальным. Выдавай ТОЛЬКО готовый результат перефразирования, без вводных фраз, кавычек и объяснений.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AiSettings _settings;
    private readonly ILogger<AiTextService> _logger;

    public AiTextService(
        IHttpClientFactory httpClientFactory,
        IOptions<AiSettings> options,
        ILogger<AiTextService> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(AiTextService));
        _settings = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> UniqueizeTextAsync(string baseText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseText))
            return baseText;

        if (string.IsNullOrWhiteSpace(_settings.AiApiKey))
        {
            _logger.LogWarning("AiApiKey не задан — отправляется исходный текст без уникализации.");
            return baseText;
        }

        try
        {
            var requestUri = BuildCompletionsUri();
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AiApiKey);
            var payload = new ChatCompletionRequest
            {
                Model = _settings.AiModelName,
                Messages =
                [
                    new ChatMessage("system", SystemPrompt),
                    new ChatMessage("user", baseText)
                ]
            };
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AI API вернул {StatusCode}: {Body}. Используется исходный текст.",
                    (int)response.StatusCode,
                    Truncate(body, 500));
                return baseText;
            }

            var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
            var uniqueText = completion?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

            if (string.IsNullOrWhiteSpace(uniqueText))
            {
                _logger.LogWarning("AI API вернул пустой ответ. Используется исходный текст.");
                return baseText;
            }

            _logger.LogDebug("Текст успешно уникализирован ({Length} символов).", uniqueText.Length);
            return uniqueText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ошибка вызова AI API. Используется исходный текст (fallback).");
            return baseText;
        }
    }

    private string BuildCompletionsUri()
    {
        var baseUrl = _settings.AiBaseUrl.TrimEnd('/');
        return $"{baseUrl}/chat/completions";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private sealed record ChatCompletionRequest
    {
        public required string Model { get; init; }
        public required ChatMessage[] Messages { get; init; }
    }

    private sealed record ChatMessage(string Role, string Content);

    private sealed class ChatCompletionResponse
    {
        public Choice[]? Choices { get; init; }
    }

    private sealed class Choice
    {
        public MessageBody? Message { get; init; }
    }

    private sealed class MessageBody
    {
        public string? Content { get; init; }
    }
}
