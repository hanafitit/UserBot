using Microsoft.Extensions.Logging;
using Starksoft.Aspen.Proxy;
using TestApp.Configuration;
using WTelegram;

namespace TestApp.Services;

/// <summary>
/// Настройка SOCKS5/HTTP прокси для WTelegramClient через <see cref="Client.TcpHandler"/>.
/// </summary>
internal static class TelegramProxyConfigurator
{
    /// <summary>
    /// Подключает прокси к клиенту, если <see cref="TelegramAppSettings.ProxyType"/> не NONE.
    /// </summary>
    public static void Apply(Client client, TelegramAppSettings settings, ILogger logger)
    {
        var proxyType = settings.ProxyType?.Trim().ToUpperInvariant() ?? "NONE";

        if (proxyType is "" or "NONE")
            return;

        if (string.IsNullOrWhiteSpace(settings.ProxyHost) || settings.ProxyPort <= 0)
        {
            logger.LogWarning(
                "ProxyType={ProxyType}, но ProxyHost/ProxyPort не заданы — подключение напрямую.",
                proxyType);
            return;
        }

        var host = settings.ProxyHost.Trim();
        var port = settings.ProxyPort;
        var user = settings.ProxyUser?.Trim() ?? string.Empty;
        var pass = settings.ProxyPass ?? string.Empty;
        var hasAuth = !string.IsNullOrEmpty(user);

        switch (proxyType)
        {
            case "SOCKS5":
                client.TcpHandler = (destinationHost, destinationPort) =>
                {
                    var proxy = hasAuth
                        ? new Socks5ProxyClient(host, port, user, pass)
                        : new Socks5ProxyClient(host, port);
                    return Task.FromResult(proxy.CreateConnection(destinationHost, destinationPort));
                };
                logger.LogInformation("Telegram: прокси SOCKS5 {Host}:{Port}", host, port);
                break;

            case "HTTP":
                client.TcpHandler = (destinationHost, destinationPort) =>
                {
                    var proxy = hasAuth
                        ? new HttpProxyClient(host, port, user, pass)
                        : new HttpProxyClient(host, port);
                    return Task.FromResult(proxy.CreateConnection(destinationHost, destinationPort));
                };
                logger.LogInformation("Telegram: прокси HTTP {Host}:{Port}", host, port);
                break;

            default:
                logger.LogWarning("Неизвестный ProxyType «{ProxyType}». Используйте NONE, SOCKS5 или HTTP.", proxyType);
                break;
        }
    }
}
