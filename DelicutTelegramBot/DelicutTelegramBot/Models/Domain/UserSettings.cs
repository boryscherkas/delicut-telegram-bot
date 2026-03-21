namespace DelicutTelegramBot.Models.Domain;

public class UserSettings
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public SelectionStrategy Strategy { get; set; }
    public List<string> StopWords { get; set; } = [];
    public bool PreferHistory { get; set; }

    // Daily macro goals (nullable = not set)
    public double? ProteinGoalGrams { get; set; }
    public double? CarbGoalGrams { get; set; }
    public double? FatGoalGrams { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
