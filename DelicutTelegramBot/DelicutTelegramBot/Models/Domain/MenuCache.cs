using DelicutTelegramBot.Models.Delicut;

namespace DelicutTelegramBot.Models.Domain;

/// <summary>
/// Caches fetched menus in the database to avoid repeated API calls.
/// One row per (user, date, mealCategory). Menu stored as JSONB.
/// </summary>
public class MenuCache
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly DeliveryDate { get; set; }
    public string MealCategory { get; set; } = string.Empty; // "meal", "breakfast"
    public List<Dish> Dishes { get; set; } = [];
    public DateTime FetchedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public User User { get; set; } = null!;
}
