using Telegram.Bot;
using Telegram.Bot.Types;
using DelicutTelegramBot.Models.Domain;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;
using Microsoft.Extensions.Logging;

namespace DelicutTelegramBot.Handlers;

public class StartHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly IDelicutApiService _delicutApi;
    private readonly IUserService _userService;
    private readonly ConversationStateManager _stateManager;
    private readonly ILogger<StartHandler> _logger;

    public StartHandler(
        ITelegramBotClient bot,
        IDelicutApiService delicutApi,
        IUserService userService,
        ConversationStateManager stateManager,
        ILogger<StartHandler> logger)
    {
        _bot = bot;
        _delicutApi = delicutApi;
        _userService = userService;
        _stateManager = stateManager;
        _logger = logger;
    }

    public async Task HandleCommandAsync(Message message, CancellationToken ct)
    {
        // Check if user is already authenticated
        var existingUser = await _userService.GetByTelegramIdAsync(message.From!.Id);
        if (existingUser is not null && !string.IsNullOrEmpty(existingUser.DelicutToken))
        {
            await _bot.SendMessage(message.Chat.Id,
                $"Welcome back! You're connected as {existingUser.DelicutEmail}.\n" +
                "Use /select to pick meals, /settings to change preferences.\n\n" +
                "To re-authenticate with a different account, use /settings and tap Re-authenticate.",
                cancellationToken: ct);
            return;
        }

        // If we have the email but no token (e.g., token expired), skip to OTP
        if (existingUser is not null && !string.IsNullOrEmpty(existingUser.DelicutEmail))
        {
            var state = _stateManager.GetOrCreate(message.From.Id);
            state.FlowData["email"] = existingUser.DelicutEmail;

            try
            {
                await _delicutApi.RequestOtpAsync(existingUser.DelicutEmail);
                state.CurrentFlow = ConversationFlow.Auth_WaitingOtp;
                state.FlowData["otp_attempts"] = 0;
                state.LastActivity = DateTime.UtcNow;

                await _bot.SendMessage(message.Chat.Id,
                    $"Welcome back! OTP sent to {existingUser.DelicutEmail}. Enter the code:",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request OTP for returning user");
                state.CurrentFlow = ConversationFlow.Auth_WaitingEmail;
                state.LastActivity = DateTime.UtcNow;
                await _bot.SendMessage(message.Chat.Id,
                    "Welcome to Delicut Bot! Please enter your Delicut email:",
                    cancellationToken: ct);
            }
            return;
        }

        // New user — ask for email
        var newState = _stateManager.GetOrCreate(message.From.Id);
        newState.CurrentFlow = ConversationFlow.Auth_WaitingEmail;
        newState.LastActivity = DateTime.UtcNow;

        await _bot.SendMessage(message.Chat.Id,
            "Welcome to Delicut Bot! Please enter your Delicut email:",
            cancellationToken: ct);
    }

    public async Task HandleTextAsync(Message message, CancellationToken ct)
    {
        var userId = message.From!.Id;
        var state = _stateManager.GetOrCreate(userId);

        if (state.CurrentFlow == ConversationFlow.Auth_WaitingEmail)
        {
            var email = message.Text?.Trim() ?? "";
            if (!email.Contains('@'))
            {
                await _bot.SendMessage(message.Chat.Id, "Please enter a valid email address:", cancellationToken: ct);
                return;
            }

            try
            {
                await _delicutApi.RequestOtpAsync(email);
                state.FlowData["email"] = email;
                state.FlowData["otp_attempts"] = 0;
                state.CurrentFlow = ConversationFlow.Auth_WaitingOtp;
                state.LastActivity = DateTime.UtcNow;

                await _bot.SendMessage(message.Chat.Id,
                    "OTP sent to your email. Enter the code:",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request OTP for {Email}", email);
                await _bot.SendMessage(message.Chat.Id,
                    "Failed to send OTP. Please try again with /start.",
                    cancellationToken: ct);
                _stateManager.Reset(userId);
            }
        }
        else if (state.CurrentFlow == ConversationFlow.Auth_WaitingOtp)
        {
            var otp = message.Text?.Trim() ?? "";
            var email = state.FlowData["email"] as string ?? "";
            var attempts = state.FlowData.TryGetValue("otp_attempts", out var attObj) && attObj is int att ? att : 0;

            try
            {
                var loginResponse = await _delicutApi.VerifyOtpAsync(email, otp);

                // Fetch subscription to get customer info
                var subscription = await _delicutApi.GetSubscriptionDetailsAsync(loginResponse.Token);

                var user = await _userService.CreateOrUpdateAsync(
                    userId, message.Chat.Id, email,
                    loginResponse.Token, loginResponse.CustomerId);

                user.DelicutSubscriptionId = subscription.Id;

                _stateManager.Reset(userId);

                var mealCount = subscription.MealTypes.Sum(mt => mt.Qty);
                await _bot.SendMessage(message.Chat.Id,
                    $"Connected! Your plan: {mealCount} meals/day, {subscription.MealTypes.FirstOrDefault()?.KcalRange ?? "Standard"}.\n" +
                    "Use /settings to configure preferences, /select to pick meals.",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                attempts++;
                state.FlowData["otp_attempts"] = attempts;

                if (attempts >= 3)
                {
                    await _bot.SendMessage(message.Chat.Id,
                        "Too many failed attempts. Please try again with /start.",
                        cancellationToken: ct);
                    _stateManager.Reset(userId);
                }
                else
                {
                    _logger.LogWarning(ex, "OTP verification failed for {Email}, attempt {Attempt}", email, attempts);
                    await _bot.SendMessage(message.Chat.Id,
                        $"Invalid OTP. Please try again ({3 - attempts} attempts remaining):",
                        cancellationToken: ct);
                }
            }
        }
    }
}
