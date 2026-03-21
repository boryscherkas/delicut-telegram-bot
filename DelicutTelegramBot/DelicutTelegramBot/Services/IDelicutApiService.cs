using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Dto;

namespace DelicutTelegramBot.Services;

public interface IDelicutApiService
{
    // Past selections tracked locally via ISelectionHistoryService — no Delicut API needed
    Task<OtpResponse> RequestOtpAsync(string email);
    Task<LoginResponse> VerifyOtpAsync(string email, string otp);
    Task<Subscription> GetSubscriptionDetailsAsync(string token);
    Task<List<Dish>> FetchMenuAsync(string token, string deliveryId, string mealCategory, string uniqueId);
    Task<WeekDeliverySchedule> GetDeliveryScheduleAsync(string token, string customerId);
    Task SubmitDishSelectionAsync(string token, string customerId, string deliveryId, string uniqueId, List<DishSubmission> selections);
}
