namespace DelicutTelegramBot.Models.Dto;

public class ProposedDish
{
    public string DishId { get; set; } = string.Empty;
    public string DishName { get; set; } = string.Empty;
    public string ProteinOption { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public string AiReasoning { get; set; } = string.Empty;
}
