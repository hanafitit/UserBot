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

WTelegram.Helpers.Log = (_, _) => { };

var builder = Host.CreateApplicationBuilder(args);

// Гарантируем подгрузку переменных окружения
builder.Configuration.AddEnvironmentVariables();

// Авто-маппинг "голых" переменных (без префикса) для удобства деплоя на Render
var fallbackConfigs = new Dictionary<string, string>
{
    { "ApiId",          "Telegram:ApiId" },
    { "ApiHash",        "Telegram:ApiHash" },
    { "PhoneNumber",    "Telegram:PhoneNumber" },
    { "Password2Fa",    "Telegram:Password2Fa" },
    { "SessionPath",    "Telegram:SessionPath" },
    { "NeuralApiToken", "Ai:AiApiKey" },
    { "AiModelName",    "Ai:AiModelName" },
    { "AiBaseUrl",      "Ai:AiBaseUrl" }
};

foreach (var fallback in fallbackConfigs)
{
    var envValue = Environment.GetEnvironmentVariable(fallback.Key);
    if (!string.IsNullOrWhiteSpace(envValue))
    {
        var currentValue = builder.Configuration[fallback.Value];
        if (string.IsNullOrWhiteSpace(currentValue) || currentValue == "0")
            builder.Configuration[fallback.Value] = envValue;
    }
}

var telegramSettings = new TelegramAppSettings();
builder.Configuration.GetSection(TelegramAppSettings.SectionName).Bind(telegramSettings);

var (isValid, errorMessage) = ValidateTelegramConfig(telegramSettings);
if (!isValid)
{
    PrintTelegramConfigHelp(telegramSettings, errorMessage);
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
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<TelegramClientManager>();
builder.Services.AddSingleton<TelegramCommandProcessor>();
builder.Services.AddSingleton<TelegramUpdateRouter>();
builder.Services.AddHostedService<AdvertisingScheduler>();

using var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();

    try
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        Console.WriteLine("Миграции БД применены успешно.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("❌ Ошибка при выполнении миграций БД:");
        Console.Error.WriteLine($"   {ex.Message}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Проверьте переменную ConnectionStrings__DefaultConnection.");
        throw;
    }
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
    Console.WriteLine("  /add_chat {ID/username} [название] [кол-во] - добавить чат");
    Console.WriteLine("  /del_chat {ID/username} - удалить чат");
    Console.WriteLine("  /set_text {текст} - установить объявление");
    Console.WriteLine("  /status - статус рассылки");
    Console.WriteLine("  /logs - последние логи");
    Console.WriteLine("  /help - справка по командам");

    // Минимальный HTTP-сервер для Render (требует открытый порт)
    var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
    var httpListener = new System.Net.HttpListener();
    httpListener.Prefixes.Add($"http://*:{port}/");
    httpListener.Start();
    _ = Task.Run(async () =>
    {
        while (httpListener.IsListening)
        {
            var ctx = await httpListener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync("OK"u8.ToArray());
            ctx.Response.Close();
        }
    });
    Console.WriteLine($"HTTP listener запущен на порту {port}");

    if (Console.IsInputRedirected)
    {
        Console.WriteLine("Планировщик рассылки активен. Ожидание сигнала завершения...");
        await host.WaitForShutdownAsync();
    }
    else
    {
        Console.WriteLine("Планировщик рассылки активен. Нажмите Enter для выхода…");
        await Task.WhenAny(host.WaitForShutdownAsync(), Task.Run(Console.ReadLine));
    }

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

static (bool IsValid, string? ErrorMessage) ValidateTelegramConfig(TelegramAppSettings? settings)
{
    if (settings == null) return (false, "Секция конфигурации 'Telegram' не найдена.");
    if (settings.ApiId <= 0) return (false, "Telegram:ApiId должен быть положительным числом.");
    if (string.IsNullOrWhiteSpace(settings.ApiHash)) return (false, "Telegram:ApiHash не может быть пустым.");
    return (true, null);
}

static void PrintTelegramConfigHelp(TelegramAppSettings? settings, string? error)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Ошибка конфигурации Telegram API:");
    Console.Error.WriteLine($"  => {error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  Telegram:ApiId   = {(settings?.ApiId != 0 ? settings?.ApiId : "(не задано)")}");
    Console.Error.WriteLine($"  Telegram:ApiHash = {SanitizeHash(settings?.ApiHash)}");
}

static string SanitizeHash(string? hash)
{
    if (string.IsNullOrWhiteSpace(hash)) return "(не задано)";
    if (hash.Length <= 4) return "****";
    return $"{hash[0]}...{hash[^1]} ({hash.Length} симв.)";
}
