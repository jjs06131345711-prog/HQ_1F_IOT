
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SANJET.Core.Configuration;
using SANJET.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace SANJET.Core.ViewModels
{
    public partial class SettingsPageViewModel : ObservableObject
    {
        private readonly ILogger<SettingsPageViewModel> _logger;
        private readonly IDatabaseManagementService _dbManagementService;
        private readonly LineAutoHotkeyOptions _lineAutoHotkeyOptions;
        private readonly ScheduledStopOptions _scheduledStopOptions;
        private readonly string _rtspSettingsPath;
        private Action? _autoStartStreamAction;

        [ObservableProperty]
        private string _pageTitle = "應用程式設定";

        [ObservableProperty]
        private string _rtspIpAddress = "192.168.70.90";

        [ObservableProperty]
        private string _rtspUsername = "SANJET";

        [ObservableProperty]
        private string _rtspPassword = "Sanjet25653819";

        [ObservableProperty]
        private int _rtspPort = 554;

        [ObservableProperty]
        private string _rtspStreamPath = "stream1";

        [ObservableProperty]
        private string _rtspIpAddress2 = "192.168.70.91";

        [ObservableProperty]
        private string _rtspUsername2 = "SANJET";

        [ObservableProperty]
        private string _rtspPassword2 = "Sanjet25653819";

        [ObservableProperty]
        private int _rtspPort2 = 554;

        [ObservableProperty]
        private string _rtspStreamPath2 = "stream1";

        // LINE 通知設定（對應 LineAutoHotkeyOptions 的 Enabled 與 TargetChatNames）
        [ObservableProperty]
        private bool _lineNotifyEnabled;

        // 多行文字，每行一個聊天室名稱
        [ObservableProperty]
        private string _lineTargetChatNames = string.Empty;

        // 排程自動停止設定（對應 ScheduledStopOptions）
        [ObservableProperty]
        private bool _scheduledStopEnabled;

        [ObservableProperty]
        private bool _scheduledStopNotifyByLine = true;

        /// <summary>排程自動停止的時間清單，可由使用者新增/刪除多組。</summary>
        public ObservableCollection<ScheduledStopTimeViewModel> ScheduledStopTimes { get; } = new();

        public SettingsPageViewModel(
            ILogger<SettingsPageViewModel> logger,
            IDatabaseManagementService dbManagementService,
            LineAutoHotkeyOptions lineAutoHotkeyOptions,
            ScheduledStopOptions scheduledStopOptions)
        {
            _logger = logger;
            _dbManagementService = dbManagementService;
            _lineAutoHotkeyOptions = lineAutoHotkeyOptions;
            _scheduledStopOptions = scheduledStopOptions;
            _rtspSettingsPath = Path.Combine(AppContext.BaseDirectory, "rtsp.settings.json");
            _logger.LogInformation("SettingsViewModel 已初始化。");
        }

        public void LoadSettings()
        {
            _logger.LogInformation("正在加載設定值...");

            // 從執行中的單例載入 LINE 通知設定（啟動時已套用使用者保存的覆寫值）。
            LineNotifyEnabled = _lineAutoHotkeyOptions.Enabled;
            LineTargetChatNames = string.Join(Environment.NewLine, _lineAutoHotkeyOptions.TargetChatNames ?? Array.Empty<string>());

            LoadScheduledStopSettings();

            try
            {
                if (!File.Exists(_rtspSettingsPath))
                {
                    _logger.LogInformation("RTSP 設定檔不存在，使用預設值。路徑: {Path}", _rtspSettingsPath);
                    return;
                }

                var json = File.ReadAllText(_rtspSettingsPath);
                var settings = JsonSerializer.Deserialize<RtspSettingsModel>(json);

                if (settings == null)
                {
                    _logger.LogWarning("RTSP 設定檔格式錯誤，使用預設值。路徑: {Path}", _rtspSettingsPath);
                    return;
                }

                // 攝像頭 1
                RtspIpAddress = settings.IpAddress;
                RtspUsername = settings.Username;
                RtspPassword = settings.Password;
                RtspPort = settings.Port;
                RtspStreamPath = settings.StreamPath;

                // 攝像頭 2
                RtspIpAddress2 = settings.IpAddress2 ?? "192.168.70.91";
                RtspUsername2 = settings.Username2 ?? "SANJET";
                RtspPassword2 = settings.Password2 ?? "Sanjet25653819";
                RtspPort2 = settings.Port2 ?? 554;
                RtspStreamPath2 = settings.StreamPath2 ?? "stream1";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加載 RTSP 設定失敗。路徑: {Path}", _rtspSettingsPath);
                MessageBox.Show($"加載 RTSP 設定失敗：{ex.Message}", "設定錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public string BuildRtspUrl()
        {
            var ip = RtspIpAddress?.Trim();
            var user = RtspUsername?.Trim();
            var pass = RtspPassword ?? string.Empty;
            var streamPath = (RtspStreamPath ?? string.Empty).Trim().TrimStart('/');

            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new InvalidOperationException("請先設定 RTSP IP 位址。");
            }

            var authPart = string.IsNullOrWhiteSpace(user)
                ? string.Empty
                : $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@";

            return string.IsNullOrWhiteSpace(streamPath)
                ? $"rtsp://{authPart}{ip}:{RtspPort}"
                : $"rtsp://{authPart}{ip}:{RtspPort}/{streamPath}";
        }

        public string BuildRtspUrl1()
        {
            return BuildRtspUrl();
        }

        public string BuildRtspUrl2()
        {
            var ip = RtspIpAddress2?.Trim();
            var user = RtspUsername2?.Trim();
            var pass = RtspPassword2 ?? string.Empty;
            var streamPath = (RtspStreamPath2 ?? string.Empty).Trim().TrimStart('/');

            if (string.IsNullOrWhiteSpace(ip))
            {
                throw new InvalidOperationException("請先設定攝像頭 2 的 RTSP IP 位址。");
            }

            var authPart = string.IsNullOrWhiteSpace(user)
                ? string.Empty
                : $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@";

            return string.IsNullOrWhiteSpace(streamPath)
                ? $"rtsp://{authPart}{ip}:{RtspPort2}"
                : $"rtsp://{authPart}{ip}:{RtspPort2}/{streamPath}";
        }

        [RelayCommand]
        private void SaveRtspSettings()
        {
            try
            {
                var settings = new RtspSettingsModel
                {
                    IpAddress = RtspIpAddress.Trim(),
                    Username = RtspUsername.Trim(),
                    Password = RtspPassword,
                    Port = RtspPort,
                    StreamPath = RtspStreamPath.Trim().TrimStart('/'),
                    IpAddress2 = RtspIpAddress2.Trim(),
                    Username2 = RtspUsername2.Trim(),
                    Password2 = RtspPassword2,
                    Port2 = RtspPort2,
                    StreamPath2 = RtspStreamPath2.Trim().TrimStart('/')
                };

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_rtspSettingsPath, json);

                _logger.LogInformation("RTSP 設定已儲存。路徑: {Path}", _rtspSettingsPath);
                MessageBox.Show("RTSP 設定已儲存。", "儲存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "儲存 RTSP 設定失敗。路徑: {Path}", _rtspSettingsPath);
                MessageBox.Show($"儲存 RTSP 設定失敗：{ex.Message}", "儲存失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void SaveLineSettings()
        {
            try
            {
                var chatNames = (LineTargetChatNames ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => name.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // 立即套用到執行中的單例，讓通知服務下次發送即生效。
                _lineAutoHotkeyOptions.Enabled = LineNotifyEnabled;
                _lineAutoHotkeyOptions.TargetChatNames = chatNames;

                // 持久化，重啟後保留。
                LineAutoHotkeySettingsStore.Save(new LineAutoHotkeyUserSettings
                {
                    Enabled = LineNotifyEnabled,
                    TargetChatNames = chatNames
                });

                // 正規化顯示（移除空行與重複）。
                LineTargetChatNames = string.Join(Environment.NewLine, chatNames);

                _logger.LogInformation("LINE 通知設定已儲存。Enabled: {Enabled}, 目標聊天室數: {Count}", LineNotifyEnabled, chatNames.Length);
                MessageBox.Show("LINE 通知設定已儲存。", "儲存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "儲存 LINE 通知設定失敗。");
                MessageBox.Show($"儲存 LINE 通知設定失敗：{ex.Message}", "儲存失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 從執行中的 ScheduledStopOptions 單例載入排程自動停止設定到畫面。
        /// </summary>
        private void LoadScheduledStopSettings()
        {
            ScheduledStopEnabled = _scheduledStopOptions.Enabled;
            ScheduledStopNotifyByLine = _scheduledStopOptions.NotifyByLine;

            ScheduledStopTimes.Clear();
            foreach (var item in _scheduledStopOptions.Times ?? new List<ScheduledStopTime>())
            {
                if (item == null)
                {
                    continue;
                }

                // 區域字串一律正規化，避免設定檔中的別名（例如 "展示區"）直接顯示在下拉選單上而選不到。
                var areaName = ScheduledStopOptions.FormatArea(ScheduledStopOptions.ParseArea(item.Area));
                ScheduledStopTimes.Add(new ScheduledStopTimeViewModel(item.Time ?? string.Empty, areaName, item.Enabled));
            }
        }

        /// <summary>
        /// 新增一組排程時間；預設帶入 18:00 且停止全部區域，使用者可再修改。
        /// </summary>
        [RelayCommand]
        private void AddScheduledStopTime()
        {
            ScheduledStopTimes.Add(new ScheduledStopTimeViewModel("18:00", ScheduledStopOptions.AllAreaName, true));
        }

        /// <summary>
        /// 刪除指定的排程時間列。
        /// </summary>
        /// <param name="item">要刪除的時間列（由清單的刪除按鈕傳入）。</param>
        [RelayCommand]
        private void RemoveScheduledStopTime(ScheduledStopTimeViewModel? item)
        {
            if (item == null)
            {
                return;
            }

            ScheduledStopTimes.Remove(item);
        }

        /// <summary>
        /// 驗證並儲存排程自動停止設定：立即套用到執行中的單例，並持久化到使用者設定檔。
        /// </summary>
        [RelayCommand]
        private void SaveScheduledStopSettings()
        {
            try
            {
                // 步驟 1：逐列驗證時間格式，任何一列有誤就中止儲存並提示使用者。
                var parsedTimes = new List<ScheduledStopTime>();
                foreach (var item in ScheduledStopTimes)
                {
                    if (!ScheduledStopOptions.TryParseTime(item.Time, out var parsed))
                    {
                        MessageBox.Show($"時間格式錯誤：「{item.Time}」\n\n請使用 24 小時制的 HH:mm 格式，例如 18:00。",
                                        "輸入錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    parsedTimes.Add(new ScheduledStopTime
                    {
                        Time = ScheduledStopOptions.FormatTime(parsed),
                        Area = ScheduledStopOptions.FormatArea(ScheduledStopOptions.ParseArea(item.Area)),
                        Enabled = item.IsEnabled
                    });
                }

                // 步驟 2：相同「時間 + 區域」只保留一筆（啟用優先），避免重複設定造成混淆。
                var normalizedTimes = parsedTimes
                    .GroupBy(item => (item.Time, item.Area))
                    .Select(group => new ScheduledStopTime
                    {
                        Time = group.Key.Time,
                        Area = group.Key.Area,
                        Enabled = group.Any(item => item.Enabled)
                    })
                    .OrderBy(item => item.Time, StringComparer.Ordinal)
                    .ThenBy(item => item.Area, StringComparer.Ordinal)
                    .ToList();

                // 步驟 3：整包替換 Times 參考（不就地增刪），背景排程服務讀取時才不會撞到集合被同時修改。
                _scheduledStopOptions.Times = normalizedTimes;
                _scheduledStopOptions.Enabled = ScheduledStopEnabled;
                _scheduledStopOptions.NotifyByLine = ScheduledStopNotifyByLine;

                // 步驟 4：持久化，重啟後保留。
                ScheduledStopSettingsStore.Save(new ScheduledStopUserSettings
                {
                    Enabled = ScheduledStopEnabled,
                    NotifyByLine = ScheduledStopNotifyByLine,
                    Times = normalizedTimes
                });

                // 步驟 5：把正規化後的結果回寫畫面（統一格式、移除重複）。
                ScheduledStopTimes.Clear();
                foreach (var item in normalizedTimes)
                {
                    ScheduledStopTimes.Add(new ScheduledStopTimeViewModel(item.Time, item.Area, item.Enabled));
                }

                var enabledSchedulesText = string.Join("、", normalizedTimes
                    .Where(t => t.Enabled)
                    .Select(t => $"{t.Time}（{t.Area}）"));

                _logger.LogInformation("排程自動停止設定已儲存。Enabled: {Enabled}, 啟用中的排程: {Schedules}",
                                       ScheduledStopEnabled,
                                       string.IsNullOrEmpty(enabledSchedulesText) ? "（無）" : enabledSchedulesText);

                MessageBox.Show(
                    ScheduledStopEnabled && normalizedTimes.Any(t => t.Enabled)
                        ? $"排程自動停止設定已儲存。\n\n將於每天 {enabledSchedulesText} 自動停止對應區域中運轉中的設備。"
                        : "排程自動停止設定已儲存。（目前未啟用任何排程時間）",
                    "儲存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "儲存排程自動停止設定失敗。");
                MessageBox.Show($"儲存排程自動停止設定失敗：{ex.Message}", "儲存失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void SetAutoStartStreamAction(Action action)
        {
            _autoStartStreamAction = action;
        }

        [RelayCommand]
        private async Task BackupDatabaseAsync()
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "資料庫備份檔案 (*.db)|*.db|所有檔案 (*.*)|*.*",
                Title = "選擇備份路徑",
                FileName = $"SNAJET_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                string destinationPath = saveFileDialog.FileName;
                _logger.LogInformation("使用者選擇備份路徑: {Path}", destinationPath);
                bool success = await _dbManagementService.BackupDatabaseAsync(destinationPath);
                if (success)
                {
                    MessageBox.Show($"資料庫已成功備份至:\n{destinationPath}", "備份成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                // 失敗的訊息由服務層顯示
            }
        }

        [RelayCommand]
        private async Task RestoreDatabaseAsync()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "資料庫備份檔案 (*.db)|*.db|所有檔案 (*.*)|*.*",
                Title = "選擇要還原的備份檔案"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string sourcePath = openFileDialog.FileName;
                var result = MessageBox.Show(
                    "警告：此操作將會用選擇的備份檔案覆蓋目前的資料庫。\n\n所有未備份的變更都將遺失，且應用程式將會重新啟動。\n\n確定要繼續嗎？",
                    "確認還原",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _logger.LogInformation("使用者確認從 '{Path}' 還原資料庫。", sourcePath);
                    await _dbManagementService.RestoreDatabaseAsync(sourcePath);
                    // 成功還原後，服務會處理重啟邏輯，此處不需再做操作
                }
            }
        }

        private class RtspSettingsModel
        {
            public string IpAddress { get; set; } = "192.168.70.90";
            public string Username { get; set; } = "SANJET";
            public string Password { get; set; } = "Sanjet25653819";
            public int Port { get; set; } = 554;
            public string StreamPath { get; set; } = "stream1";
            public string IpAddress2 { get; set; } = "192.168.70.91";
            public string Username2 { get; set; } = "SANJET";
            public string Password2 { get; set; } = "Sanjet25653819";
            public int? Port2 { get; set; } = 554;
            public string StreamPath2 { get; set; } = "stream1";
        }
    }
}
