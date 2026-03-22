using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public class FallbackSelectionService : IFallbackSelectionService
{
    // Scoring weights for the final composite score
    private const double StrategyWeight = 0.4;
    private const double VarietyWeight = 0.3;
    private const double FavouriteWeight = 0.3;

    // Macro priority weights: first macro gets highest weight, then descending
    private static readonly double[] MacroPriorityWeights = [0.5, 0.3, 0.2];

    // Variety penalties: reduce score for repeated cuisines/dishes within a day
    private const double RepeatedCuisinePenalty = 0.3;
    private const double RepeatedDishPenalty = 0.6;
    public AiSelectionResult Select(
        List<DishSummary> dishes,
        SelectionStrategy strategy,
        List<MealSlot> mealSlots,
        Dictionary<string, List<string>> weekContext,
        double? proteinGoal = null,
        double? carbGoal = null,
        double? fatGoal = null,
        List<string>? macroPriority = null,
        List<string>? favouriteDishNames = null,
        int minFavouritesPerWeek = 0,
        double randomness = 0.0)
    {
        var usedDishNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var favourites = new HashSet<string>(favouriteDishNames ?? [], StringComparer.OrdinalIgnoreCase);

        // Count how many times favourites already appear in weekContext
        var favouriteCountInWeek = 0;
        foreach (var (_, dishNames) in weekContext)
        {
            foreach (var name in dishNames)
            {
                usedDishNames.Add(name);
                if (favourites.Contains(name))
                    favouriteCountInWeek++;
            }
        }

        var favouritesStillNeeded = Math.Max(0, minFavouritesPerWeek - favouriteCountInWeek);

        // Pre-compute normalisation constants (max values across all dishes)
        double maxProtein = dishes.Count > 0 ? dishes.Max(d => d.Protein) : 1.0;
        double maxCarb = dishes.Count > 0 ? dishes.Max(d => d.Carb) : 1.0;
        double maxFat = dishes.Count > 0 ? dishes.Max(d => d.Fat) : 1.0;
        double maxKcal = dishes.Count > 0 ? dishes.Max(d => d.Kcal) : 1.0;
        if (maxProtein == 0) maxProtein = 1.0;
        if (maxCarb == 0) maxCarb = 1.0;
        if (maxFat == 0) maxFat = 1.0;
        if (maxKcal == 0) maxKcal = 1.0;

        // Group by meal category
        var pool = dishes
            .GroupBy(d => d.MealCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var picks = new List<AiDishPick>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedCuisines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int slotIndex = 0; slotIndex < mealSlots.Count; slotIndex++)
        {
            var slot = mealSlots[slotIndex];

            if (!pool.TryGetValue(slot.Category, out var candidates))
                continue;

            // Deduplicate: keep best-scoring variant per dish ID, then exclude already-used
            var available = candidates
                .Where(d => !usedIds.Contains(d.Id))
                .GroupBy(d => d.Id)
                .Select(g => g.First()) // One entry per dish (preferred variant comes first from flatten)
                .ToList();

            var rng = randomness > 0 ? new Random() : null;
            var ranked = available
                .Select(d =>
                {
                    var score = Score(d, strategy, maxProtein, maxCarb, maxFat, maxKcal,
                        usedCuisines, usedDishNames, proteinGoal, carbGoal, fatGoal,
                        macroPriority ?? ["p", "c", "f"], favourites, favouritesStillNeeded);
                    // Add random jitter: randomness=0.15 means ±15% of score range
                    if (rng != null)
                        score += (rng.NextDouble() - 0.5) * 2 * randomness;
                    return (dish: d, score);
                })
                .OrderByDescending(t => t.score)
                .Take(slot.Count)
                .ToList();

            var pickIndex = 0;
            foreach (var (dish, _) in ranked)
            {
                usedIds.Add(dish.Id);
                usedCuisines.Add(dish.Cuisine);
                if (favourites.Contains(dish.Name))
                    favouritesStillNeeded = Math.Max(0, favouritesStillNeeded - 1);

                picks.Add(new AiDishPick
                {
                    DishId = dish.Id,
                    ProteinOption = dish.ProteinOption,
                    MealCategory = dish.MealCategory,
                    SlotIndex = pickIndex++,
                    Reasoning = $"Fallback: {strategy} strategy."
                });
            }
        }

        return new AiSelectionResult { Picks = picks };
    }

    private static double Score(
        DishSummary dish,
        SelectionStrategy strategy,
        double maxProtein,
        double maxCarb,
        double maxFat,
        double maxKcal,
        HashSet<string> usedCuisines,
        HashSet<string> usedDishNames,
        double? proteinGoal,
        double? carbGoal,
        double? fatGoal,
        List<string> macroPriority,
        HashSet<string> favourites,
        int favouritesStillNeeded)
    {
        // Strategy/macro score using RELATIVE ranking within the dish pool.
        // Score = how this dish compares to the BEST available dish for each macro.
        // This ensures high-carb dishes score significantly higher than low-carb dishes
        // when carbs are the goal — even though no single dish hits the full daily target.
        double strategyScore;
        if (proteinGoal.HasValue || carbGoal.HasValue || fatGoal.HasValue)
        {
            // Relative score: how does this dish compare to the best in the pool? (0-1)
            var relativeScores = new Dictionary<string, double>
            {
                ["p"] = proteinGoal > 0 ? dish.Protein / maxProtein : 0.5,
                ["c"] = carbGoal > 0 ? dish.Carb / maxCarb : 0.5,
                ["f"] = fatGoal > 0 ? dish.Fat / maxFat : 0.5,
            };

            strategyScore = 0;
            for (int i = 0; i < macroPriority.Count && i < MacroPriorityWeights.Length; i++)
            {
                if (relativeScores.TryGetValue(macroPriority[i], out var s))
                    strategyScore += s * MacroPriorityWeights[i];
            }
        }
        else
        {
            strategyScore = strategy switch
            {
                SelectionStrategy.MacrosMax => dish.Protein / maxProtein,
                SelectionStrategy.LowestCal => 1.0 - (dish.Kcal / maxKcal),
                _ => 0.5 // No rating-based scoring — rating is ignored
            };
        }

        // Favourite bonus: if we still need favourites this week, boost them significantly
        double favouriteScore = 0.0;
        if (favourites.Contains(dish.Name) && favouritesStillNeeded > 0)
            favouriteScore = 1.0;

        // Variety penalty
        double varietyScore = 1.0;
        if (usedCuisines.Contains(dish.Cuisine))
            varietyScore -= RepeatedCuisinePenalty;
        if (usedDishNames.Contains(dish.Name))
            varietyScore -= RepeatedDishPenalty;
        varietyScore = Math.Max(varietyScore, 0.0);

        return strategyScore * StrategyWeight + varietyScore * VarietyWeight + favouriteScore * FavouriteWeight;
    }
}
