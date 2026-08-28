#include <ETH.h>
#include <WiFi.h>        // ArduinoOTA 需要 WiFi.h，即使使用 Ethernet
#include <ArduinoOTA.h>
#include <PubSubClient.h>
#include <ModbusMaster.h>
#include <Arduino_JSON.h>

// ================================================================
// 1. Ethernet 硬體設定：ESP32-Ethernet-Kit-A-V1.2 / IP101
// ================================================================
#define ETH_PHY_TYPE   ETH_PHY_IP101
#define ETH_PHY_ADDR   1
#define ETH_MDC_PIN    23
#define ETH_MDIO_PIN   18
#define ETH_POWER_PIN  5
#define ETH_CLK_MODE   ETH_CLOCK_GPIO0_IN

const unsigned long ETH_LINK_TIMEOUT = 15000;
const unsigned long ETH_IP_TIMEOUT = 15000;
const unsigned long ETH_REINIT_DELAY = 60000;
const unsigned long ETH_STATUS_LOG_INTERVAL = 30000;

// ================================================================
// 2. MQTT 設定
// ================================================================
#define DEVICE_ID "ESP32_RS485"

const char* mqtt_server = "192.168.50.138";
const int mqtt_port = 1883;

char mqtt_client_id[64];

char topic_led_set[128];
char topic_led_status[128];
char topic_modbus_read_req[128];
char topic_modbus_read_resp[128];
char topic_modbus_write_req[128];
char topic_modbus_write_resp[128];
char topic_status[128];

char lwt_topic[128];
char lwt_payload[128];
const int lwt_qos = 1;
const boolean lwt_retain = true;

WiFiClient espClient;
PubSubClient client(espClient);

#define MSG_BUFFER_SIZE (512)
bool mqtt_connected = false;

// MQTT / Modbus 解耦用 Queue
// 11 站輪巡，每站若讀 2 筆 request，一輪至少 22 筆。
// Queue 需大於一輪總請求數，避免 PC 端一次送太快時立即塞滿。
#define MQTT_QUEUE_LENGTH 40
#define MQTT_PAYLOAD_MAX 384

// 【請求時效】只要有從站離線，單筆請求最壞需 3 次 retry × 約 2 秒逾時 ≈ 6 秒，
//   遠慢於 PC 每 500 ms 送一筆的速度，佇列必然積壓。積壓時若還照單全收地執行，
//   PC 收到的會是好幾輪前的「過期回應」，UI 上就會看到狀態在正常／失敗之間跳動。
//   因此超過此年齡的「讀取」請求直接丟棄不上匯流排（PC 下一輪本來就會重讀），
//   把有限的 RS485 頻寬留給最新的請求。寫入是人為下達的命令，永遠不丟。
#define REQUEST_MAX_AGE_MS 4000

enum MqttRequestType {
  REQ_LED_SET = 1,
  REQ_MODBUS_READ = 2,
  REQ_MODBUS_WRITE = 3
};

struct MqttRequestItem {
  MqttRequestType type;
  unsigned int length;
  unsigned long enqueuedAt;          // 進入佇列的時間 (millis)，用於判斷是否已過期
  char payload[MQTT_PAYLOAD_MAX];
};

// 統計用：累計丟棄的過期／溢位請求數，方便從序列埠判斷積壓是否嚴重
unsigned long droppedStaleCount = 0;
unsigned long droppedOverflowCount = 0;

QueueHandle_t mqttRequestQueue = NULL;
SemaphoreHandle_t mqttMutex = NULL;

// ================================================================
// 3. OTA 設定
// ================================================================
const char* ota_hostname = "esp32-ethernet-rs485";
const char* ota_password = "0000";
bool ota_started = false;

// ================================================================
// 4. I/O 與 Modbus RTU 設定
// ================================================================
#define LED_BUILTIN 2

// ESP32-Ethernet-Kit-A-V1.2 使用 ESP32-WROVER-E。
// GPIO16 / GPIO17 沒有引出，不建議使用。

// 建議使用 GPIO32 / GPIO33 當作 Serial2 的 RX / TX。
#define MODBUS_RX_PIN 32
#define MODBUS_TX_PIN 33
#define MODBUS_SERIAL_BAUD 9600
#define MAX_SLAVES 11

// 【逾時說明】（已用 ModbusMaster 2.0.1 原始碼確認）
//   ModbusMaster 不使用 Stream::setTimeout()，它的回應逾時寫死在標頭檔：
//       static const uint16_t ku16MBResponseTimeout = 2000;  // ModbusMaster.h:252
//   這是 static const 且「沒有任何 setter」，所以：
//     1. Serial2.setTimeout() 對 Modbus 交易毫無作用；
//     2. 無法在程式碼裡縮短，面對無回應的從站固定等 2 秒。
//   唯一能改的方式是直接修改函式庫標頭檔，但函式庫一更新就會被覆蓋，不建議。
//   → 目前改以 PC 端的三層防護吸收這個成本：離線退避、過期請求丟棄、回應看門狗。
#define MODBUS_TIMEOUT_MS 200           // 僅作用於 Serial2 的 readBytes 等 API，不影響 Modbus
#define MODBUS_RETRIES 3
#define MODBUS_RETRY_GAP_MS 100         // retry 之間的靜默時間，讓上一次的遲到幀走完

int Address_Offset = 7000;
ModbusMaster node;

// ================================================================
// 5. 函式原型
// ================================================================
void ethernetTask(void *pvParameters);
void mqttRequestTask(void *pvParameters);
void setLEDState(bool state);
void handleMqttSetLED(const char* payloadStr);
void handleMqttReadModbus(const char* payloadStr);
void handleMqttWriteModbus(const char* payloadStr);
void flushModbusSerial();
bool initEthernet();
bool ethernetReady();
bool ethernetHasIP();
void setupOTA();
void reconnectMqtt();
void publishOnlineStatus(bool retainFlag = true);
bool mqttPublish(const char* topic, const char* payload, bool retained = false);
bool mqttIsConnected();

// ================================================================
// 6. MQTT Topic 建立
// ================================================================
void buildTopics() {
  snprintf(topic_led_set, sizeof(topic_led_set), "devices/%s/led/set", DEVICE_ID);
  snprintf(topic_led_status, sizeof(topic_led_status), "devices/%s/led/status", DEVICE_ID);
  snprintf(topic_modbus_read_req, sizeof(topic_modbus_read_req), "devices/%s/modbus/read/request", DEVICE_ID);
  snprintf(topic_modbus_read_resp, sizeof(topic_modbus_read_resp), "devices/%s/modbus/read/response", DEVICE_ID);
  snprintf(topic_modbus_write_req, sizeof(topic_modbus_write_req), "devices/%s/modbus/write/request", DEVICE_ID);
  snprintf(topic_modbus_write_resp, sizeof(topic_modbus_write_resp), "devices/%s/modbus/write/response", DEVICE_ID);
  snprintf(topic_status, sizeof(topic_status), "devices/%s/status", DEVICE_ID);
  snprintf(lwt_topic, sizeof(lwt_topic), "devices/%s/status", DEVICE_ID);
}

// ================================================================
// 7. Ethernet 初始化與狀態檢查
// ================================================================
bool ethernetReady() {
  return ETH.linkUp();
}

bool ethernetHasIP() {
  return ETH.localIP() != IPAddress(0, 0, 0, 0);
}

bool initEthernet() {
  Serial.println("[Ethernet] 正在啟動乙太網路...");

  pinMode(ETH_POWER_PIN, OUTPUT);
  digitalWrite(ETH_POWER_PIN, HIGH);
  delay(100);

  pinMode(0, INPUT_PULLUP);

  ETH.begin(
    ETH_PHY_TYPE,
    ETH_PHY_ADDR,
    ETH_MDC_PIN,
    ETH_MDIO_PIN,
    ETH_POWER_PIN,
    ETH_CLK_MODE
  );

  Serial.print("[Ethernet] 等待網路線 Link Up");
  unsigned long startLink = millis();
  while (!ETH.linkUp() && millis() - startLink < ETH_LINK_TIMEOUT) {
    Serial.print(".");
    delay(500);
  }
  Serial.println();

  if (!ETH.linkUp()) {
    Serial.println("[Ethernet] Link Up 失敗，請檢查網路線、交換器與供電");
    return false;
  }

  Serial.print("[Ethernet] Link Up，等待 DHCP 取得 IP");
  unsigned long startIP = millis();
  while (!ethernetHasIP() && millis() - startIP < ETH_IP_TIMEOUT) {
    Serial.print(".");
    delay(500);
  }
  Serial.println();

  if (!ethernetHasIP()) {
    Serial.println("[Ethernet] DHCP 尚未取得 IP，但 Link 已建立，後續會繼續等待");
    return false;
  }

  Serial.println("[Ethernet] 連線成功");
  Serial.println("[Ethernet] IP: " + ETH.localIP().toString());
  Serial.println("[Ethernet] MAC: " + ETH.macAddress());
  return true;
}

// ================================================================
// 8. OTA 初始化
// ================================================================
void setupOTA() {
  if (ota_started) return;

  ArduinoOTA.setHostname(ota_hostname);
  ArduinoOTA.setPassword(ota_password);

  ArduinoOTA.onStart([]() {
    Serial.println("[OTA] 開始更新...");
  });

  ArduinoOTA.onEnd([]() {
    Serial.println("\n[OTA] 更新完成！");
  });

  ArduinoOTA.onProgress([](unsigned int progress, unsigned int total) {
    Serial.printf("[OTA] 進度: %u%%\r", (progress / (total / 100)));
  });

  ArduinoOTA.onError([](ota_error_t error) {
    Serial.printf("[OTA] 錯誤 [%u]: ", error);
    if (error == OTA_AUTH_ERROR) Serial.println("驗證失敗");
    else if (error == OTA_BEGIN_ERROR) Serial.println("開始失敗");
    else if (error == OTA_CONNECT_ERROR) Serial.println("連線失敗");
    else if (error == OTA_RECEIVE_ERROR) Serial.println("接收失敗");
    else if (error == OTA_END_ERROR) Serial.println("結束失敗");
  });

  ArduinoOTA.begin();
  ota_started = true;
  Serial.println("[OTA] 初始化完成");
}

// ================================================================
// 9. MQTT 安全 Publish / 狀態
// ================================================================
bool mqttPublish(const char* topic, const char* payload, bool retained) {
  if (!ethernetReady() || !ethernetHasIP()) return false;
  if (mqttMutex == NULL) return false;

  bool ok = false;
  if (xSemaphoreTake(mqttMutex, pdMS_TO_TICKS(2000)) == pdTRUE) {
    if (client.connected()) {
      ok = client.publish(topic, payload, retained);
    }
    xSemaphoreGive(mqttMutex);
  }
  return ok;
}

bool mqttIsConnected() {
  bool ok = false;
  if (mqttMutex == NULL) return false;
  if (xSemaphoreTake(mqttMutex, pdMS_TO_TICKS(500)) == pdTRUE) {
    ok = client.connected();
    xSemaphoreGive(mqttMutex);
  }
  return ok;
}

void publishOnlineStatus(bool retainFlag) {
  JSONVar onlineMsg;
  onlineMsg["Status"] = "online";
  onlineMsg["IP"] = ETH.localIP().toString();
  onlineMsg["MAC"] = ETH.macAddress();
  onlineMsg["Network"] = "Ethernet";
  onlineMsg["DeviceId"] = DEVICE_ID;

  String jsonString = JSON.stringify(onlineMsg);
  mqttPublish(topic_status, jsonString.c_str(), retainFlag);
  Serial.println("[MQTT] 已發佈上線狀態: " + jsonString + " 到主題: " + String(topic_status));
}

// ================================================================
// 10. MQTT Callback：只收資料，不做 Modbus
// ================================================================
void callback(char* topic, byte* payload, unsigned int length) {
  MqttRequestItem item;
  memset(&item, 0, sizeof(item));

  if (strcmp(topic, topic_led_set) == 0) {
    item.type = REQ_LED_SET;
  } else if (strcmp(topic, topic_modbus_read_req) == 0) {
    item.type = REQ_MODBUS_READ;
  } else if (strcmp(topic, topic_modbus_write_req) == 0) {
    item.type = REQ_MODBUS_WRITE;
  } else {
    return;
  }

  if (length >= MQTT_PAYLOAD_MAX) {
    length = MQTT_PAYLOAD_MAX - 1;
    Serial.println("[MQTT] Payload 過長，已截斷");
  }

  memcpy(item.payload, payload, length);
  item.payload[length] = '\0';
  item.length = length;
  item.enqueuedAt = millis();   // 記錄入列時間，供請求任務判斷是否已過期

  Serial.println(item.payload);

  if (mqttRequestQueue == NULL) {
    Serial.println("[MQTT] Queue 尚未建立，丟棄訊息");
    return;
  }

  BaseType_t ok = xQueueSend(mqttRequestQueue, &item, pdMS_TO_TICKS(5));

  // 【溢位策略：丟舊留新】原本佇列滿時是丟棄「剛收到」的請求，等於永遠拿舊資料
  //   去撞匯流排，積壓只會越來越嚴重。改為擠掉隊首最舊的一筆再放入新的，
  //   確保 PC 最新送來的請求優先被執行。
  if (ok != pdTRUE) {
    MqttRequestItem discarded;
    if (xQueueReceive(mqttRequestQueue, &discarded, 0) == pdTRUE) {
      droppedOverflowCount++;
      Serial.println("[MQTT] Queue 已滿，擠掉最舊的一筆請求以容納新請求（累計 " +
                     String(droppedOverflowCount) + " 筆）");
      ok = xQueueSend(mqttRequestQueue, &item, 0);
    }
    if (ok != pdTRUE) {
      Serial.println("[MQTT] Queue 已滿且無法騰出空間，丟棄本次請求");
    }
  }

  if (ok == pdTRUE) {
    UBaseType_t waiting = uxQueueMessagesWaiting(mqttRequestQueue);
    if (waiting > MQTT_QUEUE_LENGTH * 0.75) {
      Serial.println("[MQTT] Queue 接近滿載，目前等待筆數: " + String(waiting));
    }
  }
}

// ================================================================
// 11. MQTT 重新連線
// ================================================================
void reconnectMqtt() {
  if (!ethernetReady() || !ethernetHasIP()) {
    mqtt_connected = false;
    return;
  }

  if (mqttMutex == NULL) return;

  if (xSemaphoreTake(mqttMutex, pdMS_TO_TICKS(3000)) != pdTRUE) {
    Serial.println("[MQTT] 取得 Mutex 失敗，略過本次重連");
    return;
  }

  if (client.connected()) {
    mqtt_connected = true;
    xSemaphoreGive(mqttMutex);
    return;
  }

  Serial.print("[MQTT] 嘗試連線 Client ID: ");
  Serial.println(mqtt_client_id);
  Serial.print("[MQTT] LWT 主題: ");
  Serial.println(lwt_topic);
  Serial.print("[MQTT] LWT 內容: ");
  Serial.println(lwt_payload);

  if (client.connect(mqtt_client_id, NULL, NULL, lwt_topic, lwt_qos, lwt_retain, lwt_payload)) {
    Serial.println("[MQTT] 已連線");
    mqtt_connected = true;

    client.subscribe(topic_led_set);
    client.subscribe(topic_modbus_read_req);
    client.subscribe(topic_modbus_write_req);

    Serial.println("[MQTT] 已訂閱主題:");
    Serial.println(topic_led_set);
    Serial.println(topic_modbus_read_req);
    Serial.println(topic_modbus_write_req);

    JSONVar onlineMsg;
    onlineMsg["Status"] = "online";
    onlineMsg["IP"] = ETH.localIP().toString();
    onlineMsg["MAC"] = ETH.macAddress();
    onlineMsg["Network"] = "Ethernet";
    onlineMsg["DeviceId"] = DEVICE_ID;
    String jsonString = JSON.stringify(onlineMsg);
    client.publish(topic_status, jsonString.c_str(), true);
    Serial.println("[MQTT] 已發佈上線狀態: " + jsonString + " 到主題: " + String(topic_status));
  } else {
    Serial.print("[MQTT] 連線失敗, rc=");
    Serial.println(client.state());
    mqtt_connected = false;
  }

  xSemaphoreGive(mqttMutex);
}

// ================================================================
// 12. LED 狀態控制
// ================================================================
void setLEDState(bool state) {
  static bool lastState = false;

  if (lastState != state) {
    Serial.println("[LED] 狀態設為: " + String(state ? "開啟" : "關閉"));
    lastState = state;
  }

  digitalWrite(LED_BUILTIN, state ? HIGH : LOW);
}

// ================================================================
// 13. Setup
// ================================================================
void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(LED_BUILTIN, OUTPUT);
  setLEDState(false);

  Serial.println("[主程式] ESP32 Ethernet MQTT RS485 初始化開始...");

  snprintf(mqtt_client_id, sizeof(mqtt_client_id), "esp32-%s", DEVICE_ID);
  buildTopics();

  JSONVar lwtMsgJson;
  lwtMsgJson["Status"] = "offline";
  lwtMsgJson["DeviceId"] = DEVICE_ID;
  String lwtJsonString = JSON.stringify(lwtMsgJson);
  strncpy(lwt_payload, lwtJsonString.c_str(), sizeof(lwt_payload) - 1);
  lwt_payload[sizeof(lwt_payload) - 1] = '\0';

  mqttMutex = xSemaphoreCreateMutex();
  mqttRequestQueue = xQueueCreate(MQTT_QUEUE_LENGTH, sizeof(MqttRequestItem));

  if (mqttMutex == NULL || mqttRequestQueue == NULL) {
    Serial.println("[主程式] 建立 MQTT Mutex 或 Queue 失敗，系統停止");
    while (true) delay(1000);
  }

  if (initEthernet()) {
    setLEDState(true);
    setupOTA();
  } else {
    setLEDState(false);
    Serial.println("[主程式] Ethernet 初始化未完全成功，稍後由連線任務監控");
  }

  Serial2.begin(MODBUS_SERIAL_BAUD, SERIAL_8N1, MODBUS_RX_PIN, MODBUS_TX_PIN);
  Serial2.setTimeout(MODBUS_TIMEOUT_MS);
  // 【重要】上面這行不會影響 Modbus 逾時，ModbusMaster 只看自己內部的
  //   ku16MBResponseTimeout（2000 ms，static const、無 setter）。
  //   ModbusMaster 2.0.1 沒有 setResponseTimeout() 這個 API，不要嘗試呼叫，會編譯失敗。

  Serial.println("[Modbus] Serial2 已初始化");
  Serial.println("[Modbus] RX=" + String(MODBUS_RX_PIN) + ", TX=" + String(MODBUS_TX_PIN));
  Serial.println("[Modbus] Serial2 Timeout=" + String(MODBUS_TIMEOUT_MS) + " ms (不影響 Modbus)"
                 ", Modbus 回應逾時=函式庫預設約 2000 ms"
                 ", Retries=" + String(MODBUS_RETRIES) +
                 ", Retry 間隔=" + String(MODBUS_RETRY_GAP_MS) + " ms");
  Serial.println("[Modbus] ESP32-Ethernet-Kit V1.2 建議使用 GPIO32 / GPIO33，不建議使用 GPIO16 / GPIO17");

  client.setServer(mqtt_server, mqtt_port);
  client.setCallback(callback);
  client.setBufferSize(MSG_BUFFER_SIZE);
  client.setKeepAlive(30);
  client.setSocketTimeout(2);     // 避免 MQTT connect 阻塞太久觸發 task watchdog

  xTaskCreatePinnedToCore(
    ethernetTask,
    "EthernetTask",
    8192,
    NULL,
    1,
    NULL,
    1   // 不要固定在核心 0，避免與 Ethernet/系統底層任務搶 CPU0
  );
  Serial.println("[主程式] Ethernet 連線管理任務已創建");

  xTaskCreatePinnedToCore(
    mqttRequestTask,
    "MqttRequestTask",
    8192,
    NULL,
    1,
    NULL,
    1
  );
  Serial.println("[主程式] MQTT 請求處理任務已創建");

  // ESP.getFreeHeap() 回傳 uint32_t，在此工具鏈等同 long unsigned int，故需用 %lu
  Serial.printf("[主程式] 可用堆記憶體: %lu bytes\n", ESP.getFreeHeap());
}

// ================================================================
// 14. Loop：專心維持 MQTT / OTA
// ================================================================
void loop() {
  if (ota_started) {
    ArduinoOTA.handle();
  }

  if (ethernetReady() && ethernetHasIP() && mqttMutex != NULL) {
    if (xSemaphoreTake(mqttMutex, pdMS_TO_TICKS(50)) == pdTRUE) {
      if (client.connected()) {
        client.loop();
      }
      xSemaphoreGive(mqttMutex);
    }
  }

  delay(10);
}

// ================================================================
// 15. Ethernet / MQTT 連線管理任務
// ================================================================
void ethernetTask(void *pvParameters) {
  Serial.println("[連線任務] 啟動於核心 " + String(xPortGetCoreID()));

  unsigned long linkDownStart = 0;
  unsigned long lastStatusLog = 0;
  unsigned long lastMqttReconnect = 0;
  bool wasLinkDown = false;

  const unsigned long mqttReconnectInterval = 10000;

  for (;;) {
    bool link = ETH.linkUp();
    bool hasIP = ethernetHasIP();
    IPAddress ip = ETH.localIP();

    if (!link) {
      setLEDState(false);
      mqtt_connected = false;

      if (!wasLinkDown) {
        wasLinkDown = true;
        linkDownStart = millis();
        Serial.println("[連線任務] Ethernet Link Down，請檢查網路線、交換器或供電");
      }

      if (millis() - linkDownStart >= ETH_REINIT_DELAY) {
        Serial.println("[連線任務] Link Down 超過 60 秒，重新初始化 Ethernet");
        initEthernet();
        linkDownStart = millis();
      }
    } else {
      if (wasLinkDown) {
        wasLinkDown = false;
        Serial.println("[連線任務] Ethernet Link 恢復");
      }

      setLEDState(true);

      if (!hasIP) {
        if (millis() - lastStatusLog >= ETH_STATUS_LOG_INTERVAL) {
          Serial.println("[連線任務] Ethernet Link Up，但尚未取得 IP，等待 DHCP...");
          lastStatusLog = millis();
        }
      } else {
        if (!ota_started) {
          setupOTA();
        }

        if (!mqttIsConnected()) {
          mqtt_connected = false;
          if (millis() - lastMqttReconnect >= mqttReconnectInterval) {
            reconnectMqtt();
            lastMqttReconnect = millis();
          }
        } else {
          mqtt_connected = true;
          if (millis() - lastStatusLog >= ETH_STATUS_LOG_INTERVAL) {
            Serial.println("[連線任務] Ethernet / MQTT 正常，IP: " + ip.toString());
            lastStatusLog = millis();
          }
        }
      }
    }

    vTaskDelay(pdMS_TO_TICKS(5000));
  }
}

// ================================================================
// 16. MQTT 請求處理任務：在這裡才執行 Modbus
// ================================================================
void mqttRequestTask(void *pvParameters) {
  Serial.println("[請求任務] 啟動於核心 " + String(xPortGetCoreID()));

  MqttRequestItem item;

  for (;;) {
    if (xQueueReceive(mqttRequestQueue, &item, portMAX_DELAY) == pdTRUE) {
      // 【過期請求處理】只丟棄過期的「讀取」請求：PC 每輪都會重讀，丟掉不會遺失資訊，
      //   卻能立刻把積壓消化掉，讓後面較新的請求及時上匯流排。
      //   寫入 (REQ_MODBUS_WRITE) 與 LED 設定是人為下達的命令，不論多舊都必須執行。
      unsigned long age = millis() - item.enqueuedAt;
      if (item.type == REQ_MODBUS_READ && age > REQUEST_MAX_AGE_MS) {
        droppedStaleCount++;
        Serial.println("[請求任務] 丟棄過期讀取請求 (已等待 " + String(age) + " ms，門檻 " +
                       String(REQUEST_MAX_AGE_MS) + " ms，累計 " + String(droppedStaleCount) +
                       " 筆，佇列剩餘 " + String((unsigned long)uxQueueMessagesWaiting(mqttRequestQueue)) + ")");
        continue;   // 不上匯流排、不回覆；PC 端由回應逾時看門狗處理
      }

      switch (item.type) {
        case REQ_LED_SET:
          handleMqttSetLED(item.payload);
          break;

        case REQ_MODBUS_READ:
          handleMqttReadModbus(item.payload);
          break;

        case REQ_MODBUS_WRITE:
          handleMqttWriteModbus(item.payload);
          break;

        default:
          Serial.println("[請求任務] 未知請求類型");
          break;
      }

      vTaskDelay(pdMS_TO_TICKS(50));
    }
  }
}

// ================================================================
// 17. MQTT LED 控制
// ================================================================
void handleMqttSetLED(const char* payloadStr) {
  JSONVar response;
  JSONVar request = JSON.parse(payloadStr);

  if (JSON.typeof(request) == "undefined") {
    Serial.println("[LED控制] JSON 解析失敗");
    response["Status"] = "error";
    response["Message"] = "無效的 JSON payload";
  } else if (!request.hasOwnProperty("state")) {
    Serial.println("[LED控制] JSON 缺少 state 欄位");
    response["Status"] = "error";
    response["Message"] = "缺少 state 參數";
  } else {
    String state = (const char*)request["state"];
    if (state == "ON") {
      setLEDState(true);
      response["Status"] = "success";
      response["Message"] = "LED 已開啟";
    } else if (state == "OFF") {
      setLEDState(false);
      response["Status"] = "success";
      response["Message"] = "LED 已關閉";
    } else {
      Serial.println("[LED控制] 無效的 state: " + state);
      response["Status"] = "error";
      response["Message"] = "無效的 state 參數: " + state;
    }
  }

  String responseString = JSON.stringify(response);
  mqttPublish(topic_led_status, responseString.c_str(), false);
}

// ================================================================
// 17.5 清空 Modbus 序列接收緩衝
// ================================================================
// 用途：在每一次 Modbus 嘗試（含每次 retry）前呼叫，清掉前一次交易（特別是逾時／
//       無回應的離線從站）殘留在 Serial2 接收緩衝中的位元組。
// 目的：避免這些殘留位元組被誤當成「下一個從站」的回應，導致相鄰的正常從站
//       發生 CRC／框架錯誤而被誤判為通訊失敗（多台連坐的主要傳染途徑）。
void flushModbusSerial() {
  while (Serial2.available()) {
    Serial2.read();
  }
}

// ================================================================
// 18. MQTT Modbus 讀取
// ================================================================
void handleMqttReadModbus(const char* payloadStr) {
  JSONVar response;
  response["DeviceId"] = DEVICE_ID;

  Serial.println("[Modbus讀取請求] Payload: " + String(payloadStr));

  JSONVar request = JSON.parse(payloadStr);

  if (JSON.typeof(request) == "undefined") {
    Serial.println("[Modbus讀取] JSON 解析失敗");
    response["Status"] = "error";
    response["Message"] = "無效的 JSON payload (read request)";
    String responseString = JSON.stringify(response);
    mqttPublish(topic_modbus_read_resp, responseString.c_str(), false);
    return;
  }

  if (!request.hasOwnProperty("slaveId") || !request.hasOwnProperty("address") ||
      !request.hasOwnProperty("quantity") || !request.hasOwnProperty("functionCode")) {
    Serial.println("[Modbus讀取] JSON 缺少必要欄位");
    response["Status"] = "error";
    response["Message"] = "請求缺少必要參數 (slaveId, address, quantity, functionCode)";
    if (request.hasOwnProperty("slaveId")) response["SlaveId"] = (int)request["slaveId"];
    if (request.hasOwnProperty("address")) response["Address"] = (int)request["address"];
    if (request.hasOwnProperty("quantity")) response["Quantity"] = (int)request["quantity"];
    if (request.hasOwnProperty("functionCode")) response["FunctionCode"] = (int)request["functionCode"];
    String responseString = JSON.stringify(response);
    mqttPublish(topic_modbus_read_resp, responseString.c_str(), false);
    return;
  }

  uint8_t slaveId = (int)request["slaveId"];
  uint16_t relativeAddress = (int)request["address"];
  uint16_t modbusAddress = relativeAddress + Address_Offset;
  uint8_t quantity = (int)request["quantity"];
  uint8_t functionCode = (int)request["functionCode"];

  response["SlaveId"] = slaveId;
  response["Address"] = relativeAddress;
  response["Quantity"] = quantity;
  response["FunctionCode"] = functionCode;

  if (slaveId < 1 || slaveId > MAX_SLAVES) {
    Serial.println("[Modbus讀取] 無效的 slaveId: " + String(slaveId));
    response["Status"] = "error";
    response["Message"] = "slaveId 必須介於 1 和 " + String(MAX_SLAVES) + " 之間";
  } else if (quantity < 1 || quantity > 10) {
    Serial.println("[Modbus讀取] 無效的 quantity: " + String(quantity));
    response["Status"] = "error";
    response["Message"] = "quantity 必須介於 1 和 10 之間";
  } else {
    node.begin(slaveId, Serial2);
    uint8_t result = 0xFF;
    uint16_t dataBuffer[10];

    Serial.println("[Modbus讀取] 開始讀取從站 " + String(slaveId) +
                   ", 實際位址 " + String(modbusAddress) +
                   " (相對位址 " + String(relativeAddress) + ")" +
                   ", 數量 " + String(quantity) +
                   ", 功能碼 " + String(functionCode));

    uint8_t retries = MODBUS_RETRIES;
    while (retries > 0) {
      // 【每次嘗試前都必須清空】而不是只在迴圈外清一次。
      // 原因：前一次逾時的從站，其「遲到」的回應位元組會殘留在 Serial2 接收緩衝中。
      //       下一次 retry 一開始 available() 就為真，ModbusMaster 會把這些殘留當成
      //       本次的回應去解析，必然得到 CRC 錯誤 (0xE3)，導致三次 retry 連坐全失敗，
      //       PC 端因此誤判為「通訊失敗」，下一輪輪詢卻又正常 —— 即狀態反覆跳動的主因。
      flushModbusSerial();
      node.clearResponseBuffer();

      if (functionCode == 3) {
        result = node.readHoldingRegisters(modbusAddress, quantity);
      } else if (functionCode == 4) {
        result = node.readInputRegisters(modbusAddress, quantity);
      } else {
        result = 0xE1;
        response["Message"] = "不支援的功能碼: " + String(functionCode);
        break;
      }

      if (result == node.ku8MBSuccess) break;
      retries--;
      if (retries > 0) {
        Serial.println("[Modbus讀取] 第 " + String(MODBUS_RETRIES - retries) + " 次嘗試失敗，錯誤碼: 0x" +
                       String(result, HEX) + "，" + String(MODBUS_RETRY_GAP_MS) + " ms 後重試");
      }
      delay(MODBUS_RETRY_GAP_MS); // 靜默等待，讓上一次的遲到幀完整走完再重試
    }

    if (result == node.ku8MBSuccess) {
      response["Status"] = "success";
      JSONVar dataArray;
      for (uint8_t i = 0; i < quantity; i++) {
        dataBuffer[i] = node.getResponseBuffer(i);
        dataArray[i] = dataBuffer[i];
      }
      response["Data"] = dataArray;
      Serial.println("[Modbus讀取] 成功，數據: " + String(JSON.stringify(dataArray)));
      Serial.println(" ");
    } else {
      response["Status"] = "error";
      if (!response.hasOwnProperty("Message")) {
        response["Message"] = "讀取 Modbus 失敗，錯誤碼: 0x" + String(result, HEX);
      }
      Serial.println("[Modbus讀取] 失敗，錯誤碼: 0x" + String(result, HEX));
      Serial.println(" ");
    }
  }

  String responseString = JSON.stringify(response);
  mqttPublish(topic_modbus_read_resp, responseString.c_str(), false);
}

// ================================================================
// 19. MQTT Modbus 寫入
// ================================================================
void handleMqttWriteModbus(const char* payloadStr) {
  JSONVar response;

  JSONVar request = JSON.parse(payloadStr);
  if (JSON.typeof(request) == "undefined") {
    Serial.println("[Modbus寫入] JSON 解析失敗");
    response["Status"] = "error";
    response["Message"] = "無效的 JSON payload";
    String responseString = JSON.stringify(response);
    mqttPublish(topic_modbus_write_resp, responseString.c_str(), false);
    return;
  }

  if (!request.hasOwnProperty("slaveId") ||
      !request.hasOwnProperty("address") ||
      (!request.hasOwnProperty("value") && !request.hasOwnProperty("values"))) {
    Serial.println("[Modbus寫入] JSON 缺少必要欄位");
    response["Status"] = "error";
    response["Message"] = "缺少必要參數 (slaveId, address, value 或 values)";
    String responseString = JSON.stringify(response);
    mqttPublish(topic_modbus_write_resp, responseString.c_str(), false);
    return;
  }

  uint8_t slaveId = (int)request["slaveId"];
  uint16_t relativeAddress = (int)request["address"];
  uint16_t address = relativeAddress + Address_Offset;

  if (slaveId < 1 || slaveId > MAX_SLAVES) {
    Serial.println("[Modbus寫入] 無效的 slaveId: " + String(slaveId));
    response["Status"] = "error";
    response["SlaveId"] = slaveId;
    response["Message"] = "slaveId 必須介於 1 和 " + String(MAX_SLAVES) + " 之間";
    String responseString = JSON.stringify(response);
    mqttPublish(topic_modbus_write_resp, responseString.c_str(), false);
    return;
  }

  if (request.hasOwnProperty("values")) {
    JSONVar values = request["values"];
    uint16_t quantity = values.length();

    if (quantity < 1 || quantity > 10) {
      Serial.println("[Modbus寫入] 無效的 values 數量: " + String(quantity));
      response["Status"] = "error";
      response["SlaveId"] = slaveId;
      response["Address"] = relativeAddress;
      response["Quantity"] = quantity;
      response["FunctionCode"] = 16;
      response["Message"] = "values 數量必須介於 1 和 10 之間";
      String responseString = JSON.stringify(response);
      mqttPublish(topic_modbus_write_resp, responseString.c_str(), false);
      return;
    }

    node.begin(slaveId, Serial2);

    Serial.println("[Modbus寫入] 功能碼 16 多筆寫入, 起始位址 " + String(address) +
                   ", 數量 " + String(quantity));

    for (uint16_t i = 0; i < quantity; i++) {
      uint16_t val = (int)values[i];
      node.setTransmitBuffer(i, val);
      Serial.println("[Modbus寫入] Buffer[" + String(i) + "] -> Addr: " + String(address + i) +
                     " Val: " + String(val));
    }

    uint8_t result = 0xFF;
    uint8_t retries = MODBUS_RETRIES;

    while (retries > 0) {
      Serial.println("[Modbus寫入] 嘗試功能碼 16 多筆寫入 (retry=" + String(MODBUS_RETRIES - retries) + ")");

      // 每次嘗試前清空 RX 殘留與回應緩衝，理由同讀取路徑（避免遲到幀污染下一次 retry）。
      // 注意：不可呼叫 clearTransmitBuffer()，否則會清掉上面 setTransmitBuffer() 準備好的資料。
      flushModbusSerial();
      node.clearResponseBuffer();

      result = node.writeMultipleRegisters(address, quantity);

      if (result == node.ku8MBSuccess) {
        Serial.println("[Modbus寫入] 功能碼 16 多筆寫入成功");
        break;
      }

      Serial.println("[Modbus寫入] 功能碼 16 多筆寫入失敗，錯誤碼: 0x" + String(result, HEX));
      retries--;
      delay(MODBUS_RETRY_GAP_MS);
    }

    response["SlaveId"] = slaveId;
    response["Address"] = relativeAddress;
    response["Quantity"] = quantity;
    response["FunctionCode"] = 16;

    if (result == node.ku8MBSuccess) {
      response["Status"] = "success";
      response["Message"] = "功能碼 16 多筆寫入成功";
    } else {
      response["Status"] = "error";
      response["Message"] = "功能碼 16 多筆寫入失敗，錯誤碼: 0x" + String(result, HEX);
    }

    String responseString = JSON.stringify(response);
    mqttPublish(topic_modbus_write_resp, responseString.c_str(), false);
    return;
  }

  uint16_t value = (int)request["value"];

  node.begin(slaveId, Serial2);
  uint8_t result = 0xFF;

  Serial.println("[Modbus寫入] 開始寫入從站 " + String(slaveId) +
                 ", 位址 " + String(address) +
                 ", 值 " + String(value));

  uint8_t retries = MODBUS_RETRIES;
  while (retries > 0) {
    // 每次嘗試前清空 RX 殘留與回應緩衝，理由同讀取路徑
    flushModbusSerial();
    node.clearResponseBuffer();

    result = node.writeSingleRegister(address, value);
    if (result == node.ku8MBSuccess) break;
    retries--;
    delay(MODBUS_RETRY_GAP_MS);
  }

  if (result == node.ku8MBSuccess) {
    response["Status"] = "success";
    response["SlaveId"] = slaveId;
    response["Address"] = relativeAddress;
    response["FunctionCode"] = 6;
    response["Message"] = "寫入成功";
    Serial.println("[Modbus寫入] 成功");
  } else {
    response["Status"] = "error";
    response["SlaveId"] = slaveId;
    response["Address"] = relativeAddress;
    response["FunctionCode"] = 6;
    response["Message"] = "寫入失敗，錯誤碼: 0x" + String(result, HEX);
    Serial.println("[Modbus寫入] 失敗，錯誤碼: 0x" + String(result, HEX));
  }

  String responseString = JSON.stringify(response);
  mqttPublish(topic_modbus_write_resp, responseString.c_str(), false);
}
