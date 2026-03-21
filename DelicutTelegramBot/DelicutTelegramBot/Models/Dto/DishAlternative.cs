namespace DelicutTelegramBot.Models.Dto;

public class DishAlternative
{
    public string DishId { get; set; } = string.Empty;
    public string DishName { get; set; } = string.Empty;
    public string ProteinOption { get; set; } = string.Empty;
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public double AvgRating { get; set; }
}
