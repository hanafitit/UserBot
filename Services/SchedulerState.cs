namespace TestApp.Services;

/// <summary>
/// Разделяемое состояние планировщика для отображения в командах статуса.
/// </summary>
public sealed class SchedulerState
{
    public DateTime? NextPlannedPostUtc { get; set; }
    public string CurrentActivityStatus { get; set; } = "Инициализация...";
    public int BurstMessagesLeft { get; set; }
    public bool IsManualPaused { get; set; }
}
