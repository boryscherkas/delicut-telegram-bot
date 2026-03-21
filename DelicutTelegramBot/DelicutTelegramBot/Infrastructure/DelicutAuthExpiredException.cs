namespace DelicutTelegramBot.Infrastructure;

public class DelicutAuthExpiredException : Exception
{
    public DelicutAuthExpiredException() : base("Delicut authentication has expired. Please re-authenticate.") { }
    public DelicutAuthExpiredException(string message) : base(message) { }
    public DelicutAuthExpiredException(string message, Exception inner) : base(message, inner) { }
}
