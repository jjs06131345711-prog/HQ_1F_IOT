// 檔案路徑: App.xaml.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SANJET.Core;
using SANJET.Core.Configuration;
using SANJET.Core.Interfaces;
using SANJET.Core.Models;
using SANJET.Core.Services;
using SANJET.Core.ViewModels;
using SANJET.UI.Views.Windows;
using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading; // 為了 DispatcherUnhandledException

namespace SANJET
{
    public partial class App : Application
    {
        public static IHost? Host { get; private set; }
        private IMqttBrokerService? _mqttBrokerService;

        /// <summary>
        /// 應用程式建構函式：在此處訂閱全域未處理例外事件。
        /// </summary>
        public App()
        {
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        }

        /// <summary>
        /// 全域例外處理器：捕捉任何在 UI 執行緒上未被處理的錯誤。
        /// </summary>
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var logger = Host?.Services.GetService<ILogger<App>>();
            logger?.LogError(e.Exception, "捕獲到一個未處理的全域例外");

            MessageBox.Show("捕獲到未處理的例外狀況，應用程式即將關閉。\n\n" +
                            $"錯誤訊息: {e.Exception.Message}\n\n" +
                            $"內部例外: {e.Exception.InnerException?.Message}\n\n" +
                            $"堆疊追蹤: {e.Exception.StackTrace}",
                            "未處理的例外狀況",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);

            e.Handled = true;
            Current.Shutdown();
        }

        /// <summary>
        /// 應用程式啟動主邏輯。
        /// </summary>
        protected override async void OnStartup(StartupEventArgs e)
        {
            // 啟動流程會先顯示 LoadingWindow，之後關閉它再顯示 MainWindow。
            // 若使用預設 OnLastWindowClose，關閉 LoadingWindow 會讓 WPF 在 MainWindow.Show() 前直接關閉應用程式。
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                SQLitePCL.Batteries.Init();

                Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                    .ConfigureServices((context, services) =>
                    {
                        services.AddLogging(configure => configure.AddDebug().SetMinimumLevel(LogLevel.Debug));

                        var lineMessagingSection = context.Configuration.GetSection("LineMessaging");
                        var lineMessagingOptions = new LineMessagingOptions
                        {
                            Enabled = bool.TryParse(lineMessagingSection["Enabled"], out var lineEnabled) && lineEnabled,
                            ChannelAccessToken = lineMessagingSection["ChannelAccessToken"] ?? string.Empty,
                            TargetIds = lineMessagingSection.GetSection("TargetIds")
                                .GetChildren()
                                .Select(child => child.Value ?? string.Empty)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .ToArray(),
                            CooldownMinutes = int.TryParse(lineMessagingSection["CooldownMinutes"], out var cooldownMinutes) ? cooldownMinutes : 30,
                            NotifyRecovery = !bool.TryParse(lineMessagingSection["NotifyRecovery"], out var notifyRecovery) || notifyRecovery
                        };
                        services.AddSingleton(lineMessagingOptions);

                        var lineAutoHotkeySection = context.Configuration.GetSection("LineAutoHotkey");
                        var lineAutoHotkeyOptions = new LineAutoHotkeyOptions
                        {
                            Enabled = bool.TryParse(lineAutoHotkeySection["Enabled"], out var autoHotkeyEnabled) && autoHotkeyEnabled,
                            AutoHotkeyExecutablePath = lineAutoHotkeySection["AutoHotkeyExecutablePath"] ?? @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe",
                            AutoHotkeyVersion = lineAutoHotkeySection["AutoHotkeyVersion"] ?? "v2",
                            LineExecutablePath = lineAutoHotkeySection["LineExecutablePath"] ?? string.Empty,
                            LineProcessName = lineAutoHotkeySection["LineProcessName"] ?? "LINE",
                            TargetChatNames = lineAutoHotkeySection.GetSection("TargetChatNames")
                                .GetChildren()
                                .Select(child => child.Value ?? string.Empty)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .ToArray(),
                            PinLineWindow = !bool.TryParse(lineAutoHotkeySection["PinLineWindow"], out var pinLineWindow) || pinLineWindow,
                            KeepLineWindowTopMost = !bool.TryParse(lineAutoHotkeySection["KeepLineWindowTopMost"], out var keepLineWindowTopMost) || keepLineWindowTopMost,
                            LineWindowLeft = int.TryParse(lineAutoHotkeySection["LineWindowLeft"], out var lineWindowLeft) ? lineWindowLeft : 0,
                            LineWindowTop = int.TryParse(lineAutoHotkeySection["LineWindowTop"], out var lineWindowTop) ? lineWindowTop : 0,
                            LineWindowWidth = int.TryParse(lineAutoHotkeySection["LineWindowWidth"], out var lineWindowWidth) ? lineWindowWidth : 1000,
                            LineWindowHeight = int.TryParse(lineAutoHotkeySection["LineWindowHeight"], out var lineWindowHeight) ? lineWindowHeight : 800,
                            MinimizeLineWindowAfterSend = bool.TryParse(lineAutoHotkeySection["MinimizeLineWindowAfterSend"], out var minimizeLineWindowAfterSend) && minimizeLineWindowAfterSend,
                            OperationTimeoutSeconds = int.TryParse(lineAutoHotkeySection["OperationTimeoutSeconds"], out var operationTimeoutSeconds) ? operationTimeoutSeconds : 15,
                            SendDelayMilliseconds = int.TryParse(lineAutoHotkeySection["SendDelayMilliseconds"], out var sendDelayMilliseconds) ? sendDelayMilliseconds : 300,
                            RestoreClipboard = !bool.TryParse(lineAutoHotkeySection["RestoreClipboard"], out var restoreClipboard) || restoreClipboard
                        };
                        // 套用使用者在設定頁保存的覆寫值（Enabled / TargetChatNames），需在首次通知前生效。
                        LineAutoHotkeySettingsStore.ApplyTo(lineAutoHotkeyOptions);
                        services.AddSingleton(lineAutoHotkeyOptions);

                        var faultNotificationSection = context.Configuration.GetSection("FaultNotification");
                        var faultNotificationOptions = new FaultNotificationOptions
                        {
                            Enabled = !bool.TryParse(faultNotificationSection["Enabled"], out var faultNotificationEnabled) || faultNotificationEnabled,
                            CooldownMinutes = int.TryParse(faultNotificationSection["CooldownMinutes"], out var faultCooldownMinutes)
                                ? faultCooldownMinutes
                                : lineMessagingOptions.CooldownMinutes,
                            NotifyRecovery = !bool.TryParse(faultNotificationSection["NotifyRecovery"], out var faultNotifyRecovery)
                                ? lineMessagingOptions.NotifyRecovery
                                : faultNotifyRecovery,
                            BatchWindowSeconds = int.TryParse(faultNotificationSection["BatchWindowSeconds"], out var faultBatchWindowSeconds)
                                ? faultBatchWindowSeconds
                                : 5,
                            MaxBatchSize = int.TryParse(faultNotificationSection["MaxBatchSize"], out var faultMaxBatchSize)
                                ? faultMaxBatchSize
                                : 50
                        };
                        services.AddSingleton(faultNotificationOptions);

                        // 將 SQLite 放在使用者 LocalAppData，避免安裝目錄沒有寫入權限導致啟動失敗。
                        var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        var appDataPath = Path.Combine(localAppDataPath, "SanjetScada");
                        Directory.CreateDirectory(appDataPath);

                        var dbPath = Path.Combine(appDataPath, "SNAJET_local.db");
                        var connectionString = $"Data Source={dbPath}";

                        services.AddSingleton(new DatabaseSettings(dbPath, connectionString));
                        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString), ServiceLifetime.Transient);

                        services.AddSingleton<MainViewModel>();
                        // HomePage 綁定的 HomeViewModel 必須與 MQTT 回應處理器更新的實例相同。
                        services.AddSingleton<HomeViewModel>();
                        services.AddTransient<SettingsPageViewModel>();
                        services.AddTransient<LoginViewModel>();
                        services.AddTransient<LoginWindow>();
                        services.AddTransient(sp => new StreamWindow(sp.GetRequiredService<SettingsPageViewModel>()));
                        services.AddTransient<AddTestDeviceViewModel>();
                        services.AddTransient<SANJET.UI.Views.Windows.AddTestDeviceWindow>();
                        //services.AddTransient<RecordWindow>();

                        services.AddSingleton<LoadingWindowViewModel>();
                        services.AddTransient<LoadingWindow>();
                        services.AddTransient<MainWindow>();

                        services.AddSingleton<IAuthenticationService, AuthenticationService>();
                        services.AddSingleton<IMqttService, MqttService>();
                        services.AddSingleton<IMqttBrokerService, MqttBrokerService>();
                        services.AddSingleton<IPollingStateService, PollingStateService>();
                        services.AddSingleton<INavigationService, NavigationService>();
                        services.AddSingleton<IAudioService, AudioService>();
                        services.AddSingleton<IDatabaseManagementService, DatabaseManagementService>();
                        services.AddSingleton<ILibVLCInitializationService, LibVLCInitializationService>();
                        services.AddSingleton<ILineNotificationChannel, LineNotificationService>();
                        services.AddSingleton<ILineNotificationChannel, LineAutoHotkeyNotificationService>();
                        services.AddSingleton<ILineNotificationService, CompositeLineNotificationService>();
                        services.AddSingleton<IFaultNotificationService, FaultNotificationService>();

                        services.AddHostedService<MqttClientConnectionService>();
                        services.AddHostedService<ModbusPollingService>();
                    })
                    .Build();

                var appLogger = Host.Services.GetRequiredService<ILogger<App>>();

                _mqttBrokerService = Host.Services.GetRequiredService<IMqttBrokerService>();
                try
                {
                    await _mqttBrokerService.StartAsync();
                    appLogger.LogInformation("MQTT Broker started successfully.");
                }
                catch (Exception brokerEx)
                {
                    appLogger.LogError(brokerEx, "Failed to start MQTT Broker.");
                    MessageBox.Show($"MQTT Broker 啟動失敗：{brokerEx.Message}\n程式將繼續運行，但 MQTT 功能可能無法使用。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                await Host.StartAsync();
                appLogger.LogInformation("Application Host started.");

                Host.Services.GetRequiredService<IPollingStateService>().DisablePolling();
                appLogger.LogInformation("Polling explicitly disabled on startup.");

                // 顯示載入視窗
                var loadingWindow = Host.Services.GetRequiredService<LoadingWindow>();
                var loadingViewModel = Host.Services.GetRequiredService<LoadingWindowViewModel>();
                loadingWindow.Show();

                // 在顯示載入視窗時，並行執行多項初始化任務以提升性能
                appLogger.LogInformation("開始並行初始化任務...");

                // 1. 預熱 LibVLC 以加快首次 RTSP 串流連接速度
                loadingViewModel.StatusText = "預熱 LibVLC 媒體引擎...";
                var libVLCService = Host.Services.GetRequiredService<ILibVLCInitializationService>();
                var preWarmTask = libVLCService.PreWarmAsync();

                // 2. 初始化資料庫（第一次啟動時會建立 SQLite 檔案與資料表）
                loadingViewModel.StatusText = "初始化資料庫...";
                var dbInitTask = InitializeDatabaseAsync(Host, appLogger);

                // 等待兩項任務完成
                await Task.WhenAll(preWarmTask, dbInitTask);
                bool isInitialized = await dbInitTask;

                if (isInitialized)
                {
                    appLogger.LogInformation("Database initialized successfully. Initializing main application.");

                    loadingViewModel.StatusText = "初始化完成，準備進入主畫面...";

                    // 所有初始化都完成後才啟動 LoadingWindow 動畫；動畫播放完畢後再進入主視窗。
                    await loadingWindow.PlayCompletionAnimationAsync();

                    // 顯示主視窗。登入邏輯將由 MainWindow 的 Loaded 事件觸發。
                    var mainWindow = Host.Services.GetRequiredService<MainWindow>();
                    Application.Current.MainWindow = mainWindow; // 明確設定應用程式的主視窗

                    // LoadingWindow 的流程已完成；先關閉 LoadingWindow，再顯示 MainWindow，
                    // 讓主視窗不會在載入視窗仍存在時提早出現。
                    loadingWindow.Close();

                    mainWindow.Show();
                    Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;

                }
                else
                {
                    appLogger.LogCritical("Database initialization failed. Application will shut down.");
                    MessageBox.Show("無法初始化資料庫，請檢查資料庫路徑權限或磁碟狀態。\n應用程式即將關閉。", "嚴重錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown();
                }
            }
            catch (Exception ex)
            {
                var logger = Host?.Services.GetService<ILogger<App>>();
                logger?.LogError(ex, "應用程式啟動時發生無法處理的錯誤");
                MessageBox.Show($"啟動失敗：{ex.Message}\n{ex.StackTrace}", "致命錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                
                Shutdown();
            }
        }

        /// <summary>
        /// 初始化資料庫：第一次啟動時建立 SQLite 檔案與資料表，並寫入預設資料。
        /// </summary>
        private async Task<bool> InitializeDatabaseAsync(IHost host, ILogger<App> logger)
        {
            using var scope = host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var databaseSettings = scope.ServiceProvider.GetRequiredService<DatabaseSettings>();

            try
            {
                logger.LogInformation("本地資料庫路徑設定為: {DbPath}", databaseSettings.Path);
                await dbContext.Database.EnsureCreatedAsync();
                await EnsureDeviceColumnsAsync(dbContext, logger);
                SeedData(dbContext, logger);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize the database.");
                return false;
            }
        }

        /// <summary>
        /// 確保既有 SQLite 資料庫具備目前 Device 模型需要的欄位。EnsureCreated 不會更新已存在的資料表，
        /// 因此舊資料庫需要在讀取 Devices 前補上新欄位，避免登入後首頁載入設備時因缺欄位而中斷導航。
        /// </summary>
        private async Task EnsureDeviceColumnsAsync(AppDbContext dbContext, ILogger<App> logger)
        {
            var connection = dbContext.Database.GetDbConnection();
            var shouldCloseConnection = connection.State == ConnectionState.Closed;

            if (shouldCloseConnection)
            {
                await connection.OpenAsync();
            }

            try
            {
                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var checkCommand = connection.CreateCommand())
                {
                    checkCommand.CommandText = "PRAGMA table_info(Devices);";
                    await using var reader = await checkCommand.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var columnName = reader[1]?.ToString();
                        if (!string.IsNullOrWhiteSpace(columnName))
                        {
                            existingColumns.Add(columnName);
                        }
                    }
                }

                if (!existingColumns.Contains("Area"))
                {
                    await using var alterAreaCommand = connection.CreateCommand();
                    alterAreaCommand.CommandText = "ALTER TABLE Devices ADD COLUMN Area TEXT NOT NULL DEFAULT '展機區';";
                    await alterAreaCommand.ExecuteNonQueryAsync();
                    logger.LogInformation("已為既有 Devices 資料表新增 Area 欄位，預設為展機區。");
                }

                if (!existingColumns.Contains("ModbusDeviceIndex"))
                {
                    await using var alterModbusDeviceIndexCommand = connection.CreateCommand();
                    alterModbusDeviceIndexCommand.CommandText = "ALTER TABLE Devices ADD COLUMN ModbusDeviceIndex INTEGER NOT NULL DEFAULT 1;";
                    await alterModbusDeviceIndexCommand.ExecuteNonQueryAsync();
                    logger.LogInformation("已為既有 Devices 資料表新增 ModbusDeviceIndex 欄位，預設為 1。");
                }

                if (!existingColumns.Contains("TargetRunCount"))
                {
                    await using var alterTargetRunCountCommand = connection.CreateCommand();
                    alterTargetRunCountCommand.CommandText = "ALTER TABLE Devices ADD COLUMN TargetRunCount INTEGER NULL;";
                    await alterTargetRunCountCommand.ExecuteNonQueryAsync();
                    logger.LogInformation("已為既有 Devices 資料表新增 TargetRunCount 欄位。");
                }

                if (!existingColumns.Contains("AutoStopOnTarget"))
                {
                    await using var alterAutoStopOnTargetCommand = connection.CreateCommand();
                    alterAutoStopOnTargetCommand.CommandText = "ALTER TABLE Devices ADD COLUMN AutoStopOnTarget INTEGER NOT NULL DEFAULT 0;";
                    await alterAutoStopOnTargetCommand.ExecuteNonQueryAsync();
                    logger.LogInformation("已為既有 Devices 資料表新增 AutoStopOnTarget 欄位，預設為 false。");
                }
            }
            finally
            {
                if (shouldCloseConnection)
                {
                    await connection.CloseAsync();
                }
            }
        }

        /// <summary>
        /// 初始化資料庫的種子資料。
        /// </summary>
        private void SeedData(AppDbContext dbContext, ILogger<App> logger)
        {
            try
            {
                logger.LogInformation("開始執行 SeedData...");

                if (!dbContext.Users.Any())
                {
                    logger.LogInformation("Users 表為空，開始插入預設資料...");
                    dbContext.Users.AddRange(
                        new User { Username = "administrator", Password = "sanjet25653819", Permissions = "ViewHome,ControlDevice,ViewSettings,All" },
                        new User { Username = "admin", Password = "0000", Permissions = "ViewHome,ControlDevice" },
                        new User { Username = "user", Password = "0000", Permissions = "ViewHome" }
                    );
                }
                else
                {
                    logger.LogInformation("Users 表已有資料，跳過插入。");
                }

                if (!dbContext.Devices.Any())
                {
                    logger.LogInformation("Devices 表為空，開始插入預設設備資料...");
                    dbContext.Devices.AddRange(
                        new Device { Name = "DKSS", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 1, Status = "閒置", IsOperational = true, RunCount = 0, Area = "展機區" },
                        new Device { Name = "HBC2", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 2, Status = "閒置", IsOperational = true, RunCount = 0, Area = "展機區" },
                        new Device { Name = "DVC雙層", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 3, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備4", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 4, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備5", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 5, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備6", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 6, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備7", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 7, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備8", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 8, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備9", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 9, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備10", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 10, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },
                        new Device { Name = "預設設備11", ControllingEsp32MqttId = "ESP32_RS485", SlaveId = 11, Status = "閒置", IsOperational = false, RunCount = 0, Area = "展機區" },

                        new Device { Name = "SRP02位移", ControllingEsp32MqttId = "ESP32_MdTCP", SlaveId = 1, Status = "閒置", IsOperational = true, RunCount = 0, Area = "展機區" }
                    );
                }
                else
                {
                    logger.LogInformation("Devices 表已有資料，跳過設備插入。");
                }
                dbContext.SaveChanges();
                logger.LogInformation("SeedData 完成。");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SeedData 失敗");
                throw; // 拋出例外，由上層的 try-catch 處理
            }
        }

        /// <summary>
        /// 應用程式關閉時的清理工作。
        /// </summary>
        protected override async void OnExit(ExitEventArgs e)
        {
            if (_mqttBrokerService != null)
            {
                await _mqttBrokerService.StopAsync();
            }

            if (Host != null)
            {
                await Host.StopAsync();
                Host.Dispose();
            }
            base.OnExit(e);
        }
    }
}
