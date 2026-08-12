using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SANJET.Core.Configuration;
using SANJET.Core.Interfaces;
using SANJET.Core.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SANJET.Core.Services
{
    /// <summary>
    /// 排程自動停止服務：依使用者設定的多組每日時間，時間到時自動觸發「全部停止」。
    /// 只在程式執行期間生效；程式關閉期間錯過的時間不會補觸發。
    /// </summary>
    public class ScheduledStopService : BackgroundService
    {
        /// <summary>檢查間隔；排程以分鐘為單位，20 秒足以在同一分鐘內命中。</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(20);

        private readonly ILogger<ScheduledStopService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ScheduledStopOptions _options;

        /// <summary>上一次檢查的本機時間；用來判斷「這段期間內是否跨過某個排定時間點」。</summary>
        private DateTime _lastCheckedLocalTime;

        public ScheduledStopService(
            ILogger<ScheduledStopService> logger,
            IServiceProvider serviceProvider,
            ScheduledStopOptions options)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _options = options;
        }

        /// <summary>
        /// 服務主迴圈：固定間隔檢查是否有排定時間點落在上次檢查與現在之間，有的話就觸發全部停止。
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _lastCheckedLocalTime = DateTime.Now;
            _logger.LogInformation("排程自動停止服務已啟動。啟用狀態: {Enabled}, 已設定排程: {Schedules}",
                                   _options.Enabled,
                                   FormatSchedules(_options.GetEnabledSchedules()));

            using var timer = new PeriodicTimer(CheckInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        await CheckAndTriggerAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // 單次檢查失敗不應讓整個排程服務停擺。
                        _logger.LogError(ex, "排程自動停止服務：檢查排程時發生錯誤。");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("排程自動停止服務：收到停止請求，結束檢查迴圈。");
            }
        }

        /// <summary>
        /// 檢查目前是否有排程到點，並在到點時觸發全部停止。
        /// </summary>
        private async Task CheckAndTriggerAsync(CancellationToken cancellationToken)
        {
            var now = DateTime.Now;

            // 系統時間被往回調整（或校時）時，直接重設基準點，避免重複觸發或整段時間被跳過。
            if (now < _lastCheckedLocalTime)
            {
                _logger.LogWarning("排程自動停止服務：偵測到系統時間往回調整，重設排程檢查基準點。");
                _lastCheckedLocalTime = now;
                return;
            }

            var dueSchedules = _options.GetDueSchedules(_lastCheckedLocalTime, now);
            _lastCheckedLocalTime = now;

            if (dueSchedules.Count == 0)
            {
                return;
            }

            _logger.LogInformation("排程自動停止服務：排程 {Schedules} 已到，開始觸發停止。", FormatSchedules(dueSchedules));

            // 同一次檢查可能同時有多筆排程到點（例如休眠後補觸發，或不同區域設在同一時間）。
            // 只要其中一筆是「全部」，就一次停止所有區域即可，不需要再分區域重複發送。
            var hasAllAreaSchedule = dueSchedules.Any(schedule => schedule.Area == ScheduledStopAreaScope.All);
            var areas = hasAllAreaSchedule
                ? new List<ScheduledStopAreaScope> { ScheduledStopAreaScope.All }
                : dueSchedules.Select(schedule => schedule.Area).Distinct().ToList();

            foreach (var area in areas)
            {
                // 描述文字只列出屬於這個區域的時間點（收斂成「全部」時則列出所有到點時間），方便日誌與通知對照。
                var timesText = FormatTimes(dueSchedules
                    .Where(schedule => hasAllAreaSchedule || schedule.Area == area)
                    .Select(schedule => schedule.Time));

                await TriggerStopAllAsync(area, timesText, cancellationToken);
            }
        }

        /// <summary>
        /// 切換到 UI 執行緒執行全部停止（設備 ViewModel 綁定在 UI 上，必須在 UI 執行緒操作），
        /// 完成後視設定發送 LINE 通知。
        /// </summary>
        /// <param name="area">要停止的區域範圍。</param>
        /// <param name="timesText">觸發的排程時間文字，用於日誌與通知。</param>
        private async Task TriggerStopAllAsync(ScheduledStopAreaScope area, string timesText, CancellationToken cancellationToken)
        {
            var areaText = ScheduledStopOptions.FormatArea(area);
            var triggerDescription = $"{timesText} / {areaText}";

            var homeViewModel = _serviceProvider.GetService<HomeViewModel>();
            if (homeViewModel == null)
            {
                _logger.LogError("排程自動停止服務：無法取得 HomeViewModel，略過這次觸發（{Trigger}）。", triggerDescription);
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _logger.LogWarning("排程自動停止服務：應用程式尚未就緒（Dispatcher 不存在），略過這次觸發（{Trigger}）。", triggerDescription);
                return;
            }

            var (total, succeeded) = await dispatcher
                .InvokeAsync(() => homeViewModel.StopAllDevicesForScheduleAsync(triggerDescription, area))
                .Task
                .Unwrap();

            if (total == 0)
            {
                return;
            }

            await SendLineNotificationAsync(timesText, areaText, total, succeeded, cancellationToken);
        }

        /// <summary>
        /// 發送排程停止結果的 LINE 通知；未啟用通知或 LINE 尚未設定時直接略過。
        /// </summary>
        private async Task SendLineNotificationAsync(string timesText, string areaText, int total, int succeeded, CancellationToken cancellationToken)
        {
            if (!_options.NotifyByLine)
            {
                return;
            }

            var lineNotificationService = _serviceProvider.GetService<ILineNotificationService>();
            if (lineNotificationService == null || !lineNotificationService.IsConfigured)
            {
                return;
            }

            try
            {
                var failed = total - succeeded;
                var title = failed > 0 ? "⚠️ 排程自動停止通知" : "🛑 排程自動停止通知";
                var message =
                    $"{title}\n\n" +
                    $"排程時間：{timesText}\n" +
                    $"停止區域：{areaText}\n" +
                    $"觸發時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"運轉中設備：{total} 台\n" +
                    $"已送出停止命令：{succeeded} 台\n" +
                    (failed > 0
                        ? $"發送失敗：{failed} 台，請至系統確認並手動停止！"
                        : "（設備實際停止狀態請至系統確認）");

                await lineNotificationService.SendTextMessageAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "排程自動停止服務：發送 LINE 通知失敗（{Times} / {Area}）。", timesText, areaText);
            }
        }

        /// <summary>
        /// 將時間清單格式化成方便閱讀的字串，例如 "12:00、18:00"。
        /// </summary>
        private static string FormatTimes(IEnumerable<TimeSpan> times)
        {
            var text = string.Join("、", times.Select(ScheduledStopOptions.FormatTime).Distinct());
            return string.IsNullOrEmpty(text) ? "（無）" : text;
        }

        /// <summary>
        /// 將排程清單格式化成方便閱讀的字串，例如 "12:00(全部)、18:00(測試區)"。
        /// </summary>
        private static string FormatSchedules(IEnumerable<ScheduledStopOccurrence> schedules)
        {
            var text = string.Join("、", schedules.Select(schedule =>
                $"{ScheduledStopOptions.FormatTime(schedule.Time)}({ScheduledStopOptions.FormatArea(schedule.Area)})"));
            return string.IsNullOrEmpty(text) ? "（無）" : text;
        }
    }
}
