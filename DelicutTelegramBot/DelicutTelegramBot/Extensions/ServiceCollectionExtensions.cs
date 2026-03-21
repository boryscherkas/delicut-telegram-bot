using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
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
        services.AddScoped<IMenuSelectionService, MenuSelectionService>();
        services.AddScoped<IDishFilterService, DishFilterService>();
        services.AddScoped<IFallbackSelectionService, FallbackSelectionService>();
        services.AddScoped<ISelectionHistoryService, SelectionHistoryService>();

        // HTTP
        services.AddHttpClient();

        return services;
    }
}
