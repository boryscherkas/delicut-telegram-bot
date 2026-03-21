using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.State;

namespace DelicutTelegramBot.Tests.State;

public class ConversationStateManagerTests
{
    [Fact]
    public void GetOrCreate_NewUser_ReturnsNoneState()
    {
        var manager = new ConversationStateManager();
        var state = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.None, state.CurrentFlow);
    }

    [Fact]
    public void GetOrCreate_ExistingUser_ReturnsSameState()
    {
        var manager = new ConversationStateManager();
        var state1 = manager.GetOrCreate(12345);
        state1.CurrentFlow = ConversationFlow.Auth_WaitingEmail;
        var state2 = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.Auth_WaitingEmail, state2.CurrentFlow);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var manager = new ConversationStateManager();
        var state = manager.GetOrCreate(12345);
        state.CurrentFlow = ConversationFlow.Auth_WaitingOtp;
        manager.Reset(12345);
        var newState = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.None, newState.CurrentFlow);
    }

    [Fact]
    public void CleanupStale_RemovesOldEntries()
    {
        var manager = new ConversationStateManager();
        var state = manager.GetOrCreate(12345);
        state.LastActivity = DateTime.UtcNow.AddMinutes(-60);
        manager.CleanupStale(TimeSpan.FromMinutes(30));
        var newState = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.None, newState.CurrentFlow);
    }

    [Fact]
    public void CleanupStale_KeepsRecentEntries()
    {
        var manager = new ConversationStateManager();
        var state = manager.GetOrCreate(12345);
        state.CurrentFlow = ConversationFlow.Auth_WaitingEmail;
        state.LastActivity = DateTime.UtcNow;
        manager.CleanupStale(TimeSpan.FromMinutes(30));
        var sameState = manager.GetOrCreate(12345);
        Assert.Equal(ConversationFlow.Auth_WaitingEmail, sameState.CurrentFlow);
    }
}
