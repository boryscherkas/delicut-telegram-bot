using DelicutTelegramBot.Helpers;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.State;
using Microsoft.Extensions.Logging;

namespace DelicutTelegramBot.Services;

public class MenuFetchService : IMenuFetchService
{
    private readonly IDelicutApiService _delicutApi;
    private readonly IDishFilterService _dishFilterService;
    private readonly ConversationStateManager _stateManager;
    private readonly ILogger<MenuFetchService> _logger;

    public MenuFetchService(
        IDelicutApiService delicutApi,
        IDishFilterService dishFilterService,
        ConversationStateManager stateManager,
        ILogger<MenuFetchService> logger)
    {
        _delicutApi = delicutApi;
        _dishFilterService = dishFilterService;
        _stateManager = stateManager;
        _logger = logger;
    }

    public async Task<WeekMenuData> FetchAndFilterMenusAsync(User user, Subscription subscription,
        WeekDeliverySchedule schedule, List<MealSlot> mealSlots)
    {
        var state = _stateManager.GetOrCreate(user.TelegramUserId);
        var dayMenuDataList = new List<DayMenuData>();
        var lockedDays = new List<DateOnly>();

        foreach (var day in schedule.Days)
        {
            if (day.IsLocked) { lockedDays.Add(day.Date); continue; }

            // Group slots by MealType (lunch/breakfast) — more reliable than MealCategory
            var slotsByType = day.Slots
                .GroupBy(s => s.MealType.ToLower() switch { "dinner" => "lunch", var t => t })
                .ToDictionary(g => g.Key, g => g.ToList());
            // Keep slotsByCategory for backward compat with ResolveDayPicks
            var slotsByCategory = day.Slots
                .GroupBy(s => s.MealCategory.ToLower())
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var mealSlot in mealSlots)
            {
                var category = mealSlot.Category;         // "meal", "breakfast"
                var apiCategory = mealSlot.ApiCategory; // "lunch", "breakfast"

                // Only process if this delivery has slots for this type
                if (!slotsByType.ContainsKey(apiCategory))
                    continue;

                var mealTypeInfo = subscription.MealTypes
                    .FirstOrDefault(mt => mt.MealType.Equals(apiCategory, StringComparison.OrdinalIgnoreCase))
                    ?? subscription.MealTypes.FirstOrDefault(mt => mt.MealCategory.Equals(mealSlot.Category, StringComparison.OrdinalIgnoreCase));

                _logger.LogInformation("MealType for {Date} {Category}: KcalRange={KcalRange}, ProteinCategory={ProteinCategory}",
                    day.Date, category, mealTypeInfo?.KcalRange, mealTypeInfo?.ProteinCategory);

                var firstSlot = slotsByType.GetValueOrDefault(apiCategory)?.FirstOrDefault();
                var uniqueIdForFetch = firstSlot?.UniqueId ?? string.Empty;

                if (string.IsNullOrEmpty(uniqueIdForFetch))
                {
                    _logger.LogWarning("No UniqueId for {Date} category '{Category}'", day.Date, category);
                    continue;
                }

                List<Dish> menu;
                var cacheKey = $"menu:{day.Date}:{category}";
                try
                {
                    _logger.LogInformation("Fetching menu: date={Date} deliveryId={DeliveryId} apiCategory={ApiCategory} uniqueId={UniqueId}",
                        day.Date, day.DeliveryId, mealSlot.ApiCategory, uniqueIdForFetch);
                    menu = await ApiCallHelper.CallApiSafeAsync(() =>
                        _delicutApi.FetchMenuAsync(user.DelicutToken!, day.DeliveryId, mealSlot.ApiCategory, uniqueIdForFetch));
                    state.FlowData[cacheKey] = menu;
                }
                catch (DelicutAuthExpiredException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch menu for {Date} {Category}", day.Date, category);
                    continue;
                }

                var filtered = _dishFilterService.Filter(menu,
                    user.Settings?.StopWords ?? [], subscription.AvoidIngredients, subscription.AvoidCategory,
                    mealTypeInfo?.KcalRange ?? string.Empty, mealTypeInfo?.ProteinCategory ?? string.Empty);

                var dishSummaries = DishSummaryHelper.FlattenToDishSummaries(filtered, category, user.Settings?.PreferredProteinVariant);
                _logger.LogInformation("Fetched {Count} dishes for {Date} {Category} (filtered: {Filtered}, summaries: {Summaries})",
                    menu.Count, day.Date, category, filtered.Count, dishSummaries.Count);
                dayMenuDataList.Add(new DayMenuData
                {
                    Day = day,
                    Filtered = filtered,
                    Summaries = dishSummaries,
                    SlotsByCategory = slotsByCategory,
                    MealSlot = mealSlot
                });
            }
        }

        // Log max possible macros per day
        foreach (var d in dayMenuDataList)
        {
            var topCarbs = d.Summaries.OrderByDescending(s => s.Carb).Take(d.MealSlot.Count).Sum(s => s.Carb);
            var topProtein = d.Summaries.OrderByDescending(s => s.Protein).Take(d.MealSlot.Count).Sum(s => s.Protein);
            _logger.LogInformation("Max possible for {Date}: C:{MaxCarb:F0}g P:{MaxProtein:F0}g (top {Count})",
                d.Day.Date, topCarbs, topProtein, d.MealSlot.Count);
        }

        return new WeekMenuData
        {
            Days = dayMenuDataList,
            LockedDays = lockedDays
        };
    }

}
