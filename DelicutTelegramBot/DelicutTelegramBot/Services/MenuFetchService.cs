using DelicutTelegramBot.Helpers;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DelicutTelegramBot.Services;

public class MenuFetchService : IMenuFetchService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly IDelicutApiService _delicutApi;
    private readonly IDishFilterService _dishFilterService;
    private readonly ConversationStateManager _stateManager;
    private readonly AppDbContext _db;
    private readonly ILogger<MenuFetchService> _logger;

    public MenuFetchService(
        IDelicutApiService delicutApi,
        IDishFilterService dishFilterService,
        ConversationStateManager stateManager,
        AppDbContext db,
        ILogger<MenuFetchService> logger)
    {
        _delicutApi = delicutApi;
        _dishFilterService = dishFilterService;
        _stateManager = stateManager;
        _db = db;
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

                var firstSlot = slotsByType.GetValueOrDefault(apiCategory)?.FirstOrDefault();

                // Use kcalRange from delivery slot if available (more accurate than subscription for breakfast)
                var kcalRange = firstSlot?.KcalRange ?? mealTypeInfo?.KcalRange ?? string.Empty;
                var proteinCategory = firstSlot?.ProteinCategory ?? mealTypeInfo?.ProteinCategory ?? string.Empty;

                _logger.LogInformation("MealType for {Date} {ApiCategory}: kcalRange={KcalRange} (slot={SlotKcal}, sub={SubKcal}), proteinCategory={ProteinCategory}",
                    day.Date, apiCategory, kcalRange, firstSlot?.KcalRange, mealTypeInfo?.KcalRange, proteinCategory);
                var uniqueIdForFetch = firstSlot?.UniqueId ?? string.Empty;

                if (string.IsNullOrEmpty(uniqueIdForFetch))
                {
                    _logger.LogWarning("No UniqueId for {Date} category '{Category}'", day.Date, category);
                    continue;
                }

                List<Dish> menu;
                var memoryCacheKey = $"menu:{day.Date}:{category}";
                try
                {
                    // Try DB cache first, then API
                    menu = await GetOrFetchMenuAsync(user, day, mealSlot, uniqueIdForFetch, category);
                    state.FlowData[memoryCacheKey] = menu;
                }
                catch (DelicutAuthExpiredException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch menu for {Date} {Category}", day.Date, category);
                    continue;
                }

                var filtered = _dishFilterService.Filter(menu,
                    user.Settings?.StopWords ?? [], subscription.AvoidIngredients, subscription.AvoidCategory,
                    kcalRange, proteinCategory);

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

    private async Task<List<Dish>> GetOrFetchMenuAsync(
        User user, DeliveryDay day, MealSlot mealSlot, string uniqueId, string category)
    {
        var now = DateTime.UtcNow;

        // Check DB cache
        var cached = await _db.MenuCaches
            .FirstOrDefaultAsync(c =>
                c.UserId == user.Id
                && c.DeliveryDate == day.Date
                && c.MealCategory == category
                && c.ExpiresAt > now);

        if (cached != null)
        {
            _logger.LogInformation("Menu cache HIT: {Date} {Category} ({DishCount} dishes, cached at {CachedAt})",
                day.Date, category, cached.Dishes.Count, cached.FetchedAt);
            return cached.Dishes;
        }

        // Cache miss — fetch from API
        _logger.LogInformation("Menu cache MISS: fetching from API: date={Date} deliveryId={DeliveryId} apiCategory={ApiCategory} uniqueId={UniqueId}",
            day.Date, day.DeliveryId, mealSlot.ApiCategory, uniqueId);

        var menu = await ApiCallHelper.CallApiSafeAsync(() =>
            _delicutApi.FetchMenuAsync(user.DelicutToken!, day.DeliveryId, mealSlot.ApiCategory, uniqueId));

        // Upsert cache
        var existing = await _db.MenuCaches
            .FirstOrDefaultAsync(c =>
                c.UserId == user.Id
                && c.DeliveryDate == day.Date
                && c.MealCategory == category);

        if (existing != null)
        {
            existing.Dishes = menu;
            existing.FetchedAt = now;
            existing.ExpiresAt = now.Add(CacheTtl);
        }
        else
        {
            _db.MenuCaches.Add(new MenuCache
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                DeliveryDate = day.Date,
                MealCategory = category,
                Dishes = menu,
                FetchedAt = now,
                ExpiresAt = now.Add(CacheTtl)
            });
        }

        await _db.SaveChangesAsync();
        return menu;
    }
}
