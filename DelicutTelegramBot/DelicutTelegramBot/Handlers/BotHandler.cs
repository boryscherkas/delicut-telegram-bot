using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using DelicutTelegramBot.State;
using DelicutTelegramBot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DelicutTelegramBot.Handlers;

public class BotHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly StartHandler _startHandler;
    private readonly SettingsHandler _settingsHandler;
    private readonly SelectWeekHandler _selectWeekHandler;
    private readonly ChangeDishHandler _changeDishHandler;
    private readonly MenuHandler _menuHandler;
    private readonly CancelHandler _cancelHandler;
    private readonly ConversationStateManager _stateManager;
    private readonly ILogger<BotHandler> _logger;

    public BotHandler(
        ITelegramBotClient bot,
        StartHandler startHandler,
        SettingsHandler settingsHandler,
        SelectWeekHandler selectWeekHandler,
        ChangeDishHandler changeDishHandler,
        MenuHandler menuHandler,
        CancelHandler cancelHandler,
        ConversationStateManager stateManager,
        ILogger<BotHandler> logger)
    {
        _bot = bot;
        _startHandler = startHandler;
        _settingsHandler = settingsHandler;
        _selectWeekHandler = selectWeekHandler;
        _changeDishHandler = changeDishHandler;
        _menuHandler = menuHandler;
        _cancelHandler = cancelHandler;
        _stateManager = stateManager;
        _logger = logger;
    }

    public async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message?.Text is { } text)
            {
                var chatId = update.Message.Chat.Id;
                var userId = update.Message.From!.Id;

                // Commands reset conversation state
                if (text.StartsWith('/'))
                {
                    _stateManager.Reset(userId);

                    if (text.StartsWith("/start"))
                        await _startHandler.HandleCommandAsync(update.Message, ct);
                    else if (text.StartsWith("/select"))
                        await _selectWeekHandler.HandleCommandAsync(update.Message, ct);
                    else if (text.StartsWith("/menu"))
                        await _menuHandler.HandleCommandAsync(update.Message, ct);
                    else if (text.StartsWith("/settings"))
                        await _settingsHandler.HandleCommandAsync(update.Message, ct);
                    else if (text.StartsWith("/cancel"))
                        await _cancelHandler.HandleCommandAsync(update.Message, ct);
                    return;
                }

                // Plain text — route by conversation state
                var state = _stateManager.GetOrCreate(userId);
                state.LastActivity = DateTime.UtcNow;

                switch (state.CurrentFlow)
                {
                    case Models.Domain.ConversationFlow.Auth_WaitingEmail:
                    case Models.Domain.ConversationFlow.Auth_WaitingOtp:
                        await _startHandler.HandleTextAsync(update.Message, ct);
                        break;
                    case Models.Domain.ConversationFlow.Settings_WaitingStopWords:
                    case Models.Domain.ConversationFlow.Settings_WaitingMacroGoals:
                    case Models.Domain.ConversationFlow.Settings_WaitingProteinVariant:
                    case Models.Domain.ConversationFlow.Settings_WaitingFavourites:
                        await _settingsHandler.HandleTextAsync(update.Message, ct);
                        break;
                    default:
                        // Ignore unexpected text
                        break;
                }
            }
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callback)
            {
                var data = callback.Data ?? string.Empty;
                if (data.StartsWith("settings:"))
                    await _settingsHandler.HandleCallbackAsync(callback, ct);
                else if (data.StartsWith("select:"))
                    await _selectWeekHandler.HandleCallbackAsync(callback, ct);
                else if (data.StartsWith("change:"))
                    await _changeDishHandler.HandleCallbackAsync(callback, ct);
            }
        }
        catch (DelicutAuthExpiredException)
        {
            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId.HasValue)
            {
                var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From.Id;
                if (userId.HasValue)
                    _stateManager.Reset(userId.Value);

                await _bot.SendMessage(chatId.Value,
                    "Session expired. Use /start to re-authenticate.",
                    cancellationToken: ct);
            }
            var logUserId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id;
            _logger.LogWarning("Delicut auth expired during update {UpdateId} for user {UserId}", update.Id, logUserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
        }
    }
}
