using System.Text.Json.Serialization;

namespace DelicutTelegramBot.Models.Delicut;

public class OtpResponse
{
    [JsonPropertyName("show_otp")]
    public bool ShowOtp { get; set; }

    [JsonPropertyName("user_register_flag")]
    public string UserRegisterFlag { get; set; } = string.Empty;
}

public class DelicutApiResponse<T>
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("status")]
    public bool Status { get; set; }
}
