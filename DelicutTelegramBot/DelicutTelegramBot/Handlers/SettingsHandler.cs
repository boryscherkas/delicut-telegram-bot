using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;
using DomainUser = DelicutTelegramBot.Models.Domain.User;

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

        switch (state.CurrentFlow)
        {
            case ConversationFlow.Settings_WaitingStopWords:
                await HandleStopWordsInputAsync(message, userId, ct);
                break;
            case ConversationFlow.Settings_WaitingMacroGoals:
                await HandleMacroGoalsInputAsync(message, userId, ct);
                break;
            case ConversationFlow.Settings_WaitingProteinVariant:
                await HandleProteinVariantInputAsync(message, userId, ct);
                break;
            case ConversationFlow.Settings_WaitingFavourites:
                await HandleFavouritesInputAsync(message, userId, ct);
                break;
        }
    }

    private async Task HandleStopWordsInputAsync(Message message, long userId, CancellationToken ct)
    {
        var input = message.Text?.Trim() ?? "";
        var stopWords = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user is null) return;

        await _userService.UpdateSettingsAsync(user.Id, s => s.StopWords = stopWords);
        _stateManager.Reset(userId);
        await _bot.SendMessage(message.Chat.Id,
            $"Stop words updated: {string.Join(", ", stopWords)}",
            cancellationToken: ct);
    }

    private async Task HandleMacroGoalsInputAsync(Message message, long userId, CancellationToken ct)
    {
        var input = message.Text?.Trim().ToLowerInvariant() ?? "";

        if (input is "clear" or "0")
        {
            await ClearMacroGoalsAsync(message.Chat.Id, userId, ct);
            return;
        }

        var (protein, carbs, fat, priority) = ParseMacroGoals(input);

        if (priority.Count == 0)
        {
            await _bot.SendMessage(message.Chat.Id,
                "Invalid format. Use: 190p 200c 50f\nOrder = priority (first = highest).\nSend 'clear' to remove.",
                cancellationToken: ct);
            return;
        }

        // Fill missing priorities at the end
        foreach (var m in new[] { "p", "c", "f" })
            if (!priority.Contains(m)) priority.Add(m);

        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user is null) return;

        await _userService.UpdateSettingsAsync(user.Id, s =>
        {
            s.ProteinGoalGrams = protein;
            s.CarbGoalGrams = carbs;
            s.FatGoalGrams = fat;
            s.MacroPriority = string.Join(",", priority);
        });
        _stateManager.Reset(userId);

        var priorityLabels = priority.Select(p => p switch
        {
            "p" => $"Protein {protein ?? 0}g",
            "c" => $"Carbs {carbs ?? 0}g",
            "f" => $"Fat {fat ?? 0}g",
            _ => p
        });
        await _bot.SendMessage(message.Chat.Id,
            $"Macro goals set!\nPriority: {string.Join(" > ", priorityLabels)}",
            cancellationToken: ct);
    }

    private async Task ClearMacroGoalsAsync(long chatId, long userId, CancellationToken ct)
    {
        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user is null) return;

        await _userService.UpdateSettingsAsync(user.Id, s =>
        {
            s.ProteinGoalGrams = null;
            s.CarbGoalGrams = null;
            s.FatGoalGrams = null;
            s.MacroPriority = "p,c,f";
        });
        _stateManager.Reset(userId);
        await _bot.SendMessage(chatId, "Macro goals cleared.", cancellationToken: ct);
    }

    private static (double? protein, double? carbs, double? fat, List<string> priority) ParseMacroGoals(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double? protein = null, carbs = null, fat = null;
        var priority = new List<string>();

        foreach (var part in parts)
        {
            if (part.EndsWith('p') && double.TryParse(part[..^1], out var pVal))
            {
                protein = pVal;
                priority.Add("p");
            }
            else if (part.EndsWith('c') && double.TryParse(part[..^1], out var cVal))
            {
                carbs = cVal;
                priority.Add("c");
            }
            else if (part.EndsWith('f') && double.TryParse(part[..^1], out var fVal))
            {
                fat = fVal;
                priority.Add("f");
            }
        }

        return (protein, carbs, fat, priority);
    }

    private async Task HandleProteinVariantInputAsync(Message message, long userId, CancellationToken ct)
    {
        var input = message.Text?.Trim() ?? "";
        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user is null) return;

        if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            await _userService.UpdateSettingsAsync(user.Id, s => s.PreferredProteinVariant = null);
            _stateManager.Reset(userId);
            await _bot.SendMessage(message.Chat.Id, "Protein preference cleared.", cancellationToken: ct);
            return;
        }

        await _userService.UpdateSettingsAsync(user.Id, s => s.PreferredProteinVariant = input);
        _stateManager.Reset(userId);
        await _bot.SendMessage(message.Chat.Id,
            $"Preferred protein set to: {input}\nThis variant will be chosen when available.",
            cancellationToken: ct);
    }

    private async Task HandleFavouritesInputAsync(Message message, long userId, CancellationToken ct)
    {
        var input = message.Text?.Trim() ?? "";
        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user is null) return;

        if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            await _userService.UpdateSettingsAsync(user.Id, s =>
            {
                s.FavouriteDishNames = [];
                s.MinFavouritesPerWeek = 0;
            });
            _stateManager.Reset(userId);
            await _bot.SendMessage(message.Chat.Id, "Favourites cleared.", cancellationToken: ct);
            return;
        }

        // Parse: "dish1, dish2 | min_count"
        var parts = input.Split('|');
        var dishNames = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var minCount = 1;
        if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var parsed))
            minCount = parsed;

        if (dishNames.Count == 0)
        {
            await _bot.SendMessage(message.Chat.Id,
                "Invalid format. Example: Fresh Poke Bowl, Teriyaki Noodles | 2",
                cancellationToken: ct);
            return;
        }

        await _userService.UpdateSettingsAsync(user.Id, s =>
        {
            s.FavouriteDishNames = dishNames;
            s.MinFavouritesPerWeek = minCount;
        });
        _stateManager.Reset(userId);
        await _bot.SendMessage(message.Chat.Id,
            $"Favourites set: {string.Join(", ", dishNames)} (min {minCount}x/week)",
            cancellationToken: ct);
    }

    public async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data ?? "";
        var userId = callback.From.Id;
        var chatId = callback.Message!.Chat.Id;

        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user?.Settings is null) return;

        if (data == "settings:strategy")
            await ShowStrategyPickerAsync(chatId, callback.Message.MessageId, user.Settings, ct);
        else if (data.StartsWith("settings:strategy:"))
            await HandleStrategyChangeAsync(chatId, callback.Message.MessageId, data, user, ct);
        else if (data == "settings:stopwords")
            await PromptForInputAsync(chatId, userId, ConversationFlow.Settings_WaitingStopWords,
                "Send stop words separated by commas (e.g., biryani, veg, paneer):", ct);
        else if (data == "settings:macros")
            await ShowMacroGoalsPromptAsync(chatId, userId, user.Settings, ct);
        else if (data == "settings:protein")
            await ShowProteinPromptAsync(chatId, userId, user.Settings, ct);
        else if (data == "settings:favourites")
            await ShowFavouritesPromptAsync(chatId, userId, user.Settings, ct);
        else if (data == "settings:ai_toggle")
            await ToggleBoolSettingAsync(chatId, callback.Message.MessageId, user,
                s => s.UseAiSelection, (s, v) => s.UseAiSelection = v, ct);
        else if (data == "settings:history")
            await ToggleBoolSettingAsync(chatId, callback.Message.MessageId, user,
                s => s.PreferHistory, (s, v) => s.PreferHistory = v, ct);
        else if (data == "settings:reauth")
            await HandleReauthAsync(chatId, userId, ct);

        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
    }

    private async Task ShowStrategyPickerAsync(long chatId, int messageId, UserSettings settings, CancellationToken ct)
    {
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithCallbackData(
                settings.Strategy == SelectionStrategy.Default ? "Default \u2713" : "Default",
                "settings:strategy:Default"),
            InlineKeyboardButton.WithCallbackData(
                settings.Strategy == SelectionStrategy.LowestCal ? "Lowest Cal \u2713" : "Lowest Cal",
                "settings:strategy:LowestCal"),
            InlineKeyboardButton.WithCallbackData(
                settings.Strategy == SelectionStrategy.MacrosMax ? "Macros Max \u2713" : "Macros Max",
                "settings:strategy:MacrosMax"),
        });
        await _bot.EditMessageReplyMarkup(chatId, messageId, keyboard, cancellationToken: ct);
    }

    private async Task HandleStrategyChangeAsync(long chatId, int messageId, string data, DomainUser user, CancellationToken ct)
    {
        var strategyStr = data["settings:strategy:".Length..];
        if (!Enum.TryParse<SelectionStrategy>(strategyStr, out var strategy)) return;

        await _userService.UpdateSettingsAsync(user.Id, s => s.Strategy = strategy);
        user.Settings!.Strategy = strategy;
        await SendSettingsKeyboard(chatId, user.Settings, ct, messageId);
    }

    private async Task PromptForInputAsync(long chatId, long userId, ConversationFlow flow, string prompt, CancellationToken ct)
    {
        var state = _stateManager.GetOrCreate(userId);
        state.CurrentFlow = flow;
        state.LastActivity = DateTime.UtcNow;
        await _bot.SendMessage(chatId, prompt, cancellationToken: ct);
    }

    private async Task ShowMacroGoalsPromptAsync(long chatId, long userId, UserSettings settings, CancellationToken ct)
    {
        var state = _stateManager.GetOrCreate(userId);
        state.CurrentFlow = ConversationFlow.Settings_WaitingMacroGoals;
        state.LastActivity = DateTime.UtcNow;

        var currentMsg = "Current goals: ";
        if (settings.ProteinGoalGrams.HasValue || settings.CarbGoalGrams.HasValue || settings.FatGoalGrams.HasValue)
            currentMsg += $"P:{settings.ProteinGoalGrams ?? 0}g C:{settings.CarbGoalGrams ?? 0}g F:{settings.FatGoalGrams ?? 0}g";
        else
            currentMsg += "not set";

        await _bot.SendMessage(chatId,
            $"{currentMsg}\nPriority: {settings.MacroPriority}\n\n" +
            "Send macro goals as: 190p 200c 50f\n" +
            "Order = priority (first = most important).\n" +
            "Examples:\n  190p 200c 50f  (protein first)\n  200c 190p 50f  (carbs first)\n  190p  (protein only)\n\n" +
            "Send 'clear' to remove goals.",
            cancellationToken: ct);
    }

    private async Task ShowProteinPromptAsync(long chatId, long userId, UserSettings settings, CancellationToken ct)
    {
        var state = _stateManager.GetOrCreate(userId);
        state.CurrentFlow = ConversationFlow.Settings_WaitingProteinVariant;
        state.LastActivity = DateTime.UtcNow;

        var current = settings.PreferredProteinVariant ?? "not set";
        await _bot.SendMessage(chatId,
            $"Current preferred protein: {current}\n\n" +
            "Send your preferred protein variant (e.g., Chicken, Shrimps, Beef, Tofu).\n" +
            "Send 'clear' to remove preference.",
            cancellationToken: ct);
    }

    private async Task ShowFavouritesPromptAsync(long chatId, long userId, UserSettings settings, CancellationToken ct)
    {
        var state = _stateManager.GetOrCreate(userId);
        state.CurrentFlow = ConversationFlow.Settings_WaitingFavourites;
        state.LastActivity = DateTime.UtcNow;

        var favs = settings.FavouriteDishNames.Count > 0
            ? string.Join(", ", settings.FavouriteDishNames)
            : "none";
        var min = settings.MinFavouritesPerWeek;
        await _bot.SendMessage(chatId,
            $"Current favourites: {favs} (min {min}x/week)\n\n" +
            "Send favourite dish names separated by commas, then a number for minimum per week.\n" +
            "Format: dish1, dish2 | min_count\n" +
            "Example: Fresh Poke Bowl, Teriyaki Noodles | 2\n\n" +
            "Send 'clear' to remove favourites.",
            cancellationToken: ct);
    }

    private async Task ToggleBoolSettingAsync(
        long chatId, int messageId, DomainUser user,
        Func<UserSettings, bool> getter, Action<UserSettings, bool> setter,
        CancellationToken ct)
    {
        var newValue = !getter(user.Settings!);
        await _userService.UpdateSettingsAsync(user.Id, s => setter(s, newValue));
        setter(user.Settings!, newValue);
        await SendSettingsKeyboard(chatId, user.Settings!, ct, messageId);
    }

    private async Task HandleReauthAsync(long chatId, long userId, CancellationToken ct)
    {
        _stateManager.Reset(userId);
        var state = _stateManager.GetOrCreate(userId);
        state.CurrentFlow = ConversationFlow.Auth_WaitingEmail;
        state.LastActivity = DateTime.UtcNow;
        await _bot.SendMessage(chatId, "Enter your Delicut email:", cancellationToken: ct);
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

        var macrosLabel = BuildMacrosLabel(settings);

        var proteinLabel = !string.IsNullOrEmpty(settings.PreferredProteinVariant)
            ? $"Preferred Protein: {settings.PreferredProteinVariant}"
            : "Preferred Protein: any";

        var favsLabel = settings.FavouriteDishNames.Count > 0
            ? $"Favourites: {settings.FavouriteDishNames.Count} dishes, min {settings.MinFavouritesPerWeek}x/wk"
            : "Favourites: none";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData($"Strategy: {strategyLabel}", "settings:strategy") },
            new[] { InlineKeyboardButton.WithCallbackData(macrosLabel, "settings:macros") },
            new[] { InlineKeyboardButton.WithCallbackData(proteinLabel, "settings:protein") },
            new[] { InlineKeyboardButton.WithCallbackData(favsLabel, "settings:favourites") },
            new[] { InlineKeyboardButton.WithCallbackData(stopWordsLabel, "settings:stopwords") },
            new[] { InlineKeyboardButton.WithCallbackData($"Selection: {(settings.UseAiSelection ? "AI (OpenAI)" : "Algorithm")}", "settings:ai_toggle") },
            new[] { InlineKeyboardButton.WithCallbackData($"Prefer History: {(settings.PreferHistory ? "ON" : "OFF")}", "settings:history") },
            new[] { InlineKeyboardButton.WithCallbackData("Re-authenticate", "settings:reauth") },
        });

        if (editMessageId.HasValue)
        {
            try
            {
                await _bot.EditMessageText(chatId, editMessageId.Value, "Settings:",
                    replyMarkup: keyboard, cancellationToken: ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
                when (ex.Message.Contains("message is not modified"))
            {
                // Ignore -- content didn't change visually
            }
        }
        else
        {
            await _bot.SendMessage(chatId, "Settings:", replyMarkup: keyboard, cancellationToken: ct);
        }
    }

    private static string BuildMacrosLabel(UserSettings settings)
    {
        if (!settings.ProteinGoalGrams.HasValue && !settings.CarbGoalGrams.HasValue && !settings.FatGoalGrams.HasValue)
            return "Macro Goals: not set";

        var parts = settings.MacroPriority.Split(',');
        var labels = parts.Select(p => p.Trim() switch
        {
            "p" => $"P:{settings.ProteinGoalGrams ?? 0}g",
            "c" => $"C:{settings.CarbGoalGrams ?? 0}g",
            "f" => $"F:{settings.FatGoalGrams ?? 0}g",
            _ => ""
        }).Where(s => s.Length > 0);
        return $"Macros: {string.Join(" > ", labels)}";
    }
}
