using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using OfficeOpenXml;
using SANJET.Core.Interfaces;
using SANJET.Core.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace SANJET.Core.ViewModels
{
    public partial class RecordViewModel : ObservableObject
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<RecordViewModel> _logger;
        private readonly DeviceViewModel _deviceViewModel;
        private readonly string _currentUsername;


        [ObservableProperty]
        private string _recordContent = string.Empty;

        // 【修改】SelectedItem 的類型現在是新的包裝類別
        [ObservableProperty]
        private RecordDisplayItemViewModel? _selectedRecord;

        [ObservableProperty]
        private string? _filterUsername;

        [ObservableProperty]
        private DateTime? _filterStartDate;

        // 【修改】ObservableCollection 的類型現在是新的包裝類別
        public ObservableCollection<RecordDisplayItemViewModel> DeviceRecords { get; } = new();
        public ICollectionView FilteredDeviceRecords { get; }

        public RecordViewModel(
            DeviceViewModel deviceViewModel,
            AppDbContext dbContext,
            ILogger<RecordViewModel> logger,
            string currentUsername)
        {
            _deviceViewModel = deviceViewModel;
            _dbContext = dbContext;
            _logger = logger;
            _currentUsername = currentUsername;

            FilteredDeviceRecords = CollectionViewSource.GetDefaultView(DeviceRecords);
            // 【修改】預設排序現在基於包裝類別的 Record.Timestamp 屬性
            FilteredDeviceRecords.SortDescriptions.Add(new SortDescription("Record.Timestamp", ListSortDirection.Descending));
            FilteredDeviceRecords.Filter = FilterRecords;

            _ = LoadRecordsAsync();
        }

        // 【修改】整個 LoadRecordsAsync 方法的邏輯
        private async Task LoadRecordsAsync()
        {
            try
            {
                _logger.LogInformation("正在為設備 ID: {DeviceId} 加載紀錄...", _deviceViewModel.Id);
                var recordsFromDb = await _dbContext.DeviceRecords
                    .Where(r => r.DeviceId == _deviceViewModel.Id)
                    .OrderByDescending(r => r.Timestamp) // 先從資料庫按時間倒序取出
                    .ToListAsync();

                DeviceRecords.Clear();
                int rowNum = 1;
                foreach (var record in recordsFromDb)
                {
                    // 將每筆紀錄包裝成 RecordDisplayItemViewModel 並給予行號
                    DeviceRecords.Add(new RecordDisplayItemViewModel(rowNum++, record));
                }
                _logger.LogInformation("成功為設備 ID: {DeviceId} 加載了 {Count} 筆紀錄。", _deviceViewModel.Id, DeviceRecords.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加載設備紀錄時發生錯誤。");
                MessageBox.Show($"加載紀錄失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand(CanExecute = nameof(CanAddOrDeleteRecord))]
        private async Task AddRecordAsync()
        {
            if (string.IsNullOrWhiteSpace(RecordContent))
            {
                MessageBox.Show("記錄內容不能為空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var newRecord = new DeviceRecord
                {
                    UniqueId = Guid.NewGuid(),
                    DeviceId = _deviceViewModel.Id,
                    DeviceName = _deviceViewModel.Name,
                    RunCount = _deviceViewModel.RunCount,
                    Username = _currentUsername,
                    Content = RecordContent.Trim(),
                    Timestamp = DateTime.Now
                };

                _dbContext.DeviceRecords.Add(newRecord);
                await _dbContext.SaveChangesAsync();

                // 【修改】新增後，重新整理整個列表以確保行號正確
                await LoadRecordsAsync();
                RecordContent = string.Empty;
                _logger.LogInformation("已為設備 ID: {DeviceId} 添加新紀錄。", _deviceViewModel.Id);


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加紀錄時出錯。");
                MessageBox.Show($"添加紀錄失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand(CanExecute = nameof(CanAddOrDeleteRecord))]
        private async Task DeleteRecordAsync()
        {
            if (SelectedRecord == null) return;

            var result = MessageBox.Show($"確定要刪除嗎？", "確認刪除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // 【修改】從包裝類別中取出要刪除的原始紀錄
            var recordToDelete = SelectedRecord.Record;

            try
            {
                _dbContext.DeviceRecords.Remove(recordToDelete);
                await _dbContext.SaveChangesAsync();
                _logger?.LogInformation("本地紀錄 ID: {RecordId} 已被刪除。", recordToDelete.Id);

                // 【修改】刪除後，重新載入整個列表以更新行號
                await LoadRecordsAsync();


            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "刪除紀錄時出錯。");
                MessageBox.Show($"刪除紀錄失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanAddOrDeleteRecord() => true;

        [RelayCommand]
        private void ApplyFilter() => FilteredDeviceRecords.Refresh();

        [RelayCommand]
        private void ResetFilter()
        {
            FilterUsername = null;
            FilterStartDate = null;
            FilteredDeviceRecords.Refresh();
        }

        // 【修改】篩選邏輯現在作用於包裝類別的 Record 屬性
        private bool FilterRecords(object item)
        {
            if (item is not RecordDisplayItemViewModel displayItem) return false;
            var record = displayItem.Record;

            bool isUserMatch = string.IsNullOrWhiteSpace(FilterUsername) ||
                               (record.Username?.Contains(FilterUsername, StringComparison.OrdinalIgnoreCase) ?? false);
            bool isDateMatch = !FilterStartDate.HasValue ||
                               record.Timestamp.Date >= FilterStartDate.Value.Date;
            return isUserMatch && isDateMatch;
        }

        [RelayCommand]
        private void ExportToExcel()
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                Title = "匯出設備紀錄",
                FileName = $"{_deviceViewModel.Name}_紀錄_{DateTime.Now:yyyyMMdd}.xlsx"
            };

            if (saveFileDialog.ShowDialog() != true) return;

            string filePath = saveFileDialog.FileName;
            // 建立模板的完整路徑
            string templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "導出模板", "CP08-003-02版-產品測試記錄表.xlsx");

            // 檢查模板是否存在
            if (!File.Exists(templatePath))
            {
                _logger.LogError("找不到 Excel 模板檔案於: {TemplatePath}", templatePath);
                MessageBox.Show($"模板檔案不存在，請確認路徑: {templatePath}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                ExcelPackage.License.SetNonCommercialOrganization("Sanjet");

                // **更正：從模板檔案載入 Excel Package**
                using var package = new ExcelPackage(new FileInfo(templatePath));

                // 假設資料要填入第一個工作表
                var worksheet = package.Workbook.Worksheets[0];

                // 根據您舊程式碼的邏輯，從第 4 列開始填寫資料
                int startRow = 4;
                int currentRow = startRow;


                // 根據時間升序排序，以便舊紀錄在前面
                var recordsToExport = FilteredDeviceRecords.Cast<RecordDisplayItemViewModel>().OrderBy(r => r.RowNumber);

                foreach (var displayItem in recordsToExport)
                {
                    var record = displayItem.Record; // 從包裝類中取得原始紀錄
                    worksheet.Cells[currentRow, 1].Value = displayItem.RowNumber; // A欄: 排序
                    worksheet.Cells[currentRow, 2].Value = record.Timestamp;
                    worksheet.Cells[currentRow, 2].Style.Numberformat.Format = "yyyy-mm-dd hh:mm:ss";
                    worksheet.Cells[currentRow, 3].Value = record.RunCount; // C欄: 次數
                    worksheet.Cells[currentRow, 4].Value = record.Content; // D欄: 測試狀況
                    worksheet.Cells[currentRow, 5].Value = record.Username; // E欄: 使用者
                    currentRow++;
                }

                // 自動調整欄寬
                //worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

                // 針對特定欄位自動調整欄寬
                worksheet.Column(1).AutoFit(); // A欄 (排序)
                worksheet.Column(2).AutoFit(); // B欄 (時間)
                worksheet.Column(3).AutoFit(); // C欄 (次數)
                worksheet.Column(5).AutoFit(); // E欄 (使用者)

                // 設定 D 欄 (測試狀況) 的格式
                worksheet.Column(4).Width = 60; // 設定一個固定的寬度，例如 60
                worksheet.Column(4).Style.WrapText = true; // 啟用該欄的自動換行功能


                // **將修改後的模板內容另存為使用者指定的新檔案**
                package.SaveAs(new FileInfo(filePath));

                MessageBox.Show($"紀錄已成功匯出至: {filePath}", "匯出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 詢問使用者是否開啟檔案
                if (MessageBox.Show("是否立即開啟匯出的檔案？", "操作確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    // 需要 System.Diagnostics.Process
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "匯出 Excel 失敗。");
                MessageBox.Show($"匯出失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
