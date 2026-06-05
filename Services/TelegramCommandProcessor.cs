using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TestApp.Data;
using TestApp.Data.Models;
using TL;

namespace TestApp.Services;

/// <summary>
/// Парсинг и выполнение команд управления из чата «Избранное».
/// </summary>
public sealed class TelegramCommandProcessor
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly TelegramClientManager _clientManager;
    private readonly SchedulerState _schedulerState;
    private readonly ILogger<TelegramCommandProcessor> _logger;

    public TelegramCommandProcessor(
        IDbContextFactory<AppDbContext> dbContextFactory,
        TelegramClientManager clientManager,
        SchedulerState schedulerState,
        ILogger<TelegramCommandProcessor> logger)
    {
        _dbContextFactory = dbContextFactory;
        _clientManager = clientManager;
        _schedulerState = schedulerState;
        _logger = logger;
    }

    /// <summary>
    /// Выполняет команду и возвращает текст ответа (без префикса «/»).
    /// </summary>
    public async Task<string> ProcessAsync(string commandText, CancellationToken cancellationToken = default)
    {
        var trimmed = commandText.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        var command = spaceIndex < 0
            ? trimmed.ToLowerInvariant()
            : trimmed[..spaceIndex].ToLowerInvariant();
        var arguments = spaceIndex < 0 ? string.Empty : trimmed[(spaceIndex + 1)..].Trim();

        return command switch
        {
            "/add_chat" => await AddChatAsync(arguments, cancellationToken),
            "/bulk_import" => await BulkImportAsync(arguments, cancellationToken),
            "/bulk_join" => await BulkJoinAsync(arguments, cancellationToken),
            "/del_chat" => await DelChatAsync(arguments, cancellationToken),
            "/set_text" => await SetTextAsync(arguments, cancellationToken),
            "/status" => await GetStatusAsync(cancellationToken),
            "/help" => GetHelp(),
            "/logs" => await GetRecentLogsAsync(cancellationToken),
            "/pause" => PauseScheduler(true),
            "/resume" => PauseScheduler(false),
            "/start" => PauseScheduler(false),
            _ => "Неизвестная команда. Используйте /help для справки"
        };
    }

    /// <summary>
    /// Добавляет целевой чат.
    /// Форматы:
    ///   /add_chat {ID} {Название} [N]
    ///   /add_chat @username [N]
    ///   /add_chat https://t.me/username [N]
    /// </summary>
    private async Task<string> AddChatAsync(string arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "Форматы:\n" +
                   "/add_chat {ID} {Название} [постов/день]\n" +
                   "/add_chat @username [постов/день]\n" +
                   "/add_chat https://t.me/username [постов/день]";

        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstArg = parts[0];

        // Определяем тип первого аргумента
        if (firstArg.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            firstArg.StartsWith("@"))
        {
            return await AddChatByUsernameAsync(parts, cancellationToken);
        }
        else
        {
            return await AddChatByIdAsync(parts, cancellationToken);
        }
    }

    /// <summary>
    /// Добавление по ID: /add_chat {ID} {Название} [N]
    /// </summary>
    private async Task<string> AddChatByIdAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
            return "Формат: /add_chat {ID_чата} {Название} [кол-во_в_день]";

        if (!long.TryParse(parts[0], out var chatId))
            return "ID чата должен быть числом (например: -1001234567890).";

        var title = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(title))
            return "Укажите название чата после ID.";

        int postsPerDay = 5;
        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], out postsPerDay) || postsPerDay < 1)
                return "Кол-во постов должно быть числом >= 1.";
        }

        return await SaveChatAsync(chatId, title, postsPerDay, cancellationToken);
    }

    /// <summary>
    /// Добавление по username или ссылке: /add_chat @username [N] или /add_chat https://t.me/username [N]
    /// Название подтягивается автоматически из Telegram.
    /// </summary>
    private async Task<string> AddChatByUsernameAsync(string[] parts, CancellationToken cancellationToken)
    {
        var raw = parts[0];

        int postsPerDay = 5;
        if (parts.Length >= 2)
        {
            if (!int.TryParse(parts[1], out postsPerDay) || postsPerDay < 1)
                return "Кол-во постов должно быть числом >= 1.";
        }

        var result = await ImportChatByUsernameInternalAsync(raw, postsPerDay, cancellationToken);
        return result.Message;
    }

    private sealed record ImportResult(bool Success, string Message);

    private static string ParseUsername(string identifier)
    {
        if (identifier.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var uri = identifier.TrimEnd('/');
            var lastSlash = uri.LastIndexOf('/');
            return lastSlash >= 0 ? uri[(lastSlash + 1)..] : uri;
        }
        return identifier.TrimStart('@');
    }

    private async Task<ImportResult> ImportChatByUsernameInternalAsync(string rawUsernameOrLink, int postsPerDay, CancellationToken cancellationToken)
    {
        if (rawUsernameOrLink.Contains("/joinchat/"))
            return new ImportResult(false, "Импорт по инвайт-ссылкам (/joinchat/) пока не поддерживается для БД. Вступите в чат сначала.");

        var username = ParseUsername(rawUsernameOrLink);
        if (string.IsNullOrWhiteSpace(username))
            return new ImportResult(false, "Не удалось извлечь username из аргумента.");

        // Резолвим через Telegram API
        try
        {
            var resolved = await _clientManager.Client.Contacts_ResolveUsername(username);
            if (resolved?.peer == null)
                return new ImportResult(false, $"Не удалось найти чат с username @{username}.");

            long chatId;
            string title;

            switch (resolved.peer)
            {
                case PeerChannel peerChannel:
                    var channel = resolved.chats[peerChannel.channel_id] as Channel;
                    chatId = -1000000000000L - peerChannel.channel_id;
                    title = channel?.title ?? username;
                    break;

                case PeerChat peerChat:
                    var chat = resolved.chats[peerChat.chat_id] as Chat;
                    chatId = -peerChat.chat_id;
                    title = chat?.title ?? username;
                    break;

                case PeerUser peerUser:
                    var user = resolved.users[peerUser.user_id] as User;
                    chatId = peerUser.user_id;
                    title = user?.first_name ?? username;
                    break;

                default:
                    return new ImportResult(false, $"Неизвестный тип peer для @{username}.");
            }

            var saveResult = await SaveChatAsync(chatId, title, postsPerDay, cancellationToken);
            return new ImportResult(true, saveResult);
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Ошибка при резолве username @{Username}", username);
            return new ImportResult(false, $"Ошибка Telegram [{ex.Code}]: {ex.Message}");
        }
    }

    /// <summary>
    /// Массовая подписка на чаты из файла Data/import_chats.txt
    /// </summary>
    private async Task<string> BulkJoinAsync(string arguments, CancellationToken cancellationToken)
    {
        const string importFilePath = "Data/import_chats.txt";
        if (!File.Exists(importFilePath))
            return $"Файл {importFilePath} не найден. Сначала создайте его.";

        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int minDelay = 180; // 3 мин
        int maxDelay = 420; // 7 мин

        if (parts.Length >= 1 && int.TryParse(parts[0], out var d1)) minDelay = d1;
        if (parts.Length >= 2 && int.TryParse(parts[1], out var d2)) maxDelay = d2;

        var lines = await File.ReadAllLinesAsync(importFilePath, cancellationToken);

        // Запускаем в фоне, чтобы не блокировать обработку других команд
        _ = Task.Run(async () =>
        {
            int success = 0;
            int failed = 0;
            var client = _clientManager.Client;

            await SendInternalStatusAsync($"🚀 Начинаю массовую подписку на {lines.Length} чатов.\nЗадержка: {minDelay}-{maxDelay} сек.");

            foreach (var line in lines)
            {
                var identifier = line.Trim();
                if (string.IsNullOrWhiteSpace(identifier)) continue;

                try
                {
                    bool joined = await JoinChatInternalAsync(identifier, cancellationToken);
                    if (joined) success++; else failed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning("Ошибка при подписке на {Chat}: {Error}", identifier, ex.Message);
                }

                // Каждые 10 чатов присылаем отчет
                if ((success + failed) % 10 == 0)
                {
                    await SendInternalStatusAsync($"📊 Прогресс подписки: {success + failed}/{lines.Length}\nУспешно: {success}, Ошибок: {failed}");
                }

                var delay = Random.Shared.Next(minDelay, maxDelay + 1);
                await Task.Delay(TimeSpan.FromSeconds(delay));
            }

            await SendInternalStatusAsync($"✅ Массовая подписка завершена!\nВсего: {lines.Length}\nУспешно: {success}\nОшибок: {failed}");
        });

        return $"⚙️ Процесс подписки запущен в фоне. Ожидайте уведомлений.";
    }

    private async Task<bool> JoinChatInternalAsync(string identifier, CancellationToken cancellationToken)
    {
        var client = _clientManager.Client;

        // 1. Обработка ссылки-приглашения (joinchat)
        if (identifier.Contains("/joinchat/"))
        {
            var hash = identifier.Split("/joinchat/").Last().Split('?').First();
            try
            {
                var invite = await client.Messages_CheckChatInvite(hash);
                if (invite is ChatInviteAlready) return true;
                await client.Messages_ImportChatInvite(hash);
                return true;
            }
            catch (RpcException ex) when (ex.Message == "USER_ALREADY_PARTICIPANT") { return true; }
            catch (Exception ex)
            {
                _logger.LogWarning("Не удалось вступить по ссылке {Hash}: {Error}", hash, ex.Message);
                return false;
            }
        }

        // 2. Обработка username или обычной ссылки
        var username = ParseUsername(identifier);

        try
        {
            var resolved = await client.Contacts_ResolveUsername(username);
            if (resolved.peer is PeerChannel pc)
            {
                var channel = resolved.chats[pc.channel_id] as Channel;
                // Проверяем, не состоим ли уже (упрощенно)
                if (channel == null || channel.flags.HasFlag(Channel.Flags.left))
                {
                    await client.Channels_JoinChannel(new InputChannel(channel.id, channel.access_hash));
                    return true;
                }
                return true;
            }
            else if (resolved.peer is PeerChat chatPeer)
            {
                // Для обычных чатов API вступления отличается, но resolveUsername чаще для каналов
                return true;
            }
            return false;
        }
        catch (RpcException ex) when (ex.Message == "CHANNELS_TOO_MUCH")
        {
            await SendInternalStatusAsync("❌ Ошибка: У вас слишком много каналов. Telegram не дает вступать в новые.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ошибка Resolve/Join для {Username}: {Error}", username, ex.Message);
            return false;
        }
    }

    private async Task SendInternalStatusAsync(string text)
    {
        try
        {
            await _clientManager.Client.SendMessageAsync(InputPeer.Self, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось отправить статус в Избранное");
        }
    }

    /// <summary>
    /// Массовый импорт чатов из файла Data/import_chats.txt
    /// </summary>
    private async Task<string> BulkImportAsync(string arguments, CancellationToken cancellationToken)
    {
        const string importFilePath = "Data/import_chats.txt";
        if (!File.Exists(importFilePath))
            return $"Файл {importFilePath} не найден. Сначала создайте его.";

        int postsPerDay = 5;
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            if (!int.TryParse(arguments, out postsPerDay) || postsPerDay < 1)
                return "Кол-во постов должно быть числом >= 1.";
        }

        var lines = await File.ReadAllLinesAsync(importFilePath, cancellationToken);
        int successCount = 0;
        int failCount = 0;
        int alreadyExistsCount = 0;

        var report = new System.Text.StringBuilder();
        report.AppendLine($"🚀 Начинаю импорт {lines.Length} чатов...");

        foreach (var line in lines)
        {
            var chatIdentifier = line.Trim();
            if (string.IsNullOrWhiteSpace(chatIdentifier)) continue;

            var result = await ImportChatByUsernameInternalAsync(chatIdentifier, postsPerDay, cancellationToken);
            if (result.Success)
            {
                if (result.Message.Contains("уже есть в базе"))
                {
                    alreadyExistsCount++;
                }
                else
                {
                    successCount++;
                }
            }
            else
            {
                failCount++;
                _logger.LogWarning("Не удалось импортировать {Chat}: {Error}", chatIdentifier, result.Message);
            }

            // Небольшая задержка чтобы не спамить API Telegram при резолве
            await Task.Delay(500, cancellationToken);
        }

        return $"""
            📊 ИТОГИ ИМПОРТА:
            ✅ Добавлено: {successCount}
            Existing: {alreadyExistsCount}
            ❌ Ошибок: {failCount}
            Всего обработано: {lines.Length}
            """;
    }

    /// <summary>
    /// Сохраняет чат в БД (общий финальный шаг).
    /// </summary>
    private async Task<string> SaveChatAsync(long chatId, string title, int postsPerDay, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (await db.TargetChats.AnyAsync(c => c.Id == chatId, cancellationToken))
            return $"Чат «{title}» (id: {chatId}) уже есть в базе.";

        db.TargetChats.Add(new TargetChat
        {
            Id = chatId,
            Title = title,
            IsActive = true,
            SlowModeSeconds = 60,
            PostsPerDay = postsPerDay
        });

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Добавлен чат {ChatId} ({Title}, {PostsPerDay} постов/день)", chatId, title, postsPerDay);

        return $"✅ Чат «{title}» добавлен (id: {chatId}, {postsPerDay} постов/день)";
    }

    /// <summary>
    /// Удаляет чат из рассылки: /del_chat {ID}
    /// </summary>
    private async Task<string> DelChatAsync(string arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "Формат: /del_chat {ID_чата}";

        if (!long.TryParse(arguments.Trim(), out var chatId))
            return "ID чата должен быть числом (например: -1001234567890).";

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var chat = await db.TargetChats.FirstOrDefaultAsync(c => c.Id == chatId, cancellationToken);
        if (chat is null)
            return $"Чат с ID {chatId} не найден в списке рассылки.";

        db.TargetChats.Remove(chat);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Удалён чат {ChatId} ({Title})", chatId, chat.Title);

        return $"❌ Чат «{chat.Title}» удалён из рассылки.";
    }

    /// <summary>
    /// Устанавливает активный шаблон объявления: /set_text {текст}
    /// </summary>
    private async Task<string> SetTextAsync(string arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "Формат: /set_text {текст объявления}";

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var previousTemplates = await db.AdvertisingTemplates
            .Where(t => t.IsCurrent)
            .ToListAsync(cancellationToken);

        foreach (var template in previousTemplates)
            template.IsCurrent = false;

        db.AdvertisingTemplates.Add(new AdvertisingTemplate
        {
            BaseText = arguments.Trim(),
            IsCurrent = true
        });

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Установлен новый шаблон ({Length} символов).", arguments.Length);

        return "✅ Новый шаблон объявления установлен и активирован!";
    }

    /// <summary>
    /// Формирует сводку по целевым чатам и текущему шаблону.
    /// </summary>
    private async Task<string> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var chats = await db.TargetChats
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var total = chats.Count;
        var active = chats.Count(c => c.IsActive);

        var chatsList = string.Join("\n", chats
            .OrderBy(c => c.Title)
            .Select(c =>
            {
                var status = c.IsActive ? "✅" : "❌";
                var postsInfo = c.PostsPerDay > 0
                    ? $" ({c.PostsTodayCount}/{c.PostsPerDay})"
                    : " (∞)";
                return $"{status} {c.Title} (id:{c.Id}){postsInfo}";
            }));

        var currentTemplate = await db.AdvertisingTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.IsCurrent, cancellationToken);

        var templateSection = currentTemplate is null || string.IsNullOrWhiteSpace(currentTemplate.BaseText)
            ? "⚠️ Шаблон не задан! Используйте /set_text"
            : currentTemplate.BaseText.Trim();

        var nextPost = _schedulerState.NextPlannedPostUtc.HasValue
            ? _schedulerState.NextPlannedPostUtc.Value.ToLocalTime().ToString("HH:mm")
            : "—";

        var pauseStatus = _schedulerState.IsManualPaused ? " [ПАУЗА]" : "";

        return $"""
            📊 СТАТУС ЮЗЕРБОТА{pauseStatus}
            Статус: {_schedulerState.CurrentActivityStatus}
            Следующая отправка: {nextPost}
            Всего чатов: {total} (Активных: {active})

            📋 Чаты в ротации:
            {chatsList}

            📝 Текущий текст объявления:
            {templateSection}
            """;
    }

    private string PauseScheduler(bool pause)
    {
        _schedulerState.IsManualPaused = pause;
        return pause ? "⏸️ Планировщик приостановлен." : "▶️ Планировщик запущен.";
    }

    private string GetHelp()
    {
        return """
            📚 СПРАВКА ПО КОМАНДАМ

            /pause - поставить рассылку на паузу
            /resume (или /start) - продолжить рассылку

            /add_chat {ID} {название} [N]
              Добавить чат по ID. N — постов в день (по умолчанию 5)
              Пример: /add_chat -1001234567890 MyGroup 3

            /add_chat @username [N]
              Добавить чат по username (название подтянется автоматически)
              Пример: /add_chat @mygroup 3

            /add_chat https://t.me/username [N]
              Добавить чат по ссылке
              Пример: /add_chat https://t.me/mygroup 3

            /del_chat {ID}
              Удалить чат из рассылки

            /set_text {текст}
              Установить текст объявления для рассылки

            /status
              Показать статус: список чатов, текущий текст, прогресс за день

            /logs
              Последние отправки и ошибки

            /bulk_import [N]
              Массовый импорт из Data/import_chats.txt (N постов/день, по умолчанию 5)

            /bulk_join [min] [max]
              Безопасная подписка на чаты из списка (задержка в сек, дефолт 180-420)

            /help
              Эта справка
            """;
    }

    private async Task<string> GetRecentLogsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var logs = await db.ExecutionLogs
            .AsNoTracking()
            .OrderByDescending(l => l.Id)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (!logs.Any())
            return "📋 Пока нет логов отправок.";

        var logLines = Enumerable.Reverse(logs).Select(l =>
        {
            var errorInfo = string.IsNullOrEmpty(l.ErrorMessage)
                ? "✅"
                : $"❌ {l.ErrorMessage[..Math.Min(30, l.ErrorMessage.Length)]}";
            return $"{l.SentAt:HH:mm:ss} | {l.Status,-7} | чат {l.ChatId} | {errorInfo}";
        }).ToList();

        return $"📋 ПОСЛЕДНИЕ 10 ОТПРАВОК\n{string.Join("\n", logLines)}";
    }
}
