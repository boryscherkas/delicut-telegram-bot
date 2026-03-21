using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public class OpenAiService : IOpenAiService
{
    private readonly ChatClient _client;
    private readonly ILogger<OpenAiService> _logger;

    public OpenAiService(IOptions<OpenAiOptions> options, ILogger<OpenAiService> logger)
    {
        _client = new ChatClient(options.Value.Model, options.Value.ApiKey);
        _logger = logger;
    }

    public async Task<AiSelectionResult?> SelectDishesAsync(AiSelectionRequest request)
    {
        try
        {
            var systemPrompt = BuildSystemPrompt(request);
            var userMessage = JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
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
                                        "dish_id": { "type": "string" },
                                        "protein_option": { "type": "string" },
                                        "meal_category": { "type": "string" },
                                        "slot_index": { "type": "integer" },
                                        "reasoning": { "type": "string" }
                                    },
                                    "required": ["dish_id", "protein_option", "meal_category", "slot_index", "reasoning"],
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
        var strategyDesc = request.Strategy switch
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
        if (request.ProteinGoalGrams.HasValue || request.CarbGoalGrams.HasValue || request.FatGoalGrams.HasValue)
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

            DAILY MACRO GOALS — these are MINIMUMS, not exact targets:
            {string.Join("\n", goals)}

            IMPORTANT: Goals are minimum thresholds. Meeting or EXCEEDING is good — never penalize going over.

            BALANCED PRIORITY LOGIC:
            - Priority order matters: {first} > {second} > remaining.
            - BUT: if a higher-priority macro is already CLOSE to goal (within ~5%),
              shift focus to whichever macro has the BIGGEST gap.
            - Example: if {first} is at 95% of goal but {second} is at 60%,
              prioritize {second} — don't chase the last 5% of {first} while {second} suffers.
            - The goal is to get ALL macros as close to their minimums as possible,
              with priority as a tiebreaker when macros are similarly deficient.
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

            VARIETY RULES:
            - STRONGLY avoid selecting the same dish on consecutive days (e.g., Mon and Tue).
            - Avoid same dish more than twice per week (unless it's a favourite with minimum count).
            - Vary cuisines across consecutive days.
            - Exception: if a dish is a user favourite AND hasn't met its weekly minimum,
              it's OK to repeat it on non-consecutive days.
            """;

        return $"""
            You are a meal selection assistant for a meal delivery service.
            Select the best dishes for each meal slot.

            Strategy: {strategyDesc}
            {macroGoals}
            {proteinNote}
            {favouritesNote}
            Rules:
            1. Do NOT use Delicut API ratings — ignore avg_rating and total_ratings fields.
            2. {historyNote}
            3. Fill each meal slot with exactly the requested count
            {varietyNote}

            Respond ONLY with valid JSON matching the required schema.
            """;
    }
}
