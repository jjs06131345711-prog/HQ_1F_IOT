using SANJET.Core.Constants;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SANJET.Core.Configuration
{
    /// <summary>
    /// 排程自動停止的作用範圍（要停止哪個區域的設備）。
    /// </summary>
    public enum ScheduledStopAreaScope
    {
        /// <summary>所有區域的設備。</summary>
        All,
        /// <summary>只停止展機區的設備。</summary>
        DisplayArea,
        /// <summary>只停止測試區的設備。</summary>
        TestArea
    }

    /// <summary>
    /// 單筆「排程自動停止」的時間設定。
    /// </summary>
    public class ScheduledStopTime
    {
        /// <summary>24 小時制的觸發時間字串，格式為 HH:mm，例如 "18:00"。</summary>
        public string Time { get; set; } = "18:00";

        /// <summary>要停止的區域："全部"、"展機區" 或 "測試區"；無法辨識時視為全部。</summary>
        public string Area { get; set; } = ScheduledStopOptions.AllAreaName;

        /// <summary>是否啟用這筆時間；未啟用時排程服務會略過。</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// 一筆「本次到點」的排程，包含觸發時間與作用區域。
    /// </summary>
    /// <param name="Time">排定的每日時間。</param>
    /// <param name="Area">要停止的區域範圍。</param>
    public record ScheduledStopOccurrence(TimeSpan Time, ScheduledStopAreaScope Area);

    /// <summary>
    /// 排程自動停止的整體設定：可設定多組每日觸發時間，並各自指定要停止的區域。
    /// 預設值來自 appsettings.json 的 ScheduledStop 區段，並可由設定頁覆寫後保存於使用者設定檔。
    /// </summary>
    public class ScheduledStopOptions
    {
        /// <summary>「全部區域」在設定檔中的顯示名稱。</summary>
        public const string AllAreaName = "全部";

        /// <summary>排程自動停止的總開關；false 時所有時間都不會觸發。</summary>
        public bool Enabled { get; set; }

        /// <summary>觸發後是否發送 LINE 通知（需 LINE 通知本身已設定完成）。</summary>
        public bool NotifyByLine { get; set; } = true;

        /// <summary>
        /// 每日觸發時間清單。
        /// 注意：設定頁儲存時是「整包換成新的 List」而非就地增刪，
        /// 讓背景排程服務讀取時不會遇到集合被同時修改的問題。
        /// </summary>
        public IReadOnlyList<ScheduledStopTime> Times { get; set; } = new List<ScheduledStopTime>();

        /// <summary>接受的時間字串格式；同時容許有無前導零與含秒數的寫法。</summary>
        private static readonly string[] AcceptedFormats = { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss" };

        /// <summary>
        /// 將使用者輸入的時間字串解析成當日的時間點。
        /// </summary>
        /// <param name="text">時間字串，例如 "18:00"。</param>
        /// <param name="time">解析成功時輸出對應的 TimeSpan（僅取到分鐘）。</param>
        /// <returns>格式正確且落在 00:00~23:59 之間則回傳 true。</returns>
        public static bool TryParseTime(string? text, out TimeSpan time)
        {
            time = TimeSpan.Zero;

            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }

            if (!TimeSpan.TryParseExact(trimmed, AcceptedFormats, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
            {
                return false;
            }

            // 統一忽略秒數，排程以「分鐘」為最小單位。
            time = new TimeSpan(parsed.Hours, parsed.Minutes, 0);
            return true;
        }

        /// <summary>
        /// 將時間格式化為固定的 HH:mm 字串，用於儲存與顯示。
        /// </summary>
        public static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        /// <summary>
        /// 解析區域設定字串；容許中文名稱與英文代碼，無法辨識時一律回傳「全部」，
        /// 避免設定檔被改壞時反而漏掉該停的設備。
        /// </summary>
        /// <param name="text">區域字串，例如 "展機區"、"測試區"、"全部"。</param>
        /// <returns>對應的區域範圍。</returns>
        public static ScheduledStopAreaScope ParseArea(string? text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return ScheduledStopAreaScope.All;
            }

            if (string.Equals(trimmed, ModbusAddressMapping.TestAreaName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Test", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, nameof(ScheduledStopAreaScope.TestArea), StringComparison.OrdinalIgnoreCase))
            {
                return ScheduledStopAreaScope.TestArea;
            }

            if (string.Equals(trimmed, ModbusAddressMapping.DisplayAreaName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "展示區", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Display", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, nameof(ScheduledStopAreaScope.DisplayArea), StringComparison.OrdinalIgnoreCase))
            {
                return ScheduledStopAreaScope.DisplayArea;
            }

            return ScheduledStopAreaScope.All;
        }

        /// <summary>
        /// 將區域範圍格式化成儲存與顯示用的中文名稱。
        /// </summary>
        public static string FormatArea(ScheduledStopAreaScope area) => area switch
        {
            ScheduledStopAreaScope.DisplayArea => ModbusAddressMapping.DisplayAreaName,
            ScheduledStopAreaScope.TestArea => ModbusAddressMapping.TestAreaName,
            _ => AllAreaName
        };

        /// <summary>
        /// 取得目前所有「已啟用且時間格式正確」的排程，並去除重複、由早到晚排序。
        /// </summary>
        public IReadOnlyList<ScheduledStopOccurrence> GetEnabledSchedules()
        {
            // 先取快照，避免列舉期間設定頁替換了 Times 參考。
            var snapshot = Times;
            if (snapshot == null || snapshot.Count == 0)
            {
                return Array.Empty<ScheduledStopOccurrence>();
            }

            return snapshot
                .Where(item => item != null && item.Enabled)
                .Select(item =>
                {
                    var ok = TryParseTime(item.Time, out var parsed);
                    return (ok, occurrence: new ScheduledStopOccurrence(parsed, ParseArea(item.Area)));
                })
                .Where(result => result.ok)
                .Select(result => result.occurrence)
                .Distinct()
                .OrderBy(occurrence => occurrence.Time)
                .ThenBy(occurrence => occurrence.Area)
                .ToList();
        }

        /// <summary>
        /// 找出落在 (lastChecked, now] 這段區間內的排程，也就是本次檢查應該觸發的項目。
        /// 同時比對昨天與今天的時間點，以涵蓋跨午夜、以及電腦休眠後才補檢查的情況；
        /// 由於區間是左開右閉，同一個時間點只會被觸發一次。
        /// </summary>
        /// <param name="lastChecked">上次檢查的本機時間（不含）。</param>
        /// <param name="now">本次檢查的本機時間（含）。</param>
        /// <returns>本次應該觸發的排程清單；總開關未啟用時回傳空清單。</returns>
        public IReadOnlyList<ScheduledStopOccurrence> GetDueSchedules(DateTime lastChecked, DateTime now)
        {
            if (!Enabled)
            {
                return Array.Empty<ScheduledStopOccurrence>();
            }

            var dueSchedules = new List<ScheduledStopOccurrence>();
            foreach (var schedule in GetEnabledSchedules())
            {
                foreach (var day in new[] { now.Date.AddDays(-1), now.Date })
                {
                    var occurrence = day + schedule.Time;
                    if (occurrence > lastChecked && occurrence <= now)
                    {
                        dueSchedules.Add(schedule);
                        break;
                    }
                }
            }

            return dueSchedules;
        }
    }
}
