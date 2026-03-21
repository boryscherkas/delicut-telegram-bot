using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.Services;

namespace DelicutTelegramBot.Infrastructure;

public class DelicutApiService : IDelicutApiService
{
    public Task<OtpResponse> RequestOtpAsync(string email)
        => throw new NotImplementedException("Delicut API not yet implemented");

    public Task<LoginResponse> VerifyOtpAsync(string email, string otp)
        => throw new NotImplementedException("Delicut API not yet implemented");

    public Task<Subscription> GetSubscriptionDetailsAsync(string token)
        => throw new NotImplementedException("Delicut API not yet implemented");

    public Task<List<Dish>> FetchMenuAsync(string token, string deliveryId, string mealCategory, string uniqueId)
        => throw new NotImplementedException("Delicut API not yet implemented");

    public Task<WeekDeliverySchedule> GetDeliveryScheduleAsync(string token, string subscriptionId)
        => throw new NotImplementedException("Delicut API not yet implemented");

    public Task SubmitDishSelectionAsync(string token, string deliveryId, string uniqueId, List<DishSubmission> selections)
        => throw new NotImplementedException("Delicut API not yet implemented");

    public Task<List<PastDishSelection>> GetPastSelectionsAsync(string token, string subscriptionId)
        => throw new NotImplementedException("Delicut API not yet implemented");
}
