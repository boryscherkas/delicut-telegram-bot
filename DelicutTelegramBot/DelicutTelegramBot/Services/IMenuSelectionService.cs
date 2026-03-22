using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public interface IMenuSelectionService
{
    Task<WeeklyProposal> SelectForWeekAsync(Guid userId, bool regenerate = false);
    Task<List<DishAlternative>> GetAlternativesAsync(Guid userId, DateOnly date, string mealCategory, int slotIndex);
    Task ReplaceDishAsync(Guid userId, DateOnly date, string mealCategory, int slotIndex, string newDishId, string proteinOption);
    Task ConfirmDayAsync(Guid userId, DateOnly date);
    Task SubmitDayAsync(Guid userId, DateOnly date);
    Task ConfirmWeekAsync(Guid userId);
}
