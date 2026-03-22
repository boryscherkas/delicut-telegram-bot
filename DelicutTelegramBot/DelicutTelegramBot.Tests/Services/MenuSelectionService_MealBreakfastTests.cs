using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;

namespace DelicutTelegramBot.Tests.Services;

/// <summary>
/// Tests that verify correct 2-meal + 1-breakfast selection.
/// The core scenario: subscription has 2 lunch + 1 breakfast per day.
/// The bot MUST produce exactly 2 meal picks + 1 breakfast pick per day.
/// </summary>
public class MenuSelectionService_MealBreakfastTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<IDelicutApiService> _delicutApi;
    private readonly Mock<IOpenAiService> _openAi;
    private readonly Mock<IMenuFetchService> _menuFetch;
    private readonly Mock<ISelectionHistoryService> _history;
    private readonly ConversationStateManager _stateManager;
    private readonly MenuSelectionService _sut;

    // Real FallbackSelectionService — NOT mocked, so we test actual selection logic
    private readonly IFallbackSelectionService _fallback = new FallbackSelectionService();

    public MenuSelectionService_MealBreakfastTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _delicutApi = new Mock<IDelicutApiService>();
        _openAi = new Mock<IOpenAiService>();
        _menuFetch = new Mock<IMenuFetchService>();
        _history = new Mock<ISelectionHistoryService>();
        _stateManager = new ConversationStateManager();

        _sut = new MenuSelectionService(
            _delicutApi.Object,
            new Mock<IUserService>().Object,
            _openAi.Object,
            _menuFetch.Object,
            _fallback,  // Real fallback!
            _history.Object,
            _stateManager,
            _db,
            Mock.Of<ILogger<MenuSelectionService>>());
    }

    public void Dispose() => _db.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Subscription Make2Meal1BreakfastSubscription() => new()
    {
        Id = "sub-123",
        MealTypes =
        [
            new MealTypeInfo { MealCategory = "meal", MealType = "lunch", Qty = 2, KcalRange = "Extra_large", ProteinCategory = "chicken" },
            new MealTypeInfo { MealCategory = "meal", MealType = "breakfast", Qty = 1, KcalRange = "standard", ProteinCategory = "chicken" }
        ],
        AvoidIngredients = [],
        AvoidCategory = []
    };

    private static DeliveryDay MakeDeliveryDayWithBreakfast(DateOnly date) => new()
    {
        Date = date,
        DayOfWeek = date.DayOfWeek.ToString(),
        DeliveryId = $"delivery-{date:yyyy-MM-dd}",
        Slots =
        [
            new DeliverySlot { UniqueId = $"u-{date:yyyyMMdd}-lunch-0", MealCategory = "meal", MealType = "lunch", KcalRange = "Extra_large", ProteinCategory = "chicken" },
            new DeliverySlot { UniqueId = $"u-{date:yyyyMMdd}-lunch-1", MealCategory = "meal", MealType = "lunch", KcalRange = "Extra_large", ProteinCategory = "chicken" },
            new DeliverySlot { UniqueId = $"u-{date:yyyyMMdd}-bf-0", MealCategory = "meal", MealType = "breakfast", KcalRange = "standard", ProteinCategory = "chicken" }
        ],
        MealCategories = ["meal"],
        IsLocked = false
    };

    private static Dish MakeLunchDish(string id, string name) => new()
    {
        Id = id, DishName = name, MealCategory = "meal", Cuisine = "Indian", AvgRating = 4.0,
        DishType = [], Description = "", SpiceLevel = "medium", Ingredients = [],
        AllergensContain = [], AllergensFreeFrom = [], AssignedDays = [], ProteinCategoryInfo = [],
        Variants = [new DishVariant { ProteinOption = "chicken", Size = "extra_large", ProteinCategory = "chicken", Kcal = 600, Protein = 40, Carb = 50, Fat = 20, Allergens = [] }]
    };

    private static Dish MakeBreakfastDish(string id, string name) => new()
    {
        Id = id, DishName = name, MealCategory = "breakfast", Cuisine = "Healthy", AvgRating = 4.0,
        DishType = [], Description = "", SpiceLevel = "mild", Ingredients = [],
        AllergensContain = [], AllergensFreeFrom = [], AssignedDays = [], ProteinCategoryInfo = [],
        Variants = [new DishVariant { ProteinOption = "chicken", Size = "standard", ProteinCategory = "chicken", Kcal = 300, Protein = 20, Carb = 30, Fat = 10, Allergens = [] }]
    };

    private static List<DishSummary> MakeLunchSummaries() =>
    [
        new() { Id = "L1", Name = "Lunch Dish 1", MealCategory = "meal", Cuisine = "Indian", ProteinOption = "chicken", Kcal = 600, Protein = 40, Carb = 50, Fat = 20 },
        new() { Id = "L2", Name = "Lunch Dish 2", MealCategory = "meal", Cuisine = "Mexican", ProteinOption = "chicken", Kcal = 550, Protein = 35, Carb = 45, Fat = 18 },
        new() { Id = "L3", Name = "Lunch Dish 3", MealCategory = "meal", Cuisine = "Italian", ProteinOption = "chicken", Kcal = 580, Protein = 38, Carb = 48, Fat = 19 }
    ];

    private static List<DishSummary> MakeBreakfastSummaries() =>
    [
        new() { Id = "B1", Name = "Breakfast Dish 1", MealCategory = "breakfast", Cuisine = "Healthy", ProteinOption = "chicken", Kcal = 300, Protein = 20, Carb = 30, Fat = 10 },
        new() { Id = "B2", Name = "Breakfast Dish 2", MealCategory = "breakfast", Cuisine = "American", ProteinOption = "chicken", Kcal = 350, Protein = 22, Carb = 35, Fat = 12 }
    ];

    private void SetupApiForSubscription(Subscription subscription, WeekDeliverySchedule schedule)
    {
        _delicutApi.Setup(a => a.GetSubscriptionDetailsAsync(It.IsAny<string>())).ReturnsAsync(subscription);
        _delicutApi.Setup(a => a.GetDeliveryScheduleAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(schedule);
        _history.Setup(h => h.GetPreviousChoiceNamesAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync([]);
    }

    private User AddUser()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            TelegramUserId = 12345,
            TelegramChatId = 12345,
            DelicutToken = "test-token",
            DelicutCustomerId = "cust-123",
            DelicutSubscriptionId = "sub-123",
            Settings = new UserSettings
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Strategy = SelectionStrategy.Default,
                StopWords = [],
                PreferHistory = false,
                UseAiSelection = false // Use algorithmic selection
            }
        };
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    // ── THE CRITICAL TEST ───────────────────────────────────────────────────

    [Fact]
    public async Task SelectForWeek_2Meals1Breakfast_ProducesCorrectCategoryCounts()
    {
        // Arrange
        var user = AddUser();
        var date1 = DateOnly.FromDateTime(DateTime.Today.AddDays(3));
        var date2 = DateOnly.FromDateTime(DateTime.Today.AddDays(4));

        var subscription = Make2Meal1BreakfastSubscription();
        var day1 = MakeDeliveryDayWithBreakfast(date1);
        var day2 = MakeDeliveryDayWithBreakfast(date2);
        var schedule = new WeekDeliverySchedule { Days = [day1, day2] };

        SetupApiForSubscription(subscription, schedule);

        // MenuFetchService returns TWO DayMenuData entries per day:
        // one for "meal" (lunch) and one for "breakfast"
        var lunchDishes1 = new List<Dish> { MakeLunchDish("L1", "Lunch 1"), MakeLunchDish("L2", "Lunch 2"), MakeLunchDish("L3", "Lunch 3") };
        var bfDishes1 = new List<Dish> { MakeBreakfastDish("B1", "Breakfast 1"), MakeBreakfastDish("B2", "Breakfast 2") };
        var lunchDishes2 = new List<Dish> { MakeLunchDish("L4", "Lunch 4"), MakeLunchDish("L5", "Lunch 5"), MakeLunchDish("L6", "Lunch 6") };
        var bfDishes2 = new List<Dish> { MakeBreakfastDish("B3", "Breakfast 3"), MakeBreakfastDish("B4", "Breakfast 4") };

        var slotsByCategoryDay1Meal = day1.Slots.Where(s => s.MealType == "lunch").GroupBy(s => s.MealCategory.ToLower()).ToDictionary(g => g.Key, g => g.ToList());
        var slotsByCategoryDay1Bf = day1.Slots.Where(s => s.MealType == "breakfast").GroupBy(s => s.MealCategory.ToLower()).ToDictionary(g => g.Key, g => g.ToList());
        var slotsByCategoryDay2Meal = day2.Slots.Where(s => s.MealType == "lunch").GroupBy(s => s.MealCategory.ToLower()).ToDictionary(g => g.Key, g => g.ToList());
        var slotsByCategoryDay2Bf = day2.Slots.Where(s => s.MealType == "breakfast").GroupBy(s => s.MealCategory.ToLower()).ToDictionary(g => g.Key, g => g.ToList());

        _menuFetch.Setup(f => f.FetchAndFilterMenusAsync(It.IsAny<User>(), It.IsAny<Subscription>(), It.IsAny<WeekDeliverySchedule>(), It.IsAny<List<MealSlot>>()))
            .ReturnsAsync(new WeekMenuData
            {
                Days =
                [
                    // Day 1: meal entry
                    new DayMenuData
                    {
                        Day = day1,
                        Filtered = lunchDishes1,
                        Summaries = MakeLunchSummaries(),
                        SlotsByCategory = slotsByCategoryDay1Meal,
                        MealSlot = new MealSlot { Category = "meal", ApiCategory = "lunch", Count = 2 }
                    },
                    // Day 1: breakfast entry
                    new DayMenuData
                    {
                        Day = day1,
                        Filtered = bfDishes1,
                        Summaries = MakeBreakfastSummaries(),
                        SlotsByCategory = slotsByCategoryDay1Bf,
                        MealSlot = new MealSlot { Category = "breakfast", ApiCategory = "breakfast", Count = 1 }
                    },
                    // Day 2: meal entry
                    new DayMenuData
                    {
                        Day = day2,
                        Filtered = lunchDishes2,
                        Summaries = [
                            new() { Id = "L4", Name = "Lunch 4", MealCategory = "meal", Cuisine = "Indian", ProteinOption = "chicken", Kcal = 600, Protein = 40, Carb = 50, Fat = 20 },
                            new() { Id = "L5", Name = "Lunch 5", MealCategory = "meal", Cuisine = "Mexican", ProteinOption = "chicken", Kcal = 550, Protein = 35, Carb = 45, Fat = 18 },
                            new() { Id = "L6", Name = "Lunch 6", MealCategory = "meal", Cuisine = "Italian", ProteinOption = "chicken", Kcal = 580, Protein = 38, Carb = 48, Fat = 19 }
                        ],
                        SlotsByCategory = slotsByCategoryDay2Meal,
                        MealSlot = new MealSlot { Category = "meal", ApiCategory = "lunch", Count = 2 }
                    },
                    // Day 2: breakfast entry
                    new DayMenuData
                    {
                        Day = day2,
                        Filtered = bfDishes2,
                        Summaries = [
                            new() { Id = "B3", Name = "Breakfast 3", MealCategory = "breakfast", Cuisine = "Healthy", ProteinOption = "chicken", Kcal = 300, Protein = 20, Carb = 30, Fat = 10 },
                            new() { Id = "B4", Name = "Breakfast 4", MealCategory = "breakfast", Cuisine = "American", ProteinOption = "chicken", Kcal = 350, Protein = 22, Carb = 35, Fat = 12 }
                        ],
                        SlotsByCategory = slotsByCategoryDay2Bf,
                        MealSlot = new MealSlot { Category = "breakfast", ApiCategory = "breakfast", Count = 1 }
                    }
                ],
                LockedDays = []
            });

        // Act
        var result = await _sut.SelectForWeekAsync(user.Id);

        // Assert
        Assert.Equal(2, result.Days.Count); // 2 days

        foreach (var day in result.Days)
        {
            var mealDishes = day.Dishes.Where(d => d.MealCategory == "meal").ToList();
            var breakfastDishes = day.Dishes.Where(d => d.MealCategory == "breakfast").ToList();

            Assert.Equal(2, mealDishes.Count);
            Assert.Single(breakfastDishes);
            Assert.Equal(3, day.Dishes.Count); // Total: 2 meal + 1 breakfast

            // Verify meal dishes come from lunch pool (L-prefix IDs)
            Assert.All(mealDishes, d => Assert.StartsWith("L", d.DishId));

            // Verify breakfast dish comes from breakfast pool (B-prefix IDs)
            Assert.All(breakfastDishes, d => Assert.StartsWith("B", d.DishId));
        }

        // Verify PendingSelections in DB match
        var allPending = await _db.PendingSelections.Where(p => p.UserId == user.Id).ToListAsync();
        Assert.Equal(6, allPending.Count); // 2 days × 3 dishes

        var mealPending = allPending.Where(p => p.MealCategory == "meal").ToList();
        var bfPending = allPending.Where(p => p.MealCategory == "breakfast").ToList();
        Assert.Equal(4, mealPending.Count); // 2 days × 2 meals
        Assert.Equal(2, bfPending.Count);   // 2 days × 1 breakfast

        // Verify MealType is set correctly for submission
        Assert.All(mealPending, p => Assert.Equal("lunch", p.MealType));
        Assert.All(bfPending, p => Assert.Equal("breakfast", p.MealType));
    }

    [Fact]
    public async Task SelectForWeek_MealsOnly_ProducesCorrectCounts()
    {
        // Arrange: subscription with only 3 lunch meals, no breakfast
        var user = AddUser();
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(3));

        var subscription = new Subscription
        {
            Id = "sub-123",
            MealTypes = [new MealTypeInfo { MealCategory = "meal", MealType = "lunch", Qty = 3, KcalRange = "Extra_large", ProteinCategory = "chicken" }],
            AvoidIngredients = [],
            AvoidCategory = []
        };
        var day = new DeliveryDay
        {
            Date = date, DayOfWeek = date.DayOfWeek.ToString(), DeliveryId = $"d-{date:yyyyMMdd}",
            Slots = [
                new DeliverySlot { UniqueId = "u0", MealCategory = "meal", MealType = "lunch", KcalRange = "Extra_large", ProteinCategory = "chicken" },
                new DeliverySlot { UniqueId = "u1", MealCategory = "meal", MealType = "lunch", KcalRange = "Extra_large", ProteinCategory = "chicken" },
                new DeliverySlot { UniqueId = "u2", MealCategory = "meal", MealType = "lunch", KcalRange = "Extra_large", ProteinCategory = "chicken" }
            ],
            MealCategories = ["meal"], IsLocked = false
        };
        var schedule = new WeekDeliverySchedule { Days = [day] };
        SetupApiForSubscription(subscription, schedule);

        var dishes = new List<Dish> { MakeLunchDish("L1", "D1"), MakeLunchDish("L2", "D2"), MakeLunchDish("L3", "D3"), MakeLunchDish("L4", "D4") };
        var summaries = new List<DishSummary>
        {
            new() { Id = "L1", Name = "D1", MealCategory = "meal", Cuisine = "Indian", ProteinOption = "chicken", Kcal = 600, Protein = 40, Carb = 50, Fat = 20 },
            new() { Id = "L2", Name = "D2", MealCategory = "meal", Cuisine = "Mexican", ProteinOption = "chicken", Kcal = 550, Protein = 35, Carb = 45, Fat = 18 },
            new() { Id = "L3", Name = "D3", MealCategory = "meal", Cuisine = "Italian", ProteinOption = "chicken", Kcal = 580, Protein = 38, Carb = 48, Fat = 19 },
            new() { Id = "L4", Name = "D4", MealCategory = "meal", Cuisine = "Thai", ProteinOption = "chicken", Kcal = 520, Protein = 36, Carb = 42, Fat = 17 }
        };

        _menuFetch.Setup(f => f.FetchAndFilterMenusAsync(It.IsAny<User>(), It.IsAny<Subscription>(), It.IsAny<WeekDeliverySchedule>(), It.IsAny<List<MealSlot>>()))
            .ReturnsAsync(new WeekMenuData
            {
                Days = [new DayMenuData
                {
                    Day = day, Filtered = dishes, Summaries = summaries,
                    SlotsByCategory = day.Slots.GroupBy(s => s.MealCategory.ToLower()).ToDictionary(g => g.Key, g => g.ToList()),
                    MealSlot = new MealSlot { Category = "meal", ApiCategory = "lunch", Count = 3 }
                }],
                LockedDays = []
            });

        // Act
        var result = await _sut.SelectForWeekAsync(user.Id);

        // Assert
        Assert.Single(result.Days);
        Assert.Equal(3, result.Days[0].Dishes.Count);
        Assert.All(result.Days[0].Dishes, d => Assert.Equal("meal", d.MealCategory));
    }

    [Fact]
    public async Task SelectForWeek_BreakfastDishesAreNotFromLunchPool()
    {
        // This test specifically verifies that breakfast picks have breakfast-category dishes,
        // not dishes from the lunch/meal pool
        var user = AddUser();
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(3));
        var subscription = Make2Meal1BreakfastSubscription();
        var day = MakeDeliveryDayWithBreakfast(date);
        var schedule = new WeekDeliverySchedule { Days = [day] };
        SetupApiForSubscription(subscription, schedule);

        // Lunch dishes have IDs starting with "LUNCH-", breakfast with "BF-"
        var lunchFiltered = new List<Dish>
        {
            MakeLunchDish("LUNCH-1", "Grilled Chicken"),
            MakeLunchDish("LUNCH-2", "Butter Paneer"),
            MakeLunchDish("LUNCH-3", "Fish Curry")
        };
        var bfFiltered = new List<Dish>
        {
            MakeBreakfastDish("BF-1", "Omelette"),
            MakeBreakfastDish("BF-2", "Pancakes")
        };

        var slotsByCatMeal = day.Slots.Where(s => s.MealType == "lunch").GroupBy(s => s.MealCategory.ToLower()).ToDictionary(g => g.Key, g => g.ToList());
        var slotsByCatBf = day.Slots.Where(s => s.MealType == "breakfast").GroupBy(s => s.MealCategory.ToLower()).ToDictionary(g => g.Key, g => g.ToList());

        _menuFetch.Setup(f => f.FetchAndFilterMenusAsync(It.IsAny<User>(), It.IsAny<Subscription>(), It.IsAny<WeekDeliverySchedule>(), It.IsAny<List<MealSlot>>()))
            .ReturnsAsync(new WeekMenuData
            {
                Days =
                [
                    new DayMenuData
                    {
                        Day = day, Filtered = lunchFiltered,
                        Summaries = [
                            new() { Id = "LUNCH-1", Name = "Grilled Chicken", MealCategory = "meal", Cuisine = "Indian", ProteinOption = "chicken", Kcal = 600, Protein = 40, Carb = 50, Fat = 20 },
                            new() { Id = "LUNCH-2", Name = "Butter Paneer", MealCategory = "meal", Cuisine = "Indian", ProteinOption = "chicken", Kcal = 550, Protein = 35, Carb = 45, Fat = 18 },
                            new() { Id = "LUNCH-3", Name = "Fish Curry", MealCategory = "meal", Cuisine = "Indian", ProteinOption = "chicken", Kcal = 580, Protein = 38, Carb = 48, Fat = 19 }
                        ],
                        SlotsByCategory = slotsByCatMeal,
                        MealSlot = new MealSlot { Category = "meal", ApiCategory = "lunch", Count = 2 }
                    },
                    new DayMenuData
                    {
                        Day = day, Filtered = bfFiltered,
                        Summaries = [
                            new() { Id = "BF-1", Name = "Omelette", MealCategory = "breakfast", Cuisine = "Healthy", ProteinOption = "chicken", Kcal = 300, Protein = 20, Carb = 30, Fat = 10 },
                            new() { Id = "BF-2", Name = "Pancakes", MealCategory = "breakfast", Cuisine = "American", ProteinOption = "chicken", Kcal = 350, Protein = 22, Carb = 35, Fat = 12 }
                        ],
                        SlotsByCategory = slotsByCatBf,
                        MealSlot = new MealSlot { Category = "breakfast", ApiCategory = "breakfast", Count = 1 }
                    }
                ],
                LockedDays = []
            });

        // Act
        var result = await _sut.SelectForWeekAsync(user.Id);

        // Assert
        var dayResult = Assert.Single(result.Days);
        Assert.Equal(3, dayResult.Dishes.Count);

        var mealDishes = dayResult.Dishes.Where(d => d.MealCategory == "meal").ToList();
        var bfDishes = dayResult.Dishes.Where(d => d.MealCategory == "breakfast").ToList();

        Assert.Equal(2, mealDishes.Count);
        Assert.Single(bfDishes);

        // KEY: breakfast dish must come from BF pool, not LUNCH pool
        Assert.All(mealDishes, d => Assert.StartsWith("LUNCH-", d.DishId));
        Assert.All(bfDishes, d => Assert.StartsWith("BF-", d.DishId));
    }
}
