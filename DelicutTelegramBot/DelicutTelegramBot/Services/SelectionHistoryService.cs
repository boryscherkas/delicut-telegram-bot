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
        var dates = selections.Select(s => s.DeliveryDate).Distinct().ToList();
        var dishIds = selections.Select(s => s.DishId).Distinct().ToList();

        var existingKeys = (await _db.SelectionHistories
            .Where(h => h.UserId == userId && dishIds.Contains(h.DishId) && dates.Contains(h.SelectedDate))
            .Select(h => new { h.DishId, h.SelectedDate, h.MealCategory })
            .ToListAsync())
            .ToHashSet();

        foreach (var sel in selections)
        {
            if (existingKeys.Contains(new { sel.DishId, SelectedDate = sel.DeliveryDate, sel.MealCategory }))
                continue;

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
