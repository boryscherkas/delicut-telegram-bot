using System.Text.Json.Serialization;

namespace DelicutTelegramBot.Models.Delicut;

public class DishVariant
{
    [JsonPropertyName("protein_option")]
    public string ProteinOption { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public string Size { get; set; } = string.Empty;

    [JsonPropertyName("protein_category")]
    public string ProteinCategory { get; set; } = string.Empty;

    [JsonPropertyName("kcal")]
    public double Kcal { get; set; }

    [JsonPropertyName("fat")]
    public double Fat { get; set; }

    [JsonPropertyName("carb")]
    public double Carb { get; set; }

    [JsonPropertyName("protein")]
    public double Protein { get; set; }

    [JsonPropertyName("net_qty")]
    public double NetWeight { get; set; }

    [JsonPropertyName("allergens")]
    public List<string> Allergens { get; set; } = [];
}
