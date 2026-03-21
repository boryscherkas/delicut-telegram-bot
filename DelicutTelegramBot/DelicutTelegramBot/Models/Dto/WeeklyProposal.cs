namespace DelicutTelegramBot.Models.Dto;

public class WeeklyProposal
{
    public List<DayProposal> Days { get; set; } = [];
    public List<DateOnly> LockedDays { get; set; } = [];
}

public class DayProposal
{
    public DateOnly Date { get; set; }
    public string DayOfWeek { get; set; } = string.Empty;
    public List<ProposedDish> Dishes { get; set; } = [];
    public double TotalKcal => Dishes.Sum(d => d.Kcal);
    public double TotalProtein => Dishes.Sum(d => d.Protein);
    public double TotalCarb => Dishes.Sum(d => d.Carb);
    public double TotalFat => Dishes.Sum(d => d.Fat);

    // Original (Delicut auto-selected) totals for comparison
    public double OriginalKcal { get; set; }
    public double OriginalProtein { get; set; }
    public double OriginalCarb { get; set; }
    public double OriginalFat { get; set; }
}
