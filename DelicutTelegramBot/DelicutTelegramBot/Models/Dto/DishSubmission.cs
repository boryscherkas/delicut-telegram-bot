namespace DelicutTelegramBot.Models.Dto;

public class DishSubmission
{
    public string DishId { get; set; } = string.Empty;
    public string ProteinOption { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public string Size { get; set; } = string.Empty;
    public string ProteinCategory { get; set; } = string.Empty;
}

public class PastDishSelection
{
    public string DishId { get; set; } = string.Empty;
    public string DishName { get; set; } = string.Empty;
    public string ProteinOption { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
}
