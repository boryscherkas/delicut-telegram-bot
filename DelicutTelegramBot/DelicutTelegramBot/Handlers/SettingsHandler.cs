using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;

namespace DelicutTelegramBot.Handlers;

public class SettingsHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly IUserService _userService;
    private readonly ConversationStateManager _stateManager;

    public SettingsHandler(
        ITelegramBotClient bot,
        IUserService userService,
        ConversationStateManager stateManager)
    {
        _bot = bot;
        _userService = userService;
        _stateManager = stateManager;
    }

    public async Task HandleCommandAsync(Message message, CancellationToken ct)
    {
        var user = await _userService.GetByTelegramIdAsync(message.From!.Id);
        if (user?.Settings is null)
        {
            await _bot.SendMessage(message.Chat.Id, "Please authenticate first with /start.", cancellationToken: ct);
            return;
        }

        await SendSettingsKeyboard(message.Chat.Id, user.Settings, ct);
    }

    public async Task HandleTextAsync(Message message, CancellationToken ct)
    {
        var userId = message.From!.Id;
        var state = _stateManager.GetOrCreate(userId);

        if (state.CurrentFlow == ConversationFlow.Settings_WaitingStopWords)
        {
            var input = message.Text?.Trim() ?? "";
            var stopWords = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => w.ToLowerInvariant())
                .Distinct()
                .ToList();

            var user = await _userService.GetByTelegramIdAsync(userId);
            if (user is not null)
            {
                await _userService.UpdateSettingsAsync(user.Id, s => s.StopWords = stopWords);
                _stateManager.Reset(userId);
                await _bot.SendMessage(message.Chat.Id,
                    $"Stop words updated: {string.Join(", ", stopWords)}",
                    cancellationToken: ct);
            }
        }
        else if (state.CurrentFlow == ConversationFlow.Settings_WaitingMacroGoals)
        {
            var input = message.Text?.Trim() ?? "";
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 3 ||
                !double.TryParse(parts[0], out var protein) ||
                !double.TryParse(parts[1], out var carbs) ||
                !double.TryParse(parts[2], out var fat))
            {
                await _bot.SendMessage(message.Chat.Id,
                    "Invalid format. Send 3 numbers: protein carbs fat\nExample: 190 200 50",
                    cancellationToken: ct);
                return;
            }

            var user = await _userService.GetByTelegramIdAsync(userId);
            if (user is not null)
            {
                await _userService.UpdateSettingsAsync(user.Id, s =>
                {
                    s.ProteinGoalGrams = protein > 0 ? protein : null;
                    s.CarbGoalGrams = carbs > 0 ? carbs : null;
                    s.FatGoalGrams = fat > 0 ? fat : null;
                });
                _stateManager.Reset(userId);

                if (protein == 0 && carbs == 0 && fat == 0)
                    await _bot.SendMessage(message.Chat.Id, "Macro goals cleared.", cancellationToken: ct);
                else
                    await _bot.SendMessage(message.Chat.Id,
                        $"Macro goals set: P:{protein}g C:{carbs}g F:{fat}g\n(Priority: protein > carbs > fat)",
                        cancellationToken: ct);
            }
        }
    }

    public async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data ?? "";
        var userId = callback.From.Id;
        var chatId = callback.Message!.Chat.Id;

        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user?.Settings is null) return;

        if (data == "settings:strategy")
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    user.Settings.Strategy == SelectionStrategy.Default ? "Default \u2713" : "Default",
                    "settings:strategy:Default"),
                InlineKeyboardButton.WithCallbackData(
                    user.Settings.Strategy == SelectionStrategy.LowestCal ? "Lowest Cal \u2713" : "Lowest Cal",
                    "settings:strategy:LowestCal"),
                InlineKeyboardButton.WithCallbackData(
                    user.Settings.Strategy == SelectionStrategy.MacrosMax ? "Macros Max \u2713" : "Macros Max",
                    "settings:strategy:MacrosMax"),
            });
            await _bot.EditMessageReplyMarkup(chatId, callback.Message.MessageId, keyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("settings:strategy:"))
        {
            var strategyStr = data["settings:strategy:".Length..];
            if (Enum.TryParse<SelectionStrategy>(strategyStr, out var strategy))
            {
                await _userService.UpdateSettingsAsync(user.Id, s => s.Strategy = strategy);
                user.Settings.Strategy = strategy;
                await SendSettingsKeyboard(chatId, user.Settings, ct, callback.Message.MessageId);
            }
        }
        else if (data == "settings:stopwords")
        {
            var state = _stateManager.GetOrCreate(userId);
            state.CurrentFlow = ConversationFlow.Settings_WaitingStopWords;
            state.LastActivity = DateTime.UtcNow;
            await _bot.SendMessage(chatId, "Send stop words separated by commas (e.g., biryani, veg, paneer):", cancellationToken: ct);
        }
        else if (data == "settings:macros")
        {
            var state = _stateManager.GetOrCreate(userId);
            state.CurrentFlow = ConversationFlow.Settings_WaitingMacroGoals;
            state.LastActivity = DateTime.UtcNow;

            var current = user.Settings;
            var currentMsg = "Current goals: ";
            if (current.ProteinGoalGrams.HasValue || current.CarbGoalGrams.HasValue || current.FatGoalGrams.HasValue)
                currentMsg += $"P:{current.ProteinGoalGrams ?? 0}g C:{current.CarbGoalGrams ?? 0}g F:{current.FatGoalGrams ?? 0}g";
            else
                currentMsg += "not set";

            await _bot.SendMessage(chatId,
                $"{currentMsg}\n\nSend daily macro goals as: protein carbs fat\n" +
                "Example: 190 200 50\n(in grams, priority: protein > carbs > fat)\n\nSend 0 0 0 to clear goals.",
                cancellationToken: ct);
        }
        else if (data == "settings:history")
        {
            await _userService.UpdateSettingsAsync(user.Id, s => s.PreferHistory = !s.PreferHistory);
            user.Settings.PreferHistory = !user.Settings.PreferHistory;
            await SendSettingsKeyboard(chatId, user.Settings, ct, callback.Message.MessageId);
        }
        else if (data == "settings:reauth")
        {
            _stateManager.Reset(userId);
            var state = _stateManager.GetOrCreate(userId);
            state.CurrentFlow = ConversationFlow.Auth_WaitingEmail;
            state.LastActivity = DateTime.UtcNow;
            await _bot.SendMessage(chatId, "Enter your Delicut email:", cancellationToken: ct);
        }

        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
    }

    private async Task SendSettingsKeyboard(long chatId, UserSettings settings, CancellationToken ct, int? editMessageId = null)
    {
        var strategyLabel = settings.Strategy switch
        {
            SelectionStrategy.LowestCal => "Lowest Cal",
            SelectionStrategy.MacrosMax => "Macros Max",
            _ => "Default"
        };

        var stopWordsLabel = settings.StopWords.Count > 0
            ? $"Stop Words: {string.Join(", ", settings.StopWords)}"
            : "Stop Words: none";

        var macrosLabel = settings.ProteinGoalGrams.HasValue || settings.CarbGoalGrams.HasValue || settings.FatGoalGrams.HasValue
            ? $"Macro Goals: P:{settings.ProteinGoalGrams ?? 0}g C:{settings.CarbGoalGrams ?? 0}g F:{settings.FatGoalGrams ?? 0}g"
            : "Macro Goals: not set";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData($"Strategy: {strategyLabel}", "settings:strategy") },
            new[] { InlineKeyboardButton.WithCallbackData(macrosLabel, "settings:macros") },
            new[] { InlineKeyboardButton.WithCallbackData(stopWordsLabel, "settings:stopwords") },
            new[] { InlineKeyboardButton.WithCallbackData($"Prefer History: {(settings.PreferHistory ? "ON" : "OFF")}", "settings:history") },
            new[] { InlineKeyboardButton.WithCallbackData("Re-authenticate", "settings:reauth") },
        });

        if (editMessageId.HasValue)
        {
            await _bot.EditMessageReplyMarkup(chatId, editMessageId.Value, keyboard, cancellationToken: ct);
        }
        else
        {
            await _bot.SendMessage(chatId, "Settings:", replyMarkup: keyboard, cancellationToken: ct);
        }
    }
}
