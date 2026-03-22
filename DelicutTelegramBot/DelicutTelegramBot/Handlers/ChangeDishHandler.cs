using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using DelicutTelegramBot.Helpers;
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

        if (!state.FlowData.TryGetValue("user_id", out var userIdObj) ||
            !state.FlowData.TryGetValue("proposal", out var proposalObj) ||
            userIdObj is not Guid dbUserId || proposalObj is not WeeklyProposal proposal)
        {
            await _bot.SendMessage(chatId, "Session expired. Please run /select again.", cancellationToken: ct);
            await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            return;
        }

        if (data.StartsWith("change:day:"))
            await HandleSelectDayAsync(chatId, data, state, proposal, ct);
        else if (data.StartsWith("change:dish:"))
            await HandleSelectDishAsync(chatId, data, state, dbUserId, proposal, ct);
        else if (data.StartsWith("change:pick:"))
            await HandlePickReplacementAsync(chatId, data, state, dbUserId, proposal, ct);
        else if (data.StartsWith("change:confirm:"))
            await HandleConfirmDayAsync(chatId, data, dbUserId, ct);

        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
    }

    private async Task HandleSelectDayAsync(
        long chatId, string data, ConversationState state,
        WeeklyProposal proposal, CancellationToken ct)
    {
        var dateStr = data["change:day:".Length..];
        var date = DateOnly.Parse(dateStr);
        var day = proposal.Days.FirstOrDefault(d => d.Date == date);
        if (day is null) return;

        state.CurrentFlow = ConversationFlow.Select_PickingDish;
        state.FlowData["selected_date"] = date;
        state.LastActivity = DateTime.UtcNow;

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
        buttons.Add([InlineKeyboardButton.WithCallbackData("Back", "select:change")]);

        await _bot.SendMessage(chatId, string.Join("\n", lines),
            replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
    }

    private async Task HandleSelectDishAsync(
        long chatId, string data, ConversationState state,
        Guid dbUserId, WeeklyProposal proposal, CancellationToken ct)
    {
        var parts = data["change:dish:".Length..].Split(':');
        var date = DateOnly.Parse(parts[0]);
        var mealCategory = parts[1];
        var slotIndex = int.Parse(parts[2]);

        state.CurrentFlow = ConversationFlow.Select_PickingReplacement;
        state.LastActivity = DateTime.UtcNow;

        var day = proposal.Days.FirstOrDefault(d => d.Date == date);
        var currentDish = day?.Dishes.FirstOrDefault(d => d.MealCategory == mealCategory && d.SlotIndex == slotIndex);
        var alternatives = await _menuService.GetAlternativesAsync(dbUserId, date, mealCategory, slotIndex);

        state.FlowData["alternatives"] = alternatives;
        state.FlowData["alt_date"] = date;
        state.FlowData["alt_cat"] = mealCategory;
        state.FlowData["alt_slot"] = slotIndex;

        var lines = BuildAlternativesMessage(currentDish, alternatives, day, proposal);

        var buttons = alternatives.Select((a, i) =>
            new[] { InlineKeyboardButton.WithCallbackData(
                $"{a.DishName} ({a.ProteinOption})",
                $"change:pick:{i}") })
            .ToList();
        buttons.Add([InlineKeyboardButton.WithCallbackData("Keep Current", $"change:day:{date:yyyy-MM-dd}")]);

        await _bot.SendMessage(chatId, string.Join("\n", lines),
            replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
    }

    private static List<string> BuildAlternativesMessage(
        ProposedDish? currentDish, List<DishAlternative> alternatives,
        DayProposal? day, WeeklyProposal proposal)
    {
        var curDayKcal = day?.TotalKcal ?? 0;
        var curDayP = day?.TotalProtein ?? 0;
        var curDayC = day?.TotalCarb ?? 0;
        var curDayF = day?.TotalFat ?? 0;
        var curWeekKcal = proposal.Days.Sum(d => d.TotalKcal);
        var curWeekP = proposal.Days.Sum(d => d.TotalProtein);

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
            lines.Add($"  Day: {newDayKcal:F0} kcal P:{newDayP:F0} ({TelegramFormatHelper.FormatDiff(kcalDiff)} kcal {TelegramFormatHelper.FormatDiff(protDiff)}P)");
            lines.Add($"  Week: {newWeekKcal:F0} kcal P:{newWeekP:F0}");
        }

        return lines;
    }

    private async Task HandlePickReplacementAsync(
        long chatId, string data, ConversationState state,
        Guid dbUserId, WeeklyProposal proposal, CancellationToken ct)
    {
        var altIndex = int.Parse(data["change:pick:".Length..]);
        var alternatives = (List<DishAlternative>)state.FlowData["alternatives"];
        var date = (DateOnly)state.FlowData["alt_date"];
        var mealCategory = (string)state.FlowData["alt_cat"];
        var slotIndex = (int)state.FlowData["alt_slot"];

        if (altIndex < 0 || altIndex >= alternatives.Count) return;
        var picked = alternatives[altIndex];

        var day = proposal.Days.FirstOrDefault(d => d.Date == date);
        var oldDish = day?.Dishes.FirstOrDefault(d => d.MealCategory == mealCategory && d.SlotIndex == slotIndex);

        // Capture totals before replacement
        var oldDayKcal = day?.TotalKcal ?? 0;
        var oldDayProtein = day?.TotalProtein ?? 0;
        var oldDayCarb = day?.TotalCarb ?? 0;
        var oldDayFat = day?.TotalFat ?? 0;
        var oldWeekKcal = proposal.Days.Sum(d => d.TotalKcal);
        var oldWeekProtein = proposal.Days.Sum(d => d.TotalProtein);

        await _menuService.ReplaceDishAsync(dbUserId, date, mealCategory, slotIndex, picked.DishId, picked.ProteinOption);

        // Update proposal in memory from cached menu
        UpdateProposalInMemory(state, day, oldDish, picked, date, mealCategory, slotIndex);

        // Compute new totals and build comparison message
        var lines = BuildReplacementSummary(
            oldDish, day, proposal, date,
            oldDayKcal, oldDayProtein, oldDayCarb, oldDayFat,
            oldWeekKcal, oldWeekProtein);

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Confirm Day", $"change:confirm:{date:yyyy-MM-dd}"),
                InlineKeyboardButton.WithCallbackData("Change Another", $"change:day:{date:yyyy-MM-dd}")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Show Full Week", "select:show_week")
            }
        });
        await _bot.SendMessage(chatId, string.Join("\n", lines), replyMarkup: keyboard, cancellationToken: ct);
    }

    private static void UpdateProposalInMemory(
        ConversationState state, DayProposal? day, ProposedDish? oldDish,
        DishAlternative picked, DateOnly date, string mealCategory, int slotIndex)
    {
        var cacheKey = $"menu:{date}:{mealCategory.ToLower()}";
        var newDishName = picked.DishId;
        double newKcal = 0, newProtein = 0, newCarb = 0, newFat = 0;

        if (state.FlowData.TryGetValue(cacheKey, out var cached) && cached is List<Models.Delicut.Dish> menu)
        {
            var dish = menu.FirstOrDefault(d => d.Id == picked.DishId);
            var variant = dish?.Variants.FirstOrDefault(v =>
                v.ProteinOption.Equals(picked.ProteinOption, StringComparison.OrdinalIgnoreCase));
            if (dish != null)
            {
                newDishName = dish.DishName;
                newKcal = variant?.Kcal ?? 0;
                newProtein = variant?.Protein ?? 0;
                newCarb = variant?.Carb ?? 0;
                newFat = variant?.Fat ?? 0;
            }
        }

        if (oldDish == null || day == null) return;

        var idx = day.Dishes.IndexOf(oldDish);
        if (idx < 0) return;

        day.Dishes[idx] = new ProposedDish
        {
            DishId = picked.DishId,
            DishName = newDishName,
            ProteinOption = picked.ProteinOption,
            MealCategory = mealCategory,
            SlotIndex = slotIndex,
            Kcal = newKcal,
            Protein = newProtein,
            Carb = newCarb,
            Fat = newFat
        };
    }

    private static List<string> BuildReplacementSummary(
        ProposedDish? oldDish, DayProposal? day, WeeklyProposal proposal,
        DateOnly date, double oldDayKcal, double oldDayProtein, double oldDayCarb,
        double oldDayFat, double oldWeekKcal, double oldWeekProtein)
    {
        var newDayKcal = day?.TotalKcal ?? 0;
        var newDayProtein = day?.TotalProtein ?? 0;
        var newDayCarb = day?.TotalCarb ?? 0;
        var newDayFat = day?.TotalFat ?? 0;
        var newWeekKcal = proposal.Days.Sum(d => d.TotalKcal);
        var newWeekProtein = proposal.Days.Sum(d => d.TotalProtein);

        // The new dish name is the current dish at that slot (already updated in memory)
        var newDishName = day?.Dishes.FirstOrDefault(d => d.SlotIndex == oldDish?.SlotIndex)?.DishName ?? "?";

        return
        [
            $"Replaced: {oldDish?.DishName ?? "?"} -> {newDishName}",
            "",
            $"Day ({day?.DayOfWeek ?? date.ToString()}):",
            $"  Before: {oldDayKcal:F0} kcal | P:{oldDayProtein:F0} C:{oldDayCarb:F0} F:{oldDayFat:F0}",
            $"  After:  {newDayKcal:F0} kcal | P:{newDayProtein:F0} C:{newDayCarb:F0} F:{newDayFat:F0}",
            $"  Diff:   {TelegramFormatHelper.FormatDiff(newDayKcal - oldDayKcal)} kcal | {TelegramFormatHelper.FormatDiff(newDayProtein - oldDayProtein)}P {TelegramFormatHelper.FormatDiff(newDayCarb - oldDayCarb)}C {TelegramFormatHelper.FormatDiff(newDayFat - oldDayFat)}F",
            "",
            "Week total:",
            $"  Before: {oldWeekKcal:F0} kcal | P:{oldWeekProtein:F0}",
            $"  After:  {newWeekKcal:F0} kcal | P:{newWeekProtein:F0}",
            $"  Diff:   {TelegramFormatHelper.FormatDiff(newWeekKcal - oldWeekKcal)} kcal | {TelegramFormatHelper.FormatDiff(newWeekProtein - oldWeekProtein)}P",
        ];
    }

    private async Task HandleConfirmDayAsync(
        long chatId, string data, Guid dbUserId, CancellationToken ct)
    {
        var dateStr = data["change:confirm:".Length..];
        var date = DateOnly.Parse(dateStr);

        try
        {
            await _menuService.SubmitDayAsync(dbUserId, date);
            await _bot.SendMessage(chatId, $"Day {date:MMM dd} submitted to Delicut!",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await _bot.SendMessage(chatId, $"Failed to submit {date:MMM dd}: {ex.Message}",
                cancellationToken: ct);
        }

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Change More Days", "select:change"),
                InlineKeyboardButton.WithCallbackData("Show Full Week", "select:show_week")
            }
        });
        await _bot.SendMessage(chatId, "Continue?", replyMarkup: keyboard, cancellationToken: ct);
    }
}
