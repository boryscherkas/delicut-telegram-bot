using DelicutTelegramBot.Models.Delicut;

namespace DelicutTelegramBot.Services;

public class DishFilterService : IDishFilterService
{
    public List<Dish> Filter(
        List<Dish> dishes,
        List<string> stopWords,
        List<string> avoidIngredients,
        List<string> avoidCategories,
        string kcalRange,
        string proteinCategory)
    {
        var result = new List<Dish>();

        foreach (var dish in dishes)
        {
            // 1. Stop-word check: remove dish if its name contains any stop word
            if (stopWords.Any(sw => dish.DishName.Contains(sw, StringComparison.OrdinalIgnoreCase)))
                continue;

            // 2. Avoid-ingredient check: remove dish if it contains any avoided ingredient
            if (avoidIngredients.Any(ai =>
                    dish.Ingredients.Any(i => i.Equals(ai, StringComparison.OrdinalIgnoreCase))))
                continue;

            // 3. Avoid-category check: remove dish if Cuisine or any DishType item matches
            if (avoidCategories.Any(ac =>
                    dish.Cuisine.Equals(ac, StringComparison.OrdinalIgnoreCase) ||
                    dish.DishType.Any(dt => dt.Equals(ac, StringComparison.OrdinalIgnoreCase))))
                continue;

            // 4. Variant filtering: keep variants matching kcalRange.
            //    Prefer the subscription's proteinCategory, but also include other categories
            //    (e.g., "balance") so the AI can pick variants with better macros.
            //    The preferred category's variants come first.
            var matchingVariants = dish.Variants
                .Where(v => v.Size.Equals(kcalRange, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.ProteinCategory.Equals(proteinCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 5. Remove dishes with no remaining variants
            if (matchingVariants.Count == 0)
                continue;

            // Create a new Dish with the filtered variants to avoid mutating the original
            var filtered = new Dish
            {
                Id = dish.Id,
                RecipeId = dish.RecipeId,
                DishName = dish.DishName,
                MealCategory = dish.MealCategory,
                Cuisine = dish.Cuisine,
                DishType = dish.DishType,
                Description = dish.Description,
                SpiceLevel = dish.SpiceLevel,
                Ingredients = dish.Ingredients,
                AllergensContain = dish.AllergensContain,
                AllergensFreeFrom = dish.AllergensFreeFrom,
                AvgRating = dish.AvgRating,
                TotalRatings = dish.TotalRatings,
                AssignedDays = dish.AssignedDays,
                ProteinCategoryInfo = dish.ProteinCategoryInfo,
                Variants = matchingVariants
            };

            result.Add(filtered);
        }

        return result;
    }
}
