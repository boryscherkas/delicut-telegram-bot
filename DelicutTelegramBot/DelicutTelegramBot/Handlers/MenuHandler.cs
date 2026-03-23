using Telegram.Bot;
using Telegram.Bot.Types;
using DelicutTelegramBot.Helpers;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Services;

namespace DelicutTelegramBot.Handlers;

public class MenuHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly IUserService _userService;
    private readonly IDelicutApiService _delicutApi;

    public MenuHandler(ITelegramBotClient bot, IUserService userService, IDelicutApiService delicutApi)
    {
        _bot = bot;
        _userService = userService;
        _delicutApi = delicutApi;
    }

    public async Task HandleCommandAsync(Message message, CancellationToken ct)
    {
        var user = await _userService.GetByTelegramIdAsync(message.From!.Id);
        if (user is null || string.IsNullOrEmpty(user.DelicutToken))
        {
            await _bot.SendMessage(message.Chat.Id, "Please authenticate first with /start.", cancellationToken: ct);
            return;
        }

        var schedule = await ApiCallHelper.CallApiSafeAsync(() =>
            _delicutApi.GetDeliveryScheduleAsync(user.DelicutToken!, user.DelicutCustomerId!));

        var lines = new List<string> { "Current Delicut menu:", "" };

        foreach (var day in schedule.Days)
        {
            var label = day.IsLocked ? " (locked)" : "";
            lines.Add($"📅 {day.DayOfWeek} ({day.Date:MMM dd}){label}:");

            var slots = day.Slots
                .OrderBy(s => s.MealType.ToLower() switch { "breakfast" => 0, "evening_snack" => 3, "dinner" => 2, _ => 1 })
                .ToList();

            double dayKcal = 0, dayP = 0, dayC = 0, dayF = 0;
            for (var i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                var emoji = s.MealType.ToLower() switch
                {
                    "breakfast" => "🥣",
                    "evening_snack" => "🍎",
                    _ => "🍽"
                };
                var name = s.CurrentDishName ?? "—";
                var protein = s.CurrentProteinOption ?? "";
                var proteinTag = string.IsNullOrEmpty(protein) ? "" : $" ({protein})";
                lines.Add($"  {emoji} {i + 1}. {name}{proteinTag} — {s.CurrentKcal:F0} kcal | P:{s.CurrentProtein:F0} C:{s.CurrentCarb:F0} F:{s.CurrentFat:F0}");
                dayKcal += s.CurrentKcal;
                dayP += s.CurrentProtein;
                dayC += s.CurrentCarb;
                dayF += s.CurrentFat;
            }

            lines.Add($"  {dayKcal:F0} kcal | P:{dayP:F0} C:{dayC:F0} F:{dayF:F0}");
            lines.Add("");
        }

        var activeDays = schedule.Days.Where(d => d.Slots.Count > 0).ToList();
        if (activeDays.Count > 0)
        {
            var avgK = activeDays.Average(d => d.Slots.Sum(s => s.CurrentKcal));
            var avgP = activeDays.Average(d => d.Slots.Sum(s => s.CurrentProtein));
            var avgC = activeDays.Average(d => d.Slots.Sum(s => s.CurrentCarb));
            var avgF = activeDays.Average(d => d.Slots.Sum(s => s.CurrentFat));
            lines.Add($"Week avg: {avgK:F0} kcal | P:{avgP:F0} C:{avgC:F0} F:{avgF:F0}");
        }

        await KeyboardBuilder.SendOrSplitMessageAsync(_bot, message.Chat.Id,
            string.Join("\n", lines), null!, ct);
    }
}
