using DelicutTelegramBot.Helpers;

namespace DelicutTelegramBot.Tests.Helpers;

public class CutoffHelperTests
{
    [Fact]
    public void Wednesday_IsLocked_AfterMonday1200_ReturnsTrue()
    {
        var target = new DateOnly(2026, 3, 26);
        var now = new DateTimeOffset(2026, 3, 24, 12, 1, 0, TimeSpan.FromHours(4));
        Assert.True(CutoffHelper.IsLocked(target, now));
    }

    [Fact]
    public void Wednesday_IsLocked_BeforeMonday1200_ReturnsFalse()
    {
        var target = new DateOnly(2026, 3, 26);
        var now = new DateTimeOffset(2026, 3, 24, 11, 59, 0, TimeSpan.FromHours(4));
        Assert.False(CutoffHelper.IsLocked(target, now));
    }

    [Fact]
    public void Monday_IsLocked_AfterSaturday1200_ReturnsTrue()
    {
        var target = new DateOnly(2026, 3, 23);
        var now = new DateTimeOffset(2026, 3, 21, 12, 0, 0, TimeSpan.FromHours(4));
        Assert.True(CutoffHelper.IsLocked(target, now));
    }

    [Fact]
    public void Today_IsAlwaysLocked()
    {
        var target = new DateOnly(2026, 3, 21);
        var now = new DateTimeOffset(2026, 3, 21, 8, 0, 0, TimeSpan.FromHours(4));
        Assert.True(CutoffHelper.IsLocked(target, now));
    }
}
