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

public class MenuSelectionServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<IDelicutApiService> _delicutApi;
    private readonly Mock<IUserService> _userService;
    private readonly Mock<IOpenAiService> _openAi;
    private readonly Mock<IDishFilterService> _dishFilter;
    private readonly Mock<IFallbackSelectionService> _fallback;
    private readonly Mock<ISelectionHistoryService> _history;
    private readonly ConversationStateManager _stateManager;
    private readonly MenuSelectionService _sut;

    public MenuSelectionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _delicutApi = new Mock<IDelicutApiService>();
        _userService = new Mock<IUserService>();
        _openAi = new Mock<IOpenAiService>();
        _dishFilter = new Mock<IDishFilterService>();
        _fallback = new Mock<IFallbackSelectionService>();
        // Default fallback: return a single pick for any request
        _fallback.Setup(f => f.Select(It.IsAny<List<DishSummary>>(), It.IsAny<SelectionStrategy>(),
                It.IsAny<List<MealSlot>>(), It.IsAny<Dictionary<string, List<string>>>(),
                It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(),
                It.IsAny<List<string>?>(), It.IsAny<List<string>?>(), It.IsAny<int>()))
            .Returns((List<DishSummary> dishes, SelectionStrategy _, List<MealSlot> slots,
                Dictionary<string, List<string>> _, double? _, double? _, double? _,
                List<string>? _, List<string>? _, int _) =>
            {
                var picks = new List<AiDishPick>();
                if (dishes.Count > 0)
                {
                    for (int i = 0; i < (slots.FirstOrDefault()?.Count ?? 1) && i < dishes.Count; i++)
                    {
                        picks.Add(new AiDishPick
                        {
                            DishId = dishes[i].Id,
                            ProteinOption = dishes[i].ProteinOption,
                            MealCategory = dishes[i].MealCategory,
                            SlotIndex = i,
                            Reasoning = "Fallback"
                        });
                    }
                }
                return new AiSelectionResult { Picks = picks };
            });
        _history = new Mock<ISelectionHistoryService>();
        _stateManager = new ConversationStateManager();
        var logger = Mock.Of<ILogger<MenuSelectionService>>();

        _sut = new MenuSelectionService(
            _delicutApi.Object, _userService.Object, _openAi.Object, _dishFilter.Object,
            _fallback.Object, _history.Object, _stateManager, _db, logger);
    }

    public void Dispose() => _db.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static User MakeUser(Guid userId, long telegramId = 12345) => new()
    {
        Id = userId,
        TelegramUserId = telegramId,
        TelegramChatId = telegramId,
        DelicutToken = "test-token",
        DelicutSubscriptionId = "sub-123",
        Settings = new UserSettings
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Strategy = SelectionStrategy.Default,
            StopWords = [],
            PreferHistory = false
        }
    };

    private static Subscription MakeSubscription(string mealCategory = "meal", string mealType = "lunch") => new()
    {
        Id = "sub-123",
        MealTypes =
        [
            new MealTypeInfo
            {
                MealCategory = mealCategory,
                MealType = mealType,
                Qty = 1,
                KcalRange = "extra_large",
                ProteinCategory = "chicken"
            }
        ],
        AvoidIngredients = [],
        AvoidCategory = []
    };

    private static WeekDeliverySchedule MakeSchedule(params DeliveryDay[] days) => new()
    {
        Days = [.. days]
    };

    private static DeliveryDay MakeDeliveryDay(DateOnly date, bool isLocked = false) => new()
    {
        Date = date,
        DayOfWeek = date.DayOfWeek.ToString(),
        DeliveryId = $"delivery-{date:yyyy-MM-dd}",
        Slots =
        [
            new DeliverySlot
            {
                UniqueId = $"unique-{date:yyyy-MM-dd}-0",
                MealCategory = "meal",
                MealType = "lunch",
                KcalRange = "Extra_large",
                ProteinCategory = "low"
            },
            new DeliverySlot
            {
                UniqueId = $"unique-{date:yyyy-MM-dd}-1",
                MealCategory = "meal",
                MealType = "lunch",
                KcalRange = "Extra_large",
                ProteinCategory = "low"
            },
            new DeliverySlot
            {
                UniqueId = $"unique-{date:yyyy-MM-dd}-2",
                MealCategory = "meal",
                MealType = "lunch",
                KcalRange = "Extra_large",
                ProteinCategory = "low"
            }
        ],
        MealCategories = ["meal"],
        IsLocked = isLocked
    };

    private static Dish MakeDish(string id, string name, string proteinOption = "chicken") => new()
    {
        Id = id,
        DishName = name,
        Cuisine = "Indian",
        AvgRating = 4.5,
        Variants =
        [
            new DishVariant
            {
                ProteinOption = proteinOption,
                Size = "extra_large",
                ProteinCategory = "chicken",
                Kcal = 500,
                Protein = 35,
                Carb = 40,
                Fat = 15
            }
        ]
    };

    private static AiSelectionResult MakeAiResult(string dishId, string? date = null, string proteinOption = "chicken", string category = "meal") =>
        new()
        {
            Picks =
            [
                new AiDishPick
                {
                    Date = date ?? DateOnly.FromDateTime(DateTime.Today.AddDays(3)).ToString("yyyy-MM-dd"),
                    DishId = dishId,
                    ProteinOption = proteinOption,
                    MealCategory = category,
                    SlotIndex = 0,
                    Reasoning = "Test reasoning"
                }
            ]
        };

    // ── 1. SelectForWeekAsync_ClearsExistingProposals ─────────────────────────

    [Fact]
    public async Task SelectForWeekAsync_ClearsExistingProposals()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = MakeUser(userId);
        _db.Users.Add(user);

        var existingProposal = new PendingSelection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeliveryDate = DateOnly.FromDateTime(DateTime.Today),
            DeliveryId = "old-delivery",
            UniqueId = "old-unique",
            MealCategory = "lunch",
            SlotIndex = 0,
            DishId = "old-dish-id",
            DishName = "Old Dish",
            VariantProtein = "chicken",
            Status = PendingSelectionStatus.Proposed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.PendingSelections.Add(existingProposal);
        await _db.SaveChangesAsync();

        var futureDate = DateOnly.FromDateTime(DateTime.Today.AddDays(3));
        var dish = MakeDish("dish-1", "Grilled Chicken");
        var subscription = MakeSubscription();
        var schedule = MakeSchedule(MakeDeliveryDay(futureDate));

        _delicutApi.Setup(a => a.GetSubscriptionDetailsAsync(It.IsAny<string>()))
            .ReturnsAsync(subscription);
        _delicutApi.Setup(a => a.GetDeliveryScheduleAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(schedule);
        _delicutApi.Setup(a => a.FetchMenuAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([dish]);
        _dishFilter.Setup(f => f.Filter(It.IsAny<List<Dish>>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns([dish]);
        _openAi.Setup(ai => ai.SelectDishesAsync(It.IsAny<AiSelectionRequest>()))
            .ReturnsAsync(MakeAiResult("dish-1", futureDate.ToString("yyyy-MM-dd")));
        _history.Setup(h => h.GetPreviousChoiceNamesAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        // Act
        await _sut.SelectForWeekAsync(userId);

        // Assert: old Proposed row is gone
        var remaining = await _db.PendingSelections
            .Where(p => p.DishId == "old-dish-id")
            .ToListAsync();
        Assert.Empty(remaining);
    }

    // ── 2. SelectForWeekAsync_SkipsLockedDays ────────────────────────────────

    [Fact]
    public async Task SelectForWeekAsync_SkipsLockedDays()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = MakeUser(userId);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var lockedDay = DateOnly.FromDateTime(DateTime.Today);
        var openDay = DateOnly.FromDateTime(DateTime.Today.AddDays(3));

        var dish = MakeDish("dish-1", "Grilled Chicken");
        var subscription = MakeSubscription();
        var schedule = MakeSchedule(
            MakeDeliveryDay(lockedDay, isLocked: true),
            MakeDeliveryDay(openDay, isLocked: false));

        _delicutApi.Setup(a => a.GetSubscriptionDetailsAsync(It.IsAny<string>()))
            .ReturnsAsync(subscription);
        _delicutApi.Setup(a => a.GetDeliveryScheduleAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(schedule);
        _delicutApi.Setup(a => a.FetchMenuAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([dish]);
        _dishFilter.Setup(f => f.Filter(It.IsAny<List<Dish>>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns([dish]);
        _openAi.Setup(ai => ai.SelectDishesAsync(It.IsAny<AiSelectionRequest>()))
            .ReturnsAsync(MakeAiResult("dish-1", openDay.ToString("yyyy-MM-dd")));
        _history.Setup(h => h.GetPreviousChoiceNamesAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        // Act
        var result = await _sut.SelectForWeekAsync(userId);

        // Assert: locked day appears in LockedDays and has no DayProposal
        Assert.Contains(lockedDay, result.LockedDays);
        Assert.DoesNotContain(result.Days, d => d.Date == lockedDay);
        Assert.Contains(result.Days, d => d.Date == openDay);
    }

    // ── 3. SelectForWeekAsync_FallsBackWhenOpenAiFails ───────────────────────

    [Fact]
    public async Task SelectForWeekAsync_FallsBackWhenOpenAiFails()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = MakeUser(userId);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var dish = MakeDish("dish-1", "Grilled Chicken");
        var subscription = MakeSubscription();
        var schedule = MakeSchedule(MakeDeliveryDay(today));

        _delicutApi.Setup(a => a.GetSubscriptionDetailsAsync(It.IsAny<string>()))
            .ReturnsAsync(subscription);
        _delicutApi.Setup(a => a.GetDeliveryScheduleAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(schedule);
        _delicutApi.Setup(a => a.FetchMenuAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync([dish]);
        _dishFilter.Setup(f => f.Filter(It.IsAny<List<Dish>>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns([dish]);

        // OpenAI returns null — should trigger fallback
        _openAi.Setup(ai => ai.SelectDishesAsync(It.IsAny<AiSelectionRequest>()))
            .ReturnsAsync((AiSelectionResult?)null);

        _fallback.Setup(f => f.Select(It.IsAny<List<DishSummary>>(), It.IsAny<SelectionStrategy>(), It.IsAny<List<MealSlot>>(), It.IsAny<Dictionary<string, List<string>>>(),
                It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(),
                It.IsAny<List<string>?>(), It.IsAny<List<string>?>(), It.IsAny<int>()))
            .Returns(MakeAiResult("dish-1"));
        _history.Setup(h => h.GetPreviousChoiceNamesAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        // Act
        await _sut.SelectForWeekAsync(userId);

        // Assert: fallback was called
        _fallback.Verify(f => f.Select(
            It.IsAny<List<DishSummary>>(),
            It.IsAny<SelectionStrategy>(),
            It.IsAny<List<MealSlot>>(),
            It.IsAny<Dictionary<string, List<string>>>(),
            It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(),
                It.IsAny<List<string>?>(), It.IsAny<List<string>?>(), It.IsAny<int>()),
            Times.Once);
    }

    // ── 4. ConfirmDayAsync_SetsStatusToConfirmed ──────────────────────────────

    [Fact]
    public async Task ConfirmDayAsync_SetsStatusToConfirmed()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var deliveryDate = DateOnly.FromDateTime(DateTime.Today);

        var pending1 = new PendingSelection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeliveryDate = deliveryDate,
            DeliveryId = "delivery-1",
            UniqueId = "unique-1",
            MealCategory = "lunch",
            SlotIndex = 0,
            DishId = "dish-1",
            DishName = "Dish One",
            VariantProtein = "chicken",
            Status = PendingSelectionStatus.Proposed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var pending2 = new PendingSelection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeliveryDate = deliveryDate,
            DeliveryId = "delivery-1",
            UniqueId = "unique-1",
            MealCategory = "dinner",
            SlotIndex = 0,
            DishId = "dish-2",
            DishName = "Dish Two",
            VariantProtein = "chicken",
            Status = PendingSelectionStatus.Proposed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.PendingSelections.AddRange(pending1, pending2);
        await _db.SaveChangesAsync();

        // Act
        await _sut.ConfirmDayAsync(userId, deliveryDate);

        // Assert: both rows are now Confirmed
        var updated = await _db.PendingSelections
            .Where(p => p.UserId == userId && p.DeliveryDate == deliveryDate)
            .ToListAsync();

        Assert.All(updated, p => Assert.Equal(PendingSelectionStatus.Confirmed, p.Status));
    }

    // ── 5. ReplaceDishAsync_UpdatesPendingSelection ───────────────────────────

    [Fact]
    public async Task ReplaceDishAsync_UpdatesPendingSelection()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var telegramId = 99999L;
        var user = MakeUser(userId, telegramId);
        _db.Users.Add(user);

        var deliveryDate = DateOnly.FromDateTime(DateTime.Today);
        const string mealCategory = "lunch";
        const int slotIndex = 0;

        var original = new PendingSelection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeliveryDate = deliveryDate,
            DeliveryId = "delivery-1",
            UniqueId = "unique-1",
            MealCategory = mealCategory,
            SlotIndex = slotIndex,
            DishId = "old-dish-id",
            DishName = "Old Dish",
            VariantProtein = "chicken",
            Kcal = 400,
            Status = PendingSelectionStatus.Proposed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.PendingSelections.Add(original);
        await _db.SaveChangesAsync();

        // Pre-populate the conversation state cache with menu data
        var newDish = MakeDish("new-dish-id", "New Grilled Fish", proteinOption: "fish");
        newDish.Variants[0].Kcal = 350;

        var state = _stateManager.GetOrCreate(telegramId);
        var cacheKey = $"menu:{deliveryDate}:{mealCategory}";
        state.FlowData[cacheKey] = new List<Dish> { newDish };

        // Act
        await _sut.ReplaceDishAsync(userId, deliveryDate, mealCategory, slotIndex, "new-dish-id", "fish");

        // Assert: pending selection is updated with new dish data
        var updated = await _db.PendingSelections.FindAsync(original.Id);
        Assert.NotNull(updated);
        Assert.Equal("new-dish-id", updated.DishId);
        Assert.Equal("New Grilled Fish", updated.DishName);
        Assert.Equal("fish", updated.VariantProtein);
        Assert.Equal(350, updated.Kcal);
    }
}
