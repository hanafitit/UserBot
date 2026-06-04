using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestApp.Data;
using TestApp.Data.Models;
using TestApp.Services;
using TL;
using WTelegram;

namespace TestApp.Workers;

/// <summary>
/// Фоновый планировщик рассылки: проверяет БД и отправляет объявления с учётом Slow Mode.
/// </summary>
public sealed class AdvertisingScheduler : BackgroundService
{
    /// <summary>
    /// Основной интервал проверки (1 минута достаточно для нечастых отправок).
    /// </summary>
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly TelegramClientManager _clientManager;
    private readonly IAiTextService _aiTextService;
    private readonly ILogger<AdvertisingScheduler> _logger;
    private readonly object _pauseLock = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    
    private int _messagesSentWithCurrentText;
    private string? _currentUniqueText;
    private DateTime _activityPeriodEndUtc = DateTime.MinValue;
    
    // Суточные смещения для реалистичности (разные каждый день)
    private int _morningStartOffsetMin;      // 8:00 ± 30 мин
    private int _lunchStartOffsetMin;        // 13:00 ± 30 мин
    private int _eveningStartOffsetMin;      // 18:00 ± 30 мин
    private int _lateNightStartOffsetMin;    // 20:00 ± 30 мин
    private DateTime _offsetsGeneratedForDate = DateTime.MinValue;  // дата, на которую сгенерированы смещения

    public AdvertisingScheduler(
        IDbContextFactory<AppDbContext> dbContextFactory,
        TelegramClientManager clientManager,
        IAiTextService aiTextService,
        ILogger<AdvertisingScheduler> logger)
    {
        _dbContextFactory = dbContextFactory;
        _clientManager = clientManager;
        _aiTextService = aiTextService;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Планировщик рассылки запущен (интервал {Interval} мин).", LoopInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var currentHour = DateTime.Now.Hour;
                
                // Ночной сон (23:00-8:00)
                if (currentHour >= 23 || currentHour < 8)
                {
                    _logger.LogInformation("💤 Ночное время. Сон до 8:00.");
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                    continue;
                }

                if (IsGloballyPaused())
                {
                    await DelayUntilPauseEndsAsync(stoppingToken);
                    continue;
                }

                if (_clientManager.User is null)
                {
                    _logger.LogDebug("Клиент Telegram ещё не авторизован — пропуск итерации.");
                }
                else
                {
                    // Определяем период активности (с суточными смещениями)
                    var now = DateTime.Now;
                    var (isActive, reason) = GetActivityStatus(now);
                    
                    if (isActive && DateTime.UtcNow >= _activityPeriodEndUtc)
                    {
                        await ProcessIterationAsync(stoppingToken);
                    }
                    else if (!isActive && DateTime.UtcNow < _activityPeriodEndUtc)
                    {
                        _logger.LogDebug("⏸️ {Reason}. Пауза до {Time:HH:mm}.", reason, _activityPeriodEndUtc.ToLocalTime());
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Неожиданная ошибка в цикле планировщика.");
            }

            await Task.Delay(LoopInterval, stoppingToken);
        }

        _logger.LogInformation("Планировщик рассылки остановлен.");
    }

    /// <summary>
    /// В активные периоды отправляем чаты с длительными случайными перерывами.
    /// Проверяет лимиты постов в день и SlowMode для каждого чата.
    /// </summary>
    private async Task ProcessIterationAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var template = await db.AdvertisingTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.IsCurrent, cancellationToken);

        if (template is null || string.IsNullOrWhiteSpace(template.BaseText))
        {
            _logger.LogWarning("Нет активного шаблона. Рассылка пропущена.");
            _messagesSentWithCurrentText = 0;
            _currentUniqueText = null;
            return;
        }

        var now = DateTime.UtcNow;
        var today = now.Date;

        var activeChats = await db.TargetChats
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken);

        var readyChat = activeChats
            .Where(c => 
            {
                // Проверка SlowMode
                if (c.LastSentAt is not null && (now - c.LastSentAt.Value).TotalSeconds <= c.SlowModeSeconds)
                    return false;

                // Проверка лимита постов в день
                if (c.PostsPerDay > 0)
                {
                    var resetDate = c.PostCountResetDateUtc?.Date ?? today;
                    
                    // Если счётчик от другого дня — сбросить
                    if (resetDate < today)
                        return true;
                    
                    // Если достигли лимита в этот день — не отправляем
                    if (c.PostsTodayCount >= c.PostsPerDay)
                        return false;
                }

                return true;
            })
            .OrderBy(c => c.LastSentAt ?? DateTime.MinValue)
            .FirstOrDefault();

        if (readyChat is null)
        {
            _logger.LogDebug("Нет чатов, готовых к отправке.");
            return;
        }

        await SendToChatAsync(db, readyChat, template.BaseText, cancellationToken);

        // После успешной отправки — установить случайный перерыв до следующей
        SetRandomBreak();
    }

    /// <summary>
    /// Отправляет текст в чат с имитацией набора и обновляет БД.
    /// Каждый 5-й чат получает уникализированный текст.
    /// </summary>
    private async Task SendToChatAsync(
        AppDbContext db,
        TargetChat chat,
        string messageText,
        CancellationToken cancellationToken)
    {
        var client = _clientManager.Client;
        var sentAt = DateTime.UtcNow;
        bool shouldUnicalize = _messagesSentWithCurrentText % 5 == 0;

        try
        {
            var inputPeer = await ResolveInputPeerAsync(client, chat.Id, cancellationToken);
            if (inputPeer is null)
            {
                WriteLog(db, chat.Id, sentAt, "Error",
                    $"Чат {chat.Id} не найден в списке диалогов Telegram. Убедитесь, что аккаунт состоит в группе.");
                _logger.LogWarning("Не удалось разрешить peer для чата {ChatId} ({Title}).", chat.Id, chat.Title);
                return;
            }

            string finalText;
            int typingDelay;

            if (shouldUnicalize)
            {
                finalText = await _aiTextService.UniqueizeTextAsync(messageText, cancellationToken);
                _currentUniqueText = finalText;
                
                if (!string.Equals(finalText, messageText, StringComparison.Ordinal))
                {
                    var diffRatio = CalculateDifferenceRatio(messageText, finalText);
                    typingDelay = (int)(3 + (5 * diffRatio));
                    
                    _logger.LogInformation(
                        "✨ «{Title}» ({ChatId}): НОВЫЙ текст от ИИ (+{DiffPercent}%), ожидание {Delay} с.",
                        chat.Title,
                        chat.Id,
                        (int)(diffRatio * 100),
                        typingDelay);
                }
                else
                {
                    typingDelay = Random.Shared.Next(3, 8);
                }
            }
            else
            {
                finalText = _currentUniqueText ?? messageText;
                typingDelay = Random.Shared.Next(500, 1200);
                
                _logger.LogInformation(
                    "📋 «{Title}» ({ChatId}): копи-паста ({Count}/5), ожидание {Delay} мс.",
                    chat.Title,
                    chat.Id,
                    _messagesSentWithCurrentText % 5,
                    typingDelay);
            }

            await client.Messages_SetTyping(inputPeer, new SendMessageTypingAction());
            await Task.Delay(TimeSpan.FromMilliseconds(typingDelay), cancellationToken);

            await client.SendMessageAsync(inputPeer, finalText);

            chat.LastSentAt = sentAt;
            
            // Обновляем счётчик постов за день
            var now = DateTime.UtcNow;
            var today = now.Date;
            if (chat.PostCountResetDateUtc?.Date < today)
            {
                chat.PostsTodayCount = 0;
                chat.PostCountResetDateUtc = now;
            }
            chat.PostsTodayCount++;
            
            _messagesSentWithCurrentText++;
            WriteLog(db, chat.Id, sentAt, "Success", null);
            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("✅ Сообщение отправлено в «{Title}» ({ChatId}) — {PostToday}/{PostsLimit} в день.", 
                chat.Title, chat.Id, chat.PostsTodayCount, chat.PostsPerDay);
        }
        catch (TelegramFloodWaitException ex)
        {
            await HandleFloodAsync(db, chat.Id, sentAt, ex.RetryAfterSeconds, ex.Message, cancellationToken);
        }
        catch (RpcException ex) when (ex.Code == 420)
        {
            var waitSeconds = ex.X > 0 ? ex.X : 30;
            await HandleFloodAsync(db, chat.Id, sentAt, waitSeconds, ex.Message, cancellationToken);
        }
        catch (RpcException ex)
        {
            bool isPermanentError = ex.Message is "USER_BANNED_IN_CHANNEL" or "CHAT_WRITE_FORBIDDEN" or "CHAT_RESTRICTED" or "PEER_ID_INVALID";

            if (isPermanentError)
            {
                chat.IsActive = false;
                _logger.LogWarning("Чат {ChatId} ({Title}) деактивирован из-за ошибки: {Error}", chat.Id, chat.Title, ex.Message);
            }

            WriteLog(db, chat.Id, sentAt, isPermanentError ? "Banned" : "Error", $"[{ex.Code}] {ex.Message}");
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogError(ex, "Ошибка Telegram при отправке в {ChatId}.", chat.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteLog(db, chat.Id, sentAt, "Error", ex.Message);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogError(ex, "Ошибка при отправке в {ChatId}.", chat.Id);
        }
    }

    /// <summary>
    /// Глобальная пауза планировщика после FLOOD_WAIT.
    /// </summary>
    private async Task HandleFloodAsync(
        AppDbContext db,
        long chatId,
        DateTime sentAt,
        int waitSeconds,
        string message,
        CancellationToken cancellationToken)
    {
        SetGlobalPause(waitSeconds);
        WriteLog(db, chatId, sentAt, "Flood", message);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "FLOOD_WAIT_{Seconds}. Планировщик приостановлен на {Seconds} с.",
            waitSeconds,
            waitSeconds);
    }

    private void SetGlobalPause(int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        lock (_pauseLock)
        {
            if (until > _pausedUntilUtc)
                _pausedUntilUtc = until;
        }
    }

    private bool IsGloballyPaused()
    {
        lock (_pauseLock)
            return DateTime.UtcNow < _pausedUntilUtc;
    }

    private async Task DelayUntilPauseEndsAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_pauseLock)
            delay = _pausedUntilUtc - DateTime.UtcNow;

        if (delay > TimeSpan.Zero)
        {
            _logger.LogDebug("Планировщик на паузе ещё {Seconds:F0} с.", delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Вычисляет процент различий между двумя строками (простая эвристика).
    /// </summary>
    private static double CalculateDifferenceRatio(string original, string modified)
    {
        if (string.IsNullOrEmpty(original))
            return 0.0;

        int differences = 0;
        int maxLen = Math.Max(original.Length, modified.Length);
        
        for (int i = 0; i < maxLen; i++)
        {
            var origChar = i < original.Length ? original[i] : '\0';
            var modChar = i < modified.Length ? modified[i] : '\0';
            if (origChar != modChar)
                differences++;
        }

        return Math.Min(1.0, (double)differences / maxLen);
    }

    /// <summary>
    /// Ищет чат в кэше Telegram по ID (в т.ч. формат -100… для супергрупп).
    /// </summary>
    private static async Task<InputPeer?> ResolveInputPeerAsync(
        Client client,
        long chatId,
        CancellationToken cancellationToken)
    {
        var chats = await client.Messages_GetAllChats();
        if (chats?.chats is null)
            return null;

        if (chats.chats.TryGetValue(chatId, out var chat))
            return chat;

        if (chatId <= -1_000_000_000_000)
        {
            var channelId = -chatId - 1_000_000_000_000;
            if (chats.chats.TryGetValue(channelId, out chat))
                return chat;
        }

        var foundChat = chats.chats.Values.FirstOrDefault(c => c?.ID == chatId);
        return foundChat;
    }

    private static void WriteLog(
        AppDbContext db,
        long chatId,
        DateTime sentAt,
        string status,
        string? errorMessage)
    {
        db.ExecutionLogs.Add(new ExecutionLog
        {
            ChatId = chatId,
            SentAt = sentAt,
            Status = status,
            ErrorMessage = errorMessage
        });
    }

    /// <summary>
    /// Определяет, активен ли период для рассылки с учётом случайных суточных смещений.
    /// Смещения разные каждый день для реалистичности.
    /// </summary>
    private (bool IsActive, string Reason) GetActivityStatus(DateTime now)
    {
        // Проверяем, нужно ли генерировать новые смещения (новый день)
        if (now.Date != _offsetsGeneratedForDate)
        {
            GenerateDailyOffsets();
            _offsetsGeneratedForDate = now.Date;
        }

        var hour = now.Hour;
        var minute = now.Minute;
        var totalMinutesFromMidnight = hour * 60 + minute;
        
        // Утро (8:00 ± 30 мин)
        int morningStart = 8 * 60 + _morningStartOffsetMin;
        int morningEnd = 9 * 60 + _morningStartOffsetMin;
        if (totalMinutesFromMidnight >= morningStart && totalMinutesFromMidnight < morningEnd)
            return (true, $"☀️ Утро ({morningStart / 60}:{morningStart % 60:D2}-{morningEnd / 60}:{morningEnd % 60:D2})");
        
        // Обед (13:00 ± 30 мин)
        int lunchStart = 13 * 60 + _lunchStartOffsetMin;
        int lunchEnd = 14 * 60 + _lunchStartOffsetMin;
        if (totalMinutesFromMidnight >= lunchStart && totalMinutesFromMidnight < lunchEnd)
            return (true, $"🍽️ Обед ({lunchStart / 60}:{lunchStart % 60:D2}-{lunchEnd / 60}:{lunchEnd % 60:D2})");
        
        // Вечер (18:00 ± 30 мин)
        int eveningStart = 18 * 60 + _eveningStartOffsetMin;
        int eveningEnd = 20 * 60 + _eveningStartOffsetMin;
        if (totalMinutesFromMidnight >= eveningStart && totalMinutesFromMidnight < eveningEnd)
            return (true, $"🌆 Вечер ({eveningStart / 60}:{eveningStart % 60:D2}-{eveningEnd / 60}:{eveningEnd % 60:D2})");
        
        // Поздний вечер (20:00 ± 30 мин)
        int lateNightStart = 20 * 60 + _lateNightStartOffsetMin;
        int lateNightEnd = 23 * 60;
        if (totalMinutesFromMidnight >= lateNightStart && totalMinutesFromMidnight < lateNightEnd)
            return (true, $"🌙 Поздно ({lateNightStart / 60}:{lateNightStart % 60:D2}-{lateNightEnd / 60}:{lateNightEnd % 60:D2})");
        
        // Неактивные периоды
        if (hour >= 9 && hour < 13)
            return (false, "💼 Работа (9-13)");
        if (hour >= 14 && hour < 18)
            return (false, "💼 Работа (14-18)");
        
        return (false, "💤 Ночь (23-8)");
    }

    /// <summary>
    /// Генерирует новые суточные смещения (вызывается каждый день).
    /// Каждый период смещается на ±30 минут для реалистичности.
    /// </summary>
    private void GenerateDailyOffsets()
    {
        _morningStartOffsetMin = Random.Shared.Next(-30, 31);
        _lunchStartOffsetMin = Random.Shared.Next(-30, 31);
        _eveningStartOffsetMin = Random.Shared.Next(-30, 31);
        _lateNightStartOffsetMin = Random.Shared.Next(-30, 31);
        
        _logger.LogInformation(
            "🎲 НОВЫЙ ДЕНЬ! Смещения: утро {MorningOffset:+#;-#;0}м, обед {LunchOffset:+#;-#;0}м, вечер {EveningOffset:+#;-#;0}м, ночь {LateOffset:+#;-#;0}м",
            _morningStartOffsetMin, _lunchStartOffsetMin, _eveningStartOffsetMin, _lateNightStartOffsetMin);
    }

    /// <summary>
    /// Устанавливает случайный перерыв перед следующей отправкой.
    /// В активные периоды (8-9, 13-14, 18-20) отправляем 3-5 чатов подряд, потом пауза.
    /// </summary>
    private void SetRandomBreak()
    {
        var hour = DateTime.Now.Hour;
        int breakMinutes;

        if (hour >= 8 && hour < 9)
        {
            breakMinutes = Random.Shared.Next(15, 40); // Утро: 15-40 мин
            _logger.LogInformation("⏸️ Утренняя пауза: {Minutes} мин.", breakMinutes);
        }
        else if (hour >= 13 && hour < 14)
        {
            breakMinutes = Random.Shared.Next(10, 25); // Обед: 10-25 мин
            _logger.LogInformation("🥗 Обеденная пауза: {Minutes} мин.", breakMinutes);
        }
        else if (hour >= 18 && hour < 20)
        {
            breakMinutes = Random.Shared.Next(20, 50); // Вечер: 20-50 мин
            _logger.LogInformation("🌆 Вечерняя пауза: {Minutes} мин.", breakMinutes);
        }
        else if (hour >= 20 && hour < 23)
        {
            breakMinutes = Random.Shared.Next(30, 90); // Поздний вечер: 30-90 мин (редко)
            _logger.LogInformation("🌙 Ночная пауза: {Minutes} мин.", breakMinutes);
        }
        else
        {
            breakMinutes = Random.Shared.Next(5, 15);
        }

        _activityPeriodEndUtc = DateTime.UtcNow.AddMinutes(breakMinutes);
    }
}
