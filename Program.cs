using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestApp.Configuration;
using TestApp.Data;
using TestApp.Services;
using TestApp.Workers;
using TL;
using WTelegram;

// Отключаем подробный MTProto-лог в консоль (Receiving Updates и т.д.).
WTelegram.Helpers.Log = (_, _) => { };

var builder = Host.CreateApplicationBuilder(args);

var telegramSettings = builder.Configuration
    .GetSection(TelegramAppSettings.SectionName)
    .Get<TelegramAppSettings>();

if (!IsTelegramConfigValid(telegramSettings))
{
    PrintTelegramConfigHelp();
    return 1;
}

builder.Services
    .AddOptions<TelegramAppSettings>()
    .Bind(builder.Configuration.GetSection(TelegramAppSettings.SectionName));

builder.Services
    .AddOptions<AiSettings>()
    .Bind(builder.Configuration.GetSection(AiSettings.SectionName))
    .Validate(ai => !string.IsNullOrWhiteSpace(ai.AiBaseUrl), "AiSettings.AiBaseUrl не должен быть пуст.")
    .ValidateOnStart();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAiTextService, AiTextService>();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<TelegramClientManager>();
builder.Services.AddSingleton<TelegramCommandProcessor>();
builder.Services.AddSingleton<TelegramUpdateRouter>();
builder.Services.AddHostedService<AdvertisingScheduler>();

using var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

await using var telegram = host.Services.GetRequiredService<TelegramClientManager>();

try
{
    var user = await telegram.LoginAsync();

    var updateRouter = host.Services.GetRequiredService<TelegramUpdateRouter>();
    updateRouter.Initialize();

    await host.StartAsync();

    Console.WriteLine();
    Console.WriteLine($"Сессия сохранена. Пользователь: {user.first_name} {user.last_name} (@{user.username ?? "—"}, id: {user.id})");
    Console.WriteLine("Команды в «Избранное»:");
    Console.WriteLine("  /add_chat {ID} {название} [кол-во] - добавить чат");
    Console.WriteLine("  /del_chat {ID} - удалить чат");
    Console.WriteLine("  /set_text {текст} - установить объявление");
    Console.WriteLine("  /status - статус рассылки");
    Console.WriteLine("  /logs - последние логи");
    Console.WriteLine("  /help - справка по командам");
    Console.WriteLine("Планировщик рассылки активен. Нажмите Enter для выхода…");
    Console.ReadLine();

    await host.StopAsync();
}
catch (TelegramFloodWaitException ex)
{
    Console.Error.WriteLine($"FLOOD_WAIT: подождите {ex.RetryAfterSeconds} с и запустите приложение снова.");
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
catch (RpcException ex)
{
    Console.Error.WriteLine($"Ошибка Telegram API [{ex.Code}]: {ex.Message}");
    Environment.ExitCode = 1;
}

return 0;

static bool IsTelegramConfigValid(TelegramAppSettings? settings) =>
    settings is { ApiId: > 0 } && !string.IsNullOrWhiteSpace(settings.ApiHash);

static void PrintTelegramConfigHelp()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Не заданы учётные данные Telegram API.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("1) Откройте https://my.telegram.org/apps и создайте приложение.");
    Console.Error.WriteLine("2) Укажите api_id и api_hash в appsettings.json:");
    Console.Error.WriteLine();
    Console.Error.WriteLine("   \"Telegram\": {");
    Console.Error.WriteLine("     \"ApiId\": 12345678,");
    Console.Error.WriteLine("     \"ApiHash\": \"ваш_api_hash\"");
    Console.Error.WriteLine("   }");
    Console.Error.WriteLine();
    Console.Error.WriteLine("   Либо через переменные окружения (PowerShell):");
    Console.Error.WriteLine("   $env:Telegram__ApiId = \"12345678\"");
    Console.Error.WriteLine("   $env:Telegram__ApiHash = \"ваш_api_hash\"");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"   Файл конфигурации: {Path.Combine(AppContext.BaseDirectory, "appsettings.json")}");
}
