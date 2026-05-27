using System.Data;

internal class DomainModels
{
internal class DomainModels
{
    internal class ChatConfig
    {
        public long ChatId { get; set; }               // ID группы / супергруппы в Telegram
        public string? Title { get; set; }             // название чата для удобства
        public string? Username { get; set; }          // username группы, если есть
        public bool IsActive { get; set; } = true;     // рассылка в этот чат включена или нет
        public int SlowModeSeconds { get; set; } = 600; // минимальный интервал между отправками
        public DateTime LastSentAt { get; set; } = DateTime.MinValue; // время последней отправки
        public int MinDelaySeconds { get; set; } = 5;  // минимальная случайная пауза перед отправкой
        public int MaxDelaySeconds { get; set; } = 20; // максимальная случайная пауза перед отправкой
        public string? Notes { get; set; }             // дополнительные заметки / пометки по чату

        public DateTime NextAllowedSendAt => LastSentAt.AddSeconds(SlowModeSeconds); // следующий момент, когда можно отправлять
    }

    internal class BotSettings
    {
        public long FavoriteChatId { get; set; }       // ID чата «Избранное», где принимаются команды
        public long OwnerUserId = 7499022252;          // ваш пользовательский ID в Telegram
           // токен для API нейросети уникализации
        public bool UseSendChatAction { get; set; } = true; // использовать ли SendChatAction перед отправкой
    }

    internal class SendLog
    {
        public long ChatId { get; set; }               // ID чата, в который отправлялось сообщение
        public DateTime SentAt { get; set; }           // время отправки
        public string? BaseText { get; set; }          // исходный текст объявления
        public string? UniqueText { get; set; }        // текст после уникализации
        public bool IsSuccess { get; set; }            // успешна ли была отправка
        public string? ErrorMessage { get; set; }      // текст ошибки, если отправка не удалась
    }
}
}
