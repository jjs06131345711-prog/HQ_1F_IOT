; SANJET Scada 安裝程式腳本（Inno Setup）
; 使用步驟：
;   1. 先產生正式發佈檔（self-contained, win-x64）：
;      dotnet publish ..\SANJET.csproj -c Release -r win-x64 --self-contained true ^
;          -p:PublishSingleFile=false -p:PublishReadyToRun=false -o ..\publish\win-x64
;   2. 再使用 Inno Setup 編譯安裝程式：
;      ISCC.exe SANJET.iss
;   3. 輸出檔位於 Installer\Output\SANJET-Setup-<版本>.exe

#define MyAppName "Sanjet Scada"
#define MyAppVersion "2.1.0"
#define MyAppPublisher "SANJET"
#define MyAppExeName "SANJET.exe"
#define MyPublishDir "..\publish\win-x64"

[Setup]
AppId={{89692EFC-E2B3-45CB-9BFE-25413B2A477C}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=SANJET-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\AnyConv.com__logotest.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[Files]
; 打包 self-contained 發佈目錄，排除非 x64 的 LibVLC 平台檔案以縮小安裝包。
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "libvlc\win-x86\*,libvlc\win-arm64\*"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸載 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; GroupDescription: "其他捷徑："

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "啟動 {#MyAppName}"; Flags: nowait postinstall skipifsilent