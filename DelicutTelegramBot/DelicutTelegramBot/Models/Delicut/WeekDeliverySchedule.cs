namespace DelicutTelegramBot.Models.Delicut;

public class WeekDeliverySchedule
{
    public List<DeliveryDay> Days { get; set; } = [];
}

public class DeliveryDay
{
    public DateOnly Date { get; set; }
    public string DayOfWeek { get; set; } = string.Empty;
    public string DeliveryId { get; set; } = string.Empty;
    public List<DeliverySlot> Slots { get; set; } = [];
    public List<string> MealCategories { get; set; } = [];
    public bool IsLocked { get; set; }
}

/// <summary>
/// Each meal slot within a delivery day has its own unique_id.
/// The unique_id is needed for FetchMenuAsync and SubmitDishSelectionAsync.
/// </summary>
public class DeliverySlot
{
    public string UniqueId { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public string MealType { get; set; } = string.Empty;
    public string KcalRange { get; set; } = string.Empty;
    public string ProteinCategory { get; set; } = string.Empty;
    public string? CurrentDishId { get; set; }
    public string? CurrentDishName { get; set; }
    public string? CurrentProteinOption { get; set; }
    public bool IsAutoSelect { get; set; }
}
