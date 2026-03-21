namespace DelicutTelegramBot.Models.Domain;

public class SelectionHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DishId { get; set; } = string.Empty;
    public string DishName { get; set; } = string.Empty;
    public string VariantProtein { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public DateOnly SelectedDate { get; set; }
    public bool WasUserChoice { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
