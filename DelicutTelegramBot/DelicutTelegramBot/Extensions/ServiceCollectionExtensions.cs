using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using DelicutTelegramBot.BackgroundServices;
using DelicutTelegramBot.Handlers;
using DelicutTelegramBot.Infrastructure;
using DelicutTelegramBot.Services;
using DelicutTelegramBot.State;

namespace DelicutTelegramBot.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDelicutBot(this IServiceCollection services, IConfiguration configuration)
    {
        // Database
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration["Supabase:ConnectionString"]));

        // Telegram Bot
        services.AddSingleton<ITelegramBotClient>(sp =>
            new TelegramBotClient(configuration["Telegram:BotToken"]!));

        // State
        services.AddSingleton<ConversationStateManager>();

        // Services
        services.Configure<OpenAiOptions>(configuration.GetSection("OpenAi"));
        services.AddScoped<IDelicutApiService, DelicutApiService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IOpenAiService, OpenAiService>();
        services.AddScoped<IMenuFetchService, MenuFetchService>();
        services.AddScoped<IMenuSelectionService, MenuSelectionService>();
        services.AddScoped<IDishFilterService, DishFilterService>();
        services.AddScoped<IFallbackSelectionService, FallbackSelectionService>();
        services.AddScoped<ISelectionHistoryService, SelectionHistoryService>();

        // Handlers
        services.AddScoped<BotHandler>();
        services.AddScoped<StartHandler>();
        services.AddScoped<SettingsHandler>();
        services.AddScoped<SelectWeekHandler>();
        services.AddScoped<ChangeDishHandler>();
        services.AddScoped<MenuHandler>();
        services.AddScoped<CancelHandler>();

        // Background Services
        services.AddHostedService<WednesdayReminderService>();

        // HTTP
        services.AddHttpClient();

        return services;
    }
}
