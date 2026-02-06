using Aloe.Apps.FileBridge.Components;
using Aloe.Apps.FileBridge.Components.Hubs;
using Aloe.Apps.FileBridgeLib.Models;
using Aloe.Apps.FileBridgeLib.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilogの設定
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .CreateLogger();

    // Serilogをロギングプロバイダーとして追加
    builder.Host.UseSerilog();

    Log.Information("アプリケーションを起動しています...");

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // SignalRの追加
    builder.Services.AddSignalR();

    // FileBridge設定の読み込み（後方互換性のため）
    var fileBridgeOptions = builder.Configuration.GetSection("FileBridge").Get<FileBridgeOptions>() ?? new FileBridgeOptions();
    builder.Services.Configure<FileBridgeOptions>(builder.Configuration.GetSection("FileBridge"));

    // appsettings.apps.jsonを読み込む
    var appsConfigPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.apps.json");
    if (File.Exists(appsConfigPath))
    {
        builder.Configuration.AddJsonFile("appsettings.apps.json", optional: true, reloadOnChange: false);
    }

    // サービスの登録（順序が重要）
    // OperationLogServiceは共通で使用（デフォルト設定を使用）
    builder.Services.AddSingleton<OperationLogService>(sp =>
    {
        return new OperationLogService(fileBridgeOptions);
    });

    // AppConfigManagerServiceを登録
    builder.Services.AddSingleton<AppConfigManagerService>(sp =>
    {
        var logService = sp.GetRequiredService<OperationLogService>();
        var logger = sp.GetService<ILogger<AppConfigManagerService>>();
        var loggerFactory = sp.GetService<ILoggerFactory>();
        var configFilePath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.apps.json");
        return new AppConfigManagerService(logService, logger, loggerFactory, configFilePath);
    });

    // 後方互換性のため、既存のサービスも登録（appsettings.apps.jsonが存在しない場合用）
    builder.Services.AddSingleton<ProcessLauncherService>(sp =>
    {
        var logService = sp.GetRequiredService<OperationLogService>();
        var logger = sp.GetService<ILogger<ProcessLauncherService>>();
        return new ProcessLauncherService(fileBridgeOptions, logService, logger);
    });

    builder.Services.AddSingleton<FileWatcherService>(sp =>
    {
        var logService = sp.GetRequiredService<OperationLogService>();
        var processLauncher = sp.GetRequiredService<ProcessLauncherService>();
        var logger = sp.GetService<ILogger<FileWatcherService>>();
        return new FileWatcherService(fileBridgeOptions, logService, processLauncher, logger);
    });

    var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// SignalR Hubのマッピング
app.MapHub<OperationLogHub>("/operationLogHub");

// サービスの初期化と開始
var logService = app.Services.GetRequiredService<OperationLogService>();
var appConfigManager = app.Services.GetRequiredService<AppConfigManagerService>();

// OperationLogServiceにSignalRコールバックを設定
var hubContext = app.Services.GetRequiredService<IHubContext<OperationLogHub>>();
logService.SetOnLogAddedCallback(async (entry) =>
{
    await hubContext.Clients.All.SendAsync("LogAdded", entry);
});

// 複数アプリ設定の読み込みと開始
var appsConfigFilePath = Path.Combine(app.Environment.ContentRootPath, "appsettings.apps.json");
if (File.Exists(appsConfigFilePath))
{
    // appsettings.apps.jsonが存在する場合は複数アプリ設定を使用
    await appConfigManager.LoadConfigsAsync();
    appConfigManager.StartAllWatchers();
    Log.Information("複数アプリ設定を読み込み、監視を開始しました");
}
else
{
    // 後方互換性のため、既存の単一設定を使用
    var fileWatcherService = app.Services.GetRequiredService<FileWatcherService>();
    var processLauncherService = app.Services.GetRequiredService<ProcessLauncherService>();
    fileWatcherService.Start();
    Log.Information("既存の設定を使用して監視を開始しました");
}

// アプリケーション終了時のクリーンアップ
app.Lifetime.ApplicationStopping.Register(() =>
{
    var appsConfigFilePath = Path.Combine(app.Environment.ContentRootPath, "appsettings.apps.json");
    if (File.Exists(appsConfigFilePath))
    {
        appConfigManager.Dispose();
    }
    else
    {
        var fileWatcherService = app.Services.GetRequiredService<FileWatcherService>();
        var processLauncherService = app.Services.GetRequiredService<ProcessLauncherService>();
        fileWatcherService.Stop();
        processLauncherService.Dispose();
    }
    logService.Dispose();
});

Log.Information("アプリケーションの起動が完了しました");

app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "アプリケーションの起動中に致命的なエラーが発生しました");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
