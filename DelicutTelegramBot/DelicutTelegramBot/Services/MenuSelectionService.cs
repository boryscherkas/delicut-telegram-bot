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
            _delicutApi.GetDeliveryScheduleAsync(user.DelicutToken!, user.DelicutCustomerId!));

        var mealSlots = subscription.MealTypes
            .Select(mt => new MealSlot
            {
                Category = mt.MealCategory.ToLower(),   // "meal", "breakfast", "snack" — for internal grouping
                ApiCategory = mt.MealType.ToLower(),     // "lunch", "breakfast", "evening_snack" — for API calls
                Count = mt.Qty
            })
            .ToList();

        var previousChoices = new List<string>();
        if (user.Settings?.PreferHistory == true)
        {
            previousChoices = await _historyService.GetPreviousChoiceNamesAsync(userId);
        }

        _logger.LogInformation(
            "Selection settings for user {UserId}: Strategy={Strategy}, " +
            "MacroGoals=P:{ProteinGoal}g C:{CarbGoal}g F:{FatGoal}g, Priority={Priority}, " +
            "PreferredProtein={PreferredProtein}, Favourites=[{Favourites}]",
            userId, user.Settings?.Strategy,
            user.Settings?.ProteinGoalGrams, user.Settings?.CarbGoalGrams, user.Settings?.FatGoalGrams,
            user.Settings?.MacroPriority,
            user.Settings?.PreferredProteinVariant,
            string.Join(", ", user.Settings?.FavouriteDishNames ?? []));

        var state = _stateManager.GetOrCreate(user.TelegramUserId);
        var dayProposals = new List<DayProposal>();
        var lockedDays = new List<DateOnly>();

        // ── Phase 1: Fetch and filter menus for all unlocked days ──
        var dayMenuData = new List<(DeliveryDay Day, List<Dish> Filtered, List<DishSummary> Summaries,
            Dictionary<string, List<DeliverySlot>> SlotsByCategory, MealSlot MealSlot)>();

        foreach (var day in schedule.Days)
        {
            if (day.IsLocked) { lockedDays.Add(day.Date); continue; }

            var slotsByCategory = day.Slots
                .GroupBy(s => s.MealCategory.ToLower())
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var mealSlot in mealSlots)
            {
                var category = mealSlot.Category;
                var mealTypeInfo = subscription.MealTypes
                    .FirstOrDefault(mt => mt.MealCategory.Equals(category, StringComparison.OrdinalIgnoreCase));

                var firstSlot = slotsByCategory.GetValueOrDefault(category)?.FirstOrDefault();
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
                    menu = await CallApiSafeAsync(() =>
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

                var dishSummaries = FlattenToDishSummaries(filtered, category, user.Settings?.PreferredProteinVariant);
                dayMenuData.Add((day, filtered, dishSummaries, slotsByCategory, mealSlot));
            }
        }

        // ── Phase 2: Parallel AI calls per day (fast + each day gets macro context) ──
        // Build all day names for variety context
        var allDayDishNames = new Dictionary<string, List<string>>();
        var macroPriority = (user.Settings?.MacroPriority ?? "p,c,f").Split(',').Select(s => s.Trim()).ToList();

        // Log max possible carbs per day so we can verify if goals are achievable
        foreach (var d in dayMenuData)
        {
            var topCarbs = d.Summaries.OrderByDescending(s => s.Carb).Take(d.MealSlot.Count).Sum(s => s.Carb);
            var topProtein = d.Summaries.OrderByDescending(s => s.Protein).Take(d.MealSlot.Count).Sum(s => s.Protein);
            _logger.LogInformation("Max possible for {Date}: C:{MaxCarb:F0}g P:{MaxProtein:F0}g (from top {Count} dishes)",
                d.Day.Date, topCarbs, topProtein, d.MealSlot.Count);
        }

        // Fire all AI requests in parallel — each gets ONLY its day's dishes (not full week)
        var aiTasks = dayMenuData.Select(async d =>
        {
            var request = new AiSelectionRequest
            {
                Strategy = user.Settings?.Strategy ?? SelectionStrategy.Default,
                Date = d.Day.Date,
                MealSlots = [new MealSlot { Category = d.MealSlot.Category, ApiCategory = d.MealSlot.ApiCategory, Count = d.MealSlot.Count }],
                AvailableDishes = d.Summaries,
                StopWords = user.Settings?.StopWords ?? [],
                PreviousChoices = previousChoices,
                PreferHistory = user.Settings?.PreferHistory ?? false,
                WeekContext = new(),
                MacroPriority = macroPriority,
                ProteinGoalGrams = user.Settings?.ProteinGoalGrams,
                CarbGoalGrams = user.Settings?.CarbGoalGrams,
                FatGoalGrams = user.Settings?.FatGoalGrams,
                PreferredProteinVariant = user.Settings?.PreferredProteinVariant,
                FavouriteDishNames = user.Settings?.FavouriteDishNames ?? [],
                MinFavouritesPerWeek = user.Settings?.MinFavouritesPerWeek ?? 0
            };

            AiSelectionResult result;
            try
            {
                var aiResult = await _openAiService.SelectDishesAsync(request);
                if (aiResult != null)
                {
                    result = aiResult;
                    // Ensure date is set on all picks
                    foreach (var pick in result.Picks)
                        if (string.IsNullOrEmpty(pick.Date))
                            pick.Date = d.Day.Date.ToString("yyyy-MM-dd");
                }
                else
                {
                    _logger.LogWarning("AI null for {Date}, using fallback", d.Day.Date);
                    result = _fallbackService.Select(d.Summaries, request.Strategy, request.MealSlots, new(),
                        request.ProteinGoalGrams, request.CarbGoalGrams, request.FatGoalGrams,
                        macroPriority, request.FavouriteDishNames, request.MinFavouritesPerWeek);
                    foreach (var pick in result.Picks) pick.Date = d.Day.Date.ToString("yyyy-MM-dd");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI failed for {Date}, using fallback", d.Day.Date);
                result = _fallbackService.Select(d.Summaries, request.Strategy,
                    request.MealSlots, new(), request.ProteinGoalGrams, request.CarbGoalGrams,
                    request.FatGoalGrams, macroPriority, request.FavouriteDishNames, request.MinFavouritesPerWeek);
                foreach (var pick in result.Picks) pick.Date = d.Day.Date.ToString("yyyy-MM-dd");
            }
            return (d, result);
        }).ToList();

        _logger.LogInformation("Sending {Count} parallel AI requests", aiTasks.Count);
        var aiResults = await Task.WhenAll(aiTasks);
        _logger.LogInformation("All AI requests completed");

        // ── Phase 3: Resolve AI picks into PendingSelections ──
        var weekContext = new Dictionary<string, List<string>>();

        foreach (var ((dayData, aiResult), index) in aiResults.Select((r, i) => (r, i)))
        {
            var (day, filtered, dishSummaries, slotsByCategory, mealSlot) = dayData;
            var category = mealSlot.Category;
            var dayPicks = aiResult.Picks;

            if (dayPicks.Count == 0)
            {
            }

            var dayDishes = new List<ProposedDish>();
            var firstSlot = slotsByCategory.GetValueOrDefault(category)?.FirstOrDefault();

            foreach (var pick in dayPicks)
            {
                var dish = filtered.FirstOrDefault(d => d.Id == pick.DishId);
                if (dish is null) { _logger.LogWarning("Dish {DishId} not found for {Date}", pick.DishId, day.Date); continue; }

                var variant = dish.Variants.FirstOrDefault(v =>
                    v.ProteinOption.Equals(pick.ProteinOption, StringComparison.OrdinalIgnoreCase));
                if (variant is null && !string.IsNullOrEmpty(user.Settings?.PreferredProteinVariant))
                    variant = dish.Variants.FirstOrDefault(v =>
                        v.ProteinOption.Equals(user.Settings.PreferredProteinVariant, StringComparison.OrdinalIgnoreCase));
                variant ??= dish.Variants.FirstOrDefault();
                if (variant is null) continue;

                var slotsForCategory = slotsByCategory.GetValueOrDefault(category);
                var matchingSlot = slotsForCategory?.ElementAtOrDefault(pick.SlotIndex);
                var slotUniqueId = matchingSlot?.UniqueId ?? firstSlot?.UniqueId ?? string.Empty;

                var pending = new PendingSelection
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    DeliveryDate = day.Date,
                    DeliveryId = day.DeliveryId,
                    UniqueId = slotUniqueId,
                    MealCategory = pick.MealCategory,
                    SlotIndex = pick.SlotIndex,
                    DishId = pick.DishId,
                    DishName = dish.DishName,
                    VariantProtein = variant.ProteinOption,
                    VariantSize = variant.Size,
                    VariantProteinCategory = variant.ProteinCategory,
                    Kcal = variant.Kcal,
                    Protein = variant.Protein,
                    Carb = variant.Carb,
                    Fat = variant.Fat,
                    Status = PendingSelectionStatus.Proposed,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                    };
                    _db.PendingSelections.Add(pending);

                    dayDishes.Add(new ProposedDish
                    {
                        DishId = pick.DishId,
                        DishName = dish.DishName,
                        ProteinOption = variant.ProteinOption,
                        MealCategory = pick.MealCategory,
                        SlotIndex = pick.SlotIndex,
                        Kcal = variant.Kcal,
                        Protein = variant.Protein,
                        Carb = variant.Carb,
                        Fat = variant.Fat,
                        AiReasoning = pick.Reasoning
                    });
                }

            await _db.SaveChangesAsync();

            // Update week context with this day's selections
            weekContext[day.Date.ToString("yyyy-MM-dd")] = dayDishes.Select(d => d.DishName).ToList();

            // Capture original (Delicut auto-selected) totals from delivery slots
            dayProposals.Add(new DayProposal
            {
                Date = day.Date,
                DayOfWeek = day.DayOfWeek,
                Dishes = dayDishes,
                OriginalKcal = day.Slots.Sum(s => s.CurrentKcal),
                OriginalProtein = day.Slots.Sum(s => s.CurrentProtein),
                OriginalCarb = day.Slots.Sum(s => s.CurrentCarb),
                OriginalFat = day.Slots.Sum(s => s.CurrentFat)
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
        pending.VariantSize = newVariant?.Size ?? pending.VariantSize;
        pending.VariantProteinCategory = newVariant?.ProteinCategory ?? pending.VariantProteinCategory;
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
                SlotIndex = p.SlotIndex,
                Size = p.VariantSize,
                ProteinCategory = p.VariantProteinCategory
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

    /// <summary>
    /// Flattens dishes to DishSummary. If preferredProtein is set, picks only that variant
    /// per dish (falls back to first variant if preferred not available).
    /// Otherwise creates one summary per variant.
    /// </summary>
    private static List<DishSummary> FlattenToDishSummaries(
        List<Dish> dishes, string mealCategory, string? preferredProtein = null)
    {
        var summaries = new List<DishSummary>();
        foreach (var dish in dishes)
        {
            IEnumerable<DishVariant> variants;
            if (!string.IsNullOrEmpty(preferredProtein))
            {
                // Pick preferred variant if available, otherwise first variant
                var preferred = dish.Variants.FirstOrDefault(v =>
                    v.ProteinOption.Equals(preferredProtein, StringComparison.OrdinalIgnoreCase));
                variants = preferred != null ? [preferred] : dish.Variants.Take(1);
            }
            else
            {
                variants = dish.Variants;
            }

            foreach (var variant in variants)
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
                    Rating = 0, // Not using Delicut API rating
                    TotalRatings = 0,
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
