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

    // Preferred protein variant (e.g., "Shrimps", "Chicken") — always pick this when available
    public string? PreferredProteinVariant { get; set; }

    // Favourite dishes that must appear at least MinFavouritesPerWeek times per week
    public List<string> FavouriteDishNames { get; set; } = [];
    public int MinFavouritesPerWeek { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
