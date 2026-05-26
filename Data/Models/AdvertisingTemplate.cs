namespace TestApp.Data.Models;

/// <summary>
/// Шаблон текста объявления для рассылки.
/// </summary>
public sealed class AdvertisingTemplate
{
    public int Id { get; set; }

    public string BaseText { get; set; } = string.Empty;

    /// <summary>Текущий активный шаблон (в БД должен быть один с true).</summary>
    public bool IsCurrent { get; set; }
}
