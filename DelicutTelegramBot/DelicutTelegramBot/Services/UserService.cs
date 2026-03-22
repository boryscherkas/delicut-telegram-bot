using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Services;

public class UserService : IUserService
{
    private readonly AppDbContext _db;
    private readonly ILogger<UserService> _logger;

    public UserService(AppDbContext db, ILogger<UserService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<User?> GetByTelegramIdAsync(long telegramUserId)
    {
        return await _db.Users
            .Include(u => u.Settings)
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);
    }

    public async Task<User> CreateOrUpdateAsync(long telegramUserId, long chatId,
        string email, string token, string customerId, string? subscriptionId = null)
    {
        var user = await _db.Users
            .Include(u => u.Settings)
            .FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                TelegramUserId = telegramUserId,
                TelegramChatId = chatId,
                DelicutEmail = email,
                DelicutToken = token,
                DelicutCustomerId = customerId,
                DelicutSubscriptionId = subscriptionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Settings = new UserSettings
                {
                    Id = Guid.NewGuid(),
                    Strategy = SelectionStrategy.Default,
                    StopWords = [],
                    PreferHistory = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }
            };
            _db.Users.Add(user);
        }
        else
        {
            user.TelegramChatId = chatId;
            user.DelicutEmail = email;
            user.DelicutToken = token;
            user.DelicutCustomerId = customerId;
            if (subscriptionId != null) user.DelicutSubscriptionId = subscriptionId;
            user.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateSettingsAsync(Guid userId, Action<UserSettings> update)
    {
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        if (settings is null)
        {
            _logger.LogWarning("No UserSettings found for user {UserId} — update silently skipped", userId);
            return;
        }
        update(settings);
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task UpdateTokenAsync(Guid userId, string token)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return;
        user.DelicutToken = token;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }
}
