using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public interface IDelicutApiService
{
    Task<OtpResponse> RequestOtpAsync(string email);
    Task<LoginResponse> VerifyOtpAsync(string email, string otp);
    Task<Subscription> GetSubscriptionDetailsAsync(string token);
    Task<List<Dish>> FetchMenuAsync(string token, string deliveryId, string mealCategory, string uniqueId);
    Task<WeekDeliverySchedule> GetDeliveryScheduleAsync(string token, string subscriptionId);
    Task SubmitDishSelectionAsync(string token, string deliveryId, string uniqueId, List<DishSubmission> selections);
    Task<List<PastDishSelection>> GetPastSelectionsAsync(string token, string subscriptionId);
}
