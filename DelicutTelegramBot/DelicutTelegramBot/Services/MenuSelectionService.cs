using System.Text.Json;
using DelicutTelegramBot.Helpers;
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
    private const string DateFormat = "yyyy-MM-dd";
    private const int MaxAiRetries = 3;

    private readonly IDelicutApiService _delicutApi;
    private readonly IUserService _userService;
    private readonly IOpenAiService _openAiService;
    private readonly IMenuFetchService _menuFetchService;
    private readonly IFallbackSelectionService _fallbackService;
    private readonly ISelectionHistoryService _historyService;
    private readonly ConversationStateManager _stateManager;
    private readonly AppDbContext _db;
    private readonly ILogger<MenuSelectionService> _logger;

    public MenuSelectionService(
        IDelicutApiService delicutApi,
        IUserService userService,
        IOpenAiService openAiService,
        IMenuFetchService menuFetchService,
        IFallbackSelectionService fallbackService,
        ISelectionHistoryService historyService,
        ConversationStateManager stateManager,
        AppDbContext db,
        ILogger<MenuSelectionService> logger)
    {
        _delicutApi = delicutApi;
        _userService = userService;
        _openAiService = openAiService;
        _menuFetchService = menuFetchService;
        _fallbackService = fallbackService;
        _historyService = historyService;
        _stateManager = stateManager;
        _db = db;
        _logger = logger;
    }

    public async Task<WeeklyProposal> SelectForWeekAsync(Guid userId, bool regenerate = false)
    {
        var user = await _db.Users
            .Include(u => u.Settings)
            .FirstAsync(u => u.Id == userId);

        await ClearExistingProposalsAsync(userId);

        var subscription = await ApiCallHelper.CallApiSafeAsync(() =>
            _delicutApi.GetSubscriptionDetailsAsync(user.DelicutToken!));

        var schedule = await ApiCallHelper.CallApiSafeAsync(() =>
            _delicutApi.GetDeliveryScheduleAsync(user.DelicutToken!, user.DelicutCustomerId!));

        // Log raw subscription meal types for debugging
        foreach (var mt in subscription.MealTypes)
            _logger.LogInformation("Subscription MealType: category={Category} type={Type} qty={Qty} kcal={Kcal}",
                mt.MealCategory, mt.MealType, mt.Qty, mt.KcalRange);

        // Group by MealType (lunch/breakfast/dinner) — NOT MealCategory (which can be "meal" for everything)
        // Merge lunch+dinner into one slot since they share the same menu
        var mealSlots = subscription.MealTypes
            .GroupBy(mt => mt.MealType.ToLower() switch
            {
                "dinner" => "lunch",  // dinner shares lunch menu
                var t => t
            })
            .Select(g => new MealSlot
            {
                Category = g.First().MealCategory.ToLower(),
                ApiCategory = g.Key,  // "lunch", "breakfast", etc
                Count = g.Sum(mt => mt.Qty)
            })
            .ToList();

        _logger.LogInformation("Resolved mealSlots: [{Slots}]",
            string.Join(", ", mealSlots.Select(s => $"{s.ApiCategory}({s.Category})x{s.Count}")));

        var previousChoices = user.Settings?.PreferHistory == true
            ? await _historyService.GetPreviousChoiceNamesAsync(userId)
            : [];

        LogSelectionSettings(userId, user);

        // Phase 1: Fetch and filter menus for all unlocked days
        var weekMenuData = await _menuFetchService.FetchAndFilterMenusAsync(user, subscription, schedule, mealSlots);
        var dayMenuData = weekMenuData.Days;

        // Phase 2: Build the selection request
        var macroPriority = (user.Settings?.MacroPriority ?? "p,c,f").Split(',').Select(s => s.Trim()).ToList();
        var weekRequest = BuildWeekRequest(user, dayMenuData, mealSlots, previousChoices, macroPriority);
        var expectedPerDay = dayMenuData
            .GroupBy(d => d.Day.Date.ToString(DateFormat))
            .ToDictionary(g => g.Key, g => g.Sum(d => d.MealSlot.Count));

        // Phase 3: AI or fallback selection
        var weekResult = await SelectDishesAsync(user, weekRequest, dayMenuData, expectedPerDay, macroPriority, regenerate);

        // Phase 4: Fix over-repeated dishes
        FixOverRepeatedDishes(weekResult, dayMenuData);

        // Phase 5: Resolve picks into PendingSelections and build proposal
        return await ResolvePicksToProposalAsync(userId, user, subscription, weekRequest, weekResult, dayMenuData, macroPriority, regenerate, weekMenuData.LockedDays);
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

        var otherSelectedDishIds = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.DeliveryDate == date
                && p.Status == PendingSelectionStatus.Proposed
                && p.SlotIndex != slotIndex)
            .Select(p => p.DishId)
            .ToListAsync();

        var available = menu.Where(d => !otherSelectedDishIds.Contains(d.Id)).ToList();

        var currentDishId = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.DeliveryDate == date
                && p.MealCategory == mealCategory && p.SlotIndex == slotIndex)
            .Select(p => p.DishId)
            .FirstOrDefaultAsync();

        if (currentDishId != null)
            available = available.Where(d => d.Id != currentDishId).ToList();

        return DishSummaryHelper.FlattenToDishSummaries(available, mealCategory,
                user.Settings?.PreferredProteinVariant)
            .OrderByDescending(ds => ds.Carb)
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
    }

    public async Task ReplaceDishAsync(Guid userId, DateOnly date, string mealCategory, int slotIndex, string newDishId, string proteinOption)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId);

        var pending = await _db.PendingSelections
            .FirstAsync(p => p.UserId == userId
                && p.DeliveryDate == date
                && p.MealCategory == mealCategory
                && p.SlotIndex == slotIndex);

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

    public async Task SubmitDayAsync(Guid userId, DateOnly date)
    {
        var user = await _db.Users.FirstAsync(u => u.Id == userId);

        // Get all pending selections for this day (any status)
        var daySelections = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.DeliveryDate == date)
            .ToListAsync();

        if (daySelections.Count == 0)
        {
            _logger.LogInformation("Day {Date} already submitted (no pending selections)", date);
            return;
        }

        // Filter out dishes that match Delicut's current selection
        var toSubmit = daySelections.Where(s => !s.MatchesOriginal).ToList();
        var skipped = daySelections.Where(s => s.MatchesOriginal).ToList();

        if (skipped.Count > 0)
            _logger.LogInformation("Skipping {Count} dishes for {Date} (already match Delicut)", skipped.Count, date);

        if (toSubmit.Count == 0)
        {
            _logger.LogInformation("Day {Date} fully matches Delicut — nothing to submit", date);
            // Clean up pending selections since no changes needed
            _db.PendingSelections.RemoveRange(daySelections);
            await _db.SaveChangesAsync();
            return;
        }

        _logger.LogInformation("Submitting {Count} changed dishes for {Date} (skipping {Skipped} matching)",
            toSubmit.Count, date, skipped.Count);

        foreach (var sel in toSubmit)
        {
            var submission = new DishSubmission
            {
                DishId = sel.DishId,
                ProteinOption = sel.VariantProtein,
                MealCategory = sel.MealCategory,
                MealType = sel.MealType,
                SlotIndex = sel.SlotIndex,
                Size = sel.VariantSize,
                ProteinCategory = sel.VariantProteinCategory
            };

            try
            {
                await ApiCallHelper.CallApiSafeAsync(async () =>
                {
                    await _delicutApi.SubmitDishSelectionAsync(
                        user.DelicutToken!, user.DelicutCustomerId!,
                        sel.DeliveryId, sel.UniqueId, [submission]);
                    return true;
                });
            }
            catch (DelicutAuthExpiredException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit dish {DishId} for {Date}", sel.DishId, date);
                throw;
            }
        }

        await _historyService.RecordSelectionsAsync(userId, daySelections, wasUserChoice: true);
        _db.PendingSelections.RemoveRange(daySelections);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Submitted {Count} dishes for {Date}", daySelections.Count, date);
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
                MealType = p.MealType,
                SlotIndex = p.SlotIndex,
                Size = p.VariantSize,
                ProteinCategory = p.VariantProteinCategory
            }).ToList();

            try
            {
                await ApiCallHelper.CallApiSafeAsync(async () =>
                {
                    await _delicutApi.SubmitDishSelectionAsync(
                        user.DelicutToken!,
                        user.DelicutCustomerId!,
                        group.Key.DeliveryId,
                        group.Key.UniqueId,
                        submissions);
                    return true;
                });

                await _historyService.RecordSelectionsAsync(userId, group.ToList(), wasUserChoice: true);
                _db.PendingSelections.RemoveRange(group);
            }
            catch (DelicutAuthExpiredException) { throw; }
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

    // ── Private helpers ──────────────────────────────────────────────

    private async Task ClearExistingProposalsAsync(Guid userId)
    {
        // Clear ALL pending selections (Proposed + stale Confirmed from previous runs)
        var existingProposed = await _db.PendingSelections
            .Where(p => p.UserId == userId)
            .ToListAsync();
        _db.PendingSelections.RemoveRange(existingProposed);
        await _db.SaveChangesAsync();
    }

    private void LogSelectionSettings(Guid userId, User user)
    {
        _logger.LogInformation(
            "Selection settings for user {UserId}: Strategy={Strategy}, " +
            "MacroGoals=P:{ProteinGoal}g C:{CarbGoal}g F:{FatGoal}g, Priority={Priority}, " +
            "PreferredProtein={PreferredProtein}, Favourites=[{Favourites}]",
            userId, user.Settings?.Strategy,
            user.Settings?.ProteinGoalGrams, user.Settings?.CarbGoalGrams, user.Settings?.FatGoalGrams,
            user.Settings?.MacroPriority,
            user.Settings?.PreferredProteinVariant,
            string.Join(", ", user.Settings?.FavouriteDishNames ?? []));
    }

    private static AiSelectionRequest BuildWeekRequest(
        User user, List<DayMenuData> dayMenuData, List<MealSlot> mealSlots,
        List<string> previousChoices, List<string> macroPriority)
    {
        var weekMenu = dayMenuData.Select(d => new AiDayMenu
        {
            Date = d.Day.Date.ToString(DateFormat),
            DayOfWeek = d.Day.DayOfWeek,
            MealsNeeded = d.MealSlot.Count,
            AvailableDishes = d.Summaries
        }).ToList();

        return new AiSelectionRequest
        {
            Strategy = user.Settings?.Strategy ?? SelectionStrategy.Default,
            Date = dayMenuData.Count > 0
                ? dayMenuData[0].Day?.Date ?? DateOnly.FromDateTime(DateTime.Today)
                : DateOnly.FromDateTime(DateTime.Today),
            MealSlots = mealSlots,
            AvailableDishes = [],
            WeekMenu = weekMenu,
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
    }

    private async Task<AiSelectionResult> SelectDishesAsync(
        User user, AiSelectionRequest weekRequest, List<DayMenuData> dayMenuData,
        Dictionary<string, int> expectedPerDay, List<string> macroPriority, bool regenerate)
    {
        var useAi = user.Settings?.UseAiSelection ?? false;
        var weekResult = useAi
            ? await RunAiSelectionWithRetriesAsync(weekRequest, dayMenuData, expectedPerDay)
            : new AiSelectionResult { Picks = [] };

        if (!useAi)
            _logger.LogInformation("Using algorithmic selection for {DayCount} days", dayMenuData.Count);

        FillIncompleteDaysWithFallback(weekResult, dayMenuData, weekRequest, macroPriority, useAi, regenerate);
        return weekResult;
    }

    private async Task<AiSelectionResult> RunAiSelectionWithRetriesAsync(
        AiSelectionRequest weekRequest, List<DayMenuData> dayMenuData,
        Dictionary<string, int> expectedPerDay)
    {
        var totalExpected = expectedPerDay.Values.Sum();
        var weekMenu = weekRequest.WeekMenu!;

        _logger.LogInformation("Using AI selection for {DayCount} days, {TotalDishes} dishes, expecting {Expected} picks",
            weekMenu.Count, weekMenu.Sum(d => d.AvailableDishes.Count), totalExpected);

        var weekResult = new AiSelectionResult { Picks = [] };

        for (int attempt = 1; attempt <= MaxAiRetries; attempt++)
        {
            try
            {
                var aiResult = await _openAiService.SelectDishesAsync(weekRequest);
                if (aiResult == null)
                {
                    _logger.LogWarning("AI attempt {Attempt}: returned null", attempt);
                    continue;
                }

                weekResult = aiResult;

                if (ValidateAiResult(weekResult, expectedPerDay, attempt))
                    break;

                if (attempt == MaxAiRetries)
                    _logger.LogWarning("Max retries reached, will fill missing days with fallback");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI attempt {Attempt} failed", attempt);
            }
        }

        return weekResult;
    }

    private bool ValidateAiResult(AiSelectionResult result, Dictionary<string, int> expectedPerDay, int attempt)
    {
        var picksByDay = result.Picks.GroupBy(p => p.Date)
            .ToDictionary(g => g.Key, g => g.Count());
        var incompleteDays = expectedPerDay
            .Where(kv => picksByDay.GetValueOrDefault(kv.Key, 0) < kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        var overRepeatedDishes = result.Picks
            .GroupBy(p => p.DishId)
            .Where(g => g.Select(p => p.Date).Distinct().Count() > 2)
            .Select(g => g.Key)
            .ToList();

        var isComplete = incompleteDays.Count == 0;
        var hasGoodVariety = overRepeatedDishes.Count == 0;

        if (isComplete && hasGoodVariety)
        {
            _logger.LogInformation("AI attempt {Attempt}: all {Days} days complete, good variety ({Picks} picks)",
                attempt, expectedPerDay.Count, result.Picks.Count);
            return true;
        }

        if (!isComplete)
            _logger.LogWarning("AI attempt {Attempt}: {Incomplete}/{Total} days incomplete",
                attempt, incompleteDays.Count, expectedPerDay.Count);
        if (!hasGoodVariety)
            _logger.LogWarning("AI attempt {Attempt}: poor variety -- dishes on >2 days: [{Dishes}]",
                attempt, string.Join(", ", overRepeatedDishes));

        return false;
    }

    private void FillIncompleteDaysWithFallback(
        AiSelectionResult weekResult, List<DayMenuData> dayMenuData,
        AiSelectionRequest weekRequest,
        List<string> macroPriority, bool useAi, bool regenerate)
    {
        var fallbackWeekContext = BuildWeekContextFromPicks(weekResult, dayMenuData);

        // Iterate per DayMenuData entry (per category per day), NOT per date.
        // Each entry represents one meal category (e.g., "meal" or "breakfast") for one day.
        // Grouping by date would use the first entry's pool for ALL categories — wrong.
        foreach (var dayData in dayMenuData)
        {
            if (dayData.Summaries == null || dayData.Summaries.Count == 0) continue;

            var dateKey = dayData.Day.Date.ToString(DateFormat);
            var category = dayData.MealSlot.Category;
            var needed = dayData.MealSlot.Count;

            // Count existing picks for this specific (date, category)
            var currentPicks = weekResult.Picks
                .Count(p => p.Date == dateKey && p.MealCategory.Equals(category, StringComparison.OrdinalIgnoreCase));

            if (currentPicks >= needed) continue;

            _logger.LogInformation("{Mode} {Date} {Category}: has {Current}/{Needed} picks, filling with fallback",
                useAi ? "AI incomplete" : "Algorithm", dateKey, category, currentPicks, needed);

            // Remove only picks for this (date, category), not the whole date
            weekResult.Picks.RemoveAll(p => p.Date == dateKey
                && p.MealCategory.Equals(category, StringComparison.OrdinalIgnoreCase));

            var fallbackResult = _fallbackService.Select(dayData.Summaries, weekRequest.Strategy,
                [new MealSlot { Category = category, ApiCategory = dayData.MealSlot.ApiCategory, Count = needed }],
                fallbackWeekContext, weekRequest.ProteinGoalGrams, weekRequest.CarbGoalGrams, weekRequest.FatGoalGrams,
                macroPriority, weekRequest.FavouriteDishNames, weekRequest.MinFavouritesPerWeek,
                randomness: regenerate ? 0.15 : 0.0);

            var dayDishNames = fallbackWeekContext.GetValueOrDefault(dateKey) ?? [];
            foreach (var pick in fallbackResult.Picks)
            {
                pick.Date = dateKey;
                var dishName = dayData.Summaries.FirstOrDefault(s => s.Id == pick.DishId)?.Name ?? pick.DishId;
                dayDishNames.Add(dishName);
            }
            weekResult.Picks.AddRange(fallbackResult.Picks);
            fallbackWeekContext[dateKey] = dayDishNames;
        }
    }

    private static Dictionary<string, List<string>> BuildWeekContextFromPicks(
        AiSelectionResult weekResult, List<DayMenuData> dayMenuData)
    {
        var context = new Dictionary<string, List<string>>();

        foreach (var pick in weekResult.Picks)
        {
            if (!context.ContainsKey(pick.Date))
                context[pick.Date] = [];

            // Search across ALL DayMenuData entries for this date (meal + breakfast)
            var dishName = pick.DishId;
            foreach (var dmd in dayMenuData.Where(d => d.Day.Date.ToString(DateFormat) == pick.Date))
            {
                var found = dmd.Filtered?.FirstOrDefault(d => d.Id == pick.DishId)?.DishName;
                if (found != null) { dishName = found; break; }
            }
            context[pick.Date].Add(dishName);
        }

        return context;
    }

    private void FixOverRepeatedDishes(AiSelectionResult weekResult, List<DayMenuData> dayMenuData)
    {
        var overRepeated = weekResult.Picks
            .GroupBy(p => p.DishId)
            .Where(g => g.Select(p => p.Date).Distinct().Count() > 2)
            .ToList();

        foreach (var group in overRepeated)
        {
            var dishId = group.Key;
            var dates = group.Select(p => p.Date).Distinct().OrderBy(d => d).ToList();
            var datesToReplace = dates.Skip(2).ToList();

            foreach (var date in datesToReplace)
            {
                var pickToReplace = weekResult.Picks
                    .FirstOrDefault(p => p.DishId == dishId && p.Date == date);
                if (pickToReplace == null) continue;

                // Find the DayMenuData matching this pick's (date, category)
                var dayData = dayMenuData.FirstOrDefault(d =>
                    d.Day.Date.ToString(DateFormat) == date
                    && d.MealSlot.Category.Equals(pickToReplace.MealCategory, StringComparison.OrdinalIgnoreCase));
                // Fallback: any entry for this date
                dayData ??= dayMenuData.FirstOrDefault(d => d.Day.Date.ToString(DateFormat) == date);
                if (dayData?.Summaries == null) continue;

                var dayDishIds = weekResult.Picks.Where(p => p.Date == date).Select(p => p.DishId).ToHashSet();
                var alternative = dayData.Summaries
                    .Where(s => !dayDishIds.Contains(s.Id) && s.Id != dishId)
                    .OrderByDescending(s => s.Carb)
                    .FirstOrDefault();

                if (alternative == null) continue;

                _logger.LogInformation("Variety fix: replacing {OldDish} on {Date} with {NewDish}",
                    dishId, date, alternative.Id);
                pickToReplace.DishId = alternative.Id;
                pickToReplace.ProteinOption = alternative.ProteinOption;
                pickToReplace.MealCategory = alternative.MealCategory;
                pickToReplace.Reasoning = "Replaced for variety (dish was on >2 days)";
            }
        }
    }

    private async Task<WeeklyProposal> ResolvePicksToProposalAsync(
        Guid userId, User user, Subscription subscription,
        AiSelectionRequest weekRequest, AiSelectionResult weekResult,
        List<DayMenuData> dayMenuData, List<string> macroPriority,
        bool regenerate, List<DateOnly> lockedDays)
    {
        var weekContext = new Dictionary<string, List<string>>();
        var dayProposals = new List<DayProposal>();

        foreach (var dayData in dayMenuData)
        {
            var day = dayData.Day;
            var category = dayData.MealSlot.Category;
            var dateKey = day.Date.ToString(DateFormat);

            // Filter picks by BOTH date AND category — each DayMenuData handles only its own category
            var dayPicks = weekResult.Picks
                .Where(p => p.Date == dateKey
                    && p.MealCategory.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (dayPicks.Count == 0)
            {
                dayPicks = RunFallbackForDay(dayData, weekRequest, weekContext, macroPriority, regenerate);
                foreach (var p in dayPicks) p.Date = dateKey;
            }

            var dayDishes = ResolveDayPicks(
                userId, user, subscription, dayData, dayPicks);

            await _db.SaveChangesAsync();
            // Accumulate (not overwrite) week context — same date has multiple categories
            var existing = weekContext.GetValueOrDefault(dateKey) ?? [];
            existing.AddRange(dayDishes.Select(d => d.DishName));
            weekContext[dateKey] = existing;

            // Only sum original macros for slots matching THIS category's MealType
            // (not all slots — that would double-count when merging meal + breakfast)
            var apiCategory = dayData.MealSlot.ApiCategory;
            var categorySlots = day.Slots
                .Where(s => s.MealType.ToLower() switch { "dinner" => "lunch", var t => t } == apiCategory)
                .ToList();

            dayProposals.Add(new DayProposal
            {
                Date = day.Date,
                DayOfWeek = day.DayOfWeek,
                Dishes = dayDishes,
                OriginalKcal = categorySlots.Sum(s => s.CurrentKcal),
                OriginalProtein = categorySlots.Sum(s => s.CurrentProtein),
                OriginalCarb = categorySlots.Sum(s => s.CurrentCarb),
                OriginalFat = categorySlots.Sum(s => s.CurrentFat)
            });
        }

        // Merge proposals for the same date (e.g., 2 meals + 1 breakfast = one day)
        var mergedProposals = dayProposals
            .GroupBy(d => d.Date)
            .Select(g => new DayProposal
            {
                Date = g.Key,
                DayOfWeek = g.First().DayOfWeek,
                Dishes = g.SelectMany(d => d.Dishes)
                    .OrderBy(d => d.MealCategory.ToLower() switch { "breakfast" => 0, "snack" => 2, _ => 1 })
                    .ThenBy(d => d.SlotIndex)
                    .ToList(),
                OriginalKcal = g.Sum(d => d.OriginalKcal),
                OriginalProtein = g.Sum(d => d.OriginalProtein),
                OriginalCarb = g.Sum(d => d.OriginalCarb),
                OriginalFat = g.Sum(d => d.OriginalFat)
            })
            .OrderBy(d => d.Date)
            .ToList();

        return new WeeklyProposal
        {
            Days = mergedProposals,
            LockedDays = lockedDays.ToList()
        };
    }

    private List<AiDishPick> RunFallbackForDay(
        DayMenuData dayData, AiSelectionRequest weekRequest,
        Dictionary<string, List<string>> weekContext,
        List<string> macroPriority, bool regenerate)
    {
        var category = dayData.MealSlot.Category;
        _logger.LogInformation("Using fallback for {Date} (no AI picks)", dayData.Day.Date);

        var fallbackResult = _fallbackService.Select(dayData.Summaries, weekRequest.Strategy,
            [new MealSlot { Category = category, ApiCategory = dayData.MealSlot.ApiCategory, Count = dayData.MealSlot.Count }],
            weekContext, weekRequest.ProteinGoalGrams, weekRequest.CarbGoalGrams, weekRequest.FatGoalGrams,
            macroPriority, weekRequest.FavouriteDishNames, weekRequest.MinFavouritesPerWeek,
            randomness: regenerate ? 0.15 : 0.0);

        return fallbackResult.Picks;
    }

    private List<ProposedDish> ResolveDayPicks(
        Guid userId, User user, Subscription subscription,
        DayMenuData dayData, List<AiDishPick> dayPicks)
    {
        var day = dayData.Day;
        var filtered = dayData.Filtered;
        var slotsByCategory = dayData.SlotsByCategory;
        var category = dayData.MealSlot.Category;
        var dayDishes = new List<ProposedDish>();
        var firstSlot = slotsByCategory.GetValueOrDefault(category)?.FirstOrDefault();

        foreach (var pick in dayPicks)
        {
            var dish = filtered.FirstOrDefault(d => d.Id == pick.DishId);
            if (dish is null)
            {
                _logger.LogWarning("Dish {DishId} not found for {Date}", pick.DishId, day.Date);
                continue;
            }

            var variant = ResolveVariant(dish, pick.ProteinOption, user.Settings?.PreferredProteinVariant);
            if (variant is null) continue;

            var slotsForCategory = slotsByCategory.GetValueOrDefault(category);
            var matchingSlot = slotsForCategory?.ElementAtOrDefault(pick.SlotIndex);
            var slotUniqueId = matchingSlot?.UniqueId ?? firstSlot?.UniqueId ?? string.Empty;
            var defaultKcalRange = subscription.MealTypes.FirstOrDefault()?.KcalRange?.ToLower() ?? string.Empty;
            var defaultProteinCategory = subscription.MealTypes.FirstOrDefault()?.ProteinCategory ?? string.Empty;

            // Check if this pick matches what Delicut already has for this slot
            var originalSlot = slotsForCategory?.ElementAtOrDefault(pick.SlotIndex);
            var matchesOriginal = originalSlot != null
                && originalSlot.CurrentDishId == pick.DishId
                && (originalSlot.CurrentProteinOption ?? "").Equals(variant.ProteinOption, StringComparison.OrdinalIgnoreCase);

            var pending = new PendingSelection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DeliveryDate = day.Date,
                DeliveryId = day.DeliveryId,
                UniqueId = slotUniqueId,
                MealCategory = pick.MealCategory,
                MealType = dayData.MealSlot.ApiCategory,
                SlotIndex = pick.SlotIndex,
                DishId = pick.DishId,
                DishName = dish.DishName,
                VariantProtein = variant.ProteinOption,
                VariantSize = !string.IsNullOrEmpty(variant.Size) ? variant.Size : defaultKcalRange,
                VariantProteinCategory = !string.IsNullOrEmpty(variant.ProteinCategory) ? variant.ProteinCategory : defaultProteinCategory,
                Kcal = variant.Kcal,
                Protein = variant.Protein,
                Carb = variant.Carb,
                Fat = variant.Fat,
                MatchesOriginal = matchesOriginal,
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
                AiReasoning = pick.Reasoning,
                MatchesOriginal = matchesOriginal
            });
        }

        return dayDishes;
    }

    private static DishVariant? ResolveVariant(Dish dish, string proteinOption, string? preferredProtein)
    {
        var variant = dish.Variants.FirstOrDefault(v =>
            v.ProteinOption.Equals(proteinOption, StringComparison.OrdinalIgnoreCase));

        if (variant is null && !string.IsNullOrEmpty(preferredProtein))
            variant = dish.Variants.FirstOrDefault(v =>
                v.ProteinOption.Equals(preferredProtein, StringComparison.OrdinalIgnoreCase));

        return variant ?? dish.Variants.FirstOrDefault();
    }
}
