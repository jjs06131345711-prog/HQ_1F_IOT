using System;
using System.IO;
using System.Text.Json;

namespace SANJET.Core.Configuration
{
    /// <summary>
    /// 使用者可覆寫的 LINE AutoHotkey 通知設定，目前只保存 Enabled 與 TargetChatNames。
    /// 此設定會覆蓋 appsettings.json 的預設值，並保存於執行目錄下的 JSON 檔案。
    /// </summary>
    public class LineAutoHotkeyUserSettings
    {
        public bool Enabled { get; set; }
        public string[] TargetChatNames { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// 負責讀寫 LINE 通知的使用者設定，並可將設定套用到 <see cref="LineAutoHotkeyOptions"/>。
    /// </summary>
    public static class LineAutoHotkeySettingsStore
    {
        private static string FilePath =>
            Path.Combine(AppContext.BaseDirectory, "lineautohotkey.settings.json");

        /// <summary>
        /// 讀取已保存的設定；若檔案不存在或讀取失敗，回傳 null 讓系統使用 appsettings.json 的預設值。
        /// </summary>
        public static LineAutoHotkeyUserSettings? Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<LineAutoHotkeyUserSettings>(json);
            }
            catch
            {
                // 讀取失敗時不阻斷啟動流程，改由 appsettings.json 的預設值接手。
                return null;
            }
        }

        /// <summary>
        /// 將使用者設定保存成 JSON 檔。
        /// </summary>
        public static void Save(LineAutoHotkeyUserSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }

        /// <summary>
        /// 將已保存的使用者設定套用到 options；若沒有保存檔，保留 appsettings.json 載入的預設值。
        /// </summary>
        public static void ApplyTo(LineAutoHotkeyOptions options)
        {
            var settings = Load();
            if (settings is null)
            {
                return;
            }

            options.Enabled = settings.Enabled;
            options.TargetChatNames = settings.TargetChatNames ?? Array.Empty<string>();
        }
    }
}