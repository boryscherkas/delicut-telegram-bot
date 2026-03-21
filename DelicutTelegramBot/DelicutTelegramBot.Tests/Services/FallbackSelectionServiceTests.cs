using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.Services;

namespace DelicutTelegramBot.Tests.Services;

public class FallbackSelectionServiceTests
{
    private readonly IFallbackSelectionService _sut = new FallbackSelectionService();

    // ── helpers ───────────────────────────────────────────────────────────────

    private static DishSummary MakeDish(
        string id,
        string mealCategory = "main",
        double kcal = 500,
        double protein = 30,
        double rating = 4.0,
        string cuisine = "Indian",
        string proteinOption = "chicken") => new()
    {
        Id = id,
        Name = id,
        MealCategory = mealCategory,
        Kcal = kcal,
        Protein = protein,
        Rating = rating,
        Cuisine = cuisine,
        ProteinOption = proteinOption
    };

    private static MealSlot Slot(string category, int count) =>
        new() { Category = category, Count = count };

    // ── MacrosMax strategy ────────────────────────────────────────────────────

    [Fact]
    public void MacrosMax_HigherProteinDishRanksFirst()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("LowProtein",  protein: 10, rating: 5.0),
            MakeDish("HighProtein", protein: 50, rating: 3.0),
            MakeDish("MidProtein",  protein: 30, rating: 4.0)
        };
        var slots = new List<MealSlot> { Slot("main", 1) };

        var result = _sut.Select(dishes, SelectionStrategy.MacrosMax, slots, new());

        Assert.Single(result.Picks);
        Assert.Equal("HighProtein", result.Picks[0].DishId);
    }

    [Fact]
    public void MacrosMax_TopNPickedInOrder()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("P10",  protein: 10, rating: 4.0),
            MakeDish("P50",  protein: 50, rating: 4.0),
            MakeDish("P30",  protein: 30, rating: 4.0)
        };
        var slots = new List<MealSlot> { Slot("main", 2) };

        var result = _sut.Select(dishes, SelectionStrategy.MacrosMax, slots, new());

        Assert.Equal(2, result.Picks.Count);
        Assert.Equal("P50", result.Picks[0].DishId);
        Assert.Equal("P30", result.Picks[1].DishId);
    }

    // ── LowestCal strategy ────────────────────────────────────────────────────

    [Fact]
    public void LowestCal_LowerCalorieDishRanksFirst()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("HighCal", kcal: 900, rating: 5.0),
            MakeDish("LowCal",  kcal: 200, rating: 3.0),
            MakeDish("MidCal",  kcal: 500, rating: 4.0)
        };
        var slots = new List<MealSlot> { Slot("main", 1) };

        var result = _sut.Select(dishes, SelectionStrategy.LowestCal, slots, new());

        Assert.Single(result.Picks);
        Assert.Equal("LowCal", result.Picks[0].DishId);
    }

    [Fact]
    public void LowestCal_TopNPickedInOrder()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("C900", kcal: 900, rating: 4.0),
            MakeDish("C200", kcal: 200, rating: 4.0),
            MakeDish("C500", kcal: 500, rating: 4.0)
        };
        var slots = new List<MealSlot> { Slot("main", 2) };

        var result = _sut.Select(dishes, SelectionStrategy.LowestCal, slots, new());

        Assert.Equal(2, result.Picks.Count);
        Assert.Equal("C200", result.Picks[0].DishId);
        Assert.Equal("C500", result.Picks[1].DishId);
    }

    // ── Default strategy ──────────────────────────────────────────────────────

    [Fact]
    public void Default_HigherRatedDishRanksFirst()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("R3", rating: 3.0),
            MakeDish("R5", rating: 5.0),
            MakeDish("R4", rating: 4.0)
        };
        var slots = new List<MealSlot> { Slot("main", 1) };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        Assert.Single(result.Picks);
        Assert.Equal("R5", result.Picks[0].DishId);
    }

    [Fact]
    public void Default_TopNPickedInOrder()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("R3", rating: 3.0),
            MakeDish("R5", rating: 5.0),
            MakeDish("R4", rating: 4.0)
        };
        var slots = new List<MealSlot> { Slot("main", 2) };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        Assert.Equal(2, result.Picks.Count);
        Assert.Equal("R5", result.Picks[0].DishId);
        Assert.Equal("R4", result.Picks[1].DishId);
    }

    // ── Variety score (cuisine penalty) ──────────────────────────────────────

    [Fact]
    public void Variety_SameDishInWeekContext_IsPenalized()
    {
        // Both dishes are "main", same rating, but "IndianDish" Name appears
        // in weekContext (used on Monday) — it should rank lower due to variety penalty.
        // MakeDish sets Name = id, so weekContext uses the id as the dish name.
        var dishes = new List<DishSummary>
        {
            MakeDish("IndianDish",    cuisine: "Indian",   rating: 4.0, protein: 30),
            MakeDish("MexicanDish",   cuisine: "Mexican",  rating: 4.0, protein: 30)
        };
        var slots = new List<MealSlot> { Slot("main", 1) };
        var weekContext = new Dictionary<string, List<string>>
        {
            ["Monday"] = ["IndianDish"]  // same Name as first dish
        };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, weekContext);

        Assert.Single(result.Picks);
        Assert.Equal("MexicanDish", result.Picks[0].DishId);
    }

    [Fact]
    public void Variety_CuisineNotInWeekContext_GetsFullVarietyScore()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("IndianDish",  cuisine: "Indian",  rating: 4.0),
            MakeDish("ItalianDish", cuisine: "Italian", rating: 3.5)
        };
        var slots = new List<MealSlot> { Slot("main", 1) };
        // weekContext has no Indian cuisine → Indian dish gets variety bonus
        var weekContext = new Dictionary<string, List<string>>
        {
            ["Monday"] = ["Mexican"]
        };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, weekContext);

        Assert.Single(result.Picks);
        Assert.Equal("IndianDish", result.Picks[0].DishId);
    }

    // ── Correct count per slot ────────────────────────────────────────────────

    [Fact]
    public void Select_ReturnsCorrectCountPerSlot_TwoMealsOnBreakfast()
    {
        // 2 meal slots: main×2 and breakfast×1
        var dishes = new List<DishSummary>
        {
            MakeDish("M1", mealCategory: "main",      rating: 5.0),
            MakeDish("M2", mealCategory: "main",      rating: 4.0),
            MakeDish("M3", mealCategory: "main",      rating: 3.0),
            MakeDish("B1", mealCategory: "breakfast", rating: 5.0),
            MakeDish("B2", mealCategory: "breakfast", rating: 4.0)
        };
        var slots = new List<MealSlot>
        {
            Slot("main",      2),
            Slot("breakfast", 1)
        };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        var mainPicks      = result.Picks.Where(p => p.MealCategory == "main").ToList();
        var breakfastPicks = result.Picks.Where(p => p.MealCategory == "breakfast").ToList();

        Assert.Equal(2, mainPicks.Count);
        Assert.Single(breakfastPicks);
        Assert.Equal(3, result.Picks.Count);
    }

    [Fact]
    public void Select_NoDuplicateDishesAcrossSlots()
    {
        // All dishes are in "main", two slots both want "main" — should not repeat
        var dishes = new List<DishSummary>
        {
            MakeDish("D1", mealCategory: "main", rating: 5.0),
            MakeDish("D2", mealCategory: "main", rating: 4.5),
            MakeDish("D3", mealCategory: "main", rating: 4.0)
        };
        var slots = new List<MealSlot>
        {
            Slot("main", 1),
            Slot("main", 1)
        };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        var ids = result.Picks.Select(p => p.DishId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ── MealCategory and SlotIndex assignment ─────────────────────────────────

    [Fact]
    public void Select_AssignsCorrectMealCategoryToEachPick()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("M1", mealCategory: "main",      rating: 5.0),
            MakeDish("B1", mealCategory: "breakfast", rating: 5.0)
        };
        var slots = new List<MealSlot>
        {
            Slot("main",      1),
            Slot("breakfast", 1)
        };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        Assert.All(result.Picks, pick =>
            Assert.Contains(pick.MealCategory, new[] { "main", "breakfast" }));

        Assert.Single(result.Picks, p => p.MealCategory == "main");
        Assert.Single(result.Picks, p => p.MealCategory == "breakfast");
    }

    [Fact]
    public void Select_AssignsCorrectSlotIndex()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("M1", mealCategory: "main", rating: 5.0),
            MakeDish("M2", mealCategory: "main", rating: 4.0),
            MakeDish("B1", mealCategory: "breakfast", rating: 5.0)
        };
        var slots = new List<MealSlot>
        {
            Slot("main",      2),   // slot index 0
            Slot("breakfast", 1)    // slot index 1
        };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        var mainPicks = result.Picks.Where(p => p.MealCategory == "main").ToList();
        var bfPicks   = result.Picks.Where(p => p.MealCategory == "breakfast").ToList();

        Assert.All(mainPicks, p => Assert.Equal(0, p.SlotIndex));
        Assert.All(bfPicks,   p => Assert.Equal(1, p.SlotIndex));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Select_NotEnoughDishesInCategory_ReturnsBestEffort()
    {
        // Only 1 main dish available, slot wants 3
        var dishes = new List<DishSummary>
        {
            MakeDish("M1", mealCategory: "main", rating: 5.0)
        };
        var slots = new List<MealSlot> { Slot("main", 3) };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        Assert.Single(result.Picks);
        Assert.Equal("M1", result.Picks[0].DishId);
    }

    [Fact]
    public void Select_EmptyDishList_ReturnsEmptyPicks()
    {
        var result = _sut.Select(
            new List<DishSummary>(),
            SelectionStrategy.Default,
            new List<MealSlot> { Slot("main", 2) },
            new Dictionary<string, List<string>>());

        Assert.Empty(result.Picks);
    }

    [Fact]
    public void Select_AssignsProteinOptionFromDish()
    {
        var dishes = new List<DishSummary>
        {
            MakeDish("D1", proteinOption: "fish", rating: 5.0)
        };
        var slots = new List<MealSlot> { Slot("main", 1) };

        var result = _sut.Select(dishes, SelectionStrategy.Default, slots, new());

        Assert.Single(result.Picks);
        Assert.Equal("fish", result.Picks[0].ProteinOption);
    }
}
