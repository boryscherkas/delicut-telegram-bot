using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using DelicutTelegramBot.Extensions;
using DelicutTelegramBot.Handlers;
using DelicutTelegramBot.Infrastructure;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);

// Always load user secrets (not just in Development)
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

// Support flat env var names for Fly.io secrets (fly secrets set TELEGRAM_BOT_TOKEN=xxx)
var flatMappings = new Dictionary<string, string>
{
    ["TELEGRAM_BOT_TOKEN"] = "Telegram:BotToken",
    ["SUPABASE_CONNECTION_STRING"] = "Supabase:ConnectionString",
    ["OPENAI_API_KEY"] = "OpenAi:ApiKey",
    ["OPENAI_MODEL"] = "OpenAi:Model",
    ["DELICUT_BASE_URL"] = "Delicut:BaseUrl",
};
foreach (var (envVar, configKey) in flatMappings)
{
    var value = Environment.GetEnvironmentVariable(envVar);
    if (!string.IsNullOrEmpty(value))
        builder.Configuration[configKey] = value;
}

builder.Services.AddDelicutBot(builder.Configuration);

var host = builder.Build();

// Apply pending migrations (skip if no connection string configured yet)
var connectionString = builder.Configuration["Supabase:ConnectionString"];
if (!string.IsNullOrEmpty(connectionString))
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// Set bot commands menu
var bot = host.Services.GetRequiredService<ITelegramBotClient>();
await bot.SetMyCommands([
    new Telegram.Bot.Types.BotCommand { Command = "select", Description = "Select meals for the week" },
    new Telegram.Bot.Types.BotCommand { Command = "settings", Description = "Configure preferences" },
    new Telegram.Bot.Types.BotCommand { Command = "start", Description = "Authenticate with Delicut" },
    new Telegram.Bot.Types.BotCommand { Command = "cancel", Description = "Cancel current action" },
]);
using var cts = new CancellationTokenSource();

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = [] // receive all update types
};

bot.StartReceiving(
    updateHandler: async (client, update, ct) =>
    {
        using var scope = host.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<BotHandler>();
        await handler.HandleUpdateAsync(update, ct);
    },
    errorHandler: (client, ex, ct) =>
    {
        Console.Error.WriteLine($"Telegram error: {ex.Message}");
        return Task.CompletedTask;
    },
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token);

Console.WriteLine("Bot started. Press Ctrl+C to stop.");
await host.RunAsync();
