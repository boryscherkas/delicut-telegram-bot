using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public interface IFallbackSelectionService
{
    AiSelectionResult Select(List<DishSummary> dishes, SelectionStrategy strategy, List<MealSlot> mealSlots, Dictionary<string, List<string>> weekContext,
        double? proteinGoal = null, double? carbGoal = null, double? fatGoal = null);
}
