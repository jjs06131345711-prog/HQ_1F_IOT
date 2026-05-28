using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet.Client; // Potentially for MqttClientOptions, etc. if used directly, though likely through IMqttService
using SANJET.Core.Constants;
using SANJET.Core.Constants.Enums; // For Permission enum
using SANJET.Core.Interfaces;
using SANJET.Core.Services; // For MqttService concrete type check
using SANJET.UI.Views.Pages; // For HomePage and Settings page
using SANJET.UI.Views.Windows; // For LoginWindow
using System; // 新增
using System.Collections.ObjectModel;
using System.Linq; // 新增
using System.Text;
using System.Text.Json;
using System.Threading.Tasks; // 新增
using System.Windows;
using System.Windows.Controls; // For Frame


namespace SANJET.Core.ViewModels
{
    public class Esp32LedStatusPayload
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public string? DeviceId { get; set; }
        public string? LedState { get; set; }
    }

    public class Esp32OnlineStatusPayload
    {
        public string? Status { get; set; }
        public string? IP { get; set; }
        public string? DeviceId { get; set; }
    }

    public class ModbusWriteResponsePayload
    {
        public string? DeviceId { get; set; }
        public byte SlaveId { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
        public ushort? Address { get; set; }
    }
    public class ModbusReadResponsePayload
    {
        public string? DeviceId { get; set; }
        public byte SlaveId { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
        public ushort[]? Data { get; set; }
        public byte FunctionCode { get; set; }
        public ushort Address { get; set; }
        public ushort Quantity { get; set; }
    }

    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IAuthenticationService _authService;
        private readonly IMqttService _mqttService;
        private readonly ILogger<MainViewModel> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IPollingStateService _pollingStateService;
        private readonly INavigationService _navigationService;
        private readonly IFaultNotificationService _faultNotificationService;

        private Frame? _mainContentFrame;
        private bool _isFrameInitialized; // 追蹤 Frame 是否就緒
        private bool _isDisposed = false;

        [ObservableProperty]
        private string? _currentUser;

        [ObservableProperty]
        private bool _isLoggedIn;

        [ObservableProperty]
        private bool _isHomeSelected;

        [ObservableProperty]
        private bool _canViewHome;

        [ObservableProperty]
        private bool _isSettingsSelected;

        [ObservableProperty]
        private bool _canViewSettings;

        [ObservableProperty]
        private bool _canControlDevice;

        [ObservableProperty]
        private bool _canAll;

        [ObservableProperty]
        private ObservableCollection<DeviceStatusViewModel> esp32Devices;

        [ObservableProperty]
        private DeviceStatusViewModel? selectedEsp32Device;

        public MainViewModel(
        IAuthenticationService authService,
        IMqttService mqttService,
        ILogger<MainViewModel> logger,
        IServiceProvider serviceProvider,
        IPollingStateService pollingStateService,
        INavigationService navigationService,
        IFaultNotificationService faultNotificationService)
        {
            _authService = authService;
            _mqttService = mqttService;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _pollingStateService = pollingStateService;
            _navigationService = navigationService;
            _faultNotificationService = faultNotificationService;

            this.esp32Devices = [];
            _isFrameInitialized = false; // Frame 未初始化

            UpdateLoginState();
            // IsHomeSelected = true; // 初始選擇狀態應由 UpdateLoginState 或 NavigateHomeAsync 處理
            _ = InitializeMqttRelatedTasksAsync();
        }

        private async Task InitializeMqttRelatedTasksAsync()
        {
            try
            {
                _logger.LogInformation("MainViewModel: 假設 MQTT 客戶端正在由 MqttClientConnectionService 連接或已連接。");

                if (_mqttService is MqttService concreteMqttService)
                {
                    concreteMqttService.ApplicationMessageReceivedAsync += HandleEsp32MqttMessagesAsync;
                }
                else
                {
                    _logger.LogWarning("MainViewModel: _mqttService 不是 MqttService 型別，無法訂閱訊息事件。");
                }

                await _mqttService.SubscribeAsync("devices/+/status");
                await _mqttService.SubscribeAsync("devices/+/led/status");
                await _mqttService.SubscribeAsync("devices/+/modbus/write/response");
                await _mqttService.SubscribeAsync("devices/+/modbus/read/response");

                _logger.LogInformation("MainViewModel: 已訂閱多個設備的 MQTT 主題，包含 Modbus 回應。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainViewModel: MQTT 相關任務初始化失敗 (訂閱等)。");
            }
        }

        private Task HandleEsp32MqttMessagesAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            var topic = e.ApplicationMessage.Topic;
            var payloadJson = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            _logger.LogDebug("MainViewModel 收到 MQTT: 主題='{Topic}', 内容='{Payload}'", topic, payloadJson);

            string? parsedEsp32Id = ParseDeviceIdFromTopic(topic);

            Application.Current.Dispatcher.Invoke(async () =>
            {
                if (_isDisposed)
                {
                    _logger.LogWarning("MainViewModel 已釋放，跳過處理主題 {Topic} 的 MQTT 訊息。", topic);
                    return;
                }

                try
                {
                    if (topic.EndsWith("/status"))
                    {
                        if (string.IsNullOrEmpty(parsedEsp32Id)) return;

                        var statusPayload = JsonSerializer.Deserialize<Esp32OnlineStatusPayload>(payloadJson);
                        if (statusPayload != null && statusPayload.DeviceId == parsedEsp32Id)
                        {
                            var deviceVm = Esp32Devices.FirstOrDefault(d => d.DeviceId == parsedEsp32Id);
                            if (deviceVm == null)
                            {
                                deviceVm = new DeviceStatusViewModel(parsedEsp32Id);
                                Esp32Devices.Add(deviceVm);
                            }
                            deviceVm.LastUpdated = DateTime.UtcNow;

                            if (statusPayload.Status == "online")
                            {
                                deviceVm.ConnectionStatus = "在線";
                                deviceVm.IpAddress = statusPayload.IP;
                                _logger.LogInformation("ESP32 設備 {DeviceId} 在線，IP: {IP}", parsedEsp32Id, statusPayload.IP);
                            }
                            else if (statusPayload.Status == "offline")
                            {
                                deviceVm.ConnectionStatus = "離線";
                                deviceVm.IpAddress = null;
                                _logger.LogInformation("ESP32 設備 {DeviceId} 離線 (LWT).", parsedEsp32Id);
                            }
                            else
                            {
                                deviceVm.ConnectionStatus = statusPayload.Status ?? "狀態未知";
                            }
                        }
                    }
                    else if (topic.EndsWith("/modbus/write/response"))
                    {
                        _logger.LogInformation("收到 Modbus Write Response on topic {Topic}: {Payload}", topic, payloadJson);
                        var responseData = JsonSerializer.Deserialize<ModbusWriteResponsePayload>(payloadJson);

                        if (responseData != null && !string.IsNullOrEmpty(responseData.DeviceId) && responseData.SlaveId > 0)
                        {
                            if (_isDisposed)
                            {
                                _logger.LogWarning("因 MainViewModel 已釋放，跳過處理 Modbus Write Response。主題: {Topic}", topic);
                                return;
                            }

                            var homeViewModel = _serviceProvider.GetService<HomeViewModel>();
                            if (homeViewModel != null && responseData.DeviceId != null)
                            {
                                _logger.LogDebug("呼叫 HomeViewModel.UpdateDeviceStatusFromMqtt 以處理 Write Response。ESP32: {DeviceId}, Slave: {SlaveId}, Status: {Status}",
                                                 responseData.DeviceId, responseData.SlaveId, responseData.Status);
                                homeViewModel.UpdateDeviceStatusFromMqtt(
                                    responseData.DeviceId,
                                    responseData.SlaveId,
                                    responseData.Status ?? "未知狀態",
                                    responseData.Message,
                                    responseData.Address
                                );
                            }
                            else
                            {
                                _logger.LogWarning("HomeViewModel 不可用或 Modbus Write Response 中的 DeviceId 為 null。ESP32: {DeviceId}", responseData.DeviceId);
                            }
                        }
                    }
                    else if (topic.EndsWith("/modbus/read/response"))
                    {
                        _logger.LogInformation("收到 Modbus Read Response on topic {Topic}: {Payload}", topic, payloadJson);
                        var responseData = JsonSerializer.Deserialize<ModbusReadResponsePayload>(payloadJson);

                        if (responseData != null && !string.IsNullOrEmpty(responseData.DeviceId))
                        {
                            if (_isDisposed)
                            {
                                _logger.LogWarning("因 MainViewModel 已釋放 (在創建 scope 之前)，跳過處理 Modbus Read Response。主題: {Topic}", topic);
                                return;
                            }

                            _logger.LogDebug("嘗試為 Modbus Read Response 創建 scope 和 DbContext。DeviceId: {DeviceId}, SlaveId: {SlaveId}, Address: {Address}, Quantity: {Quantity}",
                                             responseData.DeviceId, responseData.SlaveId, responseData.Address, responseData.Quantity);

                            using var scope = _serviceProvider.CreateScope();
                            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                            _logger.LogDebug("已獲取 DbContext。DeviceId: {DeviceId}, SlaveId: {SlaveId}", responseData.DeviceId, responseData.SlaveId);

                            var devicesInDb = await dbContext.Devices
                                .Where(d => d.ControllingEsp32MqttId == responseData.DeviceId &&
                                            d.SlaveId == responseData.SlaveId)
                                .ToListAsync();
                            var deviceInDb = devicesInDb.FirstOrDefault(d =>
                            {
                                var map = ModbusAddressMapping.GetMap(d.Area, d.ModbusDeviceIndex);
                                return responseData.Address == map.StatusAddress ||
                                       responseData.Address == map.RunCountAddress;
                            }) ?? devicesInDb.FirstOrDefault();

                            if (deviceInDb == null)
                            {
                                _logger.LogWarning("Modbus 讀取回應：在資料庫中找不到 ESP32 {Esp32Id}, Slave {SlaveId}。無法更新資料庫或 UI。",
                                                   responseData.DeviceId, responseData.SlaveId);
                            }
                            else
                            {
                                _logger.LogDebug("在資料庫中找到設備。ID: {DbId}, 名稱: {DeviceName}。目前資料庫狀態: '{DbStatus}', 運轉次數: {DbRunCount}",
                                                deviceInDb.Id, deviceInDb.Name, deviceInDb.Status, deviceInDb.RunCount);
                                bool dbChanged = false;

                                if (!deviceInDb.IsOperational)
                                {
                                    if (deviceInDb.Status != "閒置")
                                    {
                                        _logger.LogInformation("Modbus 讀取回應：設備 '{DeviceName}' (ESP32 {Esp32Id}, Slave {SlaveId}) 未啟用，將資料庫狀態由 '{OldStatus}' 同步為 '閒置' 並忽略本次回應。",
                                                               deviceInDb.Name, responseData.DeviceId, responseData.SlaveId, deviceInDb.Status);
                                        deviceInDb.Status = "閒置";
                                        deviceInDb.Timestamp = DateTime.UtcNow;
                                        await dbContext.SaveChangesAsync();
                                    }

                                    var inactiveHomeViewModel = _serviceProvider.GetService<HomeViewModel>();
                                    inactiveHomeViewModel?.UpdateDeviceStatusFromMqtt(
                                        responseData.DeviceId,
                                        responseData.SlaveId,
                                        deviceInDb.Status,
                                        deviceInDb.RunCount,
                                        "設備未啟用，已維持閒置",
                                        responseData.Address);
                                    return;
                                }

                                if (responseData.Status?.ToLower() == "success" && responseData.Data != null)
                                {
                                    var addressMap = ModbusAddressMapping.GetMap(deviceInDb.Area, deviceInDb.ModbusDeviceIndex);
                                    if (responseData.Address == addressMap.StatusAddress && responseData.Quantity == 1 && responseData.Data.Length >= 1)
                                    {
                                        ushort rawStatus = responseData.Data[0];
                                        string newDeviceStatus = ConvertRawModbusStatusToString(rawStatus);
                                        if (deviceInDb.Status != newDeviceStatus)
                                        {
                                            _logger.LogInformation("資料庫更新 (嘗試): ESP32 {Esp32Id}, Slave {SlaveId} - 狀態從 '{OldStatus}' 變為 '{NewStatus}' (原始值: {RawStatus}) (來自位址 {Addr})",
                                                                   responseData.DeviceId, responseData.SlaveId, deviceInDb.Status, newDeviceStatus, rawStatus, responseData.Address);
                                            var oldDeviceStatus = deviceInDb.Status;
                                            deviceInDb.Status = newDeviceStatus;
                                            dbChanged = true;
                                            await _faultNotificationService.NotifyStatusChangedAsync(deviceInDb, oldDeviceStatus, newDeviceStatus, DateTime.Now);
                                        }
                                    }
                                    else if (responseData.Address == addressMap.RunCountAddress && responseData.Quantity == addressMap.RunCountRegisterQuantity && responseData.Data.Length >= addressMap.RunCountRegisterQuantity)
                                    {
                                        ushort word0 = responseData.Data[0];
                                        ushort word1 = responseData.Data[1];
                                        uint unsignedRunCount = ((uint)word1 << 16) | word0;
                                        int newRunCount = (int)unsignedRunCount;
                                        _logger.LogInformation("來自 MQTT 的運轉次數原始值: Data[0]={Word0_Hex} (十進制:{Word0_Dec}), Data[1]={Word1_Hex} (十進制:{Word1_Dec})",
                                                              word0.ToString("X4"), word0, word1.ToString("X4"), word1);
                                        _logger.LogInformation("組合後的運轉次數 (MSW 優先): 無符號={UnsignedVal}, 有符號={SignedVal}",
                                                               unsignedRunCount, newRunCount);

                                        if (deviceInDb.RunCount != newRunCount)
                                        {
                                            _logger.LogInformation("資料庫更新 (嘗試): ESP32 {Esp32Id}, Slave {SlaveId} - 運轉次數從 {OldRunCount} 變為 {NewRunCount}",
                                                                   responseData.DeviceId, responseData.SlaveId, deviceInDb.RunCount, newRunCount);
                                            deviceInDb.RunCount = newRunCount;
                                            dbChanged = true;
                                        }
                                    }

                                    if (dbChanged)
                                    {
                                        deviceInDb.Timestamp = DateTime.UtcNow;
                                        _logger.LogInformation("資料庫儲存 (嘗試): 儲存 ESP32 {Esp32Id}, Slave {SlaveId} 的變更。新狀態: '{NewStatus}', 新運轉次數: {NewRunCount}",
                                                               responseData.DeviceId, responseData.SlaveId, deviceInDb.Status, deviceInDb.RunCount);
                                        await dbContext.SaveChangesAsync();
                                        _logger.LogInformation("資料庫儲存 (成功): 成功更新資料庫：ESP32 {Esp32Id}, Slave {SlaveId}。狀態現為 '{FinalStatus}', 運轉次數現為 {FinalRunCount}。",
                                                               responseData.DeviceId, responseData.SlaveId, deviceInDb.Status, deviceInDb.RunCount);
                                    }
                                    else
                                    {
                                        _logger.LogInformation("根據 MQTT 讀取回應，ESP32 {Esp32Id}, Slave {SlaveId} 在資料庫中無變更。", responseData.DeviceId, responseData.SlaveId);
                                    }
                                }
                                else if (responseData.Status?.ToLower() == "error")
                                {
                                    _logger.LogError("Modbus 讀取失敗 (ESP32: {Esp32Id}, Slave: {SlaveId}, Addr: {Addr}, Qty: {Qty}): {Message}",
                                                     responseData.DeviceId, responseData.SlaveId, responseData.Address, responseData.Quantity, responseData.Message);
                                    if (deviceInDb.Status != "通訊失敗")
                                    {
                                        _logger.LogInformation("資料庫更新 (嘗試): ESP32 {Esp32Id}, Slave {SlaveId} - 由於讀取錯誤，狀態變為 '通訊失敗'。", responseData.DeviceId, responseData.SlaveId);
                                        var oldDeviceStatus = deviceInDb.Status;
                                        deviceInDb.Status = "通訊失敗";
                                        deviceInDb.Timestamp = DateTime.UtcNow;
                                        await _faultNotificationService.NotifyStatusChangedAsync(deviceInDb, oldDeviceStatus, deviceInDb.Status, DateTime.Now);
                                        await dbContext.SaveChangesAsync();
                                        _logger.LogInformation("資料庫儲存 (成功): ESP32 {Esp32Id}, Slave {SlaveId} 的狀態已設為 '通訊失敗'。", responseData.DeviceId, responseData.SlaveId);
                                        dbChanged = true;
                                    }
                                }

                                var homeViewModel = _serviceProvider.GetService<HomeViewModel>();
                                if (homeViewModel != null)
                                {
                                    _logger.LogDebug("呼叫 HomeViewModel.UpdateDeviceStatusFromMqtt 以處理 Read Response。ESP32: {DeviceId}, Slave: {SlaveId}",
                                                     responseData.DeviceId, responseData.SlaveId);
                                    string statusForUi = deviceInDb.Status;
                                    int runCountForUi = deviceInDb.RunCount;
                                    string? contextMessageForUi = dbChanged ? "資料已從 Modbus 更新" : (responseData.Status?.ToLower() == "error" ? responseData.Message : "資料無變更或讀取成功");

                                    homeViewModel.UpdateDeviceStatusFromMqtt(
                                        responseData.DeviceId,
                                        responseData.SlaveId,
                                        statusForUi,
                                        runCountForUi,
                                        contextMessageForUi,
                                        responseData.Address
                                    );
                                }
                                else
                                {
                                    _logger.LogWarning("Modbus Read Response 後，HomeViewModel 不可用於 UI 更新。ESP32: {DeviceId}", responseData.DeviceId);
                                }
                            }
                        }
                    }
                }
                catch (ObjectDisposedException odEx)
                {
                    _logger.LogWarning(odEx, "IServiceProvider 或其 Scope 已被釋放，無法處理 MQTT 訊息（可能在資料庫操作期間）。主題: {Topic}。這可能在應用程式關閉期間發生。", topic);
                }
                catch (DbUpdateException dbEx)
                {
                    _logger.LogError(dbEx, "資料庫更新時發生錯誤 (DbUpdateException)。主題: {Topic}, Payload: {Payload}", topic, payloadJson);
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "反序列化 MQTT payload 失敗。主題: {Topic}, Payload: {Payload}", topic, payloadJson);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "處理 MQTT 訊息時發生未預期錯誤。主題: {Topic}", topic);
                }
            });
            return Task.CompletedTask;
        }

        private static string? ParseDeviceIdFromTopic(string topic)
        {
            var parts = topic.Split('/');
            if (parts.Length >= 2 && parts[0] == "devices")
            {
                return parts[1];
            }
            return null;
        }

        private static string ConvertRawModbusStatusToString(ushort rawStatus)
        {
            return rawStatus switch
            {
                0 => "閒置",
                1 => "運行中",
                2 => "故障",
                _ => $"未知狀態碼 ({rawStatus})",
            };
        }

        public async Task<bool> SendModbusReadCommandAsync(string? targetEsp32MqttId, byte slaveId, ushort address, byte quantity, byte functionCode)
        {
            if (string.IsNullOrEmpty(targetEsp32MqttId))
            {
                _logger.LogWarning("SendModbusReadCommandAsync: targetEsp32MqttId 不可為空。");
                return false;
            }

            var modbusReadPayload = new
            {
                slaveId = slaveId,
                address = address,
                quantity = quantity,
                functionCode = functionCode
            };
            string jsonPayload = JsonSerializer.Serialize(modbusReadPayload);
            string commandTopic = $"devices/{targetEsp32MqttId}/modbus/read/request";

            try
            {
                await _mqttService.PublishAsync(commandTopic, jsonPayload);
                _logger.LogInformation("已發送 Modbus Read 命令到 {Topic} (SlaveID: {SlaveId}): {Payload}", commandTopic, slaveId, jsonPayload);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "發送 MQTT Modbus Read 命令失敗到 {Topic} (SlaveID: {SlaveId})", commandTopic, slaveId);
                return false;
            }
        }

        public async Task<bool> SendModbusWriteCommandAsync(string? targetEsp32MqttId, byte slaveId, ushort address, ushort value)
        {
            if (string.IsNullOrEmpty(targetEsp32MqttId))
            {
                _logger.LogWarning("SendModbusWriteCommandAsync: targetEsp32MqttId 不可為空。");
                return false;
            }

            if (!IsLoggedIn || !CanControlDevice)
            {
                _logger.LogWarning("SendModbusWriteCommandAsync: 未登入或無權限控制設備。");
                MessageBox.Show("未登入或無權限執行 Modbus 操作。", "權限錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var modbusWritePayload = new
            {
                slaveId = slaveId,
                address = address,
                value = value
            };
            string jsonPayload = JsonSerializer.Serialize(modbusWritePayload);
            string commandTopic = $"devices/{targetEsp32MqttId}/modbus/write/request";

            try
            {
                await _mqttService.PublishAsync(commandTopic, jsonPayload);
                _logger.LogInformation("已發送 Modbus Write 命令到 {Topic} (SlaveID: {SlaveId}): {Payload}", commandTopic, slaveId, jsonPayload);

                // 發送寫入命令後，暫停輪詢以防止輪詢讀取覆蓋新寫入的值
                // 暫停時間應長於 ESP32 處理寫入命令的時間（預估 2 秒）
                //_logger.LogInformation("發送寫入命令後，暫停輪詢 5 秒以防止輪詢讀取覆蓋新值。");
                //_ = _pollingStateService.PausePollingAsync(5000); // 暫停 5 秒（fire-and-forget）

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "發送 MQTT Modbus Write 命令失敗到 {Topic} (SlaveID: {SlaveId})", commandTopic, slaveId);
                MessageBox.Show($"無法發送 Modbus 命令到 ESP32 {targetEsp32MqttId} (SlaveID: {slaveId})，請檢查 MQTT 連線。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        public async Task<bool> SendModbusWriteRunCountCommandAsync(string? targetEsp32MqttId, byte slaveId, int runCountValue, ushort? runCountAddress = null)
        {
            if (string.IsNullOrEmpty(targetEsp32MqttId))
            {
                _logger.LogWarning("SendModbusWriteRunCountCommandAsync: targetEsp32MqttId 不可為空。");
                return false;
            }

            if (!IsLoggedIn || !CanControlDevice)
            {
                _logger.LogWarning("SendModbusWriteRunCountCommandAsync: 未登入或無權限控制設備。");
                MessageBox.Show("未登入或無權限執行 Modbus 操作。", "權限錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 將 32 位的 RunCount 分成兩個 16 位的字
            uint unsignedValue = (uint)runCountValue;
            ushort word0 = (ushort)(unsignedValue & 0xFFFF);        // LSW (低位字)
            ushort word1 = (ushort)((unsignedValue >> 16) & 0xFFFF); // MSW (高位字)

            var modbusWriteRunCountPayload = new
            {
                slaveId = slaveId,
                address = runCountAddress ?? ModbusConstants.RunCountRelativeAddress,
                quantity = 2,
                values = new[] { word0, word1 }
            };
            string jsonPayload = JsonSerializer.Serialize(modbusWriteRunCountPayload);
            string commandTopic = $"devices/{targetEsp32MqttId}/modbus/write/request";

            try
            {
                _logger.LogInformation("正在發送 Modbus Write RunCount 命令到 {Topic} (SlaveID: {SlaveId}, RunCount: {RunCount}, Word0: {Word0_Hex}, Word1: {Word1_Hex}): {Payload}",
                                     commandTopic, slaveId, runCountValue, word0.ToString("X4"), word1.ToString("X4"), jsonPayload);
                await _mqttService.PublishAsync(commandTopic, jsonPayload);
                _logger.LogInformation("已成功發送 Modbus Write RunCount 命令到 {Topic} (SlaveID: {SlaveId}, RunCount: {RunCount})", commandTopic, slaveId, runCountValue);

                // 發送寫入命令後，暫停輪詢以防止輪詢讀取覆蓋新寫入的值
                // 暫停時間應長於 ESP32 處理寫入命令的時間（預估 2 秒）
                _logger.LogInformation("發送 RunCount 寫入命令後，暫停輪詢 5 秒以防止輪詢讀取覆蓋新值。");
                _ = _pollingStateService.PausePollingAsync(5000); // 暫停 5 秒（fire-and-forget）

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "發送 MQTT Modbus Write RunCount 命令失敗到 {Topic} (SlaveID: {SlaveId}, RunCount: {RunCount})", commandTopic, slaveId, runCountValue);
                MessageBox.Show($"無法發送 Modbus RunCount 命令到 ESP32 {targetEsp32MqttId} (SlaveID: {slaveId})，請檢查 MQTT 連線。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }


        public void SetMainContentFrame(Frame frame)
        {
            _mainContentFrame = frame ?? throw new ArgumentNullException(nameof(frame), "MainContentFrame 不可為 null。");
            _isFrameInitialized = true;
            _logger.LogInformation("MainContentFrame 已設置，類型：{FrameType}", frame.GetType().Name);
            UpdateLoginState();
        }


        public void UpdateLoginState()
        {
            var currentUserObject = _authService.GetCurrentUser();
            CurrentUser = currentUserObject?.Username;
            bool oldIsLoggedIn = IsLoggedIn;
            IsLoggedIn = currentUserObject != null;

            if (IsLoggedIn && currentUserObject != null && currentUserObject.PermissionsList != null)
            {
                CanViewHome = currentUserObject.PermissionsList.Contains(Permission.ViewHome.ToString()) ||
                              currentUserObject.PermissionsList.Contains(Permission.All.ToString());
                CanControlDevice = currentUserObject.PermissionsList.Contains(Permission.ControlDevice.ToString()) ||
                                   currentUserObject.PermissionsList.Contains(Permission.All.ToString());
                CanViewSettings = currentUserObject.PermissionsList.Contains(Permission.ViewSettings.ToString()) ||
                                  currentUserObject.PermissionsList.Contains(Permission.All.ToString());
                CanAll = currentUserObject.PermissionsList.Contains(Permission.All.ToString());

                if (CanViewHome)
                {
                    _logger.LogInformation("用戶已登入且擁有首頁檢視權限。正在啟用 Modbus 輪詢以更新設備狀態與運轉次數。");
                    _pollingStateService.EnablePolling();
                }
                else
                {
                    _logger.LogInformation("用戶已登入，但缺少首頁檢視權限。輪詢將保持禁用狀態。");
                    _pollingStateService.DisablePolling();
                }

                if (!_isFrameInitialized || _mainContentFrame == null)
                {
                    _logger.LogWarning("MainContentFrame 未正確初始化，跳過導航。");
                    return;
                }

                if (CanViewHome && (!IsHomeSelected && !IsSettingsSelected))
                {
                    _ = _navigationService.NavigateToHomeAsync(_mainContentFrame);
                    IsHomeSelected = true;
                    IsSettingsSelected = false;
                }
                else if (!CanViewHome && CanViewSettings && (!IsHomeSelected && !IsSettingsSelected))
                {
                    _navigationService.NavigateToSettings(_mainContentFrame);
                    IsHomeSelected = false;
                    IsSettingsSelected = true;
                }
                else if (!CanViewHome && !CanViewSettings)
                {
                    IsHomeSelected = false;
                    IsSettingsSelected = false;
                    _navigationService.ClearNavigation(_mainContentFrame);
                }
            }
            else
            {
                CanViewHome = false;
                CanControlDevice = false;
                CanViewSettings = false;
                CanAll = false;
                IsHomeSelected = false;
                IsSettingsSelected = false;

                if (_isFrameInitialized && _mainContentFrame != null)
                {
                    _navigationService.ClearNavigation(_mainContentFrame);
                }
                else
                {
                    _logger.LogWarning("MainContentFrame 未正確初始化，無法清除導航。");
                }

                if (oldIsLoggedIn && !IsLoggedIn)
                {
                    _logger.LogInformation("用戶已登出或會話結束。正在禁用 Modbus 輪詢。");
                    _pollingStateService.DisablePolling();
                }
                else if (!IsLoggedIn && !_pollingStateService.IsPollingEnabled)
                {
                    _logger.LogInformation("應用程式啟動，用戶未登入。Modbus 輪詢初始為禁用狀態。");
                    _pollingStateService.DisablePolling();
                }
            }
        }


        [RelayCommand]
        private void NavigateHome()
        {
            try
            {
                if (!_isFrameInitialized || _mainContentFrame == null)
                {
                    _logger.LogWarning("MainContentFrame 未正確初始化，嘗試重新設置。");
                    // 嘗試重新獲取 Frame
                    if (Application.Current.MainWindow is MainWindow mainWindow)
                    {
                        SetMainContentFrame(mainWindow.MainContentFrame);
                    }

                    if (!_isFrameInitialized || _mainContentFrame == null)
                    {
                        _logger.LogError("無法獲取 MainContentFrame，導航失敗。");
                        return;
                    }
                }

                _navigationService.NavigateToHomeAsync(_mainContentFrame);
                IsHomeSelected = true;
                IsSettingsSelected = false;
                _logger.LogInformation("成功導航到首頁。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "導航到首頁時發生錯誤。");

            }
        }

        [RelayCommand]
        private void NavigateSettings()
        {
            try
            {
                if (!_isFrameInitialized || _mainContentFrame == null)
                {
                    _logger.LogWarning("MainContentFrame 未正確初始化，嘗試重新設置。");
                    // 嘗試重新獲取 Frame
                    if (Application.Current.MainWindow is MainWindow mainWindow)
                    {
                        SetMainContentFrame(mainWindow.MainContentFrame);
                    }

                    if (!_isFrameInitialized || _mainContentFrame == null)
                    {
                        _logger.LogError("無法獲取 MainContentFrame，導航失敗。");
                        return;
                    }
                }

                _navigationService.NavigateToSettings(_mainContentFrame);
                IsHomeSelected = false;
                IsSettingsSelected = true;
                _logger.LogInformation("成功導航到設置頁。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "導航到設置頁時發生錯誤。");
            }
        }

        [RelayCommand]
        private void OpenStreamWindow()
        {
            try
            {
                _logger.LogInformation("打開串流窗口");
                if (App.Host != null)
                {
                    var streamWindow = App.Host.Services.GetRequiredService<StreamWindow>();
                    streamWindow.Owner = Application.Current.MainWindow;
                    streamWindow.Show();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打開串流窗口失敗。");
                MessageBox.Show($"打開串流窗口失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        [RelayCommand]
        private void Logout()
        {
            _logger.LogInformation("執行登出操作。正在禁用 Modbus 輪詢。");
            _pollingStateService.DisablePolling();
            _authService.Logout();
            // UpdateLoginState 會處理 IsHomeSelected 和 IsSettingsSelected
            UpdateLoginState();
        }


        [RelayCommand]
        private void ShowLogin()
        {
            if (App.Host != null)
            {
                var loginWindow = App.Host.Services.GetRequiredService<LoginWindow>();
                loginWindow.Owner = Application.Current.MainWindow;
                bool? result = loginWindow.ShowDialog();

                if (result == true)
                {
                    UpdateLoginState(); // 登入成功後，UpdateLoginState 會處理初始導航
                }
            }
        }


        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    if (_mqttService is MqttService concreteMqttService)
                    {
                        concreteMqttService.ApplicationMessageReceivedAsync -= HandleEsp32MqttMessagesAsync;
                        _logger.LogInformation("MainViewModel disposed, unsubscribed from MQTT messages.");
                    }
                }
                _isDisposed = true;
            }
        }
    }
}
