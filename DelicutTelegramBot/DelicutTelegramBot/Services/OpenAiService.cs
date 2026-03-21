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
            var goals = new List<string>();
            if (request.ProteinGoalGrams.HasValue) goals.Add($"Protein: {request.ProteinGoalGrams}g (HIGHEST priority)");
            if (request.CarbGoalGrams.HasValue) goals.Add($"Carbs: {request.CarbGoalGrams}g (priority #2)");
            if (request.FatGoalGrams.HasValue) goals.Add($"Fat: {request.FatGoalGrams}g (priority #3)");

            macroGoals = $"""

            DAILY MACRO GOALS — these are MINIMUMS, not exact targets:
            {string.Join("\n", goals)}

            IMPORTANT: Goals are minimum thresholds. Meeting or EXCEEDING a goal is good — never penalize going over.
            Priority logic: First, reach the protein minimum across all meals for the day.
            Once protein minimum is met, optimize carbs toward its minimum.
            Once both are met, optimize fat. Do NOT sacrifice a higher-priority macro for a lower one.
            For example: if picking dish A gives 210g protein + 150g carbs vs dish B gives 180g protein + 200g carbs,
            pick A because reaching the protein minimum is higher priority.
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
