using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DelicutTelegramBot.Helpers;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;

namespace DelicutTelegramBot.BackgroundServices;

public class WednesdayReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramBotClient _bot;
    private readonly ConversationStateManager _stateManager;
    private readonly ILogger<WednesdayReminderService> _logger;
    private static readonly TimeSpan Utc4 = TimeSpan.FromHours(4);
    private const int RunHour = 20; // 20:00 Dubai time

    public WednesdayReminderService(
        IServiceScopeFactory scopeFactory,
        ITelegramBotClient bot,
        ConversationStateManager stateManager,
        ILogger<WednesdayReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _bot = bot;
        _stateManager = stateManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(Utc4);
            var nextRun = GetNextWednesday(now);
            var delay = nextRun - now;

            if (delay > TimeSpan.Zero)
            {
                _logger.LogInformation("Next weekly auto-select at {NextTime} (in {Delay})", nextRun, delay);
                await Task.Delay(delay, stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) break;

            await RunWeeklySelectionAsync(stoppingToken);

            // Defensive delay to prevent CPU spin on clock edge cases
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task RunWeeklySelectionAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var menuService = scope.ServiceProvider.GetRequiredService<IMenuSelectionService>();

            var users = await db.Users
                .Include(u => u.Settings)
                .Where(u => u.DelicutToken != null)
                .ToListAsync(ct);

            _logger.LogInformation("Running weekly auto-select for {Count} users", users.Count);

            foreach (var user in users)
            {
                try
                {
                    var proposal = await menuService.SelectForWeekAsync(user.Id, regenerate: true);

                    // Store proposal in conversation state so buttons work
                    var state = _stateManager.GetOrCreate(user.TelegramUserId);
                    state.CurrentFlow = ConversationFlow.Select_ReviewingWeek;
                    state.FlowData["proposal"] = proposal;
                    state.FlowData["user_id"] = user.Id;
                    state.LastActivity = DateTime.UtcNow;

                    var text = FormatProposal(proposal, user.Settings);

                    await KeyboardBuilder.SendOrSplitMessageAsync(_bot, user.TelegramChatId, text,
                        KeyboardBuilder.WeekOverviewKeyboard(), ct);

                    _logger.LogInformation("Auto-selected week for user {TelegramUserId}: {DayCount} days",
                        user.TelegramUserId, proposal.Days.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed auto-select for user {TelegramUserId}", user.TelegramUserId);
                    try
                    {
                        await _bot.SendMessage(user.TelegramChatId,
                            "Weekly auto-selection failed. Use /select to pick dishes manually.",
                            cancellationToken: ct);
                    }
                    catch { /* best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run weekly auto-selection");
        }
    }

    private static string FormatProposal(Models.Dto.WeeklyProposal proposal, UserSettings? settings)
    {
        var lines = new List<string> { "Weekly auto-selection is ready!", "" };

        var hasGoals = settings?.ProteinGoalGrams != null || settings?.CarbGoalGrams != null || settings?.FatGoalGrams != null;
        if (hasGoals)
        {
            lines.Add($"Daily goals: P:{settings!.ProteinGoalGrams ?? 0}g C:{settings.CarbGoalGrams ?? 0}g F:{settings.FatGoalGrams ?? 0}g");
            lines.Add("");
        }

        foreach (var day in proposal.Days)
        {
            lines.Add($"📅 {day.DayOfWeek} ({day.Date:MMM dd}):");
            for (var i = 0; i < day.Dishes.Count; i++)
            {
                var d = day.Dishes[i];
                var emoji = d.MealCategory.ToLower() switch { "meal" => "🍽", "breakfast" => "🥣", "snack" => "🍎", _ => "🍽" };
                lines.Add($"  {emoji} {i + 1}. {d.DishName} ({d.ProteinOption}) — {d.Kcal:F0} kcal | P:{d.Protein:F0} C:{d.Carb:F0} F:{d.Fat:F0}");
            }
            lines.Add($"  {day.TotalKcal:F0} kcal | P:{day.TotalProtein:F0} C:{day.TotalCarb:F0} F:{day.TotalFat:F0}");
            if (hasGoals)
            {
                var pDiff = day.TotalProtein - (settings!.ProteinGoalGrams ?? 0);
                var cDiff = day.TotalCarb - (settings.CarbGoalGrams ?? 0);
                var fDiff = day.TotalFat - (settings.FatGoalGrams ?? 0);
                lines.Add($"  (goal: {TelegramFormatHelper.FormatDiff(pDiff)}P {TelegramFormatHelper.FormatDiff(cDiff)}C {TelegramFormatHelper.FormatDiff(fDiff)}F)");
            }
            lines.Add("");
        }

        lines.Add("Review and approve, change dishes, or regenerate.");
        return string.Join("\n", lines);
    }

    private static DateTimeOffset GetNextWednesday(DateTimeOffset now)
    {
        var daysUntilWednesday = ((int)DayOfWeek.Wednesday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilWednesday == 0 && now.TimeOfDay >= new TimeSpan(RunHour, 0, 0))
            daysUntilWednesday = 7;

        var nextWednesday = now.Date.AddDays(daysUntilWednesday);
        return new DateTimeOffset(nextWednesday.AddHours(RunHour), Utc4);
    }
}
