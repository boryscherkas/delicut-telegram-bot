using Telegram.Bot;
using Telegram.Bot.Types;

namespace DelicutTelegramBot.Handlers;

public class CancelHandler
{
    private readonly ITelegramBotClient _bot;

    public CancelHandler(ITelegramBotClient bot) => _bot = bot;

    public async Task HandleCommandAsync(Message message, CancellationToken ct)
    {
        await _bot.SendMessage(
            message.Chat.Id,
            "Cancelled. Use /select, /settings, or /start.",
            cancellationToken: ct);
    }
}
