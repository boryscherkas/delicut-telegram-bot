# Delicut Telegram Bot — Design Specification

## Overview

A .NET 9 Telegram bot that automates weekly meal selection from Delicut (Dubai meal delivery). Uses OpenAI to intelligently select dishes based on user preferences, nutritional strategy, and variety. Supports multiple users with per-user authentication and settings.

## Tech Stack

| Component | Choice |
|-----------|--------|
| Runtime | .NET 9 |
| Project structure | Single project, folder-based separation |
| Database | Supabase (PostgreSQL) via EF Core with migrations |
| Telegram | Telegram.Bot library |
| AI | OpenAI API (gpt-4o-mini) |
| DI / Hosting | Microsoft.Extensions.Hosting |
| HTTP | IHttpClientFactory |
| Config | appsettings.json + environment variables |

## Domain Model

### Subscription (from Delicut `/api/v2/subscription/get-details`)

```
Subscription
├── Id (string — Delicut _id)
├── CustomerId (string)
├── PlanType (string — "MONTHLY")
├── DeliveryDays (int — e.g. 6)
├── KcalRange (string — "Extra_large", "Large", etc.)
├── ProteinCategory (string — "low", "balance")
├── IsVegetarian (bool)
├── SelectedMeals: List<string> (["lunch"])
├── MealTypes: List<MealTypeInfo>
│   ├── MealCategory (string — "meal", "breakfast", "snack")
│   ├── MealType (string — "lunch", "breakfast", "snack")
│   ├── KcalRange (string)
│   ├── Qty (int — e.g. 3 meals, 1 breakfast)
│   ├── ProteinCategory (string)
│   └── IsVeg (bool)
├── AvoidIngredients: List<string>
├── AvoidCategory: List<string>
├── DeliveryStartDate (DateTime)
├── EndDate (DateTime)
└── IsFlexPlan (bool)
```

### Dish (from Delicut `/api/v2/recipes/fetch-all-live`)

```
Dish
├── Id (string — _id)
├── RecipeId (string)
├── DishName (string)
├── MealCategory (string — "Meal", "Breakfast", "Snack")
├── Cuisine (string — "Indian", "American", "Asian", etc.)
├── DishType: List<string> (["Pasta"], ["Rice"], ["Salad"])
├── Description (string)
├── SpiceLevel (string — "Low", "Medium")
├── Ingredients: List<string>
├── AllergensContain: List<string>
├── AllergensFreeFrom: List<string>
├── AvgRating (double)
├── TotalRatings (int)
├── AssignedDays: List<string> (["Mon", "Fri"])
├── ImageUrl (string — from protein_category_info[].image[])
└── Variants: List<DishVariant>
```

### DishVariant

```
DishVariant
├── ProteinOption (string — "Chicken", "Beef", "Tofu (Veg)", etc.)
├── Size (string — "extra_large", "large", etc.)
├── ProteinCategory (string — "balance", "low")
├── Kcal (double)
├── Fat (double)
├── Carb (double)
├── Protein (double)
├── NetWeight (double — grams)
└── Allergens: List<string>
```

### Meal Composition

Meal composition varies per user's subscription. Examples:
- User A: 3 main meals per day
- User B: 2 main meals + 1 breakfast + 1 snack

The bot reads `MealTypes` from the subscription and fetches menus per `meal_category`. AI selection considers the full day's nutrition across all categories.

## Database Schema (EF Core)

### Users Table

```csharp
public class User
{
    public Guid Id { get; set; }
    public long TelegramUserId { get; set; }  // UNIQUE
    public long TelegramChatId { get; set; }
    public string? DelicutEmail { get; set; }
    public string? DelicutToken { get; set; }  // JWT
    public string? DelicutCustomerId { get; set; }
    public string? DelicutSubscriptionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public UserSettings Settings { get; set; }
    public List<SelectionHistory> SelectionHistories { get; set; }
    public List<PendingSelection> PendingSelections { get; set; }
}
```

### UserSettings Table

```csharp
public class UserSettings
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }  // FK → Users
    public SelectionStrategy Strategy { get; set; }  // Default, LowestCal, MacrosMax
    public List<string> StopWords { get; set; }  // PostgreSQL text[]
    public bool PreferHistory { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; }
}

public enum SelectionStrategy
{
    Default,
    LowestCal,
    MacrosMax
}
```

### SelectionHistory Table

```csharp
public class SelectionHistory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DishId { get; set; }       // Delicut recipe_id
    public string DishName { get; set; }
    public string VariantProtein { get; set; } // "Chicken", "Beef", etc.
    public string MealCategory { get; set; }   // "meal", "breakfast", "snack"
    public DateOnly SelectedDate { get; set; }
    public bool WasUserChoice { get; set; }    // manual pick vs AI-selected
    public DateTime CreatedAt { get; set; }

    public User User { get; set; }
}
// UNIQUE constraint: (UserId, DishId, SelectedDate, MealCategory)
```

### PendingSelections Table

```csharp
public class PendingSelection
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly DeliveryDate { get; set; }
    public string DeliveryId { get; set; }   // from Delicut
    public string UniqueId { get; set; }     // from Delicut
    public string MealCategory { get; set; } // "meal", "breakfast", "snack"
    public int SlotIndex { get; set; }
    public string DishId { get; set; }
    public string DishName { get; set; }
    public string VariantProtein { get; set; }
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public PendingSelectionStatus Status { get; set; }  // Proposed, Confirmed
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; }
}

public enum PendingSelectionStatus
{
    Proposed,
    Confirmed
}
```

## Project Structure

```
DelicutTelegramBot/
├── Models/
│   ├── Domain/
│   │   ├── User.cs
│   │   ├── UserSettings.cs
│   │   ├── SelectionHistory.cs
│   │   ├── PendingSelection.cs
│   │   └── Enums.cs (SelectionStrategy, PendingSelectionStatus)
│   └── Delicut/
│       ├── Subscription.cs
│       ├── MealTypeInfo.cs
│       ├── Dish.cs
│       ├── DishVariant.cs
│       ├── OtpResponse.cs
│       ├── LoginResponse.cs
│       └── WeekDeliverySchedule.cs
├── Services/
│   ├── IDelicutApiService.cs
│   ├── IMenuSelectionService.cs
│   ├── IOpenAiService.cs
│   ├── IUserService.cs
│   ├── ISelectionHistoryService.cs
│   ├── MenuSelectionService.cs
│   ├── OpenAiService.cs
│   ├── UserService.cs
│   └── SelectionHistoryService.cs
├── Handlers/
│   ├── StartHandler.cs
│   ├── AuthHandler.cs
│   ├── SelectWeekHandler.cs
│   ├── SettingsHandler.cs
│   ├── ChangeDishHandler.cs
│   └── CallbackRouter.cs
├── Infrastructure/
│   ├── AppDbContext.cs
│   ├── Migrations/
│   └── DelicutApiService.cs (stub — implements IDelicutApiService with NotImplementedException)
├── BackgroundServices/
│   └── WednesdayReminderService.cs (IHostedService)
├── State/
│   └── ConversationStateManager.cs
├── Extensions/
│   └── ServiceCollectionExtensions.cs
├── Program.cs
└── appsettings.json
```

## Interfaces

### IDelicutApiService

```csharp
public interface IDelicutApiService
{
    Task<OtpResponse> RequestOtpAsync(string email);
    Task<LoginResponse> VerifyOtpAsync(string email, string otp);
    Task<SubscriptionDetails> GetSubscriptionDetailsAsync(string token);
    Task<List<Dish>> FetchMenuAsync(string token, string deliveryId,
                                     string mealCategory, string uniqueId);
    Task<WeekDeliverySchedule> GetDeliveryScheduleAsync(string token,
                                                         string subscriptionId);
    Task SubmitDishSelectionAsync(string token, string deliveryId,
                                  string uniqueId, List<DishSubmission> selections);
    Task<List<PastDishSelection>> GetPastSelectionsAsync(string token,
                                                          string subscriptionId);
}
```

### IMenuSelectionService

```csharp
public interface IMenuSelectionService
{
    Task<WeeklyProposal> SelectForWeekAsync(Guid userId);
    Task<List<DishAlternative>> GetAlternativesAsync(Guid userId, DateOnly date,
                                                      string mealCategory, int slotIndex);
    Task ReplaceDishAsync(Guid userId, DateOnly date, string mealCategory,
                           int slotIndex, string newDishId, string proteinOption);
    Task ConfirmDayAsync(Guid userId, DateOnly date);
    Task ConfirmWeekAsync(Guid userId);
}
```

### Return Types

```csharp
public class WeeklyProposal
{
    public List<DayProposal> Days { get; set; }
    public List<DateOnly> LockedDays { get; set; }  // skipped because past cutoff
}

public class DayProposal
{
    public DateOnly Date { get; set; }
    public string DayOfWeek { get; set; }
    public List<ProposedDish> Dishes { get; set; }
    public double TotalKcal { get; set; }
    public double TotalProtein { get; set; }
    public double TotalCarb { get; set; }
    public double TotalFat { get; set; }
}

public class ProposedDish
{
    public string DishId { get; set; }
    public string DishName { get; set; }
    public string ProteinOption { get; set; }
    public string MealCategory { get; set; }
    public int SlotIndex { get; set; }
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public string AiReasoning { get; set; }
}

public class DishAlternative
{
    public string DishId { get; set; }
    public string DishName { get; set; }
    public string ProteinOption { get; set; }
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public double AvgRating { get; set; }
}

public class AiSelectionResult
{
    public List<AiDishPick> Picks { get; set; }
}

public class AiDishPick
{
    public string DishId { get; set; }
    public string ProteinOption { get; set; }
    public string MealCategory { get; set; }
    public int SlotIndex { get; set; }
    public string Reasoning { get; set; }
}

public class MealSlot
{
    public string Category { get; set; }  // "meal", "breakfast", "snack"
    public int Count { get; set; }
}

public class DishSummary
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Cuisine { get; set; }
    public double Kcal { get; set; }
    public double Protein { get; set; }
    public double Carb { get; set; }
    public double Fat { get; set; }
    public double Rating { get; set; }
    public int TotalRatings { get; set; }
    public string SpiceLevel { get; set; }
    public string ProteinOption { get; set; }
    public string MealCategory { get; set; }
}

public class DishSubmission
{
    public string DishId { get; set; }
    public string ProteinOption { get; set; }
    public string MealCategory { get; set; }
    public int SlotIndex { get; set; }
}

public class PastDishSelection
{
    public string DishId { get; set; }
    public string DishName { get; set; }
    public string ProteinOption { get; set; }
    public DateOnly Date { get; set; }
}
```

### IOpenAiService

```csharp
public interface IOpenAiService
{
    Task<AiSelectionResult> SelectDishesAsync(AiSelectionRequest request);
}

public class AiSelectionRequest
{
    public SelectionStrategy Strategy { get; set; }
    public DateOnly Date { get; set; }
    public List<MealSlot> MealSlots { get; set; }  // category + count
    public List<DishSummary> AvailableDishes { get; set; }
    public List<string> StopWords { get; set; }
    public List<string> PreviousChoices { get; set; }
    public bool PreferHistory { get; set; }
    public Dictionary<string, List<string>> WeekContext { get; set; }  // other days' selections
}
```

## Core Flows

### 1. Onboarding (`/start`)

```
User sends /start
  → Bot: "Welcome! Enter your Delicut email:"
  → User: "user@email.com"
  → Bot calls IDelicutApiService.RequestOtpAsync(email)
  → Bot: "OTP sent to your email. Enter the code:"
  → User: "7544"
  → Bot calls IDelicutApiService.VerifyOtpAsync(email, otp)
  → JWT token extracted from response cookies
  → Bot fetches subscription details
  → User + settings saved to DB
  → Bot: "Connected! Your plan: 3 meals/day, Extra Large, Low Carb.
          Use /settings to configure preferences, /select to pick meals."
```

### 2. Weekly Selection (`/select`)

```
User sends /select
  → Verify authenticated (token exists and valid)
  → Fetch subscription details (refresh meal composition)
  → Get delivery schedule (delivery_ids + unique_ids per day)
  → For each upcoming UNLOCKED day:
      → For each meal category in user's subscription:
          → Fetch available menu
  → Apply hard filters:
      - Remove dishes matching stop words (case-insensitive substring on dish_name)
      - Remove dishes with user's avoided ingredients/categories
  → For each day, call OpenAI with:
      - Filtered dishes per category
      - Strategy, preferences, week context
  → Store proposals in pending_selections table
  → Send week overview to user with inline keyboards:
      📅 Monday (Mar 24):
        🍽 1. Fusilli Alfredo (Chicken) — 705 kcal | P:72 C:49 F:23
        🍽 2. Teriyaki Noodles (Chicken) — 636 kcal | P:80 C:50 F:12
        🍽 3. Fresh Poke Bowl (Shrimps) — 663 kcal | P:64 C:47 F:23
        Daily total: 2004 kcal | P:216 C:146 F:58
      [✅ Approve All] [✏️ Change Dishes]
```

### 3. Change Dish Flow

```
User taps [✏️ Change Dishes]
  → Bot shows day buttons: [Mon] [Tue] [Wed] [Thu] [Fri] [Sat]
  → User taps [Mon]
  → Bot shows Monday's dishes with per-dish change buttons:
      1. Fusilli Alfredo (Chicken) [🔄 Change]
      2. Teriyaki Noodles (Chicken) [🔄 Change]
      3. Fresh Poke Bowl (Shrimps)  [🔄 Change]
  → User taps [🔄 Change] on dish 1
  → Bot asks OpenAI for 3-5 alternatives from remaining menu
  → Bot shows alternatives:
      Pick a replacement:
      [Enchillada (Chicken) — 716 kcal]
      [Kung Pao Stir-Fry (Chicken) — 717 kcal]
      [Goulash & Mash (Chicken) — 667 kcal]
      [↩️ Keep Current]
  → User picks → pending_selection updated → show updated day
  → [✅ Confirm Day] [🔄 Change Another]
```

### 4. Settings (`/settings`)

```
/settings → Inline keyboard:
  [Strategy: Default ▼]     → tap → [Default ✓] [Lowest Cal] [Macros Max]
  [Stop Words ✏️]           → tap → "Send stop words separated by commas:"
                              → user: "biryani, veg, paneer"
                              → saved, confirmation shown
  [Prefer History: OFF 🔄]  → tap → toggles, shows new state
  [Re-authenticate 🔑]      → tap → restarts auth flow
```

### 5. Wednesday Menu Reminder

```
WednesdayReminderService (IHostedService):
  → Runs daily at 09:00 UTC+4
  → On Wednesdays only:
      → Query users where DelicutToken is not null
        AND subscription EndDate > today (active subscription)
      → For each user:
          → Send: "🍽 New menu for next week is available! Use /select to choose your dishes."
```

### 6. Cancel Flow (`/cancel`)

```
User sends /cancel at any point
  → Clear conversation state for this user
  → Bot: "Cancelled. Use /select, /settings, or /start."
```

Any command (/start, /select, /settings, /cancel) also resets the current conversation state before starting the new flow.

## Menu Caching

During a `/select` flow, the fetched menus are stored in the conversation state's `FlowData` dictionary (keyed by `"menu:{date}:{mealCategory}"`). This avoids re-fetching from Delicut API when the user requests alternatives for a dish change. The cache lives only for the duration of the flow (cleared on timeout, /cancel, or flow completion).

## Confirm/Submit Semantics

- **`ConfirmDayAsync`**: Updates `pending_selections` status to `Confirmed` for that day. Does NOT call Delicut API yet.
- **`ConfirmWeekAsync`**: For all days with `Confirmed` status, calls `IDelicutApiService.SubmitDishSelectionAsync` per day. On success, copies selections to `selection_history` and deletes `pending_selections`. On partial failure (some days succeed, some fail), reports which days failed and keeps those in `pending_selections` for retry.
- **`[Approve All]` button**: Calls `ConfirmDayAsync` for all days, then `ConfirmWeekAsync`.

## Selection Logic

### Hard Filters (always applied)

1. Remove dishes where `dish_name` contains any stop word (case-insensitive)
2. Remove dishes with ingredients in user's `avoid_ingredients` (from subscription)
3. Remove dishes in user's `avoid_category` (from subscription)
4. Only include variants matching user's `kcal_range` and `protein_category`

### AI Selection (OpenAI)

The AI receives a structured prompt with:

```json
{
  "strategy": "MacrosMax",
  "date": "2026-03-24",
  "day_of_week": "Monday",
  "meal_slots": [
    { "category": "meal", "count": 2 },
    { "category": "breakfast", "count": 1 }
  ],
  "available_dishes": [
    {
      "id": "abc123",
      "name": "Fusilli Alfredo",
      "cuisine": "Mediterranean",
      "kcal": 705, "protein": 72, "carb": 49, "fat": 23,
      "rating": 4.64, "total_ratings": 1743,
      "spice_level": "Medium",
      "protein_option": "Chicken",
      "meal_category": "meal"
    }
  ],
  "stop_words": ["biryani", "veg"],
  "previous_choices": ["Fusilli Alfredo", "Poke Bowl"],
  "prefer_history": true,
  "week_context": {
    "Sunday": ["Teriyaki Noodles", "BBQ Chicken Slider"],
    "Tuesday": ["Goulash & Mash", "Orzo Salad"]
  }
}
```

AI instructions:
1. **Strategy constraints**: MacrosMax → maximize protein; LowestCal → minimize kcal; Default → balanced
2. **Cuisine variety**: avoid same cuisine on consecutive days
3. **Rating weight**: prefer dishes with higher avg_rating (weighted by total_ratings)
4. **No repeats**: don't select the same dish on multiple days within the week
5. **History preference**: if enabled, boost dishes from previous_choices list
6. **Return**: ordered list of dish IDs per slot with brief reasoning

### Fallback (OpenAI unavailable)

Sort dishes by: `strategy_score * 0.6 + rating_score * 0.3 + variety_score * 0.1`
- `strategy_score`: protein for MacrosMax, inverse kcal for LowestCal, rating for Default
- `rating_score`: normalized avg_rating
- `variety_score`: penalize same cuisine as already-selected days

## Cutoff Logic

```csharp
public static bool IsLocked(DateOnly targetDate)
{
    var cutoff = targetDate.ToDateTime(new TimeOnly(12, 0))
                           .AddDays(-2);
    var nowUtc4 = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(4));
    return nowUtc4 >= new DateTimeOffset(cutoff, TimeSpan.FromHours(4));
}
```

- Wednesday dishes: locked after Monday 12:00 UTC+4
- Applied in: `/select` (skip locked days), change flow (reject), submit (final check)

## Conversation State

In-memory `ConcurrentDictionary<long, ConversationState>` keyed by telegram_user_id:

```csharp
public class ConversationState
{
    public ConversationFlow CurrentFlow { get; set; }
    public Dictionary<string, object> FlowData { get; set; }
    public DateTime LastActivity { get; set; }
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

Timeout cleanup: states older than 30 minutes are cleared via periodic check.

## Error Handling

| Scenario | Handling |
|----------|----------|
| Delicut token expired (401) | Abort current flow, clear conversation state, send "Session expired. Use /start to re-authenticate." |
| Invalid OTP | "Invalid OTP, try again" — 3 retries max |
| No active subscription | "No active Delicut subscription found" |
| All dishes filtered out | Relax filters, warn: "Only N dishes remain" |
| OpenAI failure | Fallback to rating-based sort |
| Cutoff passed mid-flow | Re-check before submit, inform which days locked |
| Delicut API down | Retry once, then "Delicut unavailable, try later" |
| Unexpected user input | Ignore if in no flow; show current flow prompt if in flow |
| Bot restart mid-flow | Conversation state lost; user re-triggers command. Existing `pending_selections` with `Proposed` status are cleared on next `/select` run for that user |

## NuGet Packages

```
Telegram.Bot                           — Telegram Bot API client
Microsoft.Extensions.Hosting           — DI, hosted services
Microsoft.Extensions.Http              — IHttpClientFactory
OpenAI                                 — OpenAI API client
Microsoft.EntityFrameworkCore          — ORM
Npgsql.EntityFrameworkCore.PostgreSQL   — EF Core PostgreSQL provider
Microsoft.EntityFrameworkCore.Design    — Migrations tooling
Microsoft.Extensions.Options           — Configuration binding
```

## Configuration

```json
{
  "Telegram": {
    "BotToken": "<env>"
  },
  "Supabase": {
    "ConnectionString": "<env>"
  },
  "OpenAi": {
    "ApiKey": "<env>",
    "Model": "gpt-4o-mini"
  },
  "Delicut": {
    "BaseUrl": "https://apis.delicut.ae/api"
  }
}
```

All secrets via environment variables. `.env` files excluded from git.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Project structure | Single project, folders | Fast to build, promotion path to multi-project |
| ORM | EF Core + migrations | Convenient migration management per user preference |
| AI model | gpt-4o-mini | Cheap, fast, sufficient for structured selection |
| Conversation state | In-memory | Simple, no critical state loss on restart |
| Cutoff timezone | UTC+4 fixed | Delicut is Dubai-based |
| Interaction style | Reactive + Wednesday reminder | User-triggered selection, one proactive notification |
| Settings UI | Inline keyboards | Standard Telegram UX, no typing errors |
| Fallback | Rating-based sort | Graceful degradation without AI |
