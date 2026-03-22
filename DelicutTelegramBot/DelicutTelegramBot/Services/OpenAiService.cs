using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public class OpenAiService : IOpenAiService
{
    private readonly ChatClient? _client;
    private readonly ILogger<OpenAiService> _logger;

    public OpenAiService(IOptions<OpenAiOptions> options, ILogger<OpenAiService> logger)
    {
        _logger = logger;
        if (!string.IsNullOrEmpty(options.Value.ApiKey))
            _client = new ChatClient(options.Value.Model, options.Value.ApiKey);
        else
            _logger.LogWarning("OpenAI API key not configured — AI selection disabled");
    }

    public async Task<AiSelectionResult?> SelectDishesAsync(AiSelectionRequest request)
    {
        if (_client is null) return null;

        try
        {
            var systemPrompt = BuildSystemPrompt(request);

            // Build a compact user message — only essential fields, sorted for clarity
            var compactRequest = new
            {
                macro_goals = new
                {
                    protein_g = request.ProteinGoalGrams,
                    carb_g = request.CarbGoalGrams,
                    fat_g = request.FatGoalGrams,
                    priority = request.MacroPriority
                },
                preferred_protein = request.PreferredProteinVariant,
                favourites = request.FavouriteDishNames.Count > 0 ? new { dishes = request.FavouriteDishNames, min_per_week = request.MinFavouritesPerWeek } : null,
                previous_choices = request.PreviousChoices.Count > 0 ? request.PreviousChoices : null,
                week = request.WeekMenu?.Select(day => new
                {
                    date = day.Date,
                    day = day.DayOfWeek,
                    meals_needed = day.MealsNeeded,
                    dishes = day.AvailableDishes
                        .OrderByDescending(d => d.Carb) // Sort by carb desc so AI sees high-carb options first
                        .Select(d => new
                        {
                            id = d.Id,
                            name = d.Name,
                            protein_opt = d.ProteinOption,
                            kcal = (int)d.Kcal,
                            p = (int)d.Protein,
                            c = (int)d.Carb,
                            f = (int)d.Fat,
                            cuisine = d.Cuisine
                        })
                })
            };
            var userMessage = JsonSerializer.Serialize(compactRequest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            var chatOptions = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    "dish_selection",
                    BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "picks": {
                                "type": "array",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "date": { "type": "string" },
                                        "dish_id": { "type": "string" },
                                        "protein_option": { "type": "string" },
                                        "meal_category": { "type": "string" },
                                        "slot_index": { "type": "integer" },
                                        "reasoning": { "type": "string" }
                                    },
                                    "required": ["date", "dish_id", "protein_option", "meal_category", "slot_index", "reasoning"],
                                    "additionalProperties": false
                                }
                            }
                        },
                        "required": ["picks"],
                        "additionalProperties": false
                    }
                    """),
                    jsonSchemaIsStrict: true)
            };

            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(systemPrompt),
                ChatMessage.CreateUserMessage(userMessage)
            };

            var response = await _client.CompleteChatAsync(messages, chatOptions);
            var json = response.Value.Content[0].Text;

            var result = JsonSerializer.Deserialize<AiSelectionResult>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            _logger.LogInformation("OpenAI response: {PickCount} picks, usage: {Tokens} tokens",
                result?.Picks.Count ?? 0,
                response.Value.Usage?.TotalTokenCount ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI dish selection failed");
            return null;
        }
    }

    private static string BuildSystemPrompt(AiSelectionRequest request)
    {
        var hasMacroGoals = request.ProteinGoalGrams.HasValue || request.CarbGoalGrams.HasValue || request.FatGoalGrams.HasValue;

        // When macro goals are set, they OVERRIDE the strategy
        var strategyDesc = hasMacroGoals
            ? "Use the DAILY MACRO GOALS below (they override any other strategy)."
            : request.Strategy switch
            {
                SelectionStrategy.LowestCal => "MINIMIZE total calories while maintaining variety.",
                SelectionStrategy.MacrosMax => "MAXIMIZE protein content while keeping reasonable variety.",
                _ => "Balanced selection considering taste, variety, and nutrition."
            };

        var historyNote = request.PreferHistory
            ? "The user prefers dishes they've had before — boost dishes from the previous_choices list. It's OK to repeat a favourite across days if it has great macros."
            : "No history preference — treat all dishes equally.";

        // Macro goals section
        var macroGoals = "";
        if (hasMacroGoals)
        {
            var priority = request.MacroPriority;
            var goalMap = new Dictionary<string, string>
            {
                ["p"] = request.ProteinGoalGrams.HasValue ? $"Protein: {request.ProteinGoalGrams}g" : "",
                ["c"] = request.CarbGoalGrams.HasValue ? $"Carbs: {request.CarbGoalGrams}g" : "",
                ["f"] = request.FatGoalGrams.HasValue ? $"Fat: {request.FatGoalGrams}g" : ""
            };

            var goals = new List<string>();
            for (int i = 0; i < priority.Count; i++)
            {
                var key = priority[i];
                if (goalMap.TryGetValue(key, out var label) && label.Length > 0)
                    goals.Add($"{label} (priority #{i + 1})");
            }

            var nameMap = new Dictionary<string, string> { ["p"] = "protein", ["c"] = "carbs", ["f"] = "fat" };
            var first = nameMap.GetValueOrDefault(priority.FirstOrDefault() ?? "p", "protein");
            var second = nameMap.GetValueOrDefault(priority.ElementAtOrDefault(1) ?? "c", "carbs");

            macroGoals = $"""

            DAILY MACRO GOALS — MINIMUMS for the WHOLE DAY (sum of all meals):
            {string.Join("\n", goals)}

            HOW TO SELECT:
            1. For each day, look at the "c" (carbs), "p" (protein), "f" (fat) values of each dish.
            2. Add up the macros of your selected dishes. The total MUST try to reach each goal.
            3. Dishes are sorted by carb content (highest first) in the input — look at the top dishes for carb-heavy options.
            4. Priority order: {first} > {second} > remaining.
               But if {first} goal is nearly met and {second} is far behind, prioritize {second}.
            5. Meeting or exceeding a goal is good — never penalize going over.
            6. ALWAYS verify: add up the "c" values of your 3 picks. Is the sum >= {request.CarbGoalGrams ?? 0}? If not, swap the lowest-carb pick for a higher-carb alternative.
            """;
        }

        // Protein variant preference
        var proteinNote = "";
        if (!string.IsNullOrEmpty(request.PreferredProteinVariant))
        {
            proteinNote = $"""

            PROTEIN VARIANT: The user prefers "{request.PreferredProteinVariant}".
            When a dish is available with this protein option, ALWAYS select that variant.
            The available_dishes already reflect this preference, so just pick the best dishes.
            """;
        }

        // Favourite dishes
        var favouritesNote = "";
        if (request.FavouriteDishNames.Count > 0 && request.MinFavouritesPerWeek > 0)
        {
            favouritesNote = $"""

            FAVOURITE DISHES (must appear at least {request.MinFavouritesPerWeek}x per week if on menu):
            {string.Join(", ", request.FavouriteDishNames)}
            Check week_context to see if favourites are already selected on other days.
            If a favourite hasn't reached its minimum count yet, prioritize it over other dishes.
            """;
        }

        // Variety section
        var varietyNote = """

            VARIETY RULES (STRICT):
            - NEVER put the same dish on more than 2 days in the week.
            - NEVER put the same dish on consecutive days (Mon+Tue = BAD).
            - Each day should have a UNIQUE set of dishes — no two days should be identical.
            - Vary cuisines: don't pick 3 dishes of the same cuisine on one day.
            - Exception ONLY for user favourites that need minimum weekly appearances.
            """;

        return $"""
            You are a meal selection assistant for a meal delivery service.
            You will receive a WEEK of menus — select the best dishes for ALL days at once.

            The input has a "week_menu" array with each day's available dishes and how many meals are needed.
            You must return picks for EVERY day. Each pick must include the "date" field (YYYY-MM-DD).

            Strategy: {strategyDesc}
            {macroGoals}
            {proteinNote}
            {favouritesNote}
            Rules:
            1. Do NOT use Delicut API ratings — ignore avg_rating and total_ratings fields.
            2. {historyNote}
            3. For each day, select exactly "meals_needed" dishes from that day's available_dishes.
            4. Each pick MUST include "date" matching the day it's for.
            {varietyNote}

            CRITICAL RULES:
            - Do NOT repeat the same dish on every day. A dish should appear at most 2-3 times per week.
            - Each day MUST have a DIFFERENT combination of dishes from the other days.
            - Prioritize hitting the carb goal (c) — pick dishes with the highest "c" values.
            - After picking, verify: sum of "c" for each day should be as close to {request.CarbGoalGrams ?? 0} as possible.

            Think holistically about the WHOLE WEEK:
            - Balance macros across all days, not just per-day.
            - Maximize variety across the week — use different dishes each day.
            - If one day can't hit the macro target, compensate on other days.

            Respond ONLY with valid JSON matching the required schema.
            """;
    }
}
