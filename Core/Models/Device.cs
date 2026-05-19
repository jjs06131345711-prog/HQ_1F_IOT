
namespace SANJET.Core.Models
{
    public class Device
    {
        public int Id { get; set; } // 主鍵
        public string Name { get; set; } = string.Empty;
        //public string OriginalName { get; set; } = string.Empty; // 用於儲存原始名稱
        //public string IpAddress { get; set; } = string.Empty;
        public int SlaveId { get; set; }
        public string Status { get; set; } = "閒置"; // 預設狀態
        public bool IsOperational { get; set; } = true; // 預設為可操作
        public int RunCount { get; set; } = 0; // 預設運轉次數
        public int? TargetRunCount { get; set; } // 目標運轉次數，null 表示未啟用
        public bool AutoStopOnTarget { get; set; } = false; // 到達目標次數後自動停止
        public DateTime Timestamp { get; set; }

        // 所屬區域：展機區或測試區。
        public string Area { get; set; } = "展機區";

        // 同一個 Modbus 從站內的邏輯設備編號。展機區維持 1；測試區用 1~4 對應不同暫存器位置。
        public int ModbusDeviceIndex { get; set; } = 1;

        // 新增欄位：控制此 Modbus 從設備的 ESP32 的 MQTT ID
        public string? ControllingEsp32MqttId { get; set; }

        // 如果需要，可以添加其他與資料庫儲存相關的屬性
        // 例如：
        // public DateTime LastCommunicationTime { get; set; }
        // public string? Location { get; set; }
    }
}
