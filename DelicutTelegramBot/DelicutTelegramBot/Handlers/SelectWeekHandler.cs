using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using DelicutTelegramBot.Helpers;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;
using DelicutTelegramBot.Models.Domain;
using Microsoft.Extensions.Logging;

namespace DelicutTelegramBot.Handlers;

public class SelectWeekHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly IMenuSelectionService _menuService;
    private readonly IUserService _userService;
    private readonly ConversationStateManager _stateManager;
    private readonly ILogger<SelectWeekHandler> _logger;

    public SelectWeekHandler(
        ITelegramBotClient bot,
        IMenuSelectionService menuService,
        IUserService userService,
        ConversationStateManager stateManager,
        ILogger<SelectWeekHandler> logger)
    {
        _bot = bot;
        _menuService = menuService;
        _userService = userService;
        _stateManager = stateManager;
        _logger = logger;
    }

    public async Task HandleCommandAsync(Message message, CancellationToken ct)
    {
        var user = await _userService.GetByTelegramIdAsync(message.From!.Id);
        if (user is null || string.IsNullOrEmpty(user.DelicutToken))
        {
            await _bot.SendMessage(message.Chat.Id, "Please authenticate first with /start.", cancellationToken: ct);
            return;
        }

        await _bot.SendMessage(message.Chat.Id, "Selecting dishes for the week... This may take a moment.", cancellationToken: ct);

        var proposal = await _menuService.SelectForWeekAsync(user.Id);

        var state = _stateManager.GetOrCreate(message.From.Id);
        state.CurrentFlow = ConversationFlow.Select_ReviewingWeek;
        state.FlowData["proposal"] = proposal;
        state.FlowData["user_id"] = user.Id;
        state.LastActivity = DateTime.UtcNow;

        var text = FormatWeekOverview(proposal,
            user.Settings?.ProteinGoalGrams,
            user.Settings?.CarbGoalGrams,
            user.Settings?.FatGoalGrams);

        await KeyboardBuilder.SendOrSplitMessageAsync(_bot, message.Chat.Id, text,
            KeyboardBuilder.WeekOverviewKeyboard(), ct);
    }

    public async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var data = callback.Data ?? "";
        var chatId = callback.Message!.Chat.Id;
        var userId = callback.From.Id;
        var state = _stateManager.GetOrCreate(userId);

        if (!state.FlowData.ContainsKey("proposal"))
        {
            await _bot.SendMessage(chatId, "Session expired. Please run /select again.", cancellationToken: ct);
            await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            return;
        }

        if (data == "select:approve_all")
            await HandleApproveAllAsync(chatId, userId, state, ct);
        else if (data == "select:submit_confirmed")
            await HandleSubmitConfirmedAsync(chatId, userId, state, ct);
        else if (data == "select:regenerate")
            await HandleRegenerateAsync(chatId, userId, state, ct);
        else if (data == "select:approve_day")
            await HandleApproveDayPickerAsync(chatId, state, ct);
        else if (data.StartsWith("select:submit_day:"))
            await HandleSubmitDayAsync(chatId, data, state, ct);
        else if (data == "select:show_week")
            await HandleShowWeekAsync(chatId, userId, state, ct);
        else if (data == "select:change")
            await HandleChangeAsync(chatId, userId, state, ct);

        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
    }

    private async Task HandleApproveAllAsync(long chatId, long userId, ConversationState state, CancellationToken ct)
    {
        var dbUserId = (Guid)state.FlowData["user_id"];
        var proposal = (WeeklyProposal)state.FlowData["proposal"];

        var failed = new List<string>();
        foreach (var day in proposal.Days)
        {
            try
            {
                await _menuService.SubmitDayAsync(dbUserId, day.Date);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit {Date}", day.Date);
                failed.Add(day.Date.ToString("MMM dd"));
            }
        }

        _stateManager.Reset(userId);
        if (failed.Count == 0)
            await _bot.SendMessage(chatId, "All dishes submitted to Delicut!", cancellationToken: ct);
        else
            await _bot.SendMessage(chatId, $"Submitted with errors. Failed days: {string.Join(", ", failed)}", cancellationToken: ct);
    }

    private async Task HandleSubmitConfirmedAsync(long chatId, long userId, ConversationState state, CancellationToken ct)
    {
        var dbUserId = (Guid)state.FlowData["user_id"];
        var proposal = (WeeklyProposal)state.FlowData["proposal"];

        var failed = new List<string>();
        foreach (var day in proposal.Days)
        {
            try
            {
                await _menuService.SubmitDayAsync(dbUserId, day.Date);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit {Date}", day.Date);
                failed.Add(day.Date.ToString("MMM dd"));
            }
        }

        _stateManager.Reset(userId);
        if (failed.Count == 0)
            await _bot.SendMessage(chatId, "All confirmed dishes submitted to Delicut!", cancellationToken: ct);
        else
            await _bot.SendMessage(chatId, $"Submitted with errors. Failed: {string.Join(", ", failed)}", cancellationToken: ct);
    }

    private async Task HandleRegenerateAsync(long chatId, long userId, ConversationState state, CancellationToken ct)
    {
        _stateManager.Reset(userId);
        await _bot.SendMessage(chatId, "Regenerating with different dishes...", cancellationToken: ct);

        var user = await _userService.GetByTelegramIdAsync(userId);
        if (user is null) return;

        var proposal = await _menuService.SelectForWeekAsync(user.Id, regenerate: true);

        var newState = _stateManager.GetOrCreate(userId);
        newState.CurrentFlow = ConversationFlow.Select_ReviewingWeek;
        newState.FlowData["proposal"] = proposal;
        newState.FlowData["user_id"] = user.Id;
        newState.LastActivity = DateTime.UtcNow;

        var text = FormatWeekOverview(proposal,
            user.Settings?.ProteinGoalGrams,
            user.Settings?.CarbGoalGrams,
            user.Settings?.FatGoalGrams);

        await KeyboardBuilder.SendOrSplitMessageAsync(_bot, chatId, text,
            KeyboardBuilder.WeekOverviewCompactKeyboard(), ct);
    }

    private async Task HandleApproveDayPickerAsync(long chatId, ConversationState state, CancellationToken ct)
    {
        var proposal = (WeeklyProposal)state.FlowData["proposal"];
        var buttons = proposal.Days.Select(d =>
            InlineKeyboardButton.WithCallbackData(
                $"{d.DayOfWeek[..3]} ({d.Date:MMM dd})", $"select:submit_day:{d.Date:yyyy-MM-dd}"))
            .ToArray();
        var dayKeyboard = new InlineKeyboardMarkup(buttons.Chunk(3));
        await _bot.SendMessage(chatId, "Which day to submit?", replyMarkup: dayKeyboard, cancellationToken: ct);
    }

    private async Task HandleSubmitDayAsync(long chatId, string data, ConversationState state, CancellationToken ct)
    {
        var dateStr = data["select:submit_day:".Length..];
        var date = DateOnly.Parse(dateStr);
        var dbUserId = (Guid)state.FlowData["user_id"];

        try
        {
            await _menuService.SubmitDayAsync(dbUserId, date);
            await _bot.SendMessage(chatId, $"{date:ddd MMM dd} submitted to Delicut!", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit day {Date}", date);
            await _bot.SendMessage(chatId, $"Failed to submit {date:MMM dd}: {ex.Message}", cancellationToken: ct);
        }
    }

    private async Task HandleShowWeekAsync(long chatId, long userId, ConversationState state, CancellationToken ct)
    {
        var proposal = (WeeklyProposal)state.FlowData["proposal"];
        var user = await _userService.GetByTelegramIdAsync(userId);
        var weekText = FormatWeekOverview(proposal,
            user?.Settings?.ProteinGoalGrams, user?.Settings?.CarbGoalGrams, user?.Settings?.FatGoalGrams);

        await KeyboardBuilder.SendOrSplitMessageAsync(_bot, chatId, weekText,
            KeyboardBuilder.WeekOverviewCompactKeyboard(), ct);
    }

    private async Task HandleChangeAsync(long chatId, long userId, ConversationState state, CancellationToken ct)
    {
        state.CurrentFlow = ConversationFlow.Select_PickingDay;
        state.LastActivity = DateTime.UtcNow;

        var proposal = (WeeklyProposal)state.FlowData["proposal"];
        var buttons = proposal.Days.Select(d =>
            InlineKeyboardButton.WithCallbackData(
                d.DayOfWeek[..3], $"change:day:{d.Date:yyyy-MM-dd}"))
            .ToArray();

        var keyboard = new InlineKeyboardMarkup(buttons.Chunk(3));
        await _bot.SendMessage(chatId, "Which day do you want to change?", replyMarkup: keyboard, cancellationToken: ct);
    }

    private static string FormatWeekOverview(WeeklyProposal proposal,
        double? proteinGoal = null, double? carbGoal = null, double? fatGoal = null)
    {
        var lines = new List<string>();
        var hasGoals = proteinGoal.HasValue || carbGoal.HasValue || fatGoal.HasValue;

        if (hasGoals)
        {
            lines.Add($"Daily goals: P:{proteinGoal ?? 0}g C:{carbGoal ?? 0}g F:{fatGoal ?? 0}g");
            lines.Add("");
        }

        if (proposal.LockedDays.Count > 0)
            lines.Add($"Locked days (past cutoff): {string.Join(", ", proposal.LockedDays)}\n");

        foreach (var day in proposal.Days)
        {
            FormatDayOverview(lines, day, hasGoals, proteinGoal, carbGoal, fatGoal);
            lines.Add("");
        }

        if (proposal.Days.Count > 0)
            FormatWeekSummary(lines, proposal, hasGoals, proteinGoal, carbGoal, fatGoal);

        return string.Join("\n", lines);
    }

    private static void FormatDayOverview(List<string> lines, DayProposal day,
        bool hasGoals, double? proteinGoal, double? carbGoal, double? fatGoal)
    {
        lines.Add($"📅 {day.DayOfWeek} ({day.Date:MMM dd}):");
        foreach (var dish in day.Dishes)
        {
            var emoji = dish.MealCategory.ToLower() switch
            {
                "meal" => "🍽",
                "breakfast" => "🥣",
                "snack" => "🍎",
                _ => "🍽"
            };
            lines.Add($"  {emoji} {dish.SlotIndex + 1}. {dish.DishName} ({dish.ProteinOption}) \u2014 {dish.Kcal:F0} kcal | P:{dish.Protein:F0} C:{dish.Carb:F0} F:{dish.Fat:F0}");
        }

        var dayTotal = $"  {day.TotalKcal:F0} kcal | P:{day.TotalProtein:F0} C:{day.TotalCarb:F0} F:{day.TotalFat:F0}";
        if (hasGoals)
        {
            var pDiff = day.TotalProtein - (proteinGoal ?? 0);
            var cDiff = day.TotalCarb - (carbGoal ?? 0);
            var fDiff = day.TotalFat - (fatGoal ?? 0);
            dayTotal += $" (goal: {TelegramFormatHelper.FormatDiff(pDiff)}P {TelegramFormatHelper.FormatDiff(cDiff)}C {TelegramFormatHelper.FormatDiff(fDiff)}F)";
        }
        lines.Add(dayTotal);
    }

    private static void FormatWeekSummary(List<string> lines, WeeklyProposal proposal,
        bool hasGoals, double? proteinGoal, double? carbGoal, double? fatGoal)
    {
        var hasOriginal = proposal.Days.Any(d => d.OriginalKcal > 0);
        if (hasOriginal)
        {
            var origAvgP = proposal.Days.Average(d => d.OriginalProtein);
            var origAvgC = proposal.Days.Average(d => d.OriginalCarb);
            var origAvgF = proposal.Days.Average(d => d.OriginalFat);
            var origAvgK = proposal.Days.Average(d => d.OriginalKcal);
            lines.Add($"Week avg WAS: {origAvgK:F0} kcal | P:{origAvgP:F0} C:{origAvgC:F0} F:{origAvgF:F0}");
        }
        var avgKcal = proposal.Days.Average(d => d.TotalKcal);
        var avgP = proposal.Days.Average(d => d.TotalProtein);
        var avgC = proposal.Days.Average(d => d.TotalCarb);
        var avgF = proposal.Days.Average(d => d.TotalFat);
        lines.Add($"Week avg NOW: {avgKcal:F0} kcal | P:{avgP:F0} C:{avgC:F0} F:{avgF:F0}");
        if (hasGoals)
        {
            lines.Add($"vs Goal:      {TelegramFormatHelper.FormatDiff(avgP - (proteinGoal ?? 0))}P {TelegramFormatHelper.FormatDiff(avgC - (carbGoal ?? 0))}C {TelegramFormatHelper.FormatDiff(avgF - (fatGoal ?? 0))}F");
        }
    }
}
