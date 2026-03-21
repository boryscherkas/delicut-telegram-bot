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
