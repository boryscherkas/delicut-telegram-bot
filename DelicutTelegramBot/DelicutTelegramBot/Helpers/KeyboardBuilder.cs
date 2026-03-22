using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace DelicutTelegramBot.Helpers;

public static class KeyboardBuilder
{
    /// <summary>Telegram API maximum characters per message.</summary>
    private const int TelegramMaxMessageLength = 4096;
    /// <summary>
    /// Returns the standard week overview keyboard with Approve All, Approve Day, Change Dishes, Regenerate.
    /// </summary>
    public static InlineKeyboardMarkup WeekOverviewKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Approve All", "select:approve_all"),
                InlineKeyboardButton.WithCallbackData("Approve Day", "select:approve_day"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Change Dishes", "select:change"),
                InlineKeyboardButton.WithCallbackData("Regenerate", "select:regenerate")
            }
        });

    /// <summary>
    /// Sends a message, splitting it into multiple messages if it exceeds the Telegram 4096 character limit.
    /// The keyboard is attached to the last message chunk only.
    /// </summary>
    public static async Task SendOrSplitMessageAsync(ITelegramBotClient bot, long chatId, string text,
        InlineKeyboardMarkup keyboard, CancellationToken ct)
    {
        if (text.Length <= TelegramMaxMessageLength)
        {
            await bot.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: ct);
        }
        else
        {
            var chunks = TelegramFormatHelper.SplitMessage(text, TelegramMaxMessageLength);
            for (int i = 0; i < chunks.Count - 1; i++)
                await bot.SendMessage(chatId, chunks[i], cancellationToken: ct);
            await bot.SendMessage(chatId, chunks[^1], replyMarkup: keyboard, cancellationToken: ct);
        }
    }
}
