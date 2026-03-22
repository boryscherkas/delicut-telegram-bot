using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Helpers;

/// <summary>
/// Shared helper for flattening Dish lists to DishSummary.
/// Used by both MenuFetchService (initial fetch) and MenuSelectionService (GetAlternativesAsync).
/// </summary>
public static class DishSummaryHelper
{
    /// <summary>
    /// Flattens dishes to DishSummary. If preferredProtein is set, picks only that variant
    /// per dish (falls back to first variant if preferred not available).
    /// Otherwise creates one summary per variant.
    /// </summary>
    public static List<DishSummary> FlattenToDishSummaries(
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
                    Rating = 0,
                    TotalRatings = 0,
                    SpiceLevel = dish.SpiceLevel,
                    ProteinOption = variant.ProteinOption,
                    MealCategory = mealCategory
                });
            }
        }
        return summaries;
    }
}
