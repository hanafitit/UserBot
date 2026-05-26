namespace TestApp.Data.Models;

/// <summary>
/// Журнал попыток отправки сообщения в чат.
/// </summary>
public sealed class ExecutionLog
{
    public int Id { get; set; }

    public long ChatId { get; set; }

    public DateTime SentAt { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
}
