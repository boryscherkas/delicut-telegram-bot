namespace DelicutTelegramBot.Helpers;

public static class CutoffHelper
{
    private static readonly TimeSpan Utc4 = TimeSpan.FromHours(4);

    public static bool IsLocked(DateOnly targetDate, DateTimeOffset? nowOverride = null)
    {
        var cutoff = new DateTimeOffset(
            targetDate.ToDateTime(new TimeOnly(12, 0)).AddDays(-2), Utc4);
        var now = nowOverride ?? DateTimeOffset.UtcNow.ToOffset(Utc4);
        return now >= cutoff;
    }
}
