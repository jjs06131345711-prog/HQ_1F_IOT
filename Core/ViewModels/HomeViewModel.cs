
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SANJET.Core.Interfaces;
using SANJET.Core.Models;
using SANJET.Core.Constants;
using SANJET.Core.Services;
using SANJET.UI.Views.Windows;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace SANJET.Core.ViewModels
{
    public partial class HomeViewModel : ObservableObject
    {
        private const string DisplayAreaName = "展機區";
        private const string TestAreaName = "測試區";
        private const string IdleStatus = "閒置";

        private readonly AppDbContext _dbContext;
        private readonly ILogger<HomeViewModel> _logger;
        private readonly MainViewModel? _mainViewModel;
        private readonly IAudioService _audioService;
        private readonly ILineNotificationService? _lineNotificationService;

        // 暴露 DbContext 供 DeviceViewModel 使用
        public AppDbContext DbContext => _dbContext;

        [ObservableProperty]
        private ObservableCollection<DeviceViewModel> devices = new();

        [ObservableProperty]
        private ObservableCollection<DeviceViewModel> displayAreaDevices = new();

        [ObservableProperty]
        private ObservableCollection<DeviceViewModel> testAreaDevices = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowSelectedAreaEmptyMessage))]
        private ObservableCollection<DeviceViewModel> selectedAreaDevices = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowSelectedAreaEmptyMessage))]
        private bool isAreaSelected;

        [ObservableProperty]
        private string selectedAreaName = "廠區平面圖";

        [ObservableProperty]
        private string selectedAreaDescription = "請先點選平面圖上的區域。";

        public bool ShowSelectedAreaEmptyMessage => IsAreaSelected && !SelectedAreaDevices.Any();

        [ObservableProperty]
        private bool canControlDevice;

        public HomeViewModel(
            AppDbContext dbContext, 
            ILogger<HomeViewModel> logger,
            IAudioService audioService,
            ILineNotificationService? lineNotificationService = null)
        {
            _dbContext = dbContext;
            _logger = logger;
            _audioService = audioService;
            _lineNotificationService = lineNotificationService;
            _mainViewModel = App.Host?.Services.GetService<MainViewModel>();
        }

        public async Task LoadDevicesAsync()
        {
            Devices.Clear();
            DisplayAreaDevices.Clear();
            TestAreaDevices.Clear();
            if (_dbContext.Devices == null)
            {
                _logger.LogWarning("HomeViewModel.LoadDevicesAsync: _dbContext.Devices is null.");
                return;
            }
            var inactiveDevicesWithStaleStatus = await _dbContext.Devices
                .Where(d => !d.IsOperational && d.Status != IdleStatus)
                .ToListAsync();

            if (inactiveDevicesWithStaleStatus.Any())
            {
                foreach (var inactiveDevice in inactiveDevicesWithStaleStatus)
                {
                    inactiveDevice.Status = IdleStatus;
                    inactiveDevice.Timestamp = DateTime.UtcNow;
                }

                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("已將 {Count} 個未啟用設備的資料庫狀態同步為閒置。", inactiveDevicesWithStaleStatus.Count);
            }

            var devicesFromDb = await _dbContext.Devices
                .OrderBy(d => d.SlaveId)
                .ThenBy(d => d.ModbusDeviceIndex)
                .ThenBy(d => d.Id)
                .ToListAsync();
            foreach (var deviceEntity in devicesFromDb)
            {
                var deviceVm = new DeviceViewModel(this, _mainViewModel, _logger, _audioService, _lineNotificationService)
                {
                    Id = deviceEntity.Id,
                    Name = deviceEntity.Name,
                    OriginalName = deviceEntity.Name,
                    SlaveId = deviceEntity.SlaveId,
                    Status = deviceEntity.IsOperational ? deviceEntity.Status : IdleStatus,
                    IsOperational = deviceEntity.IsOperational,
                    RunCount = deviceEntity.RunCount,
                    TargetRunCount = deviceEntity.TargetRunCount,
                    AutoStopOnTarget = deviceEntity.AutoStopOnTarget,
                    IsEditingName = false,
                    ControllingEsp32MqttId = deviceEntity.ControllingEsp32MqttId,
                    Area = deviceEntity.Area,
                    ModbusDeviceIndex = deviceEntity.ModbusDeviceIndex
                };

                if (string.IsNullOrEmpty(deviceVm.ControllingEsp32MqttId))
                {
                    _logger.LogWarning("資料庫中的設備 {DeviceName} (ID: {DeviceId}, SlaveID: {SlaveId}) 未設定 ControllingEsp32MqttId，將無法透過 MQTT 控制 Modbus。",
                                       deviceVm.Name, deviceVm.Id, deviceVm.SlaveId);
                }
                Devices.Add(deviceVm);

                if (IsTestArea(deviceEntity.Area))
                {
                    TestAreaDevices.Add(deviceVm);
                }
                else
                {
                    DisplayAreaDevices.Add(deviceVm);
                }
            }

            RefreshSelectedAreaDevices();
        }

        private static bool IsTestArea(string? area)
        {
            return string.Equals(area, TestAreaName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDisplayArea(string? area)
        {
            return string.IsNullOrWhiteSpace(area) ||
                   string.Equals(area, DisplayAreaName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(area, "展示區", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshSelectedAreaDevices()
        {
            if (!IsAreaSelected)
            {
                return;
            }

            SelectedAreaDevices = IsTestArea(SelectedAreaName)
                ? TestAreaDevices
                : DisplayAreaDevices;
            OnPropertyChanged(nameof(ShowSelectedAreaEmptyMessage));
        }

        [RelayCommand]
        private void SelectArea(string? areaKey)
        {
            IsAreaSelected = true;

            if (string.Equals(areaKey, "Test", StringComparison.OrdinalIgnoreCase))
            {
                SelectedAreaName = TestAreaName;
                SelectedAreaDescription = "測試區用於新增和測試設備。可透過 RS485 連接多達 5 個 Modbus 從站。";
                SelectedAreaDevices = TestAreaDevices;
            }
            else
            {
                SelectedAreaName = DisplayAreaName;
                SelectedAreaDescription = "可在此查看狀態、啟停設備與紀錄。";
                SelectedAreaDevices = DisplayAreaDevices;
            }

            OnPropertyChanged(nameof(ShowSelectedAreaEmptyMessage));
        }

        [RelayCommand]
        private void BackToFactoryMap()
        {
            IsAreaSelected = false;
            SelectedAreaName = "廠區平面圖";
            SelectedAreaDescription = "請先點選平面圖上的區域。";
            SelectedAreaDevices = new ObservableCollection<DeviceViewModel>();
            OnPropertyChanged(nameof(ShowSelectedAreaEmptyMessage));
        }

        /// <summary>
        /// 打開測試區設備添加對話框
        /// </summary>
        [RelayCommand]
        private void OpenAddTestDeviceDialog()
        {
            if (App.Host == null)
            {
                MessageBox.Show("應用程式服務不可用，無法打開對話框。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                _logger.LogError("App.Host 為 null，無法打開 AddTestDeviceWindow。");
                return;
            }

            try
            {
                using var scope = App.Host.Services.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<AddTestDeviceViewModel>>();

                var viewModel = new AddTestDeviceViewModel(this, logger);
                var window = new SANJET.UI.Views.Windows.AddTestDeviceWindow(viewModel)
                {
                    Owner = Application.Current.MainWindow,
                    Title = "添加測試區設備"
                };

                _logger.LogInformation("打開添加測試區設備對話框");
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打開添加測試區設備對話框時發生錯誤");
                MessageBox.Show($"無法打開對話框：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 打開展機區設備添加對話框
        /// </summary>
        [RelayCommand]
        private void OpenAddDisplayAreaDeviceDialog()
        {
            if (App.Host == null)
            {
                MessageBox.Show("應用程式服務不可用，無法打開對話框。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                _logger.LogError("App.Host 為 null，無法打開 AddDisplayAreaDeviceWindow。");
                return;
            }

            try
            {
                using var scope = App.Host.Services.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<AddDisplayAreaDeviceViewModel>>();

                var viewModel = new AddDisplayAreaDeviceViewModel(this, logger);
                var window = new SANJET.UI.Views.Windows.AddDisplayAreaDeviceWindow(viewModel)
                {
                    Owner = Application.Current.MainWindow,
                    Title = "添加展機區設備"
                };

                _logger.LogInformation("打開添加展機區設備對話框");
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打開添加展機區設備對話框時發生錯誤");
                MessageBox.Show($"無法打開對話框：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task SaveChangesToDeviceAsync(DeviceViewModel deviceVm)
        {
            if (deviceVm == null || _dbContext.Devices == null) return;

            var deviceInDb = await _dbContext.Devices.FindAsync(deviceVm.Id);
            if (deviceInDb != null)
            {
                deviceInDb.Name = deviceVm.Name;
                deviceInDb.IsOperational = deviceVm.IsOperational;
                deviceInDb.Status = deviceVm.IsOperational ? deviceVm.Status : IdleStatus;
                deviceInDb.Timestamp = DateTime.UtcNow;

                if (!deviceVm.IsOperational && deviceVm.Status != IdleStatus)
                {
                    deviceVm.Status = IdleStatus;
                }

                _dbContext.Devices.Update(deviceInDb);
                await _dbContext.SaveChangesAsync();



                deviceVm.OriginalName = deviceVm.Name;
                _logger.LogInformation("已保存設備 ID {DeviceId} 的變更。", deviceVm.Id);
            }
        }

        /// <summary>
        /// 在測試區添加新的 ESP32 RS485 設備
        /// </summary>
        /// <param name="deviceName">設備名稱</param>
        /// <param name="esp32MqttId">ESP32 的 MQTT ID (例如: ESP32_TEST_RS485)</param>
        /// <param name="slaveId">Modbus Slave ID (1-247, 測試區建議使用 100-110 避免衝突)</param>
        /// <returns>成功則回傳 true</returns>
        public async Task<bool> AddTestAreaDeviceAsync(string deviceName, string esp32MqttId, byte slaveId, int modbusDeviceIndex)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(esp32MqttId) || slaveId == 0)
            {
                _logger.LogWarning("添加測試區設備失敗：無效的參數。設備名稱: {Name}, ESP32 ID: {Esp32Id}, Slave ID: {SlaveId}",
                                   deviceName, esp32MqttId, slaveId);
                return false;
            }

            if (modbusDeviceIndex < ModbusAddressMapping.DefaultDeviceIndex || modbusDeviceIndex > ModbusAddressMapping.MaxTestAreaDeviceIndex)
            {
                _logger.LogWarning("添加測試區設備失敗：設備編號 {ModbusDeviceIndex} 超出 1-4 範圍。", modbusDeviceIndex);
                return false;
            }

            if (_dbContext.Devices == null)
            {
                _logger.LogError("數據庫上下文中 Devices DbSet 為 null，無法添加設備。");
                return false;
            }

            try
            {
                // 檢查是否已有相同的 ESP32 + Slave ID 組合
                var existingDevice = await _dbContext.Devices
                    .FirstOrDefaultAsync(d => d.ControllingEsp32MqttId == esp32MqttId &&
                                              d.SlaveId == slaveId &&
                                              d.Area == TestAreaName &&
                                              d.ModbusDeviceIndex == modbusDeviceIndex);

                if (existingDevice != null)
                {
                    _logger.LogWarning("已存在相同的測試區設備組合。ESP32: {Esp32Id}, Slave ID: {SlaveId}, 設備編號: {ModbusDeviceIndex}", esp32MqttId, slaveId, modbusDeviceIndex);
                    return false;
                }

                // 建立新設備
                var newDevice = new Device
                {
                    Name = deviceName,
                    ControllingEsp32MqttId = esp32MqttId,
                    SlaveId = slaveId,
                    Status = "閒置",
                    IsOperational = true,
                    RunCount = 0,
                    Timestamp = DateTime.UtcNow,
                    Area = TestAreaName,
                    ModbusDeviceIndex = modbusDeviceIndex
                };

                _dbContext.Devices.Add(newDevice);
                await _dbContext.SaveChangesAsync();

                // 重新載入設備列表以反映新增的設備
                await LoadDevicesAsync();

                _logger.LogInformation("成功添加測試區設備：名稱 {Name}, ESP32: {Esp32Id}, Slave ID: {SlaveId}, 設備編號: {ModbusDeviceIndex}",
                                       deviceName, esp32MqttId, slaveId, modbusDeviceIndex);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加測試區設備時發生錯誤。設備名稱: {Name}, ESP32: {Esp32Id}, Slave ID: {SlaveId}",
                                 deviceName, esp32MqttId, slaveId);
                return false;
            }
        }

        /// <summary>
        /// 在展機區添加新的 ESP32 RS485 設備
        /// </summary>
        /// <param name="deviceName">設備名稱</param>
        /// <param name="esp32MqttId">ESP32 的 MQTT ID (例如: ESP32_DISPLAY_RS485)</param>
        /// <param name="slaveId">Modbus Slave ID (1-247, 展機區建議使用 1-50 避免衝突)</param>
        /// <returns>成功則回傳 true</returns>
        public async Task<bool> AddDisplayAreaDeviceAsync(string deviceName, string esp32MqttId, byte slaveId)
        {
            if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(esp32MqttId) || slaveId == 0)
            {
                _logger.LogWarning("添加展機區設備失敗：無效的參數。設備名稱: {Name}, ESP32 ID: {Esp32Id}, Slave ID: {SlaveId}",
                                   deviceName, esp32MqttId, slaveId);
                return false;
            }

            if (_dbContext.Devices == null)
            {
                _logger.LogError("數據庫上下文中 Devices DbSet 為 null，無法添加設備。");
                return false;
            }

            try
            {
                // 檢查是否已有相同的 ESP32 + Slave ID 組合
                var existingDevice = await _dbContext.Devices
                    .FirstOrDefaultAsync(d => d.ControllingEsp32MqttId == esp32MqttId && d.SlaveId == slaveId);

                if (existingDevice != null)
                {
                    _logger.LogWarning("已存在相同的設備組合。ESP32: {Esp32Id}, Slave ID: {SlaveId}", esp32MqttId, slaveId);
                    return false;
                }

                // 建立新設備
                var newDevice = new Device
                {
                    Name = deviceName,
                    ControllingEsp32MqttId = esp32MqttId,
                    SlaveId = slaveId,
                    Status = "閒置",
                    IsOperational = true,
                    RunCount = 0,
                    Timestamp = DateTime.UtcNow,
                    Area = DisplayAreaName,
                    ModbusDeviceIndex = ModbusAddressMapping.DefaultDeviceIndex
                };

                _dbContext.Devices.Add(newDevice);
                await _dbContext.SaveChangesAsync();

                // 重新載入設備列表以反映新增的設備
                await LoadDevicesAsync();

                _logger.LogInformation("成功添加展機區設備：名稱 {Name}, ESP32: {Esp32Id}, Slave ID: {SlaveId}",
                                       deviceName, esp32MqttId, slaveId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加展機區設備時發生錯誤。設備名稱: {Name}, ESP32: {Esp32Id}, Slave ID: {SlaveId}",
                                 deviceName, esp32MqttId, slaveId);
                return false;
            }
        }

        /// <summary>
        /// 從目前設備清單和資料庫刪除指定設備。
        /// </summary>
        public async Task<bool> RemoveDeviceAsync(int deviceId)
        {
            if (_dbContext.Devices == null)
            {
                _logger.LogError("數據庫上下文中 Devices DbSet 為 null，無法刪除設備。");
                return false;
            }

            try
            {
                var deviceToRemove = await _dbContext.Devices.FindAsync(deviceId);
                if (deviceToRemove == null)
                {
                    _logger.LogWarning("找不到要刪除的設備，ID: {DeviceId}", deviceId);
                    return false;
                }

                _dbContext.Devices.Remove(deviceToRemove);
                await _dbContext.SaveChangesAsync();

                RemoveDeviceFromUiCollections(deviceId);

                _logger.LogInformation("成功刪除設備，ID: {DeviceId}, 名稱: {DeviceName}, 區域: {Area}",
                                       deviceId, deviceToRemove.Name, deviceToRemove.Area);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刪除設備時發生錯誤，ID: {DeviceId}", deviceId);
                return false;
            }
        }

        /// <summary>
        /// 從測試區移除設備。保留此方法供既有呼叫端使用。
        /// </summary>
        public Task<bool> RemoveTestAreaDeviceAsync(int deviceId)
        {
            return RemoveDeviceAsync(deviceId);
        }

        /// <summary>
        /// 從展機區移除設備。保留此方法供既有呼叫端使用。
        /// </summary>
        public Task<bool> RemoveDisplayAreaDeviceAsync(int deviceId)
        {
            return RemoveDeviceAsync(deviceId);
        }

        private void RemoveDeviceFromUiCollections(int deviceId)
        {
            var vmToRemove = Devices.FirstOrDefault(d => d.Id == deviceId);
            if (vmToRemove != null)
            {
                Devices.Remove(vmToRemove);
                DisplayAreaDevices.Remove(vmToRemove);
                TestAreaDevices.Remove(vmToRemove);
                SelectedAreaDevices.Remove(vmToRemove);
            }

            OnPropertyChanged(nameof(ShowSelectedAreaEmptyMessage));
        }

        // 重載 1: 由 MainViewModel 的 Modbus 讀取回應處理器呼叫 (輪詢更新)
        public void UpdateDeviceStatusFromMqtt(string esp32MqttIdFromResponse, byte slaveIdFromResponse, string newStatusTextFromDb, int newRunCountFromDb, string? contextMessage, ushort? responseAddress = null)
        {
            var deviceToUpdate = FindDeviceByModbusResponse(esp32MqttIdFromResponse, slaveIdFromResponse, responseAddress);

            if (deviceToUpdate != null && !deviceToUpdate.IsOperational)
            {
                if (deviceToUpdate.Status != IdleStatus)
                {
                    deviceToUpdate.Status = IdleStatus;
                }

                _logger.LogInformation("UI Update (Polling): Device '{DeviceName}' 未啟用，忽略輪詢回應並維持閒置狀態。ESP32: {Esp32Id}, Slave: {SlaveId}",
                                       deviceToUpdate.Name, esp32MqttIdFromResponse, slaveIdFromResponse);
                return;
            }

            if (deviceToUpdate != null)
            {
                string oldStatus = deviceToUpdate.Status;
                int oldRunCount = deviceToUpdate.RunCount;
                string finalStatus = newStatusTextFromDb;

                if (newStatusTextFromDb == "通訊失敗" && contextMessage != "資料已從 Modbus 更新" && !string.IsNullOrEmpty(contextMessage))
                {
                    finalStatus = $"{newStatusTextFromDb} ({contextMessage})";
                }

                bool statusChanged = deviceToUpdate.Status != finalStatus;
                bool runCountChanged = deviceToUpdate.RunCount != newRunCountFromDb;

                if (statusChanged)
                {
                    deviceToUpdate.Status = finalStatus;
                }
                if (runCountChanged)
                {
                    deviceToUpdate.RunCount = newRunCountFromDb;
                    _ = deviceToUpdate.HandleAutoStopOnTargetAsync();
                }

                if (statusChanged || runCountChanged)
                {
                    _logger.LogInformation("UI Update (Polling): Device '{DeviceName}' (ESP32: {Esp32Id}, Slave: {SlaveId}) updated. Status: '{OldStatus}' -> '{NewStatus}', RunCount: {OldRunCount} -> {NewRunCount}. Context: {Context}",
                                           deviceToUpdate.Name, esp32MqttIdFromResponse, slaveIdFromResponse, oldStatus, deviceToUpdate.Status, oldRunCount, deviceToUpdate.RunCount, contextMessage);
                }
            }
            else
            {
                _logger.LogWarning("UI Update (Polling): Device not found. ESP32: {Esp32Id}, Slave: {SlaveId}. Cannot update Status/RunCount from polling data.",
                                   esp32MqttIdFromResponse, slaveIdFromResponse);
            }
        }

        // 重載 2: 由 MainViewModel 的 Modbus 寫入回應處理器呼叫 (命令回饋)
        public void UpdateDeviceStatusFromMqtt(string esp32MqttIdFromResponse, byte slaveIdFromResponse, string responseStatus, string? responseMessage, ushort? responseAddress = null)
        {
            var deviceToUpdate = FindDeviceByModbusResponse(esp32MqttIdFromResponse, slaveIdFromResponse, responseAddress, preferPendingCommand: true);

            if (deviceToUpdate != null)
            {
                string oldStatus = deviceToUpdate.Status;
                string newUiStatus = oldStatus;

                if (responseStatus.ToLower().Contains("success") || responseStatus.ToLower().Contains("成功"))
                {
                    if (oldStatus.Contains("啟動中"))
                    {
                        newUiStatus = "運行中";
                    }
                    else if (oldStatus.Contains("停止中"))
                    {
                        newUiStatus = "閒置";
                    }
                    else
                    {
                        newUiStatus = $"命令執行成功 ({responseMessage ?? "操作成功"})";
                    }
                }
                else
                {
                    newUiStatus = $"命令執行失敗 ({responseMessage ?? responseStatus})";
                }

                if (oldStatus != newUiStatus)
                {
                    deviceToUpdate.Status = newUiStatus;
                    _logger.LogInformation("UI Update (Command): Device '{DeviceName}' (ESP32: {Esp32Id}, Slave: {SlaveId}) Status updated: '{OldStatus}' -> '{NewStatus}'. Response: {ResponseStatus} - {ResponseMessage}",
                                           deviceToUpdate.Name, esp32MqttIdFromResponse, slaveIdFromResponse, oldStatus, newUiStatus, responseStatus, responseMessage);
                }
            }
            else
            {
                _logger.LogWarning("UI Update (Command): Device not found. ESP32: {Esp32Id}, Slave: {SlaveId}. Cannot update status from command response.",
                                   esp32MqttIdFromResponse, slaveIdFromResponse);
            }
        }

        private DeviceViewModel? FindDeviceByModbusResponse(string esp32MqttId, byte slaveId, ushort? responseAddress, bool preferPendingCommand = false)
        {
            var candidates = Devices.Where(d =>
                d.ControllingEsp32MqttId == esp32MqttId &&
                d.SlaveId == slaveId);

            if (responseAddress.HasValue)
            {
                var address = responseAddress.Value;
                var addressMatchedDevice = candidates.FirstOrDefault(d =>
                {
                    var map = ModbusAddressMapping.GetMap(d.Area, d.ModbusDeviceIndex);
                    return address == map.StatusAddress ||
                           address == map.ControlAddress ||
                           address == map.RunCountAddress;
                });

                if (addressMatchedDevice != null)
                {
                    return addressMatchedDevice;
                }
            }

            if (preferPendingCommand)
            {
                var pendingDevice = candidates.FirstOrDefault(d => d.Status.Contains("啟動中") || d.Status.Contains("停止中"));
                if (pendingDevice != null)
                {
                    return pendingDevice;
                }
            }

            return candidates.FirstOrDefault();
        }
    }

    // DeviceViewModel 類別保持不變 (如先前提供)
    public partial class DeviceViewModel : ObservableObject
    {
        private const string IdleStatus = "閒置";

        private readonly HomeViewModel? _homeViewModel;
        private readonly MainViewModel? _mainViewModel;
        private readonly ILogger? _logger;
        private readonly IAudioService? _audioService;
        private readonly ILineNotificationService? _lineNotificationService;

        [ObservableProperty]
        private int id;

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string originalName = string.Empty;

        [ObservableProperty]
        private int slaveId;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        private string status = "閒置";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        private bool isOperational = true;

        [ObservableProperty]
        private int runCount;

        [ObservableProperty]
        private int? targetRunCount;

        [ObservableProperty]
        private bool autoStopOnTarget;

        [ObservableProperty]
        private bool isEditingName = false;

        [ObservableProperty]
        private string? controllingEsp32MqttId;

        [ObservableProperty]
        private string area = ModbusAddressMapping.DisplayAreaName;

        [ObservableProperty]
        private int modbusDeviceIndex = ModbusAddressMapping.DefaultDeviceIndex;

        private const ushort MODBUS_VALUE_START = 1;
        private const ushort MODBUS_VALUE_STOP = 0;

        [ObservableProperty]
        private bool isEditingRunCount = false; // 是否處於編輯 RunCount 的狀態

        [ObservableProperty]
        private string editingRunCountText = string.Empty; // 編輯時的 RunCount 文本

        [ObservableProperty]
        private string editingTargetRunCountText = string.Empty;

        [ObservableProperty]
        private bool isEditingTargetRunCount = false;

        [ObservableProperty]
        private bool isAutoStopping = false;

        public DeviceViewModel(HomeViewModel homeViewModel, MainViewModel? mainViewModel, ILogger? logger, IAudioService? audioService, ILineNotificationService? lineNotificationService)
        {
            _homeViewModel = homeViewModel;
            _mainViewModel = mainViewModel;
            _logger = logger;
            _audioService = audioService; // <--- 儲存服務
            _lineNotificationService = lineNotificationService;
        }

        public DeviceViewModel()
        {
            _homeViewModel = App.Host?.Services.GetService<HomeViewModel>();
            _mainViewModel = App.Host?.Services.GetService<MainViewModel>();
            _logger = App.Host?.Services.GetService<ILogger<DeviceViewModel>>();
            _audioService = App.Host?.Services.GetService<IAudioService>(); 
            _lineNotificationService = App.Host?.Services.GetService<ILineNotificationService>();
        }

        partial void OnIsOperationalChanged(bool value)
        {
            if (!value && Status != IdleStatus)
            {
                Status = IdleStatus;
            }

            if (_homeViewModel != null)
            {
                _ = _homeViewModel.SaveChangesToDeviceAsync(this);
            }
        }

        [RelayCommand]
        private void EditName()
        {
            OriginalName = Name;
            IsEditingName = true;
        }

        [RelayCommand]
        private async Task SaveNameAsync()
        {
            IsEditingName = false;
            if (_homeViewModel != null)
            {
                await _homeViewModel.SaveChangesToDeviceAsync(this);
            }
        }

        [RelayCommand]
        private void CancelEditName()
        {
            Name = OriginalName;
            IsEditingName = false;
        }

        [RelayCommand]
        private async Task DeleteAsync()
        {
            if (_homeViewModel == null)
            {
                MessageBox.Show("設備管理服務不可用，無法刪除設備。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var result = MessageBox.Show(
                $"確定要刪除設備 '{Name}' 嗎？\n\nESP32 ID：{ControllingEsp32MqttId ?? "未設定"}\n從站 ID：{SlaveId}\n\n此操作會從設備清單與資料庫中移除該設備。",
                "確認刪除設備",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var success = await _homeViewModel.RemoveDeviceAsync(Id);
            if (!success)
            {
                MessageBox.Show("刪除設備失敗，請檢查日誌取得詳細資訊。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanStart()
        {
            return IsOperational &&
                   !string.IsNullOrEmpty(ControllingEsp32MqttId) &&
                   (Status == "閒置" || Status.Contains("失敗") || Status.Contains("通訊失敗") || Status.Contains("錯誤") || Status == "操作成功" || Status.Contains("命令成功") || Status.Contains("命令執行成功"));
        }

        private bool CanStop()
        {
            return IsOperational &&
                   !string.IsNullOrEmpty(ControllingEsp32MqttId) &&
                   (Status == "運行中" || Status.Contains("啟動中"));
        }

        [RelayCommand(CanExecute = nameof(CanStart))]
        private async Task StartAsync()
        {
            if (_mainViewModel == null || string.IsNullOrEmpty(ControllingEsp32MqttId))
            {
                MessageBox.Show("通訊服務或目標 ESP32 ID 未設定。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (SlaveId <= 0)
            {
                MessageBox.Show("無效的 Slave ID。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Status = "啟動中...";
            bool success = await _mainViewModel.SendModbusWriteCommandAsync(
                ControllingEsp32MqttId,
                (byte)SlaveId,
                ModbusAddressMapping.GetMap(Area, ModbusDeviceIndex).ControlAddress,
                MODBUS_VALUE_START);

            if (success)
            {
                _logger?.LogInformation("已為 Slave ID {SlaveId} (由 ESP32 {Esp32Id}) 發送啟動命令。", SlaveId, ControllingEsp32MqttId);
                // ===== 在成功發送命令後，呼叫音訊服務 =====
                // 使用 _ = 來 "fire-and-forget"，不等待音訊播放完成，立即返回
                if (_audioService != null)
                {
                    _ = _audioService.PlayStartSoundAsync();
                }
            }
            else
            {
                Status = "啟動命令發送失敗";
            }
        }

        [RelayCommand(CanExecute = nameof(CanStop))]
        private async Task StopAsync()
        {
            if (_mainViewModel == null || string.IsNullOrEmpty(ControllingEsp32MqttId))
            {
                MessageBox.Show("通訊服務或目標 ESP32 ID 未設定。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (SlaveId <= 0)
            {
                MessageBox.Show("無效的 Slave ID。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Status = "停止中...";
            bool success = await _mainViewModel.SendModbusWriteCommandAsync(
                ControllingEsp32MqttId,
                (byte)SlaveId,
                ModbusAddressMapping.GetMap(Area, ModbusDeviceIndex).ControlAddress,
                MODBUS_VALUE_STOP);

            if (success)
            {
                _logger?.LogInformation("已為 Slave ID {SlaveId} (由 ESP32 {Esp32Id}) 發送停止命令。", SlaveId, ControllingEsp32MqttId);
                if (_audioService != null)
                {
                    _ = _audioService.PlayStopSoundAsync();
                }
            }
            else
            {
                Status = "停止命令發送失敗";
            }
        }

        [RelayCommand]
        private void EditRunCount()
        {
            EditingRunCountText = RunCount.ToString();
            IsEditingRunCount = true;
        }

        [RelayCommand]
        private async Task SaveRunCountAsync()
        {
            if (_mainViewModel == null || string.IsNullOrEmpty(ControllingEsp32MqttId))
            {
                MessageBox.Show("通訊服務或目標 ESP32 ID 未設定。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (SlaveId <= 0)
            {
                MessageBox.Show("無效的 Slave ID。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 驗證輸入
            if (!int.TryParse(EditingRunCountText, out int newRunCount) || newRunCount < 0)
            {
                MessageBox.Show("請輸入有效的非負整數。", "輸入錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 確認對話框
            var result = MessageBox.Show(
                $"確定要將設備 '{Name}' 的運轉次數設置為 {newRunCount} 嗎？\n\n目前運轉次數: {RunCount}",
                "確認設置運轉次數",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                IsEditingRunCount = false;
                return;
            }

            Status = "設置運轉次數中...";
            bool success = await _mainViewModel.SendModbusWriteRunCountCommandAsync(
                ControllingEsp32MqttId,
                (byte)SlaveId,
                newRunCount,
                ModbusAddressMapping.GetMap(Area, ModbusDeviceIndex).RunCountAddress);

            if (success)
            {
                _logger?.LogInformation("已為設備 '{DeviceName}' (Slave ID {SlaveId}, ESP32: {Esp32Id}) 發送運轉次數設置命令，目標值: {TargetRunCount}。", Name, SlaveId, ControllingEsp32MqttId, newRunCount);

                // 更新本地 UI
                RunCount = newRunCount;
                IsEditingRunCount = false;

                // 同時更新資料庫，避免後續輪詢時被舊值覆蓋
                if (_homeViewModel != null)
                {
                    try
                    {
                        var deviceInDb = await _homeViewModel.DbContext.Devices.FindAsync(Id);
                        if (deviceInDb != null)
                        {
                            deviceInDb.RunCount = newRunCount;
                            deviceInDb.Timestamp = DateTime.UtcNow;
                            await _homeViewModel.DbContext.SaveChangesAsync();
                            _logger?.LogInformation("資料庫已同步更新：設備 '{DeviceName}' (ID: {DeviceId}) 的運轉次數已設置為 {NewRunCount}。", Name, Id, newRunCount);
                            Status = "運轉次數已設置";
                        }
                    }
                    catch (Exception dbEx)
                    {
                        _logger?.LogError(dbEx, "更新資料庫運轉次數失敗，設備 '{DeviceName}' (ID: {DeviceId})。", Name, Id);
                        Status = "運轉次數已設置，但資料庫同步失敗";
                    }
                }
                else
                {
                    Status = "運轉次數已設置";
                }
            }
            else
            {
                Status = "設置運轉次數失敗";
                _logger?.LogError("無法為設備 '{DeviceName}' (Slave ID {SlaveId}) 設置運轉次數。", Name, SlaveId);
            }
        }

        [RelayCommand]
        private void CancelEditRunCount()
        {
            IsEditingRunCount = false;
            EditingRunCountText = string.Empty;
        }

        [RelayCommand]
        private void EditTargetRunCount()
        {
            EditingTargetRunCountText = TargetRunCount?.ToString() ?? string.Empty;
            IsEditingTargetRunCount = true;
        }

        [RelayCommand]
        private async Task SaveTargetRunCountAsync()
        {
            var text = EditingTargetRunCountText?.Trim() ?? string.Empty;
            int? newTarget = null;
            if (!string.IsNullOrEmpty(text))
            {
                if (!int.TryParse(text, out var parsed) || parsed < 0)
                {
                    MessageBox.Show("目標次數請輸入空白或非負整數。", "輸入錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                newTarget = parsed;
            }

            TargetRunCount = newTarget;
            AutoStopOnTarget = newTarget.HasValue;
            IsEditingTargetRunCount = false;
            await PersistTargetRunCountSettingsAsync();
            Status = AutoStopOnTarget ? $"已設定目標次數 {TargetRunCount}" : "已清除目標次數";
        }

        [RelayCommand]
        private void CancelEditTargetRunCount()
        {
            IsEditingTargetRunCount = false;
            EditingTargetRunCountText = string.Empty;
        }

        public async Task HandleAutoStopOnTargetAsync()
        {
            if (IsAutoStopping || !AutoStopOnTarget || !TargetRunCount.HasValue)
            {
                return;
            }

            if (RunCount < TargetRunCount.Value || !(Status == "運行中" || Status.Contains("啟動中")))
            {
                return;
            }

            IsAutoStopping = true;
            try
            {
                _logger?.LogInformation("設備 '{DeviceName}' 已達目標次數 {TargetRunCount}，自動觸發停止。", Name, TargetRunCount.Value);
                await StopAsync();
                AutoStopOnTarget = false;
                await PersistTargetRunCountSettingsAsync();

                if (_lineNotificationService != null && _lineNotificationService.IsConfigured)
                {
                    var message = $"✅ 設備自動停止通知\n\n設備：{Name}\nESP32：{ControllingEsp32MqttId ?? "未設定"}\nSlave：{SlaveId}\n目前次數：{RunCount}\n目標次數：{TargetRunCount}";
                    await _lineNotificationService.SendTextMessageAsync(message);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "設備 '{DeviceName}' 自動停止或 LINE 通知失敗。", Name);
            }
            finally
            {
                IsAutoStopping = false;
            }
        }

        private async Task PersistTargetRunCountSettingsAsync()
        {
            if (_homeViewModel?.DbContext?.Devices == null)
            {
                return;
            }

            var deviceInDb = await _homeViewModel.DbContext.Devices.FindAsync(Id);
            if (deviceInDb == null)
            {
                return;
            }

            deviceInDb.TargetRunCount = TargetRunCount;
            deviceInDb.AutoStopOnTarget = AutoStopOnTarget;
            deviceInDb.Timestamp = DateTime.UtcNow;
            await _homeViewModel.DbContext.SaveChangesAsync();
        }

        [RelayCommand]
        private void Record()
        {
            if (App.Host == null)
            {
                _logger?.LogError("無法開啟紀錄視窗，因為 App.Host 為 null。");
                return;
            }

            try
            {
                _logger?.LogInformation("為設備 {Name} (SlaveID: {SlaveId}) 開啟紀錄視窗。", Name, SlaveId);

                // **更正：建立一個獨立的 DI Scope 來管理服務的生命週期**
                using var scope = App.Host.Services.CreateScope();
                var provider = scope.ServiceProvider;

                // 從這個 scope 內解析服務
                var dbContext = provider.GetRequiredService<AppDbContext>();
                var recordLogger = provider.GetRequiredService<ILogger<RecordViewModel>>();
                var authService = provider.GetRequiredService<IAuthenticationService>();


                var currentUser = authService.GetCurrentUser()?.Username ?? "未知使用者";

                // 1. 建立 RecordViewModel，並傳入同步服務
                var recordViewModel = new RecordViewModel(this, dbContext, recordLogger, currentUser);

                // 2. 建立 RecordWindow
                var recordWindow = new RecordWindow(recordViewModel)
                {
                    Owner = Application.Current.MainWindow,
                    Title = $"{this.Name} - 設備紀錄"
                };

                // 3. 顯示視窗 (在此期間，scope 內的服務會保持存活)
                recordWindow.ShowDialog();
                // 當視窗關閉後，using 區塊結束，scope 和裡面的服務 (如 dbContext) 會被自動釋放
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "開啟紀錄視窗時發生錯誤。");
                MessageBox.Show($"無法開啟紀錄視窗: {ex.Message}", "嚴重錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
