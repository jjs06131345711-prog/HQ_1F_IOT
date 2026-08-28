# ESP32 韌體

本資料夾採用 Arduino 的正規 sketch 結構（`資料夾名/同名.ino`），
可直接用 Arduino IDE 開啟，也可用 `arduino-cli` 在命令列編譯與燒錄。

## Sketch 一覽

| 資料夾 | DEVICE_ID | 用途 |
|---|---|---|
| `ESP32_Ethernet_Kit_RS485_MQTT` | `ESP32_RS485` | 乙太網路版 RS485 Modbus RTU 主站 ↔ MQTT 閘道 |

尚未搬入本資料夾的韌體（仍為專案根目錄的 `.txt`）：

- `ESP32_WIFI_RS485_MQTT_20260515.txt` — WiFi 版 RS485 閘道（`ESP32_TEST_RS485`）
- `ESP32_MdTCP_MQTT_20260827_Fixed.txt` — Modbus TCP 從站閘道（`ESP32_MdTCP`）
- `ESP32_MdTCP_MQTT_JOSN0904_Final.txt` — 同上，舊版

## 開發環境

| 項目 | 版本 |
|---|---|
| arduino-cli | 1.5.1（Arduino IDE 2.x 內建） |
| ESP32 core | `esp32:esp32` 3.3.11 |
| 開發板 FQBN | `esp32:esp32:esp32wrover` |

`arduino-cli.exe` 位於 Arduino IDE 安裝目錄下：

```
%LOCALAPPDATA%\Programs\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe
```

### 需要的函式庫

| 函式庫 | 版本 | 用途 |
|---|---|---|
| ModbusMaster | 2.0.1 | Modbus RTU 主站 |
| PubSubClient | 2.8 | MQTT 用戶端 |
| Arduino_JSON | 0.2.2 | 請求／回應的 JSON 組解 |

安裝：

```bash
arduino-cli lib install ModbusMaster@2.0.1 PubSubClient@2.8 Arduino_JSON@0.2.2
```

## 常用指令

在 sketch 資料夾內執行（`sketch.yaml` 已設定好 FQBN，不必再加 `-b`）：

```bash
arduino-cli compile
```

查詢開發板連在哪個序列埠：

```bash
arduino-cli board list
```

燒錄（把 `COM3` 換成上面查到的埠）：

```bash
arduino-cli upload -p COM3
```

開啟序列埠監看（韌體以 115200 輸出）：

```bash
arduino-cli monitor -p COM3 -c baudrate=115200
```

## 除錯：Modbus 錯誤碼對照

韌體在讀寫失敗時會印出 `錯誤碼: 0x??`，可據此區分根因：

| 錯誤碼 | 意義 | 通常指向 |
|---|---|---|
| `0xE2` | 回應逾時 | 從站沒回應：離線、接線、站號設錯 |
| `0xE3` | CRC 錯誤 | 匯流排干擾、終端電阻／接地問題 |
| `0xE1` | 非法功能碼 | 請求參數有誤 |
| `0xE0` | 非法站號 | slaveId 超出範圍 |

## 已知限制

ModbusMaster 2.0.1 的回應逾時寫死在標頭檔中：

```cpp
static const uint16_t ku16MBResponseTimeout = 2000;  // ModbusMaster.h:252
```

這是 `static const` 且**沒有 setter**，因此：

- `Serial2.setTimeout()` 對 Modbus 交易**毫無作用**
- 無法在程式碼裡縮短，面對無回應的從站固定等 2 秒
- 一台離線從站的單筆請求最壞會佔用匯流排約 6 秒（3 次 retry）

改函式庫標頭檔可以繞過，但函式庫一更新就會被覆蓋，不建議。
目前改由 PC 端的三層防護吸收這個成本：離線退避、過期請求丟棄、回應看門狗。
