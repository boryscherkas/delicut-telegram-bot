using System.Text.Json.Serialization;

namespace DelicutTelegramBot.Models.Delicut;

public class MealTypeInfo
{
    [JsonPropertyName("meal_category")]
    public string MealCategory { get; set; } = string.Empty;

    [JsonPropertyName("meal_type")]
    public string MealType { get; set; } = string.Empty;

    [JsonPropertyName("kcal_range")]
    public string KcalRange { get; set; } = string.Empty;

    [JsonPropertyName("qty")]
    public int Qty { get; set; }

    [JsonPropertyName("protein_category")]
    public string ProteinCategory { get; set; } = string.Empty;

    [JsonPropertyName("is_veg")]
    public bool IsVeg { get; set; }
}
