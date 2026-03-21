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
        var payload = new { email };

        var response = await client.PostAsJsonAsync($"{_baseUrl}/v3/customer/sign-up?", payload);
        await EnsureSuccess(response);

        var result = await response.Content.ReadFromJsonAsync<DelicutApiResponse<OtpResponse>>(JsonOptions);
        return result?.Data ?? throw new InvalidOperationException("Empty OTP response from Delicut");
    }

    public async Task<LoginResponse> VerifyOtpAsync(string email, string otp)
    {
        // Use HttpClientHandler to capture cookies
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler);
        ApplyDefaultHeaders(client);

        var payload = new { email, otp };
        var response = await client.PostAsJsonAsync($"{_baseUrl}/v3/customer/sign-up?", payload);
        await EnsureSuccess(response);

        // Token comes from Set-Cookie header
        var cookies = handler.CookieContainer.GetCookies(new Uri(_baseUrl));
        var token = cookies["token"]?.Value;

        // Also check Authorization header in response or response body
        if (string.IsNullOrEmpty(token))
        {
            // Try to get from response body — some APIs return it there too
            var body = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("VerifyOtp response body: {Body}", body);

            // Try parsing as JSON to find a token field
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("token", out var tokenProp))
                    token = tokenProp.GetString();
                else if (data.TryGetProperty("accessToken", out var accessTokenProp))
                    token = accessTokenProp.GetString();
            }
        }

        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No token received from Delicut login");

        // Extract customer_id from JWT payload or response
        var customerId = ExtractCustomerIdFromResponse(
            await response.Content.ReadAsStringAsync(), token);

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

    public Task<WeekDeliverySchedule> GetDeliveryScheduleAsync(string token, string subscriptionId)
    {
        // TODO: Reverse-engineer the delivery schedule endpoint.
        // For now, this needs to be discovered from the Delicut website.
        throw new NotImplementedException(
            "Delivery schedule endpoint not yet reverse-engineered. " +
            "Check Network tab on delicut.ae when viewing the weekly meal plan.");
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
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("origin", "https://delicut.ae");
        client.DefaultRequestHeaders.TryAddWithoutValidation("referer", "https://delicut.ae/");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");
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

    private string ExtractCustomerIdFromResponse(string responseBody, string token)
    {
        try
        {
            // Try to get from response body
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("customer_id", out var cid))
                    return cid.GetString() ?? string.Empty;
                if (data.TryGetProperty("_id", out var id))
                    return id.GetString() ?? string.Empty;
            }
        }
        catch { /* fall through */ }

        try
        {
            // Decode JWT to get customer info (JWT is base64url)
            var parts = token.Split('.');
            if (parts.Length == 3)
            {
                var payload = parts[1];
                // Pad base64
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("customer_id", out var cid))
                    return cid.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("_id", out var id))
                    return id.GetString() ?? string.Empty;
            }
        }
        catch { /* fall through */ }

        _logger.LogWarning("Could not extract customer_id from login response or JWT");
        return string.Empty;
    }
}
