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
    public string UniqueId { get; set; } = string.Empty;
    public List<string> MealCategories { get; set; } = [];
    public bool IsLocked { get; set; }
}
