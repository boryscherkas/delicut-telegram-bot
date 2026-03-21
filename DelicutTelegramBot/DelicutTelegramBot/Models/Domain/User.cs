namespace DelicutTelegramBot.Models.Domain;

public class User
{
    public Guid Id { get; set; }
    public long TelegramUserId { get; set; }
    public long TelegramChatId { get; set; }
    public string? DelicutEmail { get; set; }
    public string? DelicutToken { get; set; }
    public string? DelicutCustomerId { get; set; }
    public string? DelicutSubscriptionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public UserSettings? Settings { get; set; }
    public List<SelectionHistory> SelectionHistories { get; set; } = [];
    public List<PendingSelection> PendingSelections { get; set; } = [];
}
