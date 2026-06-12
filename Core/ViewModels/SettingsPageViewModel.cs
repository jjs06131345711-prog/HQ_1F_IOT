
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SANJET.Core.Configuration;
using SANJET.Core.Interfaces;
using System;
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

        public SettingsPageViewModel(
            ILogger<SettingsPageViewModel> logger,
            IDatabaseManagementService dbManagementService,
            LineAutoHotkeyOptions lineAutoHotkeyOptions)
        {
            _logger = logger;
            _dbManagementService = dbManagementService;
            _lineAutoHotkeyOptions = lineAutoHotkeyOptions;
            _rtspSettingsPath = Path.Combine(AppContext.BaseDirectory, "rtsp.settings.json");
            _logger.LogInformation("SettingsViewModel 已初始化。");
        }

        public void LoadSettings()
        {
            _logger.LogInformation("正在加載設定值...");

            // 從執行中的單例載入 LINE 通知設定（啟動時已套用使用者保存的覆寫值）。
            LineNotifyEnabled = _lineAutoHotkeyOptions.Enabled;
            LineTargetChatNames = string.Join(Environment.NewLine, _lineAutoHotkeyOptions.TargetChatNames ?? Array.Empty<string>());

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
