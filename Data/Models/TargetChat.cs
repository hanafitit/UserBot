namespace TestApp.Data.Models;

/// <summary>
/// Целевой чат (группа/супергруппа) для рассылки объявлений.
/// </summary>
public sealed class TargetChat
{
    /// <summary>Peer ID чата в Telegram (не автоинкремент).</summary>
    public long Id { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Минимальный интервал между отправками в этот чат (секунды).</summary>
    public int SlowModeSeconds { get; set; }

    public DateTime? LastSentAt { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Максимум отправок объявлений в сутки (0 = без лимита).</summary>
    public int PostsPerDay { get; set; } = 5;

    /// <summary>Отслеживает, сколько объявлений отправлено за текущий день.</summary>
    public int PostsTodayCount { get; set; } = 0;

    /// <summary>Дата последней очистки счётчика (UTC).</summary>
    public DateTime? PostCountResetDateUtc { get; set; }

    /// <summary>Текст последней ошибки (для дебага).</summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>Время последней проверки/отправки.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
