using System.Text.Json.Serialization;

namespace DelicutTelegramBot.Models.Delicut;

public class Subscription
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("customer_id")]
    public string CustomerId { get; set; } = string.Empty;

    [JsonPropertyName("plan_type")]
    public string PlanType { get; set; } = string.Empty;

    [JsonPropertyName("delivery_days")]
    public string DeliveryDays { get; set; } = string.Empty;

    [JsonPropertyName("is_vegetarian")]
    public bool IsVegetarian { get; set; }

    [JsonPropertyName("selected_meal")]
    public List<string> SelectedMeals { get; set; } = [];

    [JsonPropertyName("selected_meal_type")]
    public List<MealTypeInfo> MealTypes { get; set; } = [];

    [JsonPropertyName("avoid_ingredients")]
    public List<string> AvoidIngredients { get; set; } = [];

    [JsonPropertyName("avoid_category")]
    public List<string> AvoidCategory { get; set; } = [];

    [JsonPropertyName("delivery_start_date")]
    public DateTime DeliveryStartDate { get; set; }

    [JsonPropertyName("end_date")]
    public DateTime EndDate { get; set; }

    [JsonPropertyName("is_flex_plan")]
    public bool IsFlexPlan { get; set; }

    [JsonPropertyName("no_of_meals")]
    public int NoOfMeals { get; set; }

    [JsonPropertyName("no_of_breakfast")]
    public int NoOfBreakfast { get; set; }

    [JsonPropertyName("no_of_snacks")]
    public int NoOfSnacks { get; set; }
}
