using System.Text;

namespace TestApp.Services;

public static class TelegramMessageHelper
{
    /// <summary>
    /// Разрезает длинное сообщение на части, не превышающие maxLength.
    /// Старается резать по границам строк (\n).
    /// </summary>
    public static IEnumerable<string> SplitMessage(string text, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        var sb = new StringBuilder();
        var lines = text.Split('\n');

        foreach (var line in lines)
        {
            // Если одна строка сама по себе длиннее лимита (редко, но бывает)
            if (line.Length > maxLength)
            {
                // Сначала выкидываем то, что уже накопили
                if (sb.Length > 0)
                {
                    yield return sb.ToString().TrimEnd();
                    sb.Clear();
                }

                // Режем саму длинную строку посимвольно
                for (int i = 0; i < line.Length; i += maxLength)
                {
                    yield return line.Substring(i, Math.Min(maxLength, line.Length - i));
                }
                continue;
            }

            // Если добавление строки превысит лимит
            if (sb.Length + line.Length + 1 > maxLength)
            {
                yield return sb.ToString().TrimEnd();
                sb.Clear();
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString().TrimEnd();
        }
    }
}
