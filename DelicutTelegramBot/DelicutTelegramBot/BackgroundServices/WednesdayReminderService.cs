using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Services;

namespace DelicutTelegramBot.BackgroundServices;

public class WednesdayReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelegramBotClient _bot;
    private readonly ILogger<WednesdayReminderService> _logger;
    private static readonly TimeSpan Utc4 = TimeSpan.FromHours(4);

    public WednesdayReminderService(
        IServiceScopeFactory scopeFactory,
        ITelegramBotClient bot,
        ILogger<WednesdayReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _bot = bot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(Utc4);
            var nextWednesday = GetNextWednesday0900(now);
            var delay = nextWednesday - now;

            if (delay > TimeSpan.Zero)
            {
                _logger.LogInformation("Next Wednesday reminder at {NextTime} (in {Delay})", nextWednesday, delay);
                await Task.Delay(delay, stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) break;

            await SendRemindersAsync(stoppingToken);

            // Defensive delay to prevent CPU spin on clock edge cases
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task SendRemindersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var delicutApi = scope.ServiceProvider.GetRequiredService<IDelicutApiService>();

            var users = await db.Users
                .Where(u => u.DelicutToken != null)
                .ToListAsync(ct);

            foreach (var user in users)
            {
                try
                {
                    // Check if subscription is still active
                    var subscription = await delicutApi.GetSubscriptionDetailsAsync(user.DelicutToken!);
                    if (subscription.EndDate <= DateTime.UtcNow) continue;

                    await _bot.SendMessage(
                        user.TelegramChatId,
                        "New menu for next week is available! Use /select to choose your dishes.",
                        cancellationToken: ct);

                    _logger.LogInformation("Sent reminder to user {TelegramUserId}", user.TelegramUserId);
                }
                catch (Exception ex)
                {
                    // Skip this user on error (expired token, API down, etc.)
                    _logger.LogWarning(ex, "Failed to send reminder to user {TelegramUserId}", user.TelegramUserId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Wednesday reminders");
        }
    }

    private static DateTimeOffset GetNextWednesday0900(DateTimeOffset now)
    {
        var daysUntilWednesday = ((int)DayOfWeek.Wednesday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilWednesday == 0 && now.TimeOfDay >= new TimeSpan(9, 0, 0))
            daysUntilWednesday = 7;

        var nextWednesday = now.Date.AddDays(daysUntilWednesday);
        return new DateTimeOffset(nextWednesday.AddHours(9), Utc4);
    }
}
