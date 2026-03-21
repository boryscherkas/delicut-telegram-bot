# Delicut Telegram Bot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a .NET 9 Telegram bot that automates weekly meal selection from Delicut using OpenAI, with per-user settings, inline keyboards, and a stub Delicut API interface.

**Architecture:** Single .NET 9 project with folder-based separation (Models, Services, Handlers, Infrastructure, State). EF Core for Supabase/PostgreSQL persistence. Telegram.Bot for bot interaction. OpenAI for intelligent dish selection. In-memory conversation state for multi-step flows.

**Tech Stack:** .NET 9, Telegram.Bot, EF Core + Npgsql, OpenAI, Microsoft.Extensions.Hosting

**Spec:** `docs/superpowers/specs/2026-03-21-delicut-telegram-bot-design.md`

**Project root:** `DelicutTelegramBot/DelicutTelegramBot/` (csproj location)

---

## File Map

### Models/Domain/
| File | Responsibility |
|------|---------------|
| `Enums.cs` | SelectionStrategy, PendingSelectionStatus, ConversationFlow enums |
| `User.cs` | User entity (EF Core) |
| `UserSettings.cs` | User preferences entity (EF Core) |
| `SelectionHistory.cs` | Past dish selections entity (EF Core) |
| `PendingSelection.cs` | Active week proposals entity (EF Core) |

### Models/Delicut/
| File | Responsibility |
|------|---------------|
| `Subscription.cs` | Subscription details from Delicut API |
| `MealTypeInfo.cs` | Meal type within subscription |
| `Dish.cs` | Dish from menu API |
| `DishVariant.cs` | Variant (protein + size + macros) |
| `OtpResponse.cs` | OTP request response |
| `LoginResponse.cs` | Login/verify response |
| `WeekDeliverySchedule.cs` | Delivery days with IDs |

### Models/Dto/
| File | Responsibility |
|------|---------------|
| `WeeklyProposal.cs` | Week selection result (DayProposal list) |
| `AiModels.cs` | AiSelectionRequest, AiSelectionResult, AiDishPick, MealSlot, DishSummary |
| `DishAlternative.cs` | Alternative dish for replacement flow |
| `DishSubmission.cs` | Submission payload + PastDishSelection DTO |
| `ProposedDish.cs` | Single proposed dish within a day |

### Services/
| File | Responsibility |
|------|---------------|
| `IDelicutApiService.cs` | Interface for all Delicut API calls |
| `IUserService.cs` | Interface for user CRUD + settings (not in spec's Interfaces section — defined here) |
| `IOpenAiService.cs` | Interface for AI dish selection |
| `IMenuSelectionService.cs` | Interface for selection orchestration |
| `IDishFilterService.cs` | Interface for hard filter logic |
| `IFallbackSelectionService.cs` | Interface for fallback scoring |
| `ISelectionHistoryService.cs` | Interface for querying/storing past selections |
| `UserService.cs` | EF Core implementation of IUserService |
| `OpenAiService.cs` | OpenAI API implementation |
| `MenuSelectionService.cs` | Orchestrates filtering + AI + pending selections |
| `DishFilterService.cs` | Hard filter logic (stop words, avoid, variant match) |
| `FallbackSelectionService.cs` | Rating-based fallback when OpenAI fails |
| `SelectionHistoryService.cs` | EF Core implementation of ISelectionHistoryService |

> **Note:** Spec lists `Handlers/CallbackRouter.cs` — consolidated into `BotHandler.cs` which routes both messages and callbacks. Single entry point is simpler.

### Handlers/
| File | Responsibility |
|------|---------------|
| `BotHandler.cs` | Main update handler, routes messages/callbacks |
| `StartHandler.cs` | /start + OTP auth flow |
| `SettingsHandler.cs` | /settings inline keyboard + callbacks |
| `SelectWeekHandler.cs` | /select flow + week overview |
| `ChangeDishHandler.cs` | Per-dish replacement flow |
| `CancelHandler.cs` | /cancel flow reset |

### Infrastructure/
| File | Responsibility |
|------|---------------|
| `AppDbContext.cs` | EF Core DbContext with entity configs |
| `DelicutApiService.cs` | Stub implementation (NotImplementedException) |

### Other
| File | Responsibility |
|------|---------------|
| `State/ConversationStateManager.cs` | In-memory conversation state |
| `BackgroundServices/WednesdayReminderService.cs` | Weekly reminder hosted service |
| `Extensions/ServiceCollectionExtensions.cs` | DI registration |
| `Helpers/CutoffHelper.cs` | Cutoff date/time logic |
| `Program.cs` | Host builder, bot startup |
| `appsettings.json` | Configuration template |

### Tests (new project: `DelicutTelegramBot.Tests/`)
| File | Responsibility |
|------|---------------|
| `Helpers/CutoffHelperTests.cs` | Cutoff logic tests |
| `Services/DishFilterServiceTests.cs` | Hard filter tests |
| `Services/FallbackSelectionServiceTests.cs` | Fallback scoring tests |
| `Services/MenuSelectionServiceTests.cs` | Orchestration tests |
| `State/ConversationStateManagerTests.cs` | State management tests |

---

## Task 1: Project Setup & NuGet Packages

**Files:**
- Modify: `DelicutTelegramBot/DelicutTelegramBot/DelicutTelegramBot.csproj`
- Create: `DelicutTelegramBot/DelicutTelegramBot/appsettings.json`
- Create: `DelicutTelegramBot/DelicutTelegramBot/.gitignore` (project-level for appsettings.Development.json)
- Create: `.gitignore` (root-level)
- Modify: `DelicutTelegramBot/DelicutTelegramBot.sln` (add test project)
- Create: `DelicutTelegramBot/DelicutTelegramBot.Tests/DelicutTelegramBot.Tests.csproj`

- [ ] **Step 1: Add NuGet packages to csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net9.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="Telegram.Bot" Version="22.*" />
        <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.*" />
        <PackageReference Include="Microsoft.Extensions.Http" Version="9.*" />
        <PackageReference Include="OpenAI" Version="2.*" />
        <PackageReference Include="Microsoft.EntityFrameworkCore" Version="9.*" />
        <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.*" />
        <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.*">
            <PrivateAssets>all</PrivateAssets>
            <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
        </PackageReference>
    </ItemGroup>
</Project>
```

- [ ] **Step 2: Create appsettings.json**

```json
{
  "Telegram": {
    "BotToken": ""
  },
  "Supabase": {
    "ConnectionString": ""
  },
  "OpenAi": {
    "ApiKey": "",
    "Model": "gpt-4o-mini"
  },
  "Delicut": {
    "BaseUrl": "https://apis.delicut.ae/api"
  }
}
```

- [ ] **Step 3: Create root .gitignore**

Standard .NET gitignore plus:
```
appsettings.Development.json
appsettings.*.json
!appsettings.json
*.user
.env
```

- [ ] **Step 4: Create test project**

Run: `cd DelicutTelegramBot && dotnet new xunit -n DelicutTelegramBot.Tests`
Then add project reference:
Run: `dotnet add DelicutTelegramBot.Tests/DelicutTelegramBot.Tests.csproj reference DelicutTelegramBot/DelicutTelegramBot.csproj`
Then add to solution:
Run: `dotnet sln add DelicutTelegramBot.Tests/DelicutTelegramBot.Tests.csproj`

- [ ] **Step 5: Restore and build**

Run: `cd DelicutTelegramBot && dotnet restore && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "chore: add NuGet packages, test project, and config template"
```

---

## Task 2: Enums & Domain Models

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Domain/Enums.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Domain/User.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Domain/UserSettings.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Domain/SelectionHistory.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Domain/PendingSelection.cs`

- [ ] **Step 1: Create Enums.cs**

```csharp
namespace DelicutTelegramBot.Models.Domain;

public enum SelectionStrategy
{
    Default,
    LowestCal,
    MacrosMax
}

public enum PendingSelectionStatus
{
    Proposed,
    Confirmed
}

public enum ConversationFlow
{
    None,
    Auth_WaitingEmail,
    Auth_WaitingOtp,
    Settings_WaitingStopWords,
    Select_ReviewingWeek,
    Select_PickingDay,
    Select_PickingDish,
    Select_PickingReplacement
}
```

- [ ] **Step 2: Create User.cs**

```csharp
namespace DelicutTelegramBot.Models.Domain;

public class User
{
    public Guid Id { get; set; }
    public long TelegramUserId { get; set; }
    public long TelegramChatId { get; set; }
    public string? DelicutEmail { get; set; }
    public string? DelicutToken { get; set; }
    public string? DelicutCustomerId { get; set; }
    public string? DelicutSubscriptionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public UserSettings? Settings { get; set; }
    public List<SelectionHistory> SelectionHistories { get; set; } = [];
    public List<PendingSelection> PendingSelections { get; set; } = [];
}
```

- [ ] **Step 3: Create UserSettings.cs**

```csharp
namespace DelicutTelegramBot.Models.Domain;

public class UserSettings
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public SelectionStrategy Strategy { get; set; }
    public List<string> StopWords { get; set; } = [];
    public bool PreferHistory { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
```

- [ ] **Step 4: Create SelectionHistory.cs**

```csharp
namespace DelicutTelegramBot.Models.Domain;

public class SelectionHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DishId { get; set; } = string.Empty;
    public string DishName { get; set; } = string.Empty;
    public string VariantProtein { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public DateOnly SelectedDate { get; set; }
    public bool WasUserChoice { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
```

- [ ] **Step 5: Create PendingSelection.cs**

```csharp
namespace DelicutTelegramBot.Models.Domain;

public class PendingSelection
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly DeliveryDate { get; set; }
    public string DeliveryId { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string MealCategory { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public string DishId { get; set; } = string.Empty;
    public string DishName { get; set; } = string.Empty;
    public string VariantProtein { get; set; } = string.Empty;
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public PendingSelectionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
```

- [ ] **Step 6: Build to verify**

Run: `cd DelicutTelegramBot && dotnet build`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: add domain models and enums"
```

---

## Task 3: Delicut API Models (DTOs)

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Delicut/Subscription.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Delicut/MealTypeInfo.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Delicut/Dish.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Delicut/DishVariant.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Delicut/OtpResponse.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Delicut/LoginResponse.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Delicut/WeekDeliverySchedule.cs`

- [ ] **Step 1: Create all Delicut DTOs**

Create each file matching the spec. Key points:
- Use `System.Text.Json.Serialization.JsonPropertyName` for snake_case API mapping
- `Subscription.cs`: All fields from spec including `MealTypes` list
- `Dish.cs`: Include `Variants` list, `AvgRating`, `TotalRatings`, `AssignedDays`
- `DishVariant.cs`: `ProteinOption`, `Size`, `ProteinCategory`, macros, `Allergens`
- `WeekDeliverySchedule.cs`: `DeliveryDay` with `DeliveryId`, `UniqueId`, `IsLocked`
- `OtpResponse.cs` / `LoginResponse.cs`: Simple response wrappers

- [ ] **Step 2: Build to verify**

Run: `cd DelicutTelegramBot && dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: add Delicut API DTOs"
```

---

## Task 4: Service DTOs (Return Types)

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Dto/WeeklyProposal.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Dto/ProposedDish.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Dto/DishAlternative.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Dto/DishSubmission.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Models/Dto/AiModels.cs`

- [ ] **Step 1: Create all DTO files**

- `WeeklyProposal.cs`: `WeeklyProposal` + `DayProposal` classes
- `ProposedDish.cs`: Single proposed dish with macros + reasoning
- `DishAlternative.cs`: Alternative for replacement flow
- `DishSubmission.cs`: `DishSubmission` + `PastDishSelection`
- `AiModels.cs`: `AiSelectionRequest`, `AiSelectionResult`, `AiDishPick`, `MealSlot`, `DishSummary`

All fields exactly as defined in spec (see Return Types section).

- [ ] **Step 2: Build and commit**

Run: `cd DelicutTelegramBot && dotnet build`

```bash
git add -A && git commit -m "feat: add service DTOs and AI models"
```

---

## Task 5: EF Core DbContext & Initial Migration

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Infrastructure/AppDbContext.cs`

- [ ] **Step 1: Create AppDbContext**

```csharp
using Microsoft.EntityFrameworkCore;
using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<SelectionHistory> SelectionHistories => Set<SelectionHistory>();
    public DbSet<PendingSelection> PendingSelections => Set<PendingSelection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.TelegramUserId).IsUnique();
            e.HasOne(u => u.Settings).WithOne(s => s.User)
             .HasForeignKey<UserSettings>(s => s.UserId);
            e.HasMany(u => u.SelectionHistories).WithOne(h => h.User)
             .HasForeignKey(h => h.UserId);
            e.HasMany(u => u.PendingSelections).WithOne(p => p.User)
             .HasForeignKey(p => p.UserId);
        });

        modelBuilder.Entity<UserSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Strategy).HasConversion<string>();
            // PostgreSQL text[] for StopWords
        });

        modelBuilder.Entity<SelectionHistory>(e =>
        {
            e.HasKey(h => h.Id);
            e.HasIndex(h => new { h.UserId, h.DishId, h.SelectedDate, h.MealCategory })
             .IsUnique();
        });

        modelBuilder.Entity<PendingSelection>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Status).HasConversion<string>();
        });
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `cd DelicutTelegramBot && dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Create initial migration** (requires connection string — skip actual `dotnet ef` for now, just verify DbContext compiles)

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: add EF Core DbContext with entity configuration"
```

---

## Task 6: CutoffHelper with Tests

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Helpers/CutoffHelper.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot.Tests/Helpers/CutoffHelperTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using DelicutTelegramBot.Helpers;

namespace DelicutTelegramBot.Tests.Helpers;

public class CutoffHelperTests
{
    [Fact]
    public void Wednesday_IsLocked_AfterMonday1200_ReturnsTrue()
    {
        // Wednesday Mar 26 — cutoff is Monday Mar 24 12:00 UTC+4
        var target = new DateOnly(2026, 3, 26);
        var now = new DateTimeOffset(2026, 3, 24, 12, 1, 0, TimeSpan.FromHours(4));
        Assert.True(CutoffHelper.IsLocked(target, now));
    }

    [Fact]
    public void Wednesday_IsLocked_BeforeMonday1200_ReturnsFalse()
    {
        var target = new DateOnly(2026, 3, 26);
        var now = new DateTimeOffset(2026, 3, 24, 11, 59, 0, TimeSpan.FromHours(4));
        Assert.False(CutoffHelper.IsLocked(target, now));
    }

    [Fact]
    public void Monday_IsLocked_AfterSaturday1200_ReturnsTrue()
    {
        var target = new DateOnly(2026, 3, 23);
        var now = new DateTimeOffset(2026, 3, 21, 12, 0, 0, TimeSpan.FromHours(4));
        Assert.True(CutoffHelper.IsLocked(target, now));
    }

    [Fact]
    public void Today_IsAlwaysLocked()
    {
        var target = new DateOnly(2026, 3, 21);
        var now = new DateTimeOffset(2026, 3, 21, 8, 0, 0, TimeSpan.FromHours(4));
        Assert.True(CutoffHelper.IsLocked(target, now));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd DelicutTelegramBot && dotnet test`
Expected: Compilation error — CutoffHelper doesn't exist.

- [ ] **Step 3: Implement CutoffHelper**

```csharp
namespace DelicutTelegramBot.Helpers;

public static class CutoffHelper
{
    private static readonly TimeSpan Utc4 = TimeSpan.FromHours(4);

    public static bool IsLocked(DateOnly targetDate, DateTimeOffset? nowOverride = null)
    {
        var cutoff = new DateTimeOffset(
            targetDate.ToDateTime(new TimeOnly(12, 0)).AddDays(-2), Utc4);
        var now = nowOverride ?? DateTimeOffset.UtcNow.ToOffset(Utc4);
        return now >= cutoff;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd DelicutTelegramBot && dotnet test`
Expected: All 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add CutoffHelper with UTC+4 T-2 day cutoff logic"
```

---

## Task 7: Conversation State Manager with Tests

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/State/ConversationStateManager.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot.Tests/State/ConversationStateManagerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.State;

namespace DelicutTelegramBot.Tests.State;

public class ConversationStateManagerTests
{
    [Fact]
    public void GetOrCreate_NewUser_ReturnsNoneState()
    {
        var manager = new ConversationStateManager();
        var state = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.None, state.CurrentFlow);
    }

    [Fact]
    public void GetOrCreate_ExistingUser_ReturnsSameState()
    {
        var manager = new ConversationStateManager();
        var state1 = manager.GetOrCreate(12345);
        state1.CurrentFlow = ConversationFlow.Auth_WaitingEmail;
        var state2 = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.Auth_WaitingEmail, state2.CurrentFlow);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var manager = new ConversationStateManager();
        var state = manager.GetOrCreate(12345);
        state.CurrentFlow = ConversationFlow.Auth_WaitingOtp;
        manager.Reset(12345);
        var newState = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.None, newState.CurrentFlow);
    }
}
```

- [ ] **Step 2: Run tests — expect compilation failure**

- [ ] **Step 3: Implement ConversationStateManager**

```csharp
using System.Collections.Concurrent;
using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.State;

public class ConversationState
{
    public ConversationFlow CurrentFlow { get; set; }
    public Dictionary<string, object> FlowData { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

public class ConversationStateManager
{
    private readonly ConcurrentDictionary<long, ConversationState> _states = new();

    public ConversationState GetOrCreate(long telegramUserId)
    {
        return _states.GetOrAdd(telegramUserId, _ => new ConversationState());
    }

    public void Reset(long telegramUserId)
    {
        _states.TryRemove(telegramUserId, out _);
    }

    public void CleanupStale(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var kvp in _states)
        {
            if (kvp.Value.LastActivity < cutoff)
                _states.TryRemove(kvp.Key, out _);
        }
    }
}
```

- [ ] **Step 4: Run tests — all pass**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add in-memory conversation state manager"
```

---

## Task 8: Service Interfaces

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/IDelicutApiService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/IUserService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/IOpenAiService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/IMenuSelectionService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/IDishFilterService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/IFallbackSelectionService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/ISelectionHistoryService.cs`

- [ ] **Step 1: Create all 7 interfaces**

`IDelicutApiService`, `IMenuSelectionService`, `IOpenAiService`: copy from spec verbatim.

`IUserService` (not in spec's Interfaces section — defined here for the plan):
```csharp
public interface IUserService
{
    Task<User?> GetByTelegramIdAsync(long telegramUserId);
    Task<User> CreateOrUpdateAsync(long telegramUserId, long chatId, string email,
                                    string token, string customerId);
    Task UpdateSettingsAsync(Guid userId, Action<UserSettings> update);
    Task UpdateTokenAsync(Guid userId, string token);
}
```

`IDishFilterService`:
```csharp
public interface IDishFilterService
{
    List<Dish> Filter(List<Dish> dishes, List<string> stopWords,
                      List<string> avoidIngredients, List<string> avoidCategories,
                      string kcalRange, string proteinCategory);
}
```

`IFallbackSelectionService`:
```csharp
public interface IFallbackSelectionService
{
    AiSelectionResult Select(List<DishSummary> dishes, SelectionStrategy strategy,
                              List<MealSlot> mealSlots,
                              Dictionary<string, List<string>> weekContext);
}
```

`ISelectionHistoryService`:
```csharp
public interface ISelectionHistoryService
{
    Task<List<string>> GetPreviousChoiceNamesAsync(Guid userId, int maxCount = 50);
    Task RecordSelectionsAsync(Guid userId, List<PendingSelection> selections, bool wasUserChoice);
}
```

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add service interfaces"
```

---

## Task 9: Delicut API Service Stub

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Infrastructure/DelicutApiService.cs`

- [ ] **Step 1: Create stub implementation**

Every method throws `NotImplementedException("Delicut API not yet implemented")`. This satisfies the interface for DI and allows the rest of the bot to compile.

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add IDelicutApiService stub implementation"
```

---

## Task 10: UserService Implementation

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/UserService.cs`

- [ ] **Step 1: Implement UserService**

Uses `AppDbContext` injected via constructor. Methods:
- `GetByTelegramIdAsync`: Query by `TelegramUserId`, include `Settings`
- `CreateOrUpdateAsync`: Upsert user + create default `UserSettings` if new
- `UpdateSettingsAsync`: Load settings, apply action, save
- `UpdateTokenAsync`: Update `DelicutToken` field

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add UserService with EF Core persistence"
```

---

## Task 10b: SelectionHistoryService Implementation

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/SelectionHistoryService.cs`

- [ ] **Step 1: Implement SelectionHistoryService**

Uses `AppDbContext`. Methods:
- `GetPreviousChoiceNamesAsync`: Query `SelectionHistory` for this user, ordered by `SelectedDate` desc, take `maxCount`, return distinct `DishName` list. Used to populate `AiSelectionRequest.PreviousChoices`.
- `RecordSelectionsAsync`: Bulk insert `SelectionHistory` rows from confirmed `PendingSelection` list. Set `WasUserChoice` flag.

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add SelectionHistoryService for past choices tracking"
```

---

## Task 11: DishFilterService with Tests

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/DishFilterService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot.Tests/Services/DishFilterServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Test cases:
- Dish with stop word in name is removed
- Case-insensitive stop word match
- Dish with avoided ingredient is removed
- Dish variant not matching user's kcal_range/protein_category is removed
- Dish with no matching variants after filtering is removed entirely
- Empty stop words list filters nothing

- [ ] **Step 2: Run tests — expect failure**

- [ ] **Step 3: Implement DishFilterService (implements IDishFilterService)**

```csharp
public class DishFilterService : IDishFilterService
{
    public List<Dish> Filter(List<Dish> dishes, List<string> stopWords,
                              List<string> avoidIngredients, List<string> avoidCategories,
                              string kcalRange, string proteinCategory)
    {
        return dishes
            .Where(d => !stopWords.Any(sw =>
                d.DishName.Contains(sw, StringComparison.OrdinalIgnoreCase)))
            .Where(d => !d.Ingredients.Any(i =>
                avoidIngredients.Contains(i, StringComparer.OrdinalIgnoreCase)))
            .Select(d => FilterVariants(d, kcalRange, proteinCategory))
            .Where(d => d.Variants.Count > 0)
            .ToList();
    }

    private static Dish FilterVariants(Dish dish, string kcalRange, string proteinCategory)
    {
        // Return a copy with only matching variants
        // Match size == kcalRange (lowercase) and proteinCategory
    }
}
```

- [ ] **Step 4: Run tests — all pass**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add DishFilterService with stop words, ingredient, and variant filtering"
```

---

## Task 12: FallbackSelectionService with Tests

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/FallbackSelectionService.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot.Tests/Services/FallbackSelectionServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Test cases:
- MacrosMax strategy ranks higher-protein dishes first
- LowestCal strategy ranks lower-calorie dishes first
- Default strategy ranks by rating
- Same cuisine as already-selected days gets penalized
- Returns correct count per meal slot

- [ ] **Step 2: Run tests — expect failure**

- [ ] **Step 3: Implement FallbackSelectionService (implements IFallbackSelectionService)**

Score formula: `strategy_score * 0.6 + rating_score * 0.3 + variety_score * 0.1`
Takes: filtered dishes, strategy, meal slots, week context.
Returns: `AiSelectionResult` (same shape as OpenAI would return).

- [ ] **Step 4: Run tests — all pass**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: add fallback selection service with scoring"
```

---

## Task 13: OpenAiService Implementation

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/OpenAiService.cs`

- [ ] **Step 1: Implement OpenAiService**

- Inject `OpenAI.ChatClient` (configured with API key and model from options)
- `SelectDishesAsync`: Build system prompt with strategy instructions, serialize `AiSelectionRequest` as user message JSON, request structured JSON response
- Parse response into `AiSelectionResult`
- On failure: log error, return null (caller falls back to FallbackSelectionService)

System prompt should include all 6 AI instructions from spec (strategy, cuisine variety, rating weight, no repeats, history preference, return format).

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add OpenAI service for dish selection"
```

---

## Task 14: MenuSelectionService Implementation

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Services/MenuSelectionService.cs`

- [ ] **Step 1: Implement MenuSelectionService**

Depends on: `IDelicutApiService`, `IUserService`, `IOpenAiService`, `IDishFilterService`, `IFallbackSelectionService`, `ISelectionHistoryService`, `ConversationStateManager`, `AppDbContext`.

Methods:
- `SelectForWeekAsync`: Full orchestration — fetch subscription, get schedule, fetch menus per day/category, **cache menus in ConversationStateManager FlowData** (keyed by `"menu:{date}:{mealCategory}"`), filter, **fetch previous choices via ISelectionHistoryService** to populate `AiSelectionRequest.PreviousChoices`, call AI (or fallback), store as `PendingSelection` rows, return `WeeklyProposal`. Clears existing `Proposed` pending selections for this user first.
- `GetAlternativesAsync`: **Load cached menu from ConversationStateManager FlowData**, filter out already-selected dishes, ask AI for ranked alternatives (or fallback sort), return top 5.
- `ReplaceDishAsync`: Update the specific `PendingSelection` row.
- `ConfirmDayAsync`: Set status to `Confirmed` for all pending selections on that day.
- `ConfirmWeekAsync`: For `Confirmed` rows, call `SubmitDishSelectionAsync` per day, **record to SelectionHistory via ISelectionHistoryService**, delete pending. Handle partial failures.

**Error handling pattern for Delicut 401:** Wrap all `IDelicutApiService` calls in try-catch. On 401 (HttpRequestException with StatusCode 401), throw a custom `DelicutAuthExpiredException`. Handlers catch this and send "Session expired. Use /start to re-authenticate." + reset conversation state. Since `DelicutApiService` is a stub for now, this establishes the pattern for when it's implemented.

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add MenuSelectionService orchestrator"
```

---

## Task 14b: MenuSelectionService Tests

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot.Tests/Services/MenuSelectionServiceTests.cs`

- [ ] **Step 1: Write tests with mocked dependencies**

Add NuGet `Moq` to test project: `dotnet add DelicutTelegramBot.Tests package Moq`

Test cases using mocked `IDelicutApiService`, `IDishFilterService`, `IFallbackSelectionService`, `ISelectionHistoryService`:
- `SelectForWeekAsync_ClearsExistingProposals_BeforeCreatingNew` — verify old `Proposed` rows are removed
- `SelectForWeekAsync_SkipsLockedDays` — mock schedule with past days, verify they appear in `LockedDays`
- `SelectForWeekAsync_FallsBackWhenOpenAiFails` — mock `IOpenAiService` to return null, verify `IFallbackSelectionService` is called
- `SelectForWeekAsync_PopulatesPreviousChoices` — verify `ISelectionHistoryService.GetPreviousChoiceNamesAsync` is called and result passed to AI
- `ConfirmWeekAsync_HandlesPartialFailure` — mock submit to fail for one day, verify others still succeed

- [ ] **Step 2: Run tests — expect failure (MenuSelectionService not yet wired for tests)**

- [ ] **Step 3: Adjust MenuSelectionService if needed to make tests pass**

- [ ] **Step 4: Run tests — all pass**

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "test: add MenuSelectionService orchestration tests"
```

---

## Task 15: Program.cs & DI Wiring

**Files:**
- Modify: `DelicutTelegramBot/DelicutTelegramBot/Program.cs`
- Create: `DelicutTelegramBot/DelicutTelegramBot/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create ServiceCollectionExtensions**

Register:
- `AppDbContext` with Npgsql
- `ConversationStateManager` as singleton
- `IUserService` → `UserService` as scoped
- `IDelicutApiService` → `DelicutApiService` as scoped
- `IOpenAiService` → `OpenAiService` as scoped
- `IMenuSelectionService` → `MenuSelectionService` as scoped
- `IDishFilterService` → `DishFilterService` as scoped
- `IFallbackSelectionService` → `FallbackSelectionService` as scoped
- `ISelectionHistoryService` → `SelectionHistoryService` as scoped
- `WednesdayReminderService` as hosted service
- `ITelegramBotClient` as singleton (from config)
- All handlers as scoped
- `BotHandler` as scoped
- `IHttpClientFactory` via `AddHttpClient`

- [ ] **Step 2: Rewrite Program.cs**

```csharp
using DelicutTelegramBot.Extensions;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDelicutBot(builder.Configuration);
var host = builder.Build();

// Apply pending migrations
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// Start polling
var bot = host.Services.GetRequiredService<ITelegramBotClient>();
var cts = new CancellationTokenSource();
bot.StartReceiving(
    updateHandler: async (client, update, ct) =>
    {
        using var scope = host.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<BotHandler>();
        await handler.HandleUpdateAsync(update, ct);
    },
    errorHandler: (client, ex, ct) => { /* log */ return Task.CompletedTask; },
    cancellationToken: cts.Token);

await host.RunAsync();
```

- [ ] **Step 3: Build and commit**

```bash
git add -A && git commit -m "feat: add DI wiring and Program.cs with bot polling"
```

---

## Task 16: BotHandler (Message/Callback Router)

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Handlers/BotHandler.cs`

- [ ] **Step 1: Implement BotHandler**

Routes incoming Telegram updates:
- Text messages starting with `/start` → `StartHandler`
- Text messages starting with `/select` → `SelectWeekHandler`
- Text messages starting with `/settings` → `SettingsHandler`
- Text messages starting with `/cancel` → `CancelHandler`
- Plain text messages (no command) → route based on `ConversationState.CurrentFlow` to appropriate handler
- Callback queries → parse callback data prefix, route to `SettingsHandler`, `SelectWeekHandler`, or `ChangeDishHandler`

All `/` commands reset conversation state before dispatching.

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add BotHandler with message and callback routing"
```

---

## Task 17: CancelHandler

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Handlers/CancelHandler.cs`

- [ ] **Step 1: Implement CancelHandler**

Simplest handler: resets conversation state, sends "Cancelled. Use /select, /settings, or /start."

- [ ] **Step 2: Commit**

```bash
git add -A && git commit -m "feat: add CancelHandler"
```

---

## Task 18: StartHandler (Auth Flow)

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Handlers/StartHandler.cs`

- [ ] **Step 1: Implement StartHandler**

Handles the multi-step auth flow using conversation state:
1. On `/start` → set flow to `Auth_WaitingEmail`, send "Welcome! Enter your Delicut email:"
2. On text in `Auth_WaitingEmail` → validate email format, call `RequestOtpAsync`, set flow to `Auth_WaitingOtp`, send "OTP sent. Enter the code:"
3. On text in `Auth_WaitingOtp` → call `VerifyOtpAsync`, on success: fetch subscription, save user via `IUserService`, reset flow, send welcome with plan summary. On failure: retry count in FlowData, max 3 attempts.

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add StartHandler with OTP auth flow"
```

---

## Task 19: SettingsHandler

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Handlers/SettingsHandler.cs`

- [ ] **Step 1: Implement SettingsHandler**

Handles `/settings` command and all settings-related callbacks:
- `/settings` → Show inline keyboard with current values: `[Strategy: X]` `[Stop Words]` `[Prefer History: ON/OFF]` `[Re-authenticate]`
- Callback `settings:strategy` → Show strategy options as buttons
- Callback `settings:strategy:LowestCal` (etc.) → Update DB, refresh keyboard
- Callback `settings:stopwords` → Set flow to `Settings_WaitingStopWords`, ask for comma-separated input
- Text in `Settings_WaitingStopWords` → Parse, save to DB, confirm, reset flow
- Callback `settings:history` → Toggle `PreferHistory`, refresh keyboard
- Callback `settings:reauth` → Trigger auth flow (delegate to StartHandler)

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add SettingsHandler with inline keyboard management"
```

---

## Task 20: SelectWeekHandler

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Handlers/SelectWeekHandler.cs`

- [ ] **Step 1: Implement SelectWeekHandler**

Handles `/select` and approval callbacks:
1. On `/select` → verify auth, call `MenuSelectionService.SelectForWeekAsync`, send week overview message with inline keyboard `[Approve All]` `[Change Dishes]`
2. Format each day: date, dish list with macros, daily totals
3. Callback `select:approve_all` → call `ConfirmDayAsync` for each day, then `ConfirmWeekAsync`, send confirmation
4. Callback `select:change` → delegate to `ChangeDishHandler`

Store the `WeeklyProposal` in conversation state FlowData for the change flow.

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add SelectWeekHandler with week overview and approval"
```

---

## Task 21: ChangeDishHandler

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/Handlers/ChangeDishHandler.cs`

- [ ] **Step 1: Implement ChangeDishHandler**

Handles the drill-down change flow:
1. Callback `change:pick_day` → Show day buttons `[Mon] [Tue] ...` (only unlocked)
2. Callback `change:day:2026-03-24` → Show that day's dishes with `[Change]` per dish
3. Callback `change:dish:2026-03-24:meal:0` → Call `GetAlternativesAsync`, show alternatives + `[Keep Current]`
4. Callback `change:replace:2026-03-24:meal:0:dishId:proteinOpt` → Call `ReplaceDishAsync`, show updated day with `[Confirm Day]` `[Change Another]`
5. Callback `change:confirm_day:2026-03-24` → Call `ConfirmDayAsync`

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add ChangeDishHandler with per-dish replacement flow"
```

---

## Task 22: WednesdayReminderService

**Files:**
- Create: `DelicutTelegramBot/DelicutTelegramBot/BackgroundServices/WednesdayReminderService.cs`

- [ ] **Step 1: Implement WednesdayReminderService**

`BackgroundService` that:
- Calculates next Wednesday 09:00 UTC+4
- Sleeps until then (`Task.Delay`)
- On wake: query users where `DelicutToken` is not null. For each user, call `IDelicutApiService.GetSubscriptionDetailsAsync` to check `EndDate > today` (active subscription). Send reminder only to users with active subscriptions. On 401 or error, skip that user silently.
- Send reminder message via `ITelegramBotClient` to `TelegramChatId`
- Loop: calculate next Wednesday, sleep again
- Handle cancellation token for graceful shutdown

- [ ] **Step 2: Build and commit**

```bash
git add -A && git commit -m "feat: add Wednesday menu reminder background service"
```

---

## Task 23: End-to-End Build & Smoke Test

- [ ] **Step 1: Full build**

Run: `cd DelicutTelegramBot && dotnet build`
Expected: 0 errors, 0 warnings (or only expected warnings).

- [ ] **Step 2: Run all tests**

Run: `cd DelicutTelegramBot && dotnet test`
Expected: All tests pass.

- [ ] **Step 3: Final commit**

```bash
git add -A && git commit -m "chore: ensure clean build and all tests pass"
```
