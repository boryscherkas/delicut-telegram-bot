using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public class FallbackSelectionService : IFallbackSelectionService
{
    public AiSelectionResult Select(
        List<DishSummary> dishes,
        SelectionStrategy strategy,
        List<MealSlot> mealSlots,
        Dictionary<string, List<string>> weekContext,
        double? proteinGoal = null,
        double? carbGoal = null,
        double? fatGoal = null)
    {
        // Collect used cuisines AND used dish names from other days for variety
        var usedCuisines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedDishNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, dishNames) in weekContext)
        {
            foreach (var name in dishNames)
                usedDishNames.Add(name);
        }

        // Get dish names from immediately adjacent days (yesterday/tomorrow in context)
        // for stronger penalty
        var adjacentDishNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // weekContext keys are day names; we penalize all of them but adjacent more
        // (we don't know which day "this" is relative to context, so treat all as adjacent for safety)
        foreach (var (_, dishNames) in weekContext)
        {
            foreach (var name in dishNames)
            {
                adjacentDishNames.Add(name);
                // Also extract cuisine from used dishes if possible
            }
        }

        // Pre-compute normalisation constants
        double maxProtein = dishes.Count > 0 ? dishes.Max(d => d.Protein) : 1.0;
        double maxKcal = dishes.Count > 0 ? dishes.Max(d => d.Kcal) : 1.0;
        if (maxProtein == 0) maxProtein = 1.0;
        if (maxKcal == 0) maxKcal = 1.0;

        // Group by meal category
        var pool = dishes
            .GroupBy(d => d.MealCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var picks = new List<AiDishPick>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int slotIndex = 0; slotIndex < mealSlots.Count; slotIndex++)
        {
            var slot = mealSlots[slotIndex];

            if (!pool.TryGetValue(slot.Category, out var candidates))
                continue;

            var available = candidates
                .Where(d => !usedIds.Contains(d.Id))
                .ToList();

            var ranked = available
                .Select(d => (dish: d, score: Score(d, strategy, maxProtein, maxKcal,
                    usedCuisines, usedDishNames, proteinGoal, carbGoal, fatGoal)))
                .OrderByDescending(t => t.score)
                .Take(slot.Count)
                .ToList();

            foreach (var (dish, _) in ranked)
            {
                usedIds.Add(dish.Id);
                usedCuisines.Add(dish.Cuisine);
                picks.Add(new AiDishPick
                {
                    DishId = dish.Id,
                    ProteinOption = dish.ProteinOption,
                    MealCategory = dish.MealCategory,
                    SlotIndex = slotIndex,
                    Reasoning = $"Fallback selection using {strategy} strategy."
                });
            }
        }

        return new AiSelectionResult { Picks = picks };
    }

    private static double Score(
        DishSummary dish,
        SelectionStrategy strategy,
        double maxProtein,
        double maxKcal,
        HashSet<string> usedCuisines,
        HashSet<string> usedDishNames,
        double? proteinGoal,
        double? carbGoal,
        double? fatGoal)
    {
        double strategyScore;

        if (proteinGoal.HasValue || carbGoal.HasValue || fatGoal.HasValue)
        {
            // Macro-goal scoring: how well does this dish contribute to goals?
            // Priority: protein (weight 0.5) > carbs (0.3) > fat (0.2)
            double protScore = proteinGoal > 0 ? Math.Min(dish.Protein / proteinGoal.Value, 1.0) : 0.5;
            double carbScore = carbGoal > 0 ? Math.Min(dish.Carb / carbGoal.Value, 1.0) : 0.5;
            double fatScore = fatGoal > 0 ? Math.Min(dish.Fat / fatGoal.Value, 1.0) : 0.5;
            // Penalize overshooting fat
            if (fatGoal > 0 && dish.Fat > fatGoal.Value * 0.5)
                fatScore *= 0.7;
            strategyScore = protScore * 0.5 + carbScore * 0.3 + fatScore * 0.2;
        }
        else
        {
            strategyScore = strategy switch
            {
                SelectionStrategy.MacrosMax => dish.Protein / maxProtein,
                SelectionStrategy.LowestCal => 1.0 - (dish.Kcal / maxKcal),
                _ => dish.Rating / 5.0
            };
        }

        double ratingScore = dish.Rating / 5.0;

        // Variety: penalize same cuisine (mild) and same dish name on other days (strong)
        double varietyScore = 1.0;
        if (usedCuisines.Contains(dish.Cuisine))
            varietyScore -= 0.3;
        if (usedDishNames.Contains(dish.Name))
            varietyScore -= 0.6; // Strong penalty for same dish on other days

        varietyScore = Math.Max(varietyScore, 0.0);

        return strategyScore * 0.5 + ratingScore * 0.2 + varietyScore * 0.3;
    }
}
