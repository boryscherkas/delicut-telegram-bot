using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Services;

namespace DelicutTelegramBot.Tests.Services;

public class DishFilterServiceTests
{
    private readonly IDishFilterService _sut = new DishFilterService();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Dish MakeDish(
        string name,
        string cuisine = "Indian",
        List<string>? dishType = null,
        List<string>? ingredients = null,
        List<DishVariant>? variants = null) => new()
    {
        DishName = name,
        Cuisine = cuisine,
        DishType = dishType ?? ["main"],
        Ingredients = ingredients ?? [],
        Variants = variants ?? [MakeVariant("extra_large", "chicken")]
    };

    private static DishVariant MakeVariant(string size, string proteinCategory) => new()
    {
        Size = size,
        ProteinCategory = proteinCategory
    };

    // ── stop-word tests ───────────────────────────────────────────────────────

    [Fact]
    public void StopWord_MatchesDishName_DishIsRemoved()
    {
        var dishes = new List<Dish> { MakeDish("Healthy Quinoa Biryani") };

        var result = _sut.Filter(dishes, ["biryani"], [], [], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void StopWord_CaseInsensitive_DishIsRemoved()
    {
        var dishes = new List<Dish> { MakeDish("Spicy PASTA bake") };

        var result = _sut.Filter(dishes, ["Pasta"], [], [], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void StopWord_NoMatch_DishIsKept()
    {
        var dishes = new List<Dish> { MakeDish("Grilled Salmon") };

        var result = _sut.Filter(dishes, ["biryani"], [], [], "extra_large", "chicken");

        Assert.Single(result);
    }

    [Fact]
    public void EmptyStopWords_FiltersNothing()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Biryani Bowl"),
            MakeDish("Pasta Bake")
        };

        var result = _sut.Filter(dishes, [], [], [], "extra_large", "chicken");

        Assert.Equal(2, result.Count);
    }

    // ── avoid-ingredient tests ────────────────────────────────────────────────

    [Fact]
    public void AvoidIngredient_MatchesDishIngredient_DishIsRemoved()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Lentil Soup", ingredients: ["lentils", "onion", "tomato"])
        };

        var result = _sut.Filter(dishes, [], ["lentils"], [], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void AvoidIngredient_CaseInsensitive_DishIsRemoved()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Egg Salad", ingredients: ["Eggs", "lettuce"])
        };

        var result = _sut.Filter(dishes, [], ["eggs"], [], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void AvoidIngredient_NoMatch_DishIsKept()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Grilled Chicken", ingredients: ["chicken", "spices"])
        };

        var result = _sut.Filter(dishes, [], ["lentils"], [], "extra_large", "chicken");

        Assert.Single(result);
    }

    [Fact]
    public void EmptyAvoidIngredients_FiltersNothing()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Egg Salad", ingredients: ["eggs", "lettuce"]),
            MakeDish("Lentil Soup", ingredients: ["lentils"])
        };

        var result = _sut.Filter(dishes, [], [], [], "extra_large", "chicken");

        Assert.Equal(2, result.Count);
    }

    // ── avoid-category tests (Cuisine and DishType) ──────────────────────────

    [Fact]
    public void AvoidCategory_MatchesCuisine_DishIsRemoved()
    {
        var dishes = new List<Dish> { MakeDish("Butter Chicken", cuisine: "Indian") };

        var result = _sut.Filter(dishes, [], [], ["Indian"], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void AvoidCategory_CaseInsensitiveCuisine_DishIsRemoved()
    {
        var dishes = new List<Dish> { MakeDish("Tacos", cuisine: "mexican") };

        var result = _sut.Filter(dishes, [], [], ["Mexican"], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void AvoidCategory_MatchesDishType_DishIsRemoved()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Salad Bowl", dishType: ["salad", "light"])
        };

        var result = _sut.Filter(dishes, [], [], ["salad"], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void AvoidCategory_CaseInsensitiveDishType_DishIsRemoved()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Rice Bowl", dishType: ["MAIN", "rice"])
        };

        var result = _sut.Filter(dishes, [], [], ["main"], "extra_large", "chicken");

        Assert.Empty(result);
    }

    [Fact]
    public void AvoidCategory_NoMatch_DishIsKept()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Pasta", cuisine: "Italian", dishType: ["pasta"])
        };

        var result = _sut.Filter(dishes, [], [], ["Indian"], "extra_large", "chicken");

        Assert.Single(result);
    }

    [Fact]
    public void EmptyAvoidCategories_FiltersNothing()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Butter Chicken", cuisine: "Indian"),
            MakeDish("Tacos", cuisine: "Mexican")
        };

        var result = _sut.Filter(dishes, [], [], [], "extra_large", "chicken");

        Assert.Equal(2, result.Count);
    }

    // ── variant filtering tests ───────────────────────────────────────────────

    [Fact]
    public void Variant_MatchingKcalRangeAndProteinCategory_IsKept()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Grilled Salmon", variants:
            [
                MakeVariant("extra_large", "fish"),
                MakeVariant("large", "fish")
            ])
        };

        var result = _sut.Filter(dishes, [], [], [], "Extra_Large", "fish");

        Assert.Single(result);
        Assert.Single(result[0].Variants);
        Assert.Equal("extra_large", result[0].Variants[0].Size);
    }

    [Fact]
    public void Variant_KcalRangeMatchIsCaseInsensitive()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Meal", variants: [MakeVariant("LARGE", "chicken")])
        };

        var result = _sut.Filter(dishes, [], [], [], "large", "chicken");

        Assert.Single(result);
        Assert.Single(result[0].Variants);
    }

    [Fact]
    public void Variant_ProteinCategoryMatchIsCaseInsensitive()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Meal", variants: [MakeVariant("large", "CHICKEN")])
        };

        var result = _sut.Filter(dishes, [], [], [], "large", "chicken");

        Assert.Single(result);
    }

    [Fact]
    public void Variant_NonMatchingKcalRange_IsRemoved()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Meal", variants:
            [
                MakeVariant("small", "chicken"),
                MakeVariant("extra_large", "chicken")
            ])
        };

        var result = _sut.Filter(dishes, [], [], [], "extra_large", "chicken");

        Assert.Single(result);
        Assert.Single(result[0].Variants);
        Assert.Equal("extra_large", result[0].Variants[0].Size);
    }

    [Fact]
    public void Variant_NonMatchingProteinCategory_IsRemoved()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Meal", variants:
            [
                MakeVariant("extra_large", "veg"),
                MakeVariant("extra_large", "chicken")
            ])
        };

        var result = _sut.Filter(dishes, [], [], [], "extra_large", "chicken");

        Assert.Single(result);
        Assert.Single(result[0].Variants);
        Assert.Equal("chicken", result[0].Variants[0].ProteinCategory);
    }

    [Fact]
    public void Dish_WithNoMatchingVariantsAfterFiltering_IsRemovedEntirely()
    {
        var dishes = new List<Dish>
        {
            MakeDish("Meal", variants:
            [
                MakeVariant("small", "chicken"),
                MakeVariant("medium", "chicken")
            ])
        };

        var result = _sut.Filter(dishes, [], [], [], "extra_large", "chicken");

        Assert.Empty(result);
    }

    // ── immutability test ─────────────────────────────────────────────────────

    [Fact]
    public void Filter_DoesNotMutateOriginalDish()
    {
        var original = MakeDish("Meal", variants:
        [
            MakeVariant("small", "chicken"),
            MakeVariant("extra_large", "chicken")
        ]);
        var dishes = new List<Dish> { original };

        _sut.Filter(dishes, [], [], [], "extra_large", "chicken");

        Assert.Equal(2, original.Variants.Count);
    }

    // ── combined filtering test ───────────────────────────────────────────────

    [Fact]
    public void Filter_MultipleRulesApplied_OnlyCompliantDishesRemain()
    {
        var biryani = MakeDish("Chicken Biryani", cuisine: "Indian",
            ingredients: ["rice", "chicken"],
            variants: [MakeVariant("extra_large", "chicken")]);

        var eggSalad = MakeDish("Egg Salad", cuisine: "Western",
            ingredients: ["eggs", "lettuce"],
            variants: [MakeVariant("extra_large", "chicken")]);

        var pasta = MakeDish("Pasta", cuisine: "Italian",
            ingredients: ["pasta", "tomato"],
            variants: [MakeVariant("extra_large", "chicken")]);

        var dishes = new List<Dish> { biryani, eggSalad, pasta };

        var result = _sut.Filter(dishes,
            stopWords: ["biryani"],
            avoidIngredients: ["eggs"],
            avoidCategories: [],
            kcalRange: "extra_large",
            proteinCategory: "chicken");

        Assert.Single(result);
        Assert.Equal("Pasta", result[0].DishName);
    }
}
