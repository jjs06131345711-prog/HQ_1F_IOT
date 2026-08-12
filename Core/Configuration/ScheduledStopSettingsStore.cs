using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SANJET.Core.Configuration
{
    /// <summary>
    /// 使用者在設定頁保存的排程自動停止設定，會覆蓋 appsettings.json 的預設值。
    /// </summary>
    public class ScheduledStopUserSettings
    {
        public bool Enabled { get; set; }
        public bool NotifyByLine { get; set; } = true;
        public List<ScheduledStopTime> Times { get; set; } = new();
    }

    /// <summary>
    /// 負責讀寫排程自動停止的使用者設定（保存在使用者可寫入的 AppData 目錄），
    /// 並可將設定套用到執行中的 <see cref="ScheduledStopOptions"/> 單例。
    /// </summary>
    public static class ScheduledStopSettingsStore
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sanjet Scada",
            "scheduledstop.settings.json");

        /// <summary>
        /// 讀取已保存的設定；沒有檔案或讀取失敗時回傳 null，由 appsettings.json 的預設值接手。
        /// </summary>
        public static ScheduledStopUserSettings? Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<ScheduledStopUserSettings>(json);
            }
            catch
            {
                // 讀取失敗時不阻斷啟動流程。
                return null;
            }
        }

        /// <summary>
        /// 將使用者設定保存成 JSON 檔，重啟後仍會生效。
        /// </summary>
        public static void Save(ScheduledStopUserSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }

        /// <summary>
        /// 將已保存的使用者設定套用到 options；若沒有保存檔則保留 appsettings.json 載入的預設值。
        /// </summary>
        public static void ApplyTo(ScheduledStopOptions options)
        {
            var settings = Load();
            if (settings is null)
            {
                return;
            }

            options.Enabled = settings.Enabled;
            options.NotifyByLine = settings.NotifyByLine;
            // 舊版設定檔沒有 Area 欄位，反序列化後會是 null；此時視為「全部」以維持原本行為。
            var times = settings.Times ?? new List<ScheduledStopTime>();
            foreach (var item in times.Where(item => item != null && string.IsNullOrWhiteSpace(item.Area)))
            {
                item.Area = ScheduledStopOptions.AllAreaName;
            }

            options.Times = times;
        }
    }
}
