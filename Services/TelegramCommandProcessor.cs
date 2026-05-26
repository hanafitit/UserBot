using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TestApp.Data;
using TestApp.Data.Models;

namespace TestApp.Services;

/// <summary>
/// Парсинг и выполнение команд управления из чата «Избранное».
/// </summary>
public sealed class TelegramCommandProcessor
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<TelegramCommandProcessor> _logger;

    public TelegramCommandProcessor(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<TelegramCommandProcessor> logger)
    {
        _dbContextFactory = dbContextFactory;
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
            "/del_chat" => await DelChatAsync(arguments, cancellationToken),
            "/set_text" => await SetTextAsync(arguments, cancellationToken),
            "/status" => await GetStatusAsync(cancellationToken),
            "/help" => GetHelp(),
            "/logs" => await GetRecentLogsAsync(cancellationToken),
            _ => "Неизвестная команда. Используйте /help для справки"
        };
    }

    /// <summary>
    /// Добавляет целевой чат: /add_chat {ID} {Название} [{ПостовВДень}]
    /// </summary>
    private async Task<string> AddChatAsync(string arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return "Формат: /add_chat {ID_чата} {Название} [кол-во_в_день]";

        var parts = arguments.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return "Формат: /add_chat {ID_чата} {Название} [кол-во_в_день]";

        if (!long.TryParse(parts[0], out var chatId))
            return "ID чата должен быть числом (например: -1001234567890).";

        var title = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(title))
            return "Укажите название чата после ID.";

        int postsPerDay = 5; // По умолчанию 5 постов в день
        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], out postsPerDay) || postsPerDay < 1)
                return "Кол-во постов должно быть числом >= 1. Например: 5 или 10";
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (await db.TargetChats.AnyAsync(c => c.Id == chatId, cancellationToken))
            return $"Чат с ID {chatId} уже есть в базе.";

        db.TargetChats.Add(new TargetChat
        {
            Id = chatId,
            Title = title,
            IsActive = true,
            SlowModeSeconds = 60,
            PostsPerDay = postsPerDay
        });

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Добавлен целевой чат {ChatId} ({Title}, {PostsPerDay} постов/день)", chatId, title, postsPerDay);

        return $"✅ Чат {title} успешно добавлен ({postsPerDay} объявлений в день)";
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
        _logger.LogInformation("Удалён целевой чат {ChatId} ({Title})", chatId, chat.Title);

        return $"❌ Чат {chatId} успешно удален из списка рассылки.";
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
        _logger.LogInformation("Установлен новый активный шаблон объявления ({Length} символов).", arguments.Length);

        return "✅ Новый шаблон объявления успешно установлен и активирован!";
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
        var today = DateTime.UtcNow.Date;

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

        return $"""
            📊 СТАТУС ЮЗЕРБОТА
            Всего чатов: {total} (Активных: {active})
            
            📋 Чаты в ротации:
            {chatsList}
            
            📝 Текущий текст объявления:
            {templateSection}
            """;
    }

    /// <summary>
    /// Справка по командам.
    /// </summary>
    private string GetHelp()
    {
        return """
            📚 СПРАВКА ПО КОМАНДАМ
            
            /add_chat {ID} {название} [N]
              Добавить чат в рассылку. N — макс постов в день (по умолчанию 5)
              Пример: /add_chat -1001234567890 MyGroup 3
            
            /del_chat {ID}
              Удалить чат из рассылки
            
            /set_text {текст}
              Установить текст объявления для рассылки
            
            /status
              Показать статус: список чатов, текущий текст, прогресс за день
            
            /logs
              Последние отправки и ошибки
            
            /help
              Эта справка
            """;
    }

    /// <summary>
    /// Показывает последние логи отправок.
    /// </summary>
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
            var status = l.Status ?? "?";
            var errorInfo = string.IsNullOrEmpty(l.ErrorMessage) 
                ? "✅" 
                : $"❌ {l.ErrorMessage[..Math.Min(30, l.ErrorMessage.Length)]}";
            return $"{l.SentAt:HH:mm:ss} | {status,-7} | чат {l.ChatId} | {errorInfo}";
        }).ToList();

        var logsText = string.Join("\n", logLines);

        return $"""
            📋 ПОСЛЕДНИЕ 10 ОТПРАВОК
            {logsText}
            """;
    }
}
