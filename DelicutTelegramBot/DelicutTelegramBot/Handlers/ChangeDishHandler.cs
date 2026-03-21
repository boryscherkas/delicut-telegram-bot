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
        var proposal = (WeeklyProposal)state.FlowData["proposal"];

        if (data.StartsWith("change:day:"))
        {
            var dateStr = data["change:day:".Length..];
            var date = DateOnly.Parse(dateStr);
            var day = proposal.Days.FirstOrDefault(d => d.Date == date);

            if (day is null) return;

            state.CurrentFlow = ConversationFlow.Select_PickingDish;
            state.FlowData["selected_date"] = date;
            state.LastActivity = DateTime.UtcNow;

            // Show day dishes with macros
            var lines = new List<string> { $"Dishes for {day.DayOfWeek} ({day.Date:MMM dd}):", "" };
            foreach (var d in day.Dishes)
            {
                lines.Add($"  {d.SlotIndex + 1}. {d.DishName} ({d.ProteinOption})");
                lines.Add($"     {d.Kcal:F0} kcal | P:{d.Protein:F0} C:{d.Carb:F0} F:{d.Fat:F0}");
            }
            lines.Add($"\nDay total: {day.TotalKcal:F0} kcal | P:{day.TotalProtein:F0} C:{day.TotalCarb:F0} F:{day.TotalFat:F0}");
            lines.Add("\nTap a dish to change:");

            var buttons = day.Dishes.Select(d =>
                new[] { InlineKeyboardButton.WithCallbackData(
                    $"{d.SlotIndex + 1}. {d.DishName}",
                    $"change:dish:{date:yyyy-MM-dd}:{d.MealCategory}:{d.SlotIndex}") })
                .ToList();
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Back", "select:change") });

            var keyboard = new InlineKeyboardMarkup(buttons);
            await _bot.SendMessage(chatId, string.Join("\n", lines), replyMarkup: keyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("change:dish:"))
        {
            var parts = data["change:dish:".Length..].Split(':');
            var date = DateOnly.Parse(parts[0]);
            var mealCategory = parts[1];
            var slotIndex = int.Parse(parts[2]);

            state.CurrentFlow = ConversationFlow.Select_PickingReplacement;
            state.LastActivity = DateTime.UtcNow;

            // Get current dish for comparison
            var day = proposal.Days.FirstOrDefault(d => d.Date == date);
            var currentDish = day?.Dishes.FirstOrDefault(d => d.MealCategory == mealCategory && d.SlotIndex == slotIndex);

            var alternatives = await _menuService.GetAlternativesAsync(dbUserId, date, mealCategory, slotIndex);

            // Store alternatives in state — use short index in callback (Telegram 64-byte limit)
            state.FlowData["alternatives"] = alternatives;
            state.FlowData["alt_date"] = date;
            state.FlowData["alt_cat"] = mealCategory;
            state.FlowData["alt_slot"] = slotIndex;

            // Current day/week totals
            var curDayKcal = day?.TotalKcal ?? 0;
            var curDayP = day?.TotalProtein ?? 0;
            var curDayC = day?.TotalCarb ?? 0;
            var curDayF = day?.TotalFat ?? 0;
            var curWeekKcal = proposal.Days.Sum(d => d.TotalKcal);
            var curWeekP = proposal.Days.Sum(d => d.TotalProtein);

            // Show current dish + day/week context, then alternatives with projected totals
            var lines = new List<string>();
            if (currentDish != null)
            {
                lines.Add($"Current: {currentDish.DishName} ({currentDish.ProteinOption})");
                lines.Add($"  {currentDish.Kcal:F0} kcal | P:{currentDish.Protein:F0} C:{currentDish.Carb:F0} F:{currentDish.Fat:F0}");
            }
            lines.Add($"\nDay now: {curDayKcal:F0} kcal | P:{curDayP:F0} C:{curDayC:F0} F:{curDayF:F0}");
            lines.Add($"Week now: {curWeekKcal:F0} kcal | P:{curWeekP:F0}");
            lines.Add("\nIf you pick:");
            foreach (var a in alternatives)
            {
                var kcalDiff = a.Kcal - (currentDish?.Kcal ?? 0);
                var protDiff = a.Protein - (currentDish?.Protein ?? 0);
                var newDayKcal = curDayKcal + kcalDiff;
                var newDayP = curDayP + protDiff;
                var newWeekKcal = curWeekKcal + kcalDiff;
                var newWeekP = curWeekP + protDiff;
                lines.Add($"\n  {a.DishName} ({a.ProteinOption})");
                lines.Add($"  {a.Kcal:F0} kcal | P:{a.Protein:F0} C:{a.Carb:F0} F:{a.Fat:F0}");
                lines.Add($"  Day: {newDayKcal:F0} kcal P:{newDayP:F0} ({FormatDiff(kcalDiff)} kcal {FormatDiff(protDiff)}P)");
                lines.Add($"  Week: {newWeekKcal:F0} kcal P:{newWeekP:F0}");
            }

            var buttons = alternatives.Select((a, i) =>
                new[] { InlineKeyboardButton.WithCallbackData(
                    $"{a.DishName} ({a.ProteinOption})",
                    $"change:pick:{i}") })  // Short callback: index into stored alternatives
                .ToList();
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Keep Current", $"change:day:{date:yyyy-MM-dd}") });

            var keyboard = new InlineKeyboardMarkup(buttons);
            await _bot.SendMessage(chatId, string.Join("\n", lines), replyMarkup: keyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("change:pick:"))
        {
            // Resolve from stored alternatives
            var altIndex = int.Parse(data["change:pick:".Length..]);
            var alternatives = (List<DishAlternative>)state.FlowData["alternatives"];
            var date = (DateOnly)state.FlowData["alt_date"];
            var mealCategory = (string)state.FlowData["alt_cat"];
            var slotIndex = (int)state.FlowData["alt_slot"];

            if (altIndex < 0 || altIndex >= alternatives.Count) return;
            var picked = alternatives[altIndex];
            var newDishId = picked.DishId;
            var proteinOption = picked.ProteinOption;

            // Capture old day/week totals before replacement
            var day = proposal.Days.FirstOrDefault(d => d.Date == date);
            var oldDayKcal = day?.TotalKcal ?? 0;
            var oldDayProtein = day?.TotalProtein ?? 0;
            var oldDayCarb = day?.TotalCarb ?? 0;
            var oldDayFat = day?.TotalFat ?? 0;
            var oldWeekKcal = proposal.Days.Sum(d => d.TotalKcal);
            var oldWeekProtein = proposal.Days.Sum(d => d.TotalProtein);

            // Get old dish info
            var oldDish = day?.Dishes.FirstOrDefault(d => d.MealCategory == mealCategory && d.SlotIndex == slotIndex);

            await _menuService.ReplaceDishAsync(dbUserId, date, mealCategory, slotIndex, newDishId, proteinOption);

            // Update the proposal in state with new dish data from DB
            // Find the new dish info from cached menu
            var cacheKey = $"menu:{date}:{mealCategory.ToLower()}";
            var newDishName = newDishId;
            double newKcal = 0, newProtein = 0, newCarb = 0, newFat = 0;
            if (state.FlowData.TryGetValue(cacheKey, out var cached) && cached is List<Models.Delicut.Dish> menu)
            {
                var dish = menu.FirstOrDefault(d => d.Id == newDishId);
                var variant = dish?.Variants.FirstOrDefault(v =>
                    v.ProteinOption.Equals(proteinOption, StringComparison.OrdinalIgnoreCase));
                if (dish != null)
                {
                    newDishName = dish.DishName;
                    newKcal = variant?.Kcal ?? 0;
                    newProtein = variant?.Protein ?? 0;
                    newCarb = variant?.Carb ?? 0;
                    newFat = variant?.Fat ?? 0;
                }
            }

            // Update proposal in memory
            if (oldDish != null && day != null)
            {
                var idx = day.Dishes.IndexOf(oldDish);
                if (idx >= 0)
                {
                    day.Dishes[idx] = new ProposedDish
                    {
                        DishId = newDishId,
                        DishName = newDishName,
                        ProteinOption = proteinOption,
                        MealCategory = mealCategory,
                        SlotIndex = slotIndex,
                        Kcal = newKcal,
                        Protein = newProtein,
                        Carb = newCarb,
                        Fat = newFat
                    };
                }
            }

            // Compute new totals
            var newDayKcal = day?.TotalKcal ?? 0;
            var newDayProtein = day?.TotalProtein ?? 0;
            var newDayCarb = day?.TotalCarb ?? 0;
            var newDayFat = day?.TotalFat ?? 0;
            var newWeekKcal = proposal.Days.Sum(d => d.TotalKcal);
            var newWeekProtein = proposal.Days.Sum(d => d.TotalProtein);

            // Build comparison message
            var lines = new List<string>
            {
                $"Replaced: {oldDish?.DishName ?? "?"} -> {newDishName}",
                "",
                $"Day ({day?.DayOfWeek ?? date.ToString()}):",
                $"  Before: {oldDayKcal:F0} kcal | P:{oldDayProtein:F0} C:{oldDayCarb:F0} F:{oldDayFat:F0}",
                $"  After:  {newDayKcal:F0} kcal | P:{newDayProtein:F0} C:{newDayCarb:F0} F:{newDayFat:F0}",
                $"  Diff:   {FormatDiff(newDayKcal - oldDayKcal)} kcal | {FormatDiff(newDayProtein - oldDayProtein)}P {FormatDiff(newDayCarb - oldDayCarb)}C {FormatDiff(newDayFat - oldDayFat)}F",
                "",
                "Week total:",
                $"  Before: {oldWeekKcal:F0} kcal | P:{oldWeekProtein:F0}",
                $"  After:  {newWeekKcal:F0} kcal | P:{newWeekProtein:F0}",
                $"  Diff:   {FormatDiff(newWeekKcal - oldWeekKcal)} kcal | {FormatDiff(newWeekProtein - oldWeekProtein)}P",
            };

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Confirm Day", $"change:confirm:{date:yyyy-MM-dd}"),
                    InlineKeyboardButton.WithCallbackData("Change Another", $"change:day:{date:yyyy-MM-dd}")
                }
            });
            await _bot.SendMessage(chatId, string.Join("\n", lines), replyMarkup: keyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("change:confirm:"))
        {
            var dateStr = data["change:confirm:".Length..];
            var date = DateOnly.Parse(dateStr);
            await _menuService.ConfirmDayAsync(dbUserId, date);

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Submit All Confirmed", "select:submit_confirmed"),
                    InlineKeyboardButton.WithCallbackData("Change More Days", "select:change")
                }
            });
            await _bot.SendMessage(chatId, $"Day {date:MMM dd} confirmed! Submit to Delicut or keep changing?",
                replyMarkup: keyboard, cancellationToken: ct);
        }

        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
    }

    private static string FormatDiff(double diff) =>
        diff >= 0 ? $"+{diff:F0}" : $"{diff:F0}";
}
