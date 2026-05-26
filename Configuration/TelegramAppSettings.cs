namespace TestApp.Configuration;

/// <summary>
/// Параметры подключения к Telegram API (my.telegram.org).
/// </summary>
public sealed class TelegramAppSettings
{
    public const string SectionName = "Telegram";

    /// <summary>Идентификатор приложения (api_id).</summary>
    public int ApiId { get; set; }

    /// <summary>Секрет приложения (api_hash).</summary>
    public string ApiHash { get; set; } = string.Empty;

    /// <summary>
    /// Путь к файлу MTProto-сессии. При повторном запуске авторизация по SMS не потребуется.
    /// </summary>
    public string SessionPath { get; set; } = "sessions/userbot.session";

    /// <summary>
    /// Номер телефона для автоматического входа (международный формат, например +77076298774).
    /// </summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Пароль облачного пароля (2FA), если включён в аккаунте.</summary>
    public string? Password2Fa { get; set; }

    /// <summary>Тип прокси: NONE, SOCKS5 или HTTP.</summary>
    public string ProxyType { get; set; } = "NONE";

    public string? ProxyHost { get; set; }

    public int ProxyPort { get; set; }

    public string? ProxyUser { get; set; }

    public string? ProxyPass { get; set; }
}
