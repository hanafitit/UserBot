namespace TestApp.Services;

/// <summary>
/// Уникализация (рерайт) текста объявления через нейросеть.
/// </summary>
public interface IAiTextService
{
    /// <summary>
    /// Перефразирует исходный текст. При ошибке API возвращает <paramref name="baseText"/>.
    /// </summary>
    Task<string> UniqueizeTextAsync(string baseText, CancellationToken cancellationToken = default);
}
