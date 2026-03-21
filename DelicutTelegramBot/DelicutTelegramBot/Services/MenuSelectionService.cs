using System.Text.Json;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DelicutTelegramBot.Services;

public class MenuSelectionService : IMenuSelectionService
{
    private readonly IDelicutApiService _delicutApi;
    private readonly IUserService _userService;
    private readonly IOpenAiService _openAiService;
    private readonly IDishFilterService _dishFilterService;
    private readonly IFallbackSelectionService _fallbackService;
    private readonly ISelectionHistoryService _historyService;
    private readonly ConversationStateManager _stateManager;
    private readonly AppDbContext _db;
    private readonly ILogger<MenuSelectionService> _logger;

    public MenuSelectionService(
        IDelicutApiService delicutApi,
        IUserService userService,
        IOpenAiService openAiService,
        IDishFilterService dishFilterService,
        IFallbackSelectionService fallbackService,
        ISelectionHistoryService historyService,
        ConversationStateManager stateManager,
        AppDbContext db,
        ILogger<MenuSelectionService> logger)
    {
        _delicutApi = delicutApi;
        _userService = userService;
        _openAiService = openAiService;
        _dishFilterService = dishFilterService;
        _fallbackService = fallbackService;
        _historyService = historyService;
        _stateManager = stateManager;
        _db = db;
        _logger = logger;
    }

    public async Task<WeeklyProposal> SelectForWeekAsync(Guid userId)
    {
        var user = await _db.Users
            .Include(u => u.Settings)
            .FirstAsync(u => u.Id == userId);

        // Clear existing proposed selections
        var existingProposed = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.Status == PendingSelectionStatus.Proposed)
            .ToListAsync();
        _db.PendingSelections.RemoveRange(existingProposed);
        await _db.SaveChangesAsync();

        var subscription = await CallApiSafeAsync(() =>
            _delicutApi.GetSubscriptionDetailsAsync(user.DelicutToken!));

        var schedule = await CallApiSafeAsync(() =>
            _delicutApi.GetDeliveryScheduleAsync(user.DelicutToken!, user.DelicutSubscriptionId!));

        var mealSlots = subscription.MealTypes
            .Select(mt => new MealSlot { Category = mt.MealCategory.ToLower(), Count = mt.Qty })
            .ToList();

        var previousChoices = new List<string>();
        if (user.Settings?.PreferHistory == true)
        {
            previousChoices = await _historyService.GetPreviousChoiceNamesAsync(userId);
        }

        var state = _stateManager.GetOrCreate(user.TelegramUserId);
        var dayProposals = new List<DayProposal>();
        var lockedDays = new List<DateOnly>();
        var weekContext = new Dictionary<string, List<string>>();

        foreach (var day in schedule.Days)
        {
            if (day.IsLocked)
            {
                lockedDays.Add(day.Date);
                continue;
            }

            var dayDishes = new List<ProposedDish>();

            foreach (var mealSlot in mealSlots)
            {
                var category = mealSlot.Category;
                var mealTypeInfo = subscription.MealTypes
                    .FirstOrDefault(mt => mt.MealCategory.Equals(category, StringComparison.OrdinalIgnoreCase));

                // Fetch menu for this day + category
                List<Dish> menu;
                var cacheKey = $"menu:{day.Date}:{category}";
                try
                {
                    menu = await CallApiSafeAsync(() =>
                        _delicutApi.FetchMenuAsync(user.DelicutToken!, day.DeliveryId, category, day.UniqueId));
                    state.FlowData[cacheKey] = menu;
                }
                catch (DelicutAuthExpiredException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch menu for {Date} {Category}", day.Date, category);
                    continue;
                }

                // Apply hard filters
                var filtered = _dishFilterService.Filter(
                    menu,
                    user.Settings?.StopWords ?? [],
                    subscription.AvoidIngredients,
                    subscription.AvoidCategory,
                    mealTypeInfo?.KcalRange ?? string.Empty,
                    mealTypeInfo?.ProteinCategory ?? string.Empty);

                // Convert to DishSummary (flatten: each dish+variant = one DishSummary)
                var dishSummaries = FlattenToDishSummaries(filtered, category);

                // Build AI selection request
                var request = new AiSelectionRequest
                {
                    Strategy = user.Settings?.Strategy ?? SelectionStrategy.Default,
                    Date = day.Date,
                    MealSlots = [new MealSlot { Category = category, Count = mealSlot.Count }],
                    AvailableDishes = dishSummaries,
                    StopWords = user.Settings?.StopWords ?? [],
                    PreviousChoices = previousChoices,
                    PreferHistory = user.Settings?.PreferHistory ?? false,
                    WeekContext = weekContext
                };

                // Try AI, fall back if null
                AiSelectionResult result;
                try
                {
                    var aiResult = await _openAiService.SelectDishesAsync(request);
                    if (aiResult != null)
                    {
                        result = aiResult;
                    }
                    else
                    {
                        _logger.LogWarning("AI selection returned null for {Date} {Category}, using fallback", day.Date, category);
                        result = _fallbackService.Select(dishSummaries, request.Strategy, request.MealSlots, weekContext);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI selection failed for {Date} {Category}, using fallback", day.Date, category);
                    result = _fallbackService.Select(dishSummaries, request.Strategy, request.MealSlots, weekContext);
                }

                // Create PendingSelection rows and ProposedDish entries
                foreach (var pick in result.Picks)
                {
                    var dish = filtered.FirstOrDefault(d => d.Id == pick.DishId);
                    var variant = dish?.Variants.FirstOrDefault(v =>
                        v.ProteinOption.Equals(pick.ProteinOption, StringComparison.OrdinalIgnoreCase));

                    var pending = new PendingSelection
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        DeliveryDate = day.Date,
                        DeliveryId = day.DeliveryId,
                        UniqueId = day.UniqueId,
                        MealCategory = pick.MealCategory,
                        SlotIndex = pick.SlotIndex,
                        DishId = pick.DishId,
                        DishName = dish?.DishName ?? string.Empty,
                        VariantProtein = pick.ProteinOption,
                        Kcal = variant?.Kcal ?? 0,
                        Protein = variant?.Protein ?? 0,
                        Carb = variant?.Carb ?? 0,
                        Fat = variant?.Fat ?? 0,
                        Status = PendingSelectionStatus.Proposed,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _db.PendingSelections.Add(pending);

                    dayDishes.Add(new ProposedDish
                    {
                        DishId = pick.DishId,
                        DishName = dish?.DishName ?? string.Empty,
                        ProteinOption = pick.ProteinOption,
                        MealCategory = pick.MealCategory,
                        SlotIndex = pick.SlotIndex,
                        Kcal = variant?.Kcal ?? 0,
                        Protein = variant?.Protein ?? 0,
                        Carb = variant?.Carb ?? 0,
                        Fat = variant?.Fat ?? 0,
                        AiReasoning = pick.Reasoning
                    });
                }
            }

            await _db.SaveChangesAsync();

            // Update week context with this day's selections
            weekContext[day.Date.ToString("yyyy-MM-dd")] = dayDishes.Select(d => d.DishName).ToList();

            dayProposals.Add(new DayProposal
            {
                Date = day.Date,
                DayOfWeek = day.DayOfWeek,
                Dishes = dayDishes
            });
        }

        return new WeeklyProposal
        {
            Days = dayProposals,
            LockedDays = lockedDays
        };
    }

    public async Task<List<DishAlternative>> GetAlternativesAsync(Guid userId, DateOnly date, string mealCategory, int slotIndex)
    {
        var user = await _db.Users
            .Include(u => u.Settings)
            .FirstAsync(u => u.Id == userId);

        var state = _stateManager.GetOrCreate(user.TelegramUserId);
        var cacheKey = $"menu:{date}:{mealCategory}";

        if (!state.FlowData.TryGetValue(cacheKey, out var cached) || cached is not List<Dish> menu)
        {
            _logger.LogWarning("No cached menu found for {Date} {Category}", date, mealCategory);
            return [];
        }

        // Get currently selected dish IDs for this day
        var selectedDishIds = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.DeliveryDate == date && p.Status == PendingSelectionStatus.Proposed)
            .Select(p => p.DishId)
            .ToListAsync();

        // Filter out already-selected dishes
        var available = menu.Where(d => !selectedDishIds.Contains(d.Id)).ToList();

        // Convert to DishSummary, sort by rating descending, take top 5
        var alternatives = FlattenToDishSummaries(available, mealCategory)
            .OrderByDescending(ds => ds.Rating)
            .Take(5)
            .Select(ds => new DishAlternative
            {
                DishId = ds.Id,
                DishName = ds.Name,
                ProteinOption = ds.ProteinOption,
                Kcal = ds.Kcal,
                Protein = ds.Protein,
                Carb = ds.Carb,
                Fat = ds.Fat,
                AvgRating = ds.Rating
            })
            .ToList();

        return alternatives;
    }

    public async Task ReplaceDishAsync(Guid userId, DateOnly date, string mealCategory, int slotIndex, string newDishId, string proteinOption)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId);

        var pending = await _db.PendingSelections
            .FirstAsync(p => p.UserId == userId
                && p.DeliveryDate == date
                && p.MealCategory == mealCategory
                && p.SlotIndex == slotIndex);

        // Load dish data from cached menu
        var state = _stateManager.GetOrCreate(user.TelegramUserId);
        var cacheKey = $"menu:{date}:{mealCategory}";

        Dish? newDish = null;
        DishVariant? newVariant = null;

        if (state.FlowData.TryGetValue(cacheKey, out var cached) && cached is List<Dish> menu)
        {
            newDish = menu.FirstOrDefault(d => d.Id == newDishId);
            newVariant = newDish?.Variants.FirstOrDefault(v =>
                v.ProteinOption.Equals(proteinOption, StringComparison.OrdinalIgnoreCase));
        }

        pending.DishId = newDishId;
        pending.DishName = newDish?.DishName ?? string.Empty;
        pending.VariantProtein = proteinOption;
        pending.Kcal = newVariant?.Kcal ?? 0;
        pending.Protein = newVariant?.Protein ?? 0;
        pending.Carb = newVariant?.Carb ?? 0;
        pending.Fat = newVariant?.Fat ?? 0;
        pending.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task ConfirmDayAsync(Guid userId, DateOnly date)
    {
        var pending = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.DeliveryDate == date && p.Status == PendingSelectionStatus.Proposed)
            .ToListAsync();

        foreach (var p in pending)
        {
            p.Status = PendingSelectionStatus.Confirmed;
            p.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    public async Task ConfirmWeekAsync(Guid userId)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId);

        var confirmed = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.Status == PendingSelectionStatus.Confirmed)
            .ToListAsync();

        var grouped = confirmed
            .GroupBy(p => new { p.DeliveryDate, p.DeliveryId, p.UniqueId });

        var failedDays = new List<DateOnly>();

        foreach (var group in grouped)
        {
            var submissions = group.Select(p => new DishSubmission
            {
                DishId = p.DishId,
                ProteinOption = p.VariantProtein,
                MealCategory = p.MealCategory,
                SlotIndex = p.SlotIndex
            }).ToList();

            try
            {
                await CallApiSafeAsync(async () =>
                {
                    await _delicutApi.SubmitDishSelectionAsync(
                        user.DelicutToken!,
                        user.DelicutCustomerId!,
                        group.Key.DeliveryId,
                        group.Key.UniqueId,
                        submissions);
                    return true; // dummy return for generic helper
                });

                // Record to history and remove from pending
                await _historyService.RecordSelectionsAsync(userId, group.ToList(), wasUserChoice: true);
                _db.PendingSelections.RemoveRange(group);
            }
            catch (DelicutAuthExpiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit selections for {Date}", group.Key.DeliveryDate);
                failedDays.Add(group.Key.DeliveryDate);
            }
        }

        await _db.SaveChangesAsync();

        if (failedDays.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to submit selections for: {string.Join(", ", failedDays)}. These days are kept for retry.");
        }
    }

    private static List<DishSummary> FlattenToDishSummaries(List<Dish> dishes, string mealCategory)
    {
        var summaries = new List<DishSummary>();
        foreach (var dish in dishes)
        {
            foreach (var variant in dish.Variants)
            {
                summaries.Add(new DishSummary
                {
                    Id = dish.Id,
                    Name = dish.DishName,
                    Cuisine = dish.Cuisine,
                    Kcal = variant.Kcal,
                    Protein = variant.Protein,
                    Carb = variant.Carb,
                    Fat = variant.Fat,
                    Rating = dish.AvgRating,
                    TotalRatings = dish.TotalRatings,
                    SpiceLevel = dish.SpiceLevel,
                    ProteinOption = variant.ProteinOption,
                    MealCategory = mealCategory
                });
            }
        }
        return summaries;
    }

    private async Task<T> CallApiSafeAsync<T>(Func<Task<T>> apiCall)
    {
        try
        {
            return await apiCall();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new DelicutAuthExpiredException("Delicut token expired or invalid.", ex);
        }
    }
}
