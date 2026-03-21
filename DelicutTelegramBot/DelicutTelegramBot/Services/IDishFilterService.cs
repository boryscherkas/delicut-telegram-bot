using DelicutTelegramBot.Models.Delicut;

namespace DelicutTelegramBot.Services;

public interface IDishFilterService
{
    List<Dish> Filter(List<Dish> dishes, List<string> stopWords, List<string> avoidIngredients, List<string> avoidCategories, string kcalRange, string proteinCategory);
}
