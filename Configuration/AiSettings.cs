namespace TestApp.Configuration;

/// <summary>
/// Настройки OpenAI-совместимого API для уникализации текста объявлений.
/// </summary>
public sealed class AiSettings
{
    public const string SectionName = "Ai";

    /// <summary>API-ключ (Bearer).</summary>
    public string AiApiKey { get; set; } = string.Empty;

    /// <summary>Имя модели (например gpt-3.5-turbo, openai/gpt-4o-mini на OpenRouter).</summary>
    public string AiModelName { get; set; } = "gpt-3.5-turbo";

    /// <summary>Базовый URL API без суффикса chat/completions.</summary>
    public string AiBaseUrl { get; set; } = "https://api.openai.com/v1/";
}
