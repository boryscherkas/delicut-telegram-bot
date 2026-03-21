namespace DelicutTelegramBot.Models.Domain;

public enum SelectionStrategy
{
    Default,
    LowestCal,
    MacrosMax
}

public enum PendingSelectionStatus
{
    Proposed,
    Confirmed
}

public enum ConversationFlow
{
    None,
    Auth_WaitingEmail,
    Auth_WaitingOtp,
    Settings_WaitingStopWords,
    Select_ReviewingWeek,
    Select_PickingDay,
    Select_PickingDish,
    Select_PickingReplacement
}
