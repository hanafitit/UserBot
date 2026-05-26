using Microsoft.Extensions.Logging;
using TL;
using WTelegram;

namespace TestApp.Services;

/// <summary>
/// Маршрутизатор live-обновлений Telegram: фильтрует «Избранное» и передаёт команды в <see cref="TelegramCommandProcessor"/>.
/// </summary>
public sealed class TelegramUpdateRouter
{
    private readonly TelegramClientManager _clientManager;
    private readonly TelegramCommandProcessor _commandProcessor;
    private readonly ILogger<TelegramUpdateRouter> _logger;
    private long _savedMessagesPeerId;
    private bool _initialized;

    public TelegramUpdateRouter(
        TelegramClientManager clientManager,
        TelegramCommandProcessor commandProcessor,
        ILogger<TelegramUpdateRouter> logger)
    {
        _clientManager = clientManager;
        _commandProcessor = commandProcessor;
        _logger = logger;
    }

    /// <summary>
    /// Подписывается на <see cref="Client.OnUpdates"/> после успешной авторизации.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
            return;

        var user = _clientManager.User
            ?? throw new InvalidOperationException("Сначала выполните LoginAsync в TelegramClientManager.");

        _savedMessagesPeerId = user.id;
        var client = _clientManager.Client;

        // WTelegramClient предоставляет событие OnUpdates (контейнер UpdatesBase).
        client.OnUpdates += HandleUpdatesAsync;
        _initialized = true;

        _logger.LogInformation(
            "Роутер обновлений активен. Команды принимаются в «Избранное» (peer_id: {PeerId}).",
            _savedMessagesPeerId);
    }

    /// <summary>
    /// Обрабатывает пакет обновлений и вызывает <see cref="HandleUpdate"/> для каждого элемента.
    /// </summary>
    private async Task HandleUpdatesAsync(UpdatesBase updates)
    {
        try
        {
            foreach (var update in updates.UpdateList)
                await HandleUpdate(update);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ошибка при обработке пакета обновлений Telegram.");
        }
    }

    /// <summary>
    /// Обрабатывает одно обновление; реагирует на <see cref="UpdateNewMessage"/>.
    /// </summary>
    private async Task HandleUpdate(Update update)
    {
        if (update is not UpdateNewMessage { message: Message message })
            return;

        await TryHandleSavedMessageCommandAsync(message);
    }

    /// <summary>
    /// Фильтр «Избранное» + защита от ответов бота, затем выполнение команды.
    /// </summary>
    private async Task TryHandleSavedMessageCommandAsync(Message message)
    {
        if (!IsSavedMessagesPeer(message))
            return;

        var text = message.message?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        // Исходящие без «/» — ответы бота или обычные заметки; команды пользователя всегда с «/».
        if (message.flags.HasFlag(Message.Flags.out_) && !text.StartsWith('/'))
            return;

        if (!text.StartsWith('/'))
            return;

        _logger.LogInformation("Команда в «Избранное»: {Command}", text.Split(' ', 2)[0]);

        try
        {
            var response = await _commandProcessor.ProcessAsync(text);
            await ReplyToSavedMessagesAsync(response);
        }
        catch (RpcException ex) when (ex.Code == 420)
        {
            var waitSeconds = ex.X > 0 ? ex.X : 30;
            _logger.LogWarning("FLOOD_WAIT_{Seconds} при ответе на команду.", waitSeconds);
            await ReplyToSavedMessagesAsync(
                $"⏳ Telegram просит подождать {waitSeconds} с (FLOOD_WAIT). Повторите команду позже.");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Ошибка Telegram API при обработке команды.");
            await ReplyToSavedMessagesAsync($"❌ Ошибка Telegram [{ex.Code}]: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при обработке команды.");
            await ReplyToSavedMessagesAsync("❌ Внутренняя ошибка при выполнении команды.");
        }
    }

    /// <summary>
    /// «Избранное» — диалог с peer_id, равным ID вашего аккаунта.
    /// </summary>
    private bool IsSavedMessagesPeer(Message message) =>
        message.peer_id is PeerUser peerUser && peerUser.user_id == _savedMessagesPeerId;

    /// <summary>
    /// Отправляет ответ в «Избранное» (без префикса «/»).
    /// </summary>
    private async Task ReplyToSavedMessagesAsync(string text)
    {
        var client = _clientManager.Client;
        
        // Имитация печатания для естественности
        await client.Messages_SetTyping(InputPeer.Self, new SendMessageTypingAction());
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(300, 800)));
        
        await client.SendMessageAsync(InputPeer.Self, text);
    }
}
