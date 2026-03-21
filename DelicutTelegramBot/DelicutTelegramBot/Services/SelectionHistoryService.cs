using Microsoft.EntityFrameworkCore;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Services;

public class SelectionHistoryService : ISelectionHistoryService
{
    private readonly AppDbContext _db;

    public SelectionHistoryService(AppDbContext db) => _db = db;

    public async Task<List<string>> GetPreviousChoiceNamesAsync(Guid userId, int maxCount = 50)
    {
        return await _db.SelectionHistories
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.SelectedDate)
            .Select(h => h.DishName)
            .Distinct()
            .Take(maxCount)
            .ToListAsync();
    }

    public async Task RecordSelectionsAsync(Guid userId, List<PendingSelection> selections, bool wasUserChoice)
    {
        foreach (var sel in selections)
        {
            var exists = await _db.SelectionHistories.AnyAsync(h =>
                h.UserId == userId &&
                h.DishId == sel.DishId &&
                h.SelectedDate == sel.DeliveryDate &&
                h.MealCategory == sel.MealCategory);

            if (exists) continue;

            _db.SelectionHistories.Add(new SelectionHistory
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DishId = sel.DishId,
                DishName = sel.DishName,
                VariantProtein = sel.VariantProtein,
                MealCategory = sel.MealCategory,
                SelectedDate = sel.DeliveryDate,
                WasUserChoice = wasUserChoice,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
    }
}
