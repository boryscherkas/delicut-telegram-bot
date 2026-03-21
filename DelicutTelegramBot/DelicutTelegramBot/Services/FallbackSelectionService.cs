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
        double? fatGoal = null,
        List<string>? favouriteDishNames = null,
        int minFavouritesPerWeek = 0)
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
        var usedCuisines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    usedCuisines, usedDishNames, proteinGoal, carbGoal, fatGoal,
                    favourites, favouritesStillNeeded)))
                .OrderByDescending(t => t.score)
                .Take(slot.Count)
                .ToList();

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
                    SlotIndex = slotIndex,
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
        double maxKcal,
        HashSet<string> usedCuisines,
        HashSet<string> usedDishNames,
        double? proteinGoal,
        double? carbGoal,
        double? fatGoal,
        HashSet<string> favourites,
        int favouritesStillNeeded)
    {
        // Strategy/macro score
        double strategyScore;
        if (proteinGoal.HasValue || carbGoal.HasValue || fatGoal.HasValue)
        {
            double protScore = proteinGoal > 0 ? Math.Min(dish.Protein / proteinGoal.Value, 1.0) : 0.5;
            double carbScore = carbGoal > 0 ? Math.Min(dish.Carb / carbGoal.Value, 1.0) : 0.5;
            double fatScore = fatGoal > 0 ? Math.Min(dish.Fat / fatGoal.Value, 1.0) : 0.5;
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
            varietyScore -= 0.3;
        if (usedDishNames.Contains(dish.Name))
            varietyScore -= 0.6;
        varietyScore = Math.Max(varietyScore, 0.0);

        // Weights: strategy 0.4, variety 0.3, favourites 0.3
        return strategyScore * 0.4 + varietyScore * 0.3 + favouriteScore * 0.3;
    }
}
