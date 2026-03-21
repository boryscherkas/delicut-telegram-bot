using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Services;

public interface IUserService
{
    Task<User?> GetByTelegramIdAsync(long telegramUserId);
    Task<User> CreateOrUpdateAsync(long telegramUserId, long chatId, string email, string token, string customerId);
    Task UpdateSettingsAsync(Guid userId, Action<UserSettings> update);
    Task UpdateTokenAsync(Guid userId, string token);
}
