using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Models.Dto;

public class AiSelectionRequest
{
    public SelectionStrategy Strategy { get; set; }
    public DateOnly Date { get; set; }
    public List<MealSlot> MealSlots { get; set; } = [];
    public List<DishSummary> AvailableDishes { get; set; } = [];

    // Multi-day: when set, AI selects for the whole week in one call
    public List<AiDayMenu>? WeekMenu { get; set; }
    public List<string> StopWords { get; set; } = [];
    public List<string> PreviousChoices { get; set; } = [];
    public bool PreferHistory { get; set; }
    public Dictionary<string, List<string>> WeekContext { get; set; } = new();

    // Daily macro goals (priority order: protein > carbs > fat)
    public double? ProteinGoalGrams { get; set; }
    public double? CarbGoalGrams { get; set; }
    public double? FatGoalGrams { get; set; }

    // Priority order: ["p","c","f"] — first = highest priority
    public List<string> MacroPriority { get; set; } = ["p", "c", "f"];

    // Preferred protein variant — always pick this when available for a dish
    public string? PreferredProteinVariant { get; set; }

    // Favourite dishes that must appear at least MinPerWeek times across the week
    public List<string> FavouriteDishNames { get; set; } = [];
    public int MinFavouritesPerWeek { get; set; }
}

public class AiDayMenu
{
    public string Date { get; set; } = string.Empty;
    public string DayOfWeek { get; set; } = string.Empty;
    public int MealsNeeded { get; set; }
    public List<DishSummary> AvailableDishes { get; set; } = [];
}

public class AiSelectionResult
{
    public List<AiDishPick> Picks { get; set; } = [];
}

public class AiDishPick
{
    public string Date { get; set; } = string.Empty;  // "2026-03-24" — which day
    public string DishId { get; set; } = string.Empty;
    public string ProteinOption { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public string Reasoning { get; set; } = string.Empty;
}

public class MealSlot
{
    /// <summary>Internal category: "meal", "breakfast", "snack" — used for grouping slots</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>API category: "lunch", "breakfast", "evening_snack" — used for FetchMenuAsync</summary>
    public string ApiCategory { get; set; } = string.Empty;

    public int Count { get; set; }
}

public class DishSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Cuisine { get; set; } = string.Empty;
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public double Rating { get; set; }
    public int TotalRatings { get; set; }
    public string SpiceLevel { get; set; } = string.Empty;
    public string ProteinOption { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
}
