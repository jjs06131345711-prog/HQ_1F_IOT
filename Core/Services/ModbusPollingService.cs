
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SANJET.Core.Interfaces; // 新增
using SANJET.Core.ViewModels;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SANJET.Core;
using Microsoft.EntityFrameworkCore;
using SANJET.Core.Constants;

namespace SANJET.Core.Services
{
    public class ModbusPollingService : BackgroundService
    {
        private readonly ILogger<ModbusPollingService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IPollingStateService _pollingStateService; // 新增
        private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(3);//--輪巡時間--// 預設為 3 秒
        private readonly ManualResetEventSlim _pollingSignal = new ManualResetEventSlim(false); // 新增，初始為未發信號

        public ModbusPollingService(ILogger<ModbusPollingService> logger,
                                    IServiceProvider serviceProvider,
                                    IPollingStateService pollingStateService) // 新增注入
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _pollingStateService = pollingStateService; // 儲存注入的服務

            // 訂閱狀態變更事件
            _pollingStateService.PollingStateChanged += OnPollingStateChanged;
            // 設定初始信號狀態
            OnPollingStateChanged();
        }

        private void OnPollingStateChanged()
        {
            if (_pollingStateService.IsPollingEnabled)
            {
                _pollingSignal.Set(); // 發信號，允許輪詢執行
                _logger.LogInformation("Modbus輪詢服務：收到啟用輪詢的信號。");
            }
            else
            {
                _pollingSignal.Reset(); // 重置信號，暫停輪詢
                _logger.LogInformation("Modbus輪詢服務：收到禁用輪詢的信號。");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Modbus輪詢服務已啟動，等待啟用信號...");

            // 註冊取消操作，以便在服務停止時解除 _pollingSignal 的等待
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("Modbus輪詢服務：收到停止請求，解除輪詢信號等待。");
                _pollingSignal.Set(); // 確保 WaitAsync 可以被解除阻塞以優雅關閉
            });

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogTrace("Modbus輪詢服務：等待輪詢啟用信號或取消請求...");
                    // await _pollingSignal.WaitAsync(stoppingToken); // 舊的錯誤行
                    // 新的修改：使用 Task.Run 配合同步的 Wait 方法
                    await Task.Run(() =>
                    {
                        try
                        {
                            _pollingSignal.Wait(stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // 當 stoppingToken 被取消時，_pollingSignal.Wait(stoppingToken) 會拋出此異常
                            // Task.Run 會捕獲它並使 Task 進入 Canceled 狀態
                            _logger.LogInformation("Modbus輪詢服務：_pollingSignal.Wait 被取消。");
                            // 重新拋出以確保外部的 await Task.Run(...) 能正確處理取消
                            throw;
                        }
                    }, stoppingToken);

                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Modbus輪詢服務：在等待後檢測到取消請求，退出循環。");
                        break;
                    }

                    // 此時 _pollingSignal 被設定，表示輪詢應該是活動的
                    _logger.LogInformation("Modbus輪詢服務：輪詢週期開始於: {time}", DateTimeOffset.Now);

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var mainViewModel = scope.ServiceProvider.GetRequiredService<MainViewModel>();

                        var inactiveDevicesWithStaleStatus = await dbContext.Devices
                            .Where(d => !d.IsOperational && d.Status != "閒置")
                            .ToListAsync(stoppingToken);

                        if (inactiveDevicesWithStaleStatus.Any())
                        {
                            foreach (var inactiveDevice in inactiveDevicesWithStaleStatus)
                            {
                                inactiveDevice.Status = "閒置";
                                inactiveDevice.Timestamp = DateTime.UtcNow;
                            }

                            await dbContext.SaveChangesAsync(stoppingToken);
                            _logger.LogInformation("Modbus輪詢服務：已將 {Count} 個未啟用設備的資料庫狀態同步為閒置。", inactiveDevicesWithStaleStatus.Count);
                        }

                        var devicesToPoll = await dbContext.Devices
                            .Where(d => !string.IsNullOrEmpty(d.ControllingEsp32MqttId) && d.IsOperational)
                            .OrderBy(d => d.SlaveId)
                            .ThenBy(d => d.ModbusDeviceIndex)
                            .ThenBy(d => d.Id)
                            .ToListAsync(stoppingToken);

                        if (!devicesToPoll.Any())
                        {
                            _logger.LogInformation("Modbus輪詢服務：此週期無設備配置為 Modbus 輪詢。");
                            // 不需要 continue，直接進入下一個 Task.Delay
                        }
                        else
                        {
                            foreach (var device in devicesToPoll)
                            {
                                if (stoppingToken.IsCancellationRequested) break;

                                if (string.IsNullOrEmpty(device.ControllingEsp32MqttId))
                                {
                                    _logger.LogWarning("設備 ID {DbDeviceId} (Slave {SlaveId}) 缺少 ControllingEsp32MqttId，跳過輪詢。", device.Id, device.SlaveId);
                                    continue;
                                }

                                var addressMap = ModbusAddressMapping.GetMap(device.Area, device.ModbusDeviceIndex);

                                _logger.LogInformation("輪詢狀態 - ESP32: {Esp32Id}, Slave: {SlaveId}, 區域: {Area}, 設備編號: {ModbusDeviceIndex}, 地址: {Address}",
                                    device.ControllingEsp32MqttId, device.SlaveId, device.Area, device.ModbusDeviceIndex, addressMap.StatusAddress);
                                await mainViewModel.SendModbusReadCommandAsync(
                                    device.ControllingEsp32MqttId,
                                    (byte)device.SlaveId,
                                    addressMap.StatusAddress,
                                    1, addressMap.FunctionCode
                                );

                                //單筆輪尋間隔時間
                                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);

                                if (stoppingToken.IsCancellationRequested) break;

                                _logger.LogInformation("輪詢運轉次數 - ESP32: {Esp32Id}, Slave: {SlaveId}, 區域: {Area}, 設備編號: {ModbusDeviceIndex}, 地址: {Address}",
                                    device.ControllingEsp32MqttId, device.SlaveId, device.Area, device.ModbusDeviceIndex, addressMap.RunCountAddress);
                                await mainViewModel.SendModbusReadCommandAsync(
                                    device.ControllingEsp32MqttId,
                                    (byte)device.SlaveId,
                                    addressMap.RunCountAddress,
                                    addressMap.RunCountRegisterQuantity, addressMap.FunctionCode
                                );
                                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                            }
                        }
                    }

                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Modbus輪詢服務：輪詢週期後檢測到取消請求。");
                        break;
                    }
                    _logger.LogDebug("Modbus輪詢服務：輪詢週期完成，延遲 {PollingInterval} 後開始下一個週期。", _pollingInterval);
                    await Task.Delay(_pollingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Modbus輪詢服務：ExecuteAsync 循環被取消。");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Modbus輪詢服務執行週期中發生錯誤。");
                    // 發生錯誤後，仍會等待 _pollingInterval，除非服務被停止
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(_pollingInterval, stoppingToken); // 錯誤後也延遲，避免快速連續失敗
                    }
                }
            }

            _pollingStateService.PollingStateChanged -= OnPollingStateChanged; // 取消訂閱事件
            _logger.LogInformation("Modbus輪詢服務已停止。");
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Modbus輪詢服務正在停止 (StopAsync)。");
            _pollingSignal.Set(); // 確保 ExecuteAsync 中的 WaitAsync 可以解除
            return base.StopAsync(cancellationToken);
        }
    }
}