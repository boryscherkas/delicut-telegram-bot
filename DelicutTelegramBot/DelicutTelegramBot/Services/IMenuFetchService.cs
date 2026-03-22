using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

/// <summary>
/// Fetches menus from the Delicut API, applies hard filters, flattens to DishSummary,
/// caches menus in ConversationState, and returns structured per-day data.
/// </summary>
public interface IMenuFetchService
{
    Task<WeekMenuData> FetchAndFilterMenusAsync(User user, Subscription subscription,
        WeekDeliverySchedule schedule, List<MealSlot> mealSlots);
}

/// <summary>
/// Contains all per-day menu data needed by the selection orchestrator.
/// </summary>
public class WeekMenuData
{
    public List<DayMenuData> Days { get; init; } = [];
    public List<DateOnly> LockedDays { get; init; } = [];
}

public class DayMenuData
{
    public required DeliveryDay Day { get; init; }
    public required List<Dish> Filtered { get; init; }
    public required List<DishSummary> Summaries { get; init; }
    public required Dictionary<string, List<DeliverySlot>> SlotsByCategory { get; init; }
    public required MealSlot MealSlot { get; init; }
}
