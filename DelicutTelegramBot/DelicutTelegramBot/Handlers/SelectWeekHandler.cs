using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
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

        var keyboard = new InlineKeyboardMarkup(new[]
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

        // Telegram has a 4096 char limit — split if needed
        if (text.Length <= 4096)
        {
            await _bot.SendMessage(message.Chat.Id, text, replyMarkup: keyboard, cancellationToken: ct);
        }
        else
        {
            // Send overview in chunks, keyboard on last message
            var chunks = SplitMessage(text, 4096);
            for (int i = 0; i < chunks.Count - 1; i++)
                await _bot.SendMessage(message.Chat.Id, chunks[i], cancellationToken: ct);
            await _bot.SendMessage(message.Chat.Id, chunks[^1], replyMarkup: keyboard, cancellationToken: ct);
        }
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
        {
            var dbUserId = (Guid)state.FlowData["user_id"];
            var proposal = (WeeklyProposal)state.FlowData["proposal"];

            foreach (var day in proposal.Days)
                await _menuService.ConfirmDayAsync(dbUserId, day.Date);

            try
            {
                await _menuService.ConfirmWeekAsync(dbUserId);
                _stateManager.Reset(userId);
                await _bot.SendMessage(chatId, "All dishes confirmed and submitted!", cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit week for user {UserId}", dbUserId);
                await _bot.SendMessage(chatId, $"Some days failed to submit. Try /select again.\n{ex.Message}", cancellationToken: ct);
            }
        }
        else if (data == "select:submit_confirmed")
        {
            // Submit only days that have been explicitly confirmed by the user
            var dbUserId = (Guid)state.FlowData["user_id"];
            try
            {
                await _menuService.ConfirmWeekAsync(dbUserId);
                _stateManager.Reset(userId);
                await _bot.SendMessage(chatId, "Confirmed dishes submitted to Delicut!", cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit confirmed days for user {UserId}", dbUserId);
                await _bot.SendMessage(chatId, $"Some days failed to submit. Try again.\n{ex.Message}", cancellationToken: ct);
            }
        }
        else if (data == "select:regenerate")
        {
            var dbUserId = (Guid)state.FlowData["user_id"];
            _stateManager.Reset(userId);
            await _bot.SendMessage(chatId, "Regenerating with different dishes...", cancellationToken: ct);

            // Re-run with randomness
            var user = await _userService.GetByTelegramIdAsync(userId);
            if (user is null) return;

            var proposal = await _menuService.SelectForWeekAsync(user.Id, regenerate: true);

            var newState = _stateManager.GetOrCreate(userId);
            newState.CurrentFlow = ConversationFlow.Select_ReviewingWeek;
            newState.FlowData["proposal"] = proposal;
            newState.FlowData["user_id"] = user.Id;
            newState.LastActivity = DateTime.UtcNow;

            var newText = FormatWeekOverview(proposal,
                user.Settings?.ProteinGoalGrams,
                user.Settings?.CarbGoalGrams,
                user.Settings?.FatGoalGrams);

            var newKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Approve All", "select:approve_all"),
                    InlineKeyboardButton.WithCallbackData("Change Dishes", "select:change")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Regenerate", "select:regenerate")
                }
            });

            if (newText.Length <= 4096)
            {
                await _bot.SendMessage(chatId, newText, replyMarkup: newKeyboard, cancellationToken: ct);
            }
            else
            {
                var chunks = SplitMessage(newText, 4096);
                for (int i = 0; i < chunks.Count - 1; i++)
                    await _bot.SendMessage(chatId, chunks[i], cancellationToken: ct);
                await _bot.SendMessage(chatId, chunks[^1], replyMarkup: newKeyboard, cancellationToken: ct);
            }
        }
        else if (data == "select:approve_day")
        {
            // Show day picker for per-day approval
            var proposal = (WeeklyProposal)state.FlowData["proposal"];
            var buttons = proposal.Days.Select(d =>
                InlineKeyboardButton.WithCallbackData(
                    $"{d.DayOfWeek[..3]} ({d.Date:MMM dd})", $"select:submit_day:{d.Date:yyyy-MM-dd}"))
                .ToArray();
            var dayKeyboard = new InlineKeyboardMarkup(buttons.Chunk(3));
            await _bot.SendMessage(chatId, "Which day to submit?", replyMarkup: dayKeyboard, cancellationToken: ct);
        }
        else if (data.StartsWith("select:submit_day:"))
        {
            var dateStr = data["select:submit_day:".Length..];
            var date = DateOnly.Parse(dateStr);
            var dbUserId = (Guid)state.FlowData["user_id"];

            await _menuService.ConfirmDayAsync(dbUserId, date);
            try
            {
                await _menuService.ConfirmWeekAsync(dbUserId); // Submits only Confirmed days
                await _bot.SendMessage(chatId, $"{date:ddd MMM dd} submitted to Delicut!", cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit day {Date}", date);
                await _bot.SendMessage(chatId, $"Failed to submit {date:MMM dd}: {ex.Message}", cancellationToken: ct);
            }
        }
        else if (data == "select:show_week")
        {
            var proposal = (WeeklyProposal)state.FlowData["proposal"];
            var user = await _userService.GetByTelegramIdAsync(userId);
            var weekText = FormatWeekOverview(proposal,
                user?.Settings?.ProteinGoalGrams, user?.Settings?.CarbGoalGrams, user?.Settings?.FatGoalGrams);

            var weekKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Approve All", "select:approve_all"),
                    InlineKeyboardButton.WithCallbackData("Change Dishes", "select:change")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Regenerate", "select:regenerate")
                }
            });

            if (weekText.Length <= 4096)
            {
                await _bot.SendMessage(chatId, weekText, replyMarkup: weekKeyboard, cancellationToken: ct);
            }
            else
            {
                var chunks = SplitMessage(weekText, 4096);
                for (int i = 0; i < chunks.Count - 1; i++)
                    await _bot.SendMessage(chatId, chunks[i], cancellationToken: ct);
                await _bot.SendMessage(chatId, chunks[^1], replyMarkup: weekKeyboard, cancellationToken: ct);
            }
        }
        else if (data == "select:change")
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

        await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
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
            lines.Add($"\ud83d\udcc5 {day.DayOfWeek} ({day.Date:MMM dd}):");
            foreach (var dish in day.Dishes)
            {
                var emoji = dish.MealCategory.ToLower() switch
                {
                    "meal" => "\ud83c\udf7d",
                    "breakfast" => "\ud83e\udd63",
                    "snack" => "\ud83c\udf4e",
                    _ => "\ud83c\udf7d"
                };
                lines.Add($"  {emoji} {dish.SlotIndex + 1}. {dish.DishName} ({dish.ProteinOption}) \u2014 {dish.Kcal:F0} kcal | P:{dish.Protein:F0} C:{dish.Carb:F0} F:{dish.Fat:F0}");
            }

            // Compact daily summary
            var dayTotal = $"  {day.TotalKcal:F0} kcal | P:{day.TotalProtein:F0} C:{day.TotalCarb:F0} F:{day.TotalFat:F0}";
            if (hasGoals)
            {
                var pDiff = day.TotalProtein - (proteinGoal ?? 0);
                var cDiff = day.TotalCarb - (carbGoal ?? 0);
                var fDiff = day.TotalFat - (fatGoal ?? 0);
                dayTotal += $" (goal: {Diff(pDiff)}P {Diff(cDiff)}C {Diff(fDiff)}F)";
            }
            lines.Add(dayTotal);
            lines.Add("");
        }

        // Week summary
        if (proposal.Days.Count > 0)
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
                lines.Add($"vs Goal:      {Diff(avgP - (proteinGoal ?? 0))}P {Diff(avgC - (carbGoal ?? 0))}C {Diff(avgF - (fatGoal ?? 0))}F");
            }
        }

        return string.Join("\n", lines);
    }

    private static string Diff(double val) => val >= 0 ? $"+{val:F0}" : $"{val:F0}";

    private static List<string> SplitMessage(string text, int maxLen)
    {
        var chunks = new List<string>();
        var lines = text.Split('\n');
        var current = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length + line.Length + 1 > maxLen && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.AppendLine();
            current.Append(line);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }
}
