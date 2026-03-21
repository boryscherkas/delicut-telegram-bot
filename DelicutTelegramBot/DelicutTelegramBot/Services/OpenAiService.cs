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

            DAILY MACRO GOALS (in priority order):
            {string.Join("\n", goals)}

            Priority logic: First, get as close to the protein goal as possible across all meals for the day.
            Once protein is near the goal, optimize carbs to hit the carb target.
            Once both are met, optimize fat. Do NOT sacrifice a higher-priority macro for a lower one.
            For example: if picking dish A gives 190g protein + 150g carbs vs dish B gives 170g protein + 200g carbs,
            pick A because protein goal is higher priority.
            """;
        }

        // Variety section
        var varietyNote = """

            VARIETY RULES:
            - STRONGLY avoid selecting the same dish on consecutive days (e.g., Mon and Tue).
            - Avoid same dish more than twice per week.
            - Vary cuisines across consecutive days.
            - Exception: if a dish is in user's previous_choices AND has excellent macros for their goals,
              it's OK to repeat it on non-consecutive days (e.g., Mon and Wed).
            """;

        return $"""
            You are a meal selection assistant for a meal delivery service.
            Select the best dishes for each meal slot.

            Strategy: {strategyDesc}
            {macroGoals}
            Rules:
            1. Rating: prefer dishes with higher avg_rating (weighted by total_ratings)
            2. {historyNote}
            3. Fill each meal slot with exactly the requested count
            {varietyNote}

            Respond ONLY with valid JSON matching the required schema.
            """;
    }
}
