using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Services;

public interface ISelectionHistoryService
{
    Task<List<string>> GetPreviousChoiceNamesAsync(Guid userId, int maxCount = 50);
    Task RecordSelectionsAsync(Guid userId, List<PendingSelection> selections, bool wasUserChoice);
}
