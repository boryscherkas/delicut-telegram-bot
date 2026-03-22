namespace DelicutTelegramBot.Helpers;

public static class TelegramFormatHelper
{
    public static string FormatDiff(double val) =>
        val >= 0 ? $"+{val:F0}" : $"{val:F0}";

    public static List<string> SplitMessage(string text, int maxLen)
    {
        var chunks = new List<string>();
        var lines = text.Split('\n');
        var current = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length + line.Length + 1 > maxLen && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.AppendLine();
            current.Append(line);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }
}
