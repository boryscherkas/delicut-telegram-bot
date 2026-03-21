using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;

namespace DelicutTelegramBot.Handlers;

public class ChangeDishHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly IMenuSelectionService _menuService;
    private readonly ConversationStateManager _stateManager;

    public ChangeDishHandler(
        ITelegramBotClient bot,
        IMenuSelectionService menuService,
        ConversationStateManager stateManager)
    {
        _bot = bot;
        _menuService = menuService;
        _stateManager = stateManager;
    }

    public async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data ?? "";
        var chatId = callback.Message!.Chat.Id;
        var userId = callback.From.Id;
        var state = _stateManager.GetOrCreate(userId);
        var dbUserId = (Guid)state.FlowData["user_id"];

        if (data.StartsWith("change:day:"))
        {
            var dateStr = data["change:day:".Length..];
            var date = DateOnly.Parse(dateStr);
            var proposal = (WeeklyProposal)state.FlowData["proposal"];
            var day = proposal.Days.FirstOrDefault(d => d.Date == date);

            if (day is null) return;

            state.CurrentFlow = ConversationFlow.Select_PickingDish;
            state.FlowData["selected_date"] = date;
            state.LastActivity = DateTime.UtcNow;

            var buttons = day.Dishes.Select(d =>
                new[] { InlineKeyboardButton.WithCallbackData(
                    $"{d.DishName} ({d.ProteinOption})",
                    $"change:dish:{date:yyyy-MM-dd}:{d.MealCategory}:{d.SlotIndex}") })
                .ToList();
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Back", "select:change") });

            var keyboard = new InlineKeyboardMarkup(buttons);
            await _bot.SendMessage(chatId, $"Dishes for {day.DayOfWeek} \u2014 tap to change:", replyMarkup: keyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("change:dish:"))
        {
            var parts = data["change:dish:".Length..].Split(':');
            var date = DateOnly.Parse(parts[0]);
            var mealCategory = parts[1];
            var slotIndex = int.Parse(parts[2]);

            state.CurrentFlow = ConversationFlow.Select_PickingReplacement;
            state.LastActivity = DateTime.UtcNow;

            var alternatives = await _menuService.GetAlternativesAsync(dbUserId, date, mealCategory, slotIndex);

            var buttons = alternatives.Select(a =>
                new[] { InlineKeyboardButton.WithCallbackData(
                    $"{a.DishName} ({a.ProteinOption}) \u2014 {a.Kcal:F0} kcal",
                    $"change:replace:{date:yyyy-MM-dd}:{mealCategory}:{slotIndex}:{a.DishId}:{a.ProteinOption}") })
                .ToList();
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Keep Current", $"change:day:{date:yyyy-MM-dd}") });

            var keyboard = new InlineKeyboardMarkup(buttons);
            await _bot.SendMessage(chatId, "Pick a replacement:", replyMarkup: keyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("change:replace:"))
        {
            var parts = data["change:replace:".Length..].Split(':');
            var date = DateOnly.Parse(parts[0]);
            var mealCategory = parts[1];
            var slotIndex = int.Parse(parts[2]);
            var newDishId = parts[3];
            var proteinOption = parts[4];

            await _menuService.ReplaceDishAsync(dbUserId, date, mealCategory, slotIndex, newDishId, proteinOption);

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Confirm Day", $"change:confirm:{date:yyyy-MM-dd}"),
                    InlineKeyboardButton.WithCallbackData("Change Another", $"change:day:{date:yyyy-MM-dd}")
                }
            });
            await _bot.SendMessage(chatId, "Dish replaced!", replyMarkup: keyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("change:confirm:"))
        {
            var dateStr = data["change:confirm:".Length..];
            var date = DateOnly.Parse(dateStr);
            await _menuService.ConfirmDayAsync(dbUserId, date);
            await _bot.SendMessage(chatId, $"Day {date:MMM dd} confirmed!", cancellationToken: ct);
        }

        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
    }
}
