using System.Text.Json.Serialization;

namespace DelicutTelegramBot.Models.Delicut;

public class Dish
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("recipe_id")]
    public string RecipeId { get; set; } = string.Empty;

    [JsonPropertyName("dish_name")]
    public string DishName { get; set; } = string.Empty;

    [JsonPropertyName("meal_category")]
    public string MealCategory { get; set; } = string.Empty;

    [JsonPropertyName("cuisine")]
    public string Cuisine { get; set; } = string.Empty;

    [JsonPropertyName("dish_type")]
    public List<string> DishType { get; set; } = [];

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("spice_level")]
    public string SpiceLevel { get; set; } = string.Empty;

    [JsonPropertyName("ingredients")]
    public List<string> Ingredients { get; set; } = [];

    [JsonPropertyName("allergens_contain")]
    public List<string> AllergensContain { get; set; } = [];

    [JsonPropertyName("allergens_free_from")]
    public List<string> AllergensFreeFrom { get; set; } = [];

    [JsonPropertyName("avg_rating")]
    public double AvgRating { get; set; }

    [JsonPropertyName("total_ratings")]
    public int TotalRatings { get; set; }

    [JsonPropertyName("days")]
    public List<string> AssignedDays { get; set; } = [];

    [JsonPropertyName("variants")]
    public List<DishVariant> Variants { get; set; } = [];

    [JsonPropertyName("protein_category_info")]
    public List<ProteinCategoryInfo> ProteinCategoryInfo { get; set; } = [];

    // Computed from ProteinCategoryInfo
    public string ImageUrl => ProteinCategoryInfo.FirstOrDefault()?.Image.FirstOrDefault() ?? string.Empty;
}

public class ProteinCategoryInfo
{
    [JsonPropertyName("image")]
    public List<string> Image { get; set; } = [];

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("dish_name")]
    public string DishName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
