namespace DelicutTelegramBot.Models.Domain;

public class PendingSelection
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly DeliveryDate { get; set; }
    public string DeliveryId { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public string DishId { get; set; } = string.Empty;
    public string DishName { get; set; } = string.Empty;
    public string VariantProtein { get; set; } = string.Empty;
    public string VariantSize { get; set; } = string.Empty;
    public string VariantProteinCategory { get; set; } = string.Empty;
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public PendingSelectionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
