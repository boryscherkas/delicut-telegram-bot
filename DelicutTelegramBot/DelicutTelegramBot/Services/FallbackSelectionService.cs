using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public class FallbackSelectionService : IFallbackSelectionService
{
    public AiSelectionResult Select(
        List<DishSummary> dishes,
        SelectionStrategy strategy,
        List<MealSlot> mealSlots,
        Dictionary<string, List<string>> weekContext)
    {
        var usedCuisines = weekContext.Values
            .SelectMany(v => v)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pre-compute normalisation constants across the full dish list
        double maxProtein = dishes.Count > 0 ? dishes.Max(d => d.Protein) : 1.0;
        double maxKcal    = dishes.Count > 0 ? dishes.Max(d => d.Kcal)    : 1.0;

        // Avoid divide-by-zero
        if (maxProtein == 0) maxProtein = 1.0;
        if (maxKcal    == 0) maxKcal    = 1.0;

        // Group by meal category (case-insensitive)
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

            // Filter out dishes already picked
            var available = candidates
                .Where(d => !usedIds.Contains(d.Id))
                .ToList();

            // Score and rank
            var ranked = available
                .Select(d => (dish: d, score: Score(d, strategy, maxProtein, maxKcal, usedCuisines)))
                .OrderByDescending(t => t.score)
                .Take(slot.Count)
                .ToList();

            foreach (var (dish, _) in ranked)
            {
                usedIds.Add(dish.Id);
                picks.Add(new AiDishPick
                {
                    DishId        = dish.Id,
                    ProteinOption = dish.ProteinOption,
                    MealCategory  = dish.MealCategory,
                    SlotIndex     = slotIndex,
                    Reasoning     = $"Fallback selection using {strategy} strategy."
                });
            }
        }

        return new AiSelectionResult { Picks = picks };
    }

    // ── scoring ───────────────────────────────────────────────────────────────

    private static double Score(
        DishSummary dish,
        SelectionStrategy strategy,
        double maxProtein,
        double maxKcal,
        HashSet<string> usedCuisines)
    {
        double strategyScore = strategy switch
        {
            SelectionStrategy.MacrosMax => dish.Protein / maxProtein,
            SelectionStrategy.LowestCal => 1.0 - (dish.Kcal / maxKcal),
            _                           => dish.Rating / 5.0
        };

        double ratingScore  = dish.Rating / 5.0;
        double varietyScore = usedCuisines.Contains(dish.Cuisine) ? 0.0 : 1.0;

        return strategyScore * 0.6 + ratingScore * 0.3 + varietyScore * 0.1;
    }
}
