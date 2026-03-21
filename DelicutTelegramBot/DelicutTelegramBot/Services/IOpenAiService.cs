using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public interface IOpenAiService
{
    Task<AiSelectionResult?> SelectDishesAsync(AiSelectionRequest request);
}
