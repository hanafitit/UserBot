namespace TestApp.Services;

/// <summary>
/// Telegram вернул FLOOD_WAIT (RpcException, код 420) — нужно подождать перед повтором.
/// </summary>
public sealed class TelegramFloodWaitException : Exception
{
    public TelegramFloodWaitException(int retryAfterSeconds, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>Рекомендуемая пауза в секундах (из FLOOD_WAIT_X).</summary>
    public int RetryAfterSeconds { get; }
}
