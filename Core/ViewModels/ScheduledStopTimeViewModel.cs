using CommunityToolkit.Mvvm.ComponentModel;
using SANJET.Core.Configuration;

namespace SANJET.Core.ViewModels
{
    /// <summary>
    /// 設定頁中「排程自動停止」清單的單一列，對應一組每日觸發時間與其停止區域。
    /// </summary>
    public partial class ScheduledStopTimeViewModel : ObservableObject
    {
        /// <summary>24 小時制的時間字串（HH:mm），由使用者直接輸入。</summary>
        [ObservableProperty]
        private string time = "18:00";

        /// <summary>要停止的區域名稱："全部"、"展機區" 或 "測試區"，對應畫面上的下拉選單。</summary>
        [ObservableProperty]
        private string area = ScheduledStopOptions.AllAreaName;

        /// <summary>這筆時間是否啟用。</summary>
        [ObservableProperty]
        private bool isEnabled = true;

        public ScheduledStopTimeViewModel()
        {
        }

        /// <param name="time">時間字串（HH:mm）。</param>
        /// <param name="area">停止區域名稱。</param>
        /// <param name="isEnabled">是否啟用這筆時間。</param>
        public ScheduledStopTimeViewModel(string time, string area, bool isEnabled)
        {
            Time = time;
            Area = area;
            IsEnabled = isEnabled;
        }
    }
}
