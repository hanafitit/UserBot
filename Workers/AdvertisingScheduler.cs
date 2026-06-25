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

        // Перед началом — ждем авторизации и проверяем подписки
        while (!stoppingToken.IsCancellationRequested && _clientManager.User is null)
        {
            _logger.LogDebug("Ожидание авторизации для проверки подписок...");
            await Task.Delay(5000, stoppingToken);
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            await EnsureAllActiveChatsJoinedAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var currentHour = now.Hour;
                
                // Ночной сон (23:00-8:00) по местному времени сервера
                var localNow = DateTime.Now;
                if (localNow.Hour >= 23 || localNow.Hour < 8)
                {
                    _logger.LogInformation("💤 Ночное время (местное: {Time:HH:mm}). Сон до 8:00.", localNow);
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
                    var (isActive, reason) = GetActivityStatus(now);
                    
                    if (isActive && now >= _activityPeriodEndUtc)
                    {
                        await ProcessIterationAsync(stoppingToken);
                    }
                    else if (!isActive)
                    {
                         _logger.LogDebug("⏸️ {Reason}.", reason);
                    }
                    else if (now < _activityPeriodEndUtc)
                    {
                        _logger.LogDebug("⏸️ {Reason}. Ожидание окончания перерыва до {Time:HH:mm} (UTC).", reason, _activityPeriodEndUtc.ToShortTimeString());
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
    /// В активные периоды отправляем пачку чатов (burst) с короткими перерывами,
    /// затем делаем длительную паузу.
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

        _logger.LogInformation("Всего активных чатов в БД: {Count}.", activeChats.Count);

        var readyChats = activeChats
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
            .ToList();

        if (readyChats.Count == 0)
        {
            _logger.LogDebug("Нет чатов, готовых к отправке (SlowMode или лимит). Всего активных: {Active}.", activeChats.Count);
            return;
        }

        // Рандомизация порядка первых 50 чатов для имитации человеческого выбора
        if (readyChats.Count > 1)
        {
            var shuffleLimit = Math.Min(readyChats.Count, 50);
            var first50 = readyChats.Take(shuffleLimit).OrderBy(_ => Random.Shared.Next()).ToList();
            readyChats = first50.Concat(readyChats.Skip(shuffleLimit)).ToList();
        }

        // Перед началом — принудительно обновляем кэш диалогов, чтобы Telegram "увидел" чаты
        _logger.LogInformation("🔄 Обновляю список диалогов для резолва peers...");
        var dialogs = await _clientManager.Client.Messages_GetDialogs();
        var chatsCache = dialogs is Messages_Dialogs md ? md.chats :
                         dialogs is Messages_DialogsSlice mds ? mds.chats : new Dictionary<long, ChatBase>();

        _logger.LogInformation("🚀 Начинаю отправку для {Count} чатов. (Доступно диалогов в TG: {TgCount})", readyChats.Count, chatsCache.Count);

        // Выполняем обход чатов по очереди (согласно техническому заданию)
        await ProcessChatsAsync(db, readyChats, template.BaseText, chatsCache, cancellationToken);

        // После завершения — установить случайный перерыв до следующей итерации
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
        Dictionary<long, ChatBase> chatsCache,
        CancellationToken cancellationToken)
    {
        var client = _clientManager.Client;
        var sentAt = DateTime.UtcNow;
        bool shouldUnicalize = _messagesSentWithCurrentText % 5 == 0;

        try
        {
            _logger.LogInformation("Проверяю чат {ChatId} ({Title})...", chat.Id, chat.Title);

            // 1. Предварительная проверка и вступление
            var inputPeer = await EnsureMembershipAndPermissionsAsync(client, chat, chatsCache, cancellationToken);
            if (inputPeer is null)
            {
                chat.LastSentAt = sentAt; // Обновляем время, чтобы чат ушел в конец очереди
                chat.UpdatedAt = DateTime.UtcNow;

                // Если чат был деактивирован (например, забанен), ставим статус Banned, иначе просто Error
                string logStatus = chat.IsActive ? "Error" : "Banned";
                WriteLog(db, chat.Id, sentAt, logStatus, chat.LastErrorMessage ?? "Pre-validation failed");

                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            string finalText;
            int typingDelay;

            if (shouldUnicalize)
            {
                finalText = await _aiTextService.UniqueizeTextAsync(messageText, cancellationToken);
                _currentUniqueText = finalText;
                
                // Для ИИ-текста имитируем более долгое "раздумье" и ввод (4-11 секунд)
                typingDelay = Random.Shared.Next(4000, 11001);

                if (!string.Equals(finalText, messageText, StringComparison.Ordinal))
                {
                    var diffRatio = CalculateDifferenceRatio(messageText, finalText);
                    _logger.LogInformation(
                        "✨ «{Title}» ({ChatId}): НОВЫЙ текст от ИИ (+{DiffPercent}%), время набора {Delay} мс.",
                        chat.Title,
                        chat.Id,
                        (int)(diffRatio * 100),
                        typingDelay);
                }
                else
                {
                    _logger.LogInformation(
                        "✨ «{Title}» ({ChatId}): ИИ вернул тот же текст, время набора {Delay} мс.",
                        chat.Title,
                        chat.Id,
                        typingDelay);
                }
            }
            else
            {
                finalText = _currentUniqueText ?? messageText;
                // Для копипасты задержка меньше (1-3 секунды)
                typingDelay = Random.Shared.Next(1000, 3001);
                
                _logger.LogInformation(
                    "📋 «{Title}» ({ChatId}): копи-паста ({Count}/5), время набора {Delay} мс.",
                    chat.Title,
                    chat.Id,
                    _messagesSentWithCurrentText % 5,
                    typingDelay);
            }

            await client.Messages_SetTyping(inputPeer, new SendMessageTypingAction());
            await Task.Delay(typingDelay, cancellationToken);

            await client.SendMessageAsync(inputPeer, finalText);

            chat.LastSentAt = sentAt;
            chat.UpdatedAt = DateTime.UtcNow;
            chat.LastErrorMessage = null;
            
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
            throw new RpcException(420, ex.Message); // Пробрасываем для ProcessChatsAsync
        }
        catch (RpcException ex) when (ex.Code == 420)
        {
            var waitSeconds = ex.X > 0 ? ex.X : 30;
            await HandleFloodAsync(db, chat.Id, sentAt, waitSeconds, ex.Message, cancellationToken);
            throw; // Пробрасываем для ProcessChatsAsync
        }
        catch (RpcException ex)
        {
            var msg = ex.Message ?? "";
            bool isPermanentError = msg.Contains("USER_BANNED_IN_CHANNEL") ||
                                    msg.Contains("CHAT_WRITE_FORBIDDEN") ||
                                    msg.Contains("CHAT_RESTRICTED") ||
                                    msg.Contains("SCHEDULE_STATUS_PRIVATE") ||
                                    msg.Contains("CHANNEL_PRIVATE") ||
                                    msg.Contains("CHANNEL_INVALID");

            if (isPermanentError)
            {
                chat.IsActive = false;
                _logger.LogWarning("Чат {ChatId} ({Title}) деактивирован из-за перманентной ошибки: {Error}", chat.Id, chat.Title, ex.Message);
            }

            chat.LastSentAt = sentAt; // Даже при ошибке обновляем время попытки
            chat.LastErrorMessage = $"[{ex.Code}] {ex.Message}";
            chat.UpdatedAt = DateTime.UtcNow;
            WriteLog(db, chat.Id, sentAt, isPermanentError ? "Banned" : "Error", chat.LastErrorMessage);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogError(ex, "Ошибка Telegram при отправке в {ChatId}.", chat.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            chat.LastSentAt = sentAt; // Даже при ошибке обновляем время попытки
            chat.LastErrorMessage = ex.Message;
            chat.UpdatedAt = DateTime.UtcNow;
            WriteLog(db, chat.Id, sentAt, "Error", ex.Message);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogError(ex, "Ошибка при отправке в {ChatId}.", chat.Id);
        }
    }

    /// <summary>
    /// Проверяет подписку, пытается вступить и проверяет права на отправку.
    /// </summary>
    private async Task<InputPeer?> EnsureMembershipAndPermissionsAsync(Client client, TargetChat chat, Dictionary<long, ChatBase> chatsCache, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Проверяю подписку на {ChatId}...", chat.Id);
            ChatBase? chatInfo = null;

            if (chatsCache.TryGetValue(chat.Id, out chatInfo)) { }
            else if (chat.Id <= -1_000_000_000_000)
            {
                var channelId = -chat.Id - 1_000_000_000_000;
                chatsCache.TryGetValue(channelId, out chatInfo);
            }

            // А. Проверка подписки и попытка Join
            if (chatInfo is null || (chatInfo is Channel c && c.flags.HasFlag(Channel.Flags.left)))
            {
                _logger.LogInformation("Аккаунт не состоит в чате {ChatId}. Попытка вступления...", chat.Id);
                var inputPeer = await ResolveInputPeerAsync(client, chat.Id, chatsCache, cancellationToken);
                if (inputPeer is null)
                {
                    _logger.LogWarning("Не удалось разрешить peer для чата {ChatId}. Пропуск.", chat.Id);
                    chat.LastErrorMessage = "Peer not found";
                    return null;
                }

                if (inputPeer is InputPeerChannel inputChannel)
                {
                    await client.Channels_JoinChannel(inputChannel);
                    _logger.LogInformation("Успешное вступление в канал {ChatId}.", chat.Id);
                    // Перезапрашиваем инфо после вступления и обновляем кэш
                    var fullChannel = await client.Channels_GetFullChannel(inputChannel);
                    foreach (var pair in fullChannel.chats)
                    {
                        chatsCache[pair.Key] = pair.Value;
                        if (pair.Value is Channel ch)
                        {
                            chatsCache[-1000000000000L - ch.ID] = ch;
                        }
                    }
                    chatInfo = fullChannel.chats.Values.FirstOrDefault();
                }
                else
                {
                    // Для обычных чатов (редко)
                    _logger.LogWarning("Авто-вступление в обычные чаты не реализовано. Пропуск.");
                    return null;
                }
            }

            // Б. Проверка прав (Permissions)
            if (chatInfo is Channel channel)
            {
                if (channel.IsBanned())
                {
                    _logger.LogWarning("Аккаунт забанен в канале {ChatId}. Деактивация.", chat.Id);
                    chat.IsActive = false;
                    chat.LastErrorMessage = "USER_BANNED_IN_CHANNEL";
                    return null;
                }

                // Проверка прав на отправку сообщений
                if (channel.default_banned_rights?.flags.HasFlag(ChatBannedRights.Flags.send_messages) == true)
                {
                    _logger.LogWarning("В канале {ChatId} запрещена отправка сообщений (SlowMode или права). Пропуск.", chat.Id);
                    chat.LastErrorMessage = "CHAT_WRITE_FORBIDDEN (default rights)";
                    return null;
                }
            }

            return await ResolveInputPeerAsync(client, chat.Id, chatsCache, cancellationToken);
        }
        catch (RpcException ex)
        {
            var msg = ex.Message ?? "";
            bool isPermanentError = msg.Contains("USER_BANNED_IN_CHANNEL") ||
                                    msg.Contains("CHAT_WRITE_FORBIDDEN") ||
                                    msg.Contains("CHAT_RESTRICTED") ||
                                    msg.Contains("SCHEDULE_STATUS_PRIVATE") ||
                                    msg.Contains("CHANNEL_PRIVATE") ||
                                    msg.Contains("CHANNEL_INVALID");

            if (isPermanentError)
            {
                _logger.LogWarning("Ошибка пре-валидации для {ChatId}: {Error}. Деактивация.", chat.Id, ex.Message);
                chat.IsActive = false;
            }
            else
            {
                _logger.LogError(ex, "Ошибка Telegram при пре-валидации {ChatId}.", chat.Id);
            }

            chat.LastErrorMessage = ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Непредвиденная ошибка при пре-валидации чата {ChatId}.", chat.Id);
            chat.LastErrorMessage = $"Pre-validation error: {ex.Message}";
            return null;
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
        var totalPause = waitSeconds + 10;
        SetGlobalPause(totalPause);
        WriteLog(db, chatId, sentAt, "Flood", message);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "FLOOD_WAIT_{Seconds}. Планировщик приостановлен на {TotalPause} с.",
            waitSeconds,
            totalPause);
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
        Dictionary<long, ChatBase> chatsCache,
        CancellationToken cancellationToken)
    {
        if (chatsCache.TryGetValue(chatId, out var chat))
            return chat;

        if (chatId <= -1_000_000_000_000)
        {
            var channelId = -chatId - 1_000_000_000_000;
            if (chatsCache.TryGetValue(channelId, out chat))
                return chat;
        }

        var foundChat = chatsCache.Values.FirstOrDefault(c => c?.ID == chatId);
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
    /// Использует локальное время сервера.
    /// </summary>
    private (bool IsActive, string Reason) GetActivityStatus(DateTime nowUtc)
    {
        var localNow = nowUtc.ToLocalTime();

        // Проверяем, нужно ли генерировать новые смещения (новый день)
        if (localNow.Date != _offsetsGeneratedForDate)
        {
            GenerateDailyOffsets();
            _offsetsGeneratedForDate = localNow.Date;
        }

        var hour = localNow.Hour;
        var minute = localNow.Minute;
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
    /// Проверяет все активные чаты в БД и вступает в те, где аккаунт не состоит.
    /// </summary>
    private async Task EnsureAllActiveChatsJoinedAsync(CancellationToken ct)
    {
        _logger.LogInformation("🚀 Начинаю предварительную проверку подписок на все активные чаты...");

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            var activeChats = await db.TargetChats
                .Where(c => c.IsActive)
                .ToListAsync(ct);

            if (activeChats.Count == 0)
            {
                _logger.LogInformation("Нет активных чатов для проверки.");
                return;
            }

            var client = _clientManager.Client;
            var dialogs = await client.Messages_GetDialogs();
            var chatsCache = dialogs is Messages_Dialogs md ? md.chats :
                             dialogs is Messages_DialogsSlice mds ? mds.chats : new Dictionary<long, ChatBase>();

            int checkedCount = 0;
            int joinedCount = 0;

            foreach (var chat in activeChats)
            {
                if (ct.IsCancellationRequested) break;

                // Проверяем, есть ли чат в кэше и не вышли ли мы из него
                bool alreadyJoined = false;
                if (chatsCache.TryGetValue(chat.Id, out var cInfo))
                {
                    if (cInfo is not Channel channel || !channel.flags.HasFlag(Channel.Flags.left))
                        alreadyJoined = true;
                }

                if (alreadyJoined)
                {
                    _logger.LogDebug("Аккаунт уже состоит в чате {ChatId} ({Title}). Пропуск вступления.", chat.Id, chat.Title);
                    checkedCount++;
                    continue;
                }

                _logger.LogInformation("Проверка чата {ChatId} ({Title})...", chat.Id, chat.Title);

                var inputPeer = await EnsureMembershipAndPermissionsAsync(client, chat, chatsCache, ct);

                if (inputPeer == null && chat.IsActive)
                {
                    _logger.LogWarning("Не удалось вступить в чат {ChatId} ({Title}). Ошибка: {Error}", chat.Id, chat.Title, chat.LastErrorMessage);
                }
                else if (inputPeer != null)
                {
                    joinedCount++;
                    // Тайм-аут 10 секунд ТОЛЬКО если вступление реально произошло (согласно ТЗ)
                    await Task.Delay(10000, ct);
                }

                checkedCount++;
            }

            _logger.LogInformation("✅ Проверка подписок завершена. Проверено: {Total}, Вступили в: {Joined}.", activeChats.Count, joinedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка во время предварительной проверки подписок.");
        }
    }

    /// <summary>
    /// Асинхронный метод для последовательного обхода чатов с задержкой и обработкой FLOOD_WAIT.
    /// </summary>
    private async Task ProcessChatsAsync(AppDbContext db, List<TargetChat> chats, string messageText, Dictionary<long, ChatBase> chatsCache, CancellationToken cancellationToken)
    {
        int processedInSession = 0;
        int sessionLimitBeforeBreak = Random.Shared.Next(15, 26); // Пауза каждые 15-25 чатов

        for (int i = 0; i < chats.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // "Человеческий перерыв" (Human Break)
            if (processedInSession >= sessionLimitBeforeBreak)
            {
                var breakMinutes = Random.Shared.Next(10, 21);
                _logger.LogInformation("☕ Человеческий перерыв: {Minutes} мин после обработки {Count} чатов.", breakMinutes, processedInSession);
                await Task.Delay(TimeSpan.FromMinutes(breakMinutes), cancellationToken);

                processedInSession = 0;
                sessionLimitBeforeBreak = Random.Shared.Next(15, 26);
            }

            // Если планировщик на глобальной паузе - ждем
            if (IsGloballyPaused())
            {
                await DelayUntilPauseEndsAsync(cancellationToken);
            }

            var chat = chats[i];

            // Рандомизация задержки (7-12с, 10% шанс на 20-45с)
            var delaySeconds = Random.Shared.Next(100) < 10
                ? Random.Shared.Next(20, 46)
                : Random.Shared.Next(7, 13);

            // Логирование согласно ТЗ: индекс, ID чата и задержка
            _logger.LogInformation("Обработка {Current} из {Total} | ID чата: {ChatId} | Задержка до следующего шага: {Delay}с",
                i + 1, chats.Count, chat.Id, delaySeconds);

            try
            {
                await SendToChatAsync(db, chat, messageText, chatsCache, cancellationToken);
                processedInSession++;
            }
            catch (RpcException ex) when (ex.Code == 420)
            {
                // Обработка FLOOD_WAIT согласно ТЗ: сон на e.X + 1 секунд и продолжение
                var waitSeconds = ex.X + 1;
                _logger.LogWarning("🛑 FLOOD_WAIT: засыпаю на {Seconds}с. После паузы выполнение продолжится.", waitSeconds);

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);

                // Пробуем этот же чат снова после паузы
                i--;
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Ошибка при обработке чата {ChatId} в цикле обхода.", chat.Id);
            }

            // Задержка между запросами согласно ТЗ (кроме последнего чата в списке)
            if (i < chats.Count - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Устанавливает случайный перерыв перед следующей отправкой.
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
