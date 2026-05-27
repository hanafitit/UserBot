using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestApp.Configuration;
using TL;
using WTelegram;

namespace TestApp.Services;

/// <summary>
/// Инициализация <see cref="Client"/>, хранение сессии и интерактивная авторизация пользователя.
/// </summary>
public sealed class TelegramClientManager : IAsyncDisposable
{
    private readonly TelegramAppSettings _settings;
    private readonly ILogger<TelegramClientManager> _logger;
    private Client? _client;
    private string? _phoneNumber;

    public TelegramClientManager(
        IOptions<TelegramAppSettings> options,
        ILogger<TelegramClientManager> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    /// <summary>Активный клиент после успешного входа.</summary>
    public Client Client => _client ?? throw new InvalidOperationException(
        "Клиент Telegram ещё не инициализирован. Сначала вызовите LoginAsync.");

    /// <summary>Текущий авторизованный пользователь.</summary>
    public User? User { get; private set; }

    /// <summary>
    /// Создаёт клиент, подключается к Telegram и выполняет вход (или восстанавливает сессию из файла).
    /// </summary>
    /// <param name="cancellationToken">Токен отмены ожидания при FLOOD_WAIT.</param>
    /// <returns>Профиль вошедшего пользователя.</returns>
    /// <exception cref="TelegramFloodWaitException">
    /// Превышено число автоматических повторов при FLOOD_WAIT.
    /// </exception>
    public async Task<User> LoginAsync(CancellationToken cancellationToken = default)
    {
        const int maxFloodRetries = 5;
        var floodAttempt = 0;
        _phoneNumber = null;

        while (true)
        {
            try
            {
                _client?.Dispose();
                WTelegram.Helpers.Log = (_, _) => { };
                _client = new Client(Config);
                TelegramProxyConfigurator.Apply(_client, _settings, _logger);

                _logger.LogInformation("Подключение к Telegram…");
                User = await _client.LoginUserIfNeeded();

                _logger.LogInformation(
                    "Авторизация успешна: {FirstName} {LastName} (id: {UserId})",
                    User.first_name,
                    User.last_name,
                    User.id);

                return User;
            }
            catch (RpcException ex) when (ex.Code == 420)
            {
                floodAttempt++;
                var waitSeconds = ex.X > 0 ? ex.X : 30;

                if (floodAttempt > maxFloodRetries)
                {
                    throw new TelegramFloodWaitException(
                        waitSeconds,
                        $"FLOOD_WAIT_{waitSeconds}: превышено число попыток входа ({maxFloodRetries}).",
                        ex);
                }

                _logger.LogWarning(
                    "FLOOD_WAIT_{Seconds}. Ожидание {Seconds} с перед повтором ({Attempt}/{Max})…",
                    waitSeconds,
                    waitSeconds,
                    floodAttempt,
                    maxFloodRetries);

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            }
            catch (RpcException ex)
            {
                _logger.LogError(ex, "Ошибка Telegram API при авторизации: {Message}", ex.Message);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Неожиданная ошибка при авторизации.");
                throw;
            }
        }
    }

    /// <summary>
    /// Callback конфигурации WTelegramClient: отдаёт api_id/api_hash, путь сессии и ответы для входа.
    /// </summary>
    private string? Config(string what)
    {
        switch (what)
        {
            case "api_id":
                return _settings.ApiId.ToString();

            case "api_hash":
                return _settings.ApiHash;

            case "session_pathname":
                return EnsureSessionPath();

            case "phone_number":
                return GetPhoneNumber();

            case "verification_code":
                if (Console.IsInputRedirected)
                {
                    throw new InvalidOperationException(
                        "Требуется код подтверждения (SMS), но приложение запущено в неинтерактивном режиме (например, на Render). " +
                        "Пожалуйста, сначала запустите приложение локально, чтобы создать файл сессии, и загрузите его, " +
                        "либо обеспечьте постоянное хранение файла сессии.");
                }
                Console.WriteLine();
                Console.Write("Код из SMS/Telegram: ");
                return Console.ReadLine()?.Trim();

            case "password":
                if (!string.IsNullOrWhiteSpace(_settings.Password2Fa))
                {
                    _logger.LogInformation("Автовход: пароль 2FA взят из конфигурации");
                    return _settings.Password2Fa;
                }

                if (Console.IsInputRedirected)
                {
                    _logger.LogWarning("Пароль 2FA не задан в конфигурации, а ввод перенаправлен. Попытка входа без пароля.");
                    return null;
                }

                Console.WriteLine();
                Console.Write("Пароль двухфакторной аутентификации (2FA), Enter если нет: ");
                var password = Console.ReadLine();
                return string.IsNullOrWhiteSpace(password) ? null : password.Trim();

            case "first_name":
                if (Console.IsInputRedirected) return "User";
                Console.Write("Имя (регистрация нового аккаунта): ");
                return Console.ReadLine()?.Trim() ?? "User";

            case "last_name":
                if (Console.IsInputRedirected) return "";
                Console.Write("Фамилия (регистрация нового аккаунта): ");
                return Console.ReadLine()?.Trim() ?? "";

            default:
                return null;
        }
    }

    private string EnsureSessionPath()
    {
        var fullPath = Path.GetFullPath(_settings.SessionPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _logger.LogDebug("Файл сессии: {SessionPath}", fullPath);
        return fullPath;
    }

    /// <summary>
    /// Номер из конфигурации или консоли (нормализованный формат +XXXXXXXX).
    /// </summary>
    private string GetPhoneNumber()
    {
        if (!string.IsNullOrWhiteSpace(_phoneNumber))
            return _phoneNumber;

        if (!string.IsNullOrWhiteSpace(_settings.PhoneNumber))
        {
            _phoneNumber = NormalizePhoneNumber(_settings.PhoneNumber);
            _logger.LogInformation("Автовход: номер взят из конфигурации");
            return _phoneNumber;
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Номер телефона не задан в конфигурации (Telegram:PhoneNumber), а ввод перенаправлен. " +
                "Укажите номер телефона в переменных окружения или appsettings.json.");
        }

        Console.WriteLine();
        Console.Write("Номер телефона (международный формат, например +77076298774): ");
        var input = Console.ReadLine()?.Trim()
            ?? throw new InvalidOperationException("Номер телефона не может быть пустым.");
        _phoneNumber = NormalizePhoneNumber(input);
        return _phoneNumber;
    }

    private static string NormalizePhoneNumber(string phone)
    {
        var digits = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (string.IsNullOrEmpty(digits))
            throw new InvalidOperationException("Некорректный номер телефона.");

        if (!digits.StartsWith('+'))
            digits = "+" + digits.TrimStart('+');

        if (digits.Length < 8)
            throw new InvalidOperationException("Номер телефона слишком короткий.");

        return digits;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }

        _phoneNumber = null;
        await ValueTask.CompletedTask;
    }
}
