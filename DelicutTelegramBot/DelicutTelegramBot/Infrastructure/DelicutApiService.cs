using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DelicutTelegramBot.Models.Delicut;
using DelicutTelegramBot.Models.Dto;
using DelicutTelegramBot.Services;

namespace DelicutTelegramBot.Infrastructure;

/// <summary>Raw delivery item from GET /v1/delivery/list</summary>
internal class DelicutDeliveryItem
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subscription_id")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("delivery_date")]
    public DateTime DeliveryDate { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("not_deliverable")]
    public bool NotDeliverable { get; set; }

    [JsonPropertyName("is_delivery_freezed")]
    public bool IsDeliveryFreezed { get; set; }
}

public class DelicutApiService : IDelicutApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly ILogger<DelicutApiService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DelicutApiService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<DelicutApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = configuration["Delicut:BaseUrl"] ?? "https://apis.delicut.ae/api";
        _logger = logger;
    }

    // --- AUTH ---

    public async Task<OtpResponse> RequestOtpAsync(string email)
    {
        using var client = CreateClient();
        var response = await PostJsonRawAsync(client, $"{_baseUrl}/v3/customer/sign-up?",
            JsonSerializer.Serialize(new { email }));
        await EnsureSuccess(response);

        var result = await response.Content.ReadFromJsonAsync<DelicutApiResponse<OtpResponse>>(JsonOptions);
        return result?.Data ?? throw new InvalidOperationException("Empty OTP response from Delicut");
    }

    public async Task<LoginResponse> VerifyOtpAsync(string email, string otp)
    {
        using var client = CreateClient();

        var response = await PostJsonRawAsync(client, $"{_baseUrl}/v3/customer/sign-up?",
            JsonSerializer.Serialize(new { email, otp }));
        await EnsureSuccess(response);

        // Token and customer details come from response body
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var data = doc.RootElement.GetProperty("data");

        // data.x_acess_token (note: "acess" is Delicut's typo, not ours)
        var token = data.GetProperty("x_acess_token").GetString()
            ?? throw new InvalidOperationException("No token in login response");

        // data.customerDetails._id
        var customerId = data.GetProperty("customerDetails").GetProperty("_id").GetString()
            ?? string.Empty;

        return new LoginResponse
        {
            Token = token,
            CustomerId = customerId
        };
    }

    // --- SUBSCRIPTION ---

    public async Task<Subscription> GetSubscriptionDetailsAsync(string token)
    {
        using var client = CreateClient(token);
        var response = await client.GetAsync($"{_baseUrl}/v2/subscription/get-details");
        await EnsureSuccess(response);

        var result = await response.Content.ReadFromJsonAsync<DelicutApiResponse<List<Subscription>>>(JsonOptions);

        // API returns an array — take the first active subscription
        var subscription = result?.Data?.FirstOrDefault()
            ?? throw new InvalidOperationException("No subscription found");

        return subscription;
    }

    // --- MENU ---

    public async Task<List<Dish>> FetchMenuAsync(string token, string deliveryId,
        string mealCategory, string uniqueId)
    {
        using var client = CreateClient(token);
        var url = $"{_baseUrl}/v2/recipes/fetch-all-live" +
                  $"?delivery_id={Uri.EscapeDataString(deliveryId)}" +
                  $"&meal_category={Uri.EscapeDataString(mealCategory)}" +
                  $"&unique_id={Uri.EscapeDataString(uniqueId)}";

        var response = await client.GetAsync(url);
        await EnsureSuccess(response);

        var result = await response.Content.ReadFromJsonAsync<DelicutApiResponse<List<Dish>>>(JsonOptions);
        return result?.Data ?? [];
    }

    // --- NOT YET REVERSE-ENGINEERED ---

    public async Task<WeekDeliverySchedule> GetDeliveryScheduleAsync(string token, string customerId)
    {
        using var client = CreateClient(token);

        var response = await PostJsonRawAsync(client, $"{_baseUrl}/v1/delivery/week-wise",
            JsonSerializer.Serialize(new { customer_id = customerId, week = "current_week" }));
        await EnsureSuccess(response);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var schedule = new WeekDeliverySchedule();

        if (doc.RootElement.TryGetProperty("data", out var dataArray) &&
            dataArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var dayElement in dataArray.EnumerateArray())
            {
                var deliveryId = dayElement.GetProperty("_id").GetString() ?? "";
                var deliveryDateStr = dayElement.GetProperty("delivery_date").GetString() ?? "";
                var status = dayElement.GetProperty("status").GetString() ?? "";
                var notDeliverable = dayElement.TryGetProperty("not_deliverable", out var nd) && nd.GetBoolean();
                var isFrozen = dayElement.TryGetProperty("is_delivery_freezed", out var fr) && fr.GetBoolean();

                if (status != "Pending" || notDeliverable || isFrozen)
                    continue;

                var deliveryDate = DateTime.Parse(deliveryDateStr);
                var date = DateOnly.FromDateTime(deliveryDate);
                var slots = new List<DeliverySlot>();
                var mealCategories = new HashSet<string>();

                if (dayElement.TryGetProperty("delivery_item", out var items) &&
                    items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var mealCategory = item.TryGetProperty("meal_category", out var mc)
                            ? mc.GetString() ?? "" : "";
                        var mealType = item.TryGetProperty("meal_type", out var mt)
                            ? mt.GetString() ?? "" : "";
                        var kcalRange = item.TryGetProperty("kcal_range", out var kr)
                            ? kr.GetString() ?? "" : "";
                        var proteinCategory = item.TryGetProperty("protein_category", out var pc)
                            ? pc.GetString() ?? "" : "";

                        mealCategories.Add(mealCategory);

                        // Extract unique_id and current dish from selected_meal
                        string uniqueId = "", currentDishId = "", currentDishName = "",
                               currentProteinOption = "";
                        double curKcal = 0, curProtein = 0, curCarb = 0, curFat = 0;
                        bool isAutoSelect = false;

                        if (item.TryGetProperty("selected_meal", out var meal))
                        {
                            uniqueId = meal.TryGetProperty("unique_id", out var uid)
                                ? uid.GetString() ?? "" : "";
                            currentDishId = meal.TryGetProperty("recipe_id", out var rid)
                                ? rid.GetString() ?? "" : "";
                            currentDishName = meal.TryGetProperty("dish_name", out var dn)
                                ? dn.GetString() ?? "" : "";
                            isAutoSelect = meal.TryGetProperty("is_auto_select", out var auto)
                                && auto.GetBoolean();

                            // Get protein option and macros from variants
                            if (meal.TryGetProperty("variants", out var variants))
                            {
                                if (variants.TryGetProperty("protein_option", out var po))
                                    currentProteinOption = po.GetString() ?? "";
                                if (variants.TryGetProperty("kcal", out var kcalProp))
                                    curKcal = kcalProp.GetDouble();
                                if (variants.TryGetProperty("protein", out var protProp))
                                    curProtein = protProp.GetDouble();
                                if (variants.TryGetProperty("carb", out var carbProp))
                                    curCarb = carbProp.GetDouble();
                                if (variants.TryGetProperty("fat", out var fatProp))
                                    curFat = fatProp.GetDouble();
                            }
                        }

                        slots.Add(new DeliverySlot
                        {
                            UniqueId = uniqueId,
                            MealCategory = mealCategory,
                            MealType = mealType,
                            KcalRange = kcalRange,
                            ProteinCategory = proteinCategory,
                            CurrentDishId = currentDishId,
                            CurrentDishName = currentDishName,
                            CurrentProteinOption = currentProteinOption,
                            CurrentKcal = curKcal,
                            CurrentProtein = curProtein,
                            CurrentCarb = curCarb,
                            CurrentFat = curFat,
                            IsAutoSelect = isAutoSelect
                        });
                    }
                }

                schedule.Days.Add(new DeliveryDay
                {
                    Date = date,
                    DayOfWeek = date.DayOfWeek.ToString(),
                    DeliveryId = deliveryId,
                    Slots = slots,
                    MealCategories = mealCategories.ToList(),
                    IsLocked = Helpers.CutoffHelper.IsLocked(date)
                });
            }
        }

        schedule.Days = schedule.Days.OrderBy(d => d.Date).ToList();
        return schedule;
    }

    public async Task SubmitDishSelectionAsync(string token, string customerId,
        string deliveryId, string uniqueId, List<DishSubmission> selections)
    {
        using var client = CreateClient(token);

        // API accepts one dish at a time via POST /v2/delivery/add-recipe
        foreach (var dish in selections)
        {
            var payload = new
            {
                type = dish.MealCategory.ToLower(),
                customer_id = customerId,
                delivery_id = deliveryId,
                recipe_id = dish.DishId,
                variant = dish.ProteinOption,
                size = dish.Size,
                unique_id = uniqueId,
                protein_category = dish.ProteinCategory
            };

            var response = await client.PostAsJsonAsync($"{_baseUrl}/v2/delivery/add-recipe", payload);
            await EnsureSuccess(response);

            _logger.LogInformation("Submitted dish {DishId} ({Variant}/{Size}) for delivery {DeliveryId}",
                dish.DishId, dish.ProteinOption, dish.Size, deliveryId);
        }
    }


    // --- HELPERS ---

    private HttpClient CreateClient(string? token = null)
    {
        var client = _httpClientFactory.CreateClient("Delicut");
        ApplyDefaultHeaders(client);
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.TryAddWithoutValidation("authorization", token);
        return client;
    }

    private static void ApplyDefaultHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("origin", "https://delicut.ae");
        client.DefaultRequestHeaders.TryAddWithoutValidation("referer", "https://delicut.ae/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"146\", \"Not-A.Brand\";v=\"24\", \"Google Chrome\";v=\"146\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"macOS\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "empty");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "cors");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "same-site");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// Posts JSON with exact Content-Type: application/json (no charset suffix).
    /// Some APIs reject the default application/json; charset=utf-8 from PostAsJsonAsync.
    /// </summary>
    private static async Task<HttpResponseMessage> PostJsonRawAsync(HttpClient client, string url, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await client.PostAsync(url, content);
    }

    private async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DelicutAuthExpiredException();

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("Delicut API error {StatusCode}: {Body}", response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }
    }

}
