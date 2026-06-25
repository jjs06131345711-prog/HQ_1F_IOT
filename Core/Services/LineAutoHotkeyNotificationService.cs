using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using SANJET.Core.Configuration;
using SANJET.Core.Interfaces;
using Clipboard = System.Windows.Clipboard;

namespace SANJET.Core.Services
{
    public class LineAutoHotkeyNotificationService : ILineNotificationChannel
    {
        private readonly LineAutoHotkeyOptions _options;
        private readonly ILogger<LineAutoHotkeyNotificationService> _logger;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public LineAutoHotkeyNotificationService(
            LineAutoHotkeyOptions options,
            ILogger<LineAutoHotkeyNotificationService> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string ChannelName => "LINE AutoHotkey";

        public bool IsEnabled => _options.Enabled;

        public bool IsConfigured =>
            IsEnabled &&
            !string.IsNullOrWhiteSpace(_options.AutoHotkeyExecutablePath);

        public async Task SendTextMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                _logger.LogWarning("LINE AutoHotkey 未啟用或尚未設定 AutoHotkeyExecutablePath，略過推播。訊息: {Message}", message);
                return;
            }

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var targetChatNames = GetTargetChatNames();
                if (targetChatNames.Length == 0)
                {
                    _logger.LogWarning("LINE AutoHotkey 未設定 TargetChatNames，將沿用目前 LINE 聊天室直接發送。建議設定目標聊天室以避免傳錯對象。");
                    cancellationToken.ThrowIfCancellationRequested();
                    await SendTextMessageToLineWindowAsync(null, message, cancellationToken);
                    return;
                }

                foreach (var chatName in targetChatNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await SendTextMessageToLineWindowAsync(chatName, message, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendTextMessageToLineWindowAsync(string? chatName, string message, CancellationToken cancellationToken)
        {
            string? originalClipboardText = null;
            var hadClipboardText = false;

            if (_options.RestoreClipboard)
            {
                (hadClipboardText, originalClipboardText) = await GetClipboardTextAsync(cancellationToken);
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "SANJET-LineAutoHotkey", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var chatFilePath = Path.Combine(tempDirectory, "chat.txt");
                var messageFilePath = Path.Combine(tempDirectory, "message.txt");
                var scriptFilePath = Path.Combine(tempDirectory, "send-line-message.ahk");

                await File.WriteAllTextAsync(chatFilePath, chatName ?? string.Empty, new UTF8Encoding(false), cancellationToken);
                await File.WriteAllTextAsync(messageFilePath, message, new UTF8Encoding(false), cancellationToken);
                await File.WriteAllTextAsync(scriptFilePath, BuildAutoHotkeyScript(chatFilePath, messageFilePath), new UTF8Encoding(false), cancellationToken);

                await EnsureLineWindowPinnedAsync(cancellationToken);
                await RunAutoHotkeyScriptAsync(scriptFilePath, chatName, cancellationToken);
                TryMinimizeLineWindowAfterSend();
                if (string.IsNullOrWhiteSpace(chatName))
                {
                    _logger.LogInformation("LINE AutoHotkey 故障通知已送出。以目前 LINE 視窗直接貼上並送出。");
                }
                else
                {
                    _logger.LogInformation("LINE AutoHotkey 故障通知已送出。ChatName: {ChatName}", chatName);
                }
            }
            finally
            {
                if (_options.RestoreClipboard && hadClipboardText && originalClipboardText is not null)
                {
                    await SetClipboardTextAsync(originalClipboardText, cancellationToken);
                }

                TryDeleteDirectory(tempDirectory);
            }
        }

        private async Task RunAutoHotkeyScriptAsync(string scriptFilePath, string? chatName, CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _options.AutoHotkeyExecutablePath,
                    Arguments = $"/ErrorStdOut {QuoteArgument(scriptFilePath)}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                },
                EnableRaisingEvents = true
            };

            _logger.LogDebug("啟動 AutoHotkey 傳送 LINE 訊息。ExecutablePath: {ExecutablePath}, ScriptPath: {ScriptPath}, ChatName: {ChatName}",
                _options.AutoHotkeyExecutablePath,
                scriptFilePath,
                chatName);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"無法啟動 AutoHotkey。請確認 AutoHotkeyExecutablePath 是否正確：{_options.AutoHotkeyExecutablePath}", ex);
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(_options.OperationTimeoutSeconds, 5));
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completedTask = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken));

            if (completedTask != waitTask)
            {
                TryKillProcess(process);
                throw new TimeoutException($"AutoHotkey 操作 LINE 超過 {timeout.TotalSeconds:0} 秒仍未完成。ChatName: {chatName ?? "目前聊天室"}");
            }

            await waitTask;
            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"AutoHotkey 傳送 LINE 訊息失敗。ExitCode: {process.ExitCode}, ChatName: {chatName ?? "目前聊天室"}, Output: {standardOutput}, Error: {standardError}");
            }
        }

        private string BuildAutoHotkeyScript(string chatFilePath, string messageFilePath)
        {
            return IsAutoHotkeyV1()
                ? BuildAutoHotkeyV1Script(chatFilePath, messageFilePath)
                : BuildAutoHotkeyV2Script(chatFilePath, messageFilePath);
        }

        private string BuildAutoHotkeyV2Script(string chatFilePath, string messageFilePath)
        {
            var processName = GetLineProcessExecutableName();
            var delay = Math.Max(_options.SendDelayMilliseconds, 300);
            var timeout = Math.Max(_options.OperationTimeoutSeconds, 10);
            var minimizeAfterSend = _options.MinimizeLineWindowAfterSend ? 1 : 0;

            var pinLineWindow = _options.PinLineWindow ? 1 : 0;
            var keepLineWindowTopMost = _options.KeepLineWindowTopMost ? 1 : 0;
            var lineWindowLeft = _options.LineWindowLeft;
            var lineWindowTop = _options.LineWindowTop;
            var lineWindowWidth = _options.LineWindowWidth;
            var lineWindowHeight = _options.LineWindowHeight;

            return $$"""
            #Requires AutoHotkey v2.0
            #SingleInstance Force
            SetTitleMatchMode 2
            SetKeyDelay {{delay}}, {{delay}}
            CoordMode "Mouse", "Screen"

            lineExe := "{{EscapeAutoHotkeyV2String(_options.LineExecutablePath)}}"
            processName := "{{EscapeAutoHotkeyV2String(processName)}}"
            chatFile := "{{EscapeAutoHotkeyV2String(chatFilePath)}}"
            messageFile := "{{EscapeAutoHotkeyV2String(messageFilePath)}}"

            delay := {{delay}}
            timeoutSeconds := {{timeout}}
            minimizeAfterSend := {{minimizeAfterSend}}

            pinLineWindow := {{pinLineWindow}}
            keepLineWindowTopMost := {{keepLineWindowTopMost}}
            lineWindowLeft := {{lineWindowLeft}}
            lineWindowTop := {{lineWindowTop}}
            lineWindowWidth := {{lineWindowWidth}}
            lineWindowHeight := {{lineWindowHeight}}

            targetChatName := Trim(FileRead(chatFile, "UTF-8"), "`r`n`t ")
            message := FileRead(messageFile, "UTF-8")

            if (Trim(targetChatName) = "") {
                MsgBox "TargetChatNames 是空的，已取消，避免貼到目前聊天室。", "LINE 發送取消", "Icon!"
                ExitApp 31
            }

            if (Trim(message) = "") {
                MsgBox "訊息內容是空的，已取消。", "LINE 發送取消", "Icon!"
                ExitApp 25
            }

            if !ProcessExist(processName) {
                if (lineExe = "")
                    ExitApp 20

                Run lineExe
                Sleep delay * 10
            }

            if !WinWait("ahk_exe " processName, , timeoutSeconds)
                ExitApp 21

            lineWindow := WinExist("ahk_exe " processName)

            if !lineWindow
                ExitApp 21

            WinActivate "ahk_id " lineWindow

            if !WinWaitActive("ahk_id " lineWindow, , timeoutSeconds)
                ExitApp 22

            Sleep delay

            ; ==================================================
            ; 依 appsettings.json 固定 LINE 視窗位置與大小
            ; ==================================================
            if (pinLineWindow) {
                WinMove lineWindowLeft, lineWindowTop, lineWindowWidth, lineWindowHeight, "ahk_id " lineWindow
                Sleep delay

                WinActivate "ahk_id " lineWindow
                Sleep delay
            }

            if (keepLineWindowTopMost) {
                WinSetAlwaysOnTop 1, "ahk_id " lineWindow
                Sleep delay
            }

            ; ==================================================
            ; 搜尋並切換聊天室
            ; ==================================================
            OpenTargetChat(lineWindow, targetChatName, delay)

            ; ==================================================
            ; 點擊輸入框
            ; ==================================================
            FocusLineMessageInput(lineWindow, delay)

            ; ==================================================
            ; 貼上訊息
            ; ==================================================
            A_Clipboard := ""
            Sleep 50
            A_Clipboard := message

            if !ClipWait(2)
                ExitApp 24

            Send "^v"
            Sleep delay

            ; 測試階段先不要自動送出
            Send "{Enter}"

            Sleep delay * 2

            if (minimizeAfterSend) {
                WinSetAlwaysOnTop 0, "ahk_id " lineWindow
                Sleep delay
                WinMinimize "ahk_id " lineWindow
            }

            ExitApp 0


            OpenTargetChat(lineWindow, targetChatName, delay) {
                WinActivate "ahk_id " lineWindow
                Sleep delay

                ; LINE 已固定在 0,0,1000,800
                ; 搜尋框座標
                ; RyanPC：138,101
                ; 機聯網PC聊天 : 110,80
                ; 機聯網PC好友 : 110,50
                Click 110,50
                Sleep delay

                Send "^a"
                Sleep delay
                Send "{Backspace}"
                Sleep delay

                A_Clipboard := ""
                Sleep 50
                A_Clipboard := targetChatName

                if !ClipWait(1) {
                    MsgBox "聊天室名稱寫入剪貼簿失敗。", "LINE 搜尋失敗", "Icon!"
                    ExitApp 23
                }

                Send "^v"
                Sleep delay * 2

                ; 搜尋結果第一筆座標
                ; RyanPC：250,210
                ; 機聯網PC聊天 : 180,160
                ; 機聯網PC好友 : 200,135
                Click 200,135
                Sleep delay * 2
            }

            FocusLineMessageInput(lineWindow, delay) {
                WinActivate "ahk_id " lineWindow
                Sleep delay

                ; LINE 輸入框座標：
                ; RyanPC：495,675
                ; 機聯網PC : 385,700
                Click 385,700
                Sleep delay
            }
            """;                                
        }

        private string BuildAutoHotkeyV1Script(string chatFilePath, string messageFilePath)
        {
            var processName = GetLineProcessExecutableName();
            var delay = Math.Max(_options.SendDelayMilliseconds, 100);
            var timeout = Math.Max(_options.OperationTimeoutSeconds, 5);
            var minimizeAfterSend = _options.MinimizeLineWindowAfterSend ? 1 : 0;

            return $$"""
                #NoEnv
                #SingleInstance Force
                SetTitleMatchMode, 2
                SetKeyDelay, {{delay}}, {{delay}}
                CoordMode, Mouse, Screen

                lineExe := "{{EscapeAutoHotkeyV1String(_options.LineExecutablePath)}}"
                processName := "{{EscapeAutoHotkeyV1String(processName)}}"
                chatFile := "{{EscapeAutoHotkeyV1String(chatFilePath)}}"
                messageFile := "{{EscapeAutoHotkeyV1String(messageFilePath)}}"
                delay := {{delay}}
                timeoutSeconds := {{timeout}}
                minimizeAfterSend := {{minimizeAfterSend}}

                FileRead, targetChatName, *P65001 %chatFile%
                FileRead, message, *P65001 %messageFile%
                targetChatName := Trim(targetChatName, "`r`n`t ")

                Process, Exist, %processName%
                if (ErrorLevel = 0) {
                    if (lineExe = "")
                        ExitApp, 20
                    Run, %lineExe%
                }

                WinWait, ahk_exe %processName%,, %timeoutSeconds%
                if ErrorLevel
                    ExitApp, 21

                WinGet, lineWindow, ID, ahk_exe %processName%
                if (!lineWindow)
                    ExitApp, 21

                WinActivate, ahk_id %lineWindow%
                WinWaitActive, ahk_id %lineWindow%,, %timeoutSeconds%
                if ErrorLevel
                    ExitApp, 22

                if (targetChatName != "") {
                    Gosub, IsTargetChatActive
                    if (!isTargetChatActive)
                        Gosub, OpenTargetChat
                }

                Gosub, FocusLineMessageInput

                Clipboard := message
                ClipWait, 2
                if ErrorLevel
                    ExitApp, 24

                Send, ^v
                Sleep, %delay%
                Send, {Enter}
                sleepAfterSend := delay * 2
                Sleep, %sleepAfterSend%
                if (minimizeAfterSend) {
                    WinSet, AlwaysOnTop, Off, ahk_id %lineWindow%
                    Sleep, %delay%
                    WinMinimize, ahk_id %lineWindow%
                }
                ExitApp, 0

                IsTargetChatActive:
                    WinGetTitle, windowTitle, ahk_id %lineWindow%
                    isTargetChatActive := InStr(windowTitle, targetChatName) > 0
                Return

                OpenTargetChat:
                    WinActivate, ahk_id %lineWindow%
                    Clipboard := targetChatName
                    ClipWait, 2
                    if ErrorLevel
                        ExitApp, 23

                    Send, ^f
                    Sleep, %delay%
                    Send, ^a
                    Sleep, %delay%
                    Send, ^v
                    Sleep, %delay%
                    Send, {Enter}
                    sleepAfterOpen := delay * 3
                    Sleep, %sleepAfterOpen%
                    Send, {Esc}
                    Sleep, %delay%
                Return

                FocusLineMessageInput:
                    WinActivate, ahk_id %lineWindow%
                    WinGetPos, windowX, windowY, windowWidth, windowHeight, ahk_id %lineWindow%
                    clickX := windowX + Round(windowWidth * 0.55)
                    clickY := windowY + windowHeight - 85
                    Click, %clickX%, %clickY%
                    Sleep, %delay%
                Return
                """;
        }


        private async Task EnsureLineWindowPinnedAsync(CancellationToken cancellationToken)
        {
            if (!_options.PinLineWindow)
            {
                return;
            }

            var windowHandle = await WaitForLineWindowAsync(cancellationToken);
            if (windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"找不到 LINE 視窗，無法固定視窗位置。ProcessName: {GetLineProcessExecutableName()}");
            }

            ShowWindow(windowHandle, ShowWindowCommand.Restore);

            var width = Math.Max(_options.LineWindowWidth, 320);
            var height = Math.Max(_options.LineWindowHeight, 240);
            var insertAfter = _options.KeepLineWindowTopMost ? HwndTopMost : HwndNoTopMost;
            const SetWindowPosFlags flags = SetWindowPosFlags.ShowWindow;

            if (!SetWindowPos(windowHandle, insertAfter, _options.LineWindowLeft, _options.LineWindowTop, width, height, flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "固定 LINE 視窗位置失敗。");
            }

            _logger.LogDebug(
                "LINE 視窗已透過 Win32 API 固定。Handle: {WindowHandle}, TopMost: {TopMost}, X: {X}, Y: {Y}, Width: {Width}, Height: {Height}",
                windowHandle,
                _options.KeepLineWindowTopMost,
                _options.LineWindowLeft,
                _options.LineWindowTop,
                width,
                height);
        }

        private void TryMinimizeLineWindowAfterSend()
        {
            if (!_options.MinimizeLineWindowAfterSend)
            {
                return;
            }

            var windowHandle = FindLineMainWindow(GetLineProcessNameWithoutExtension());
            if (windowHandle == IntPtr.Zero)
            {
                _logger.LogWarning("MinimizeLineWindowAfterSend 已啟用，但找不到 LINE 視窗可最小化。ProcessName: {ProcessName}", GetLineProcessExecutableName());
                return;
            }

            if (_options.KeepLineWindowTopMost)
            {
                SetWindowPos(windowHandle, HwndNoTopMost, 0, 0, 0, 0, SetWindowPosFlags.NoMove | SetWindowPosFlags.NoSize | SetWindowPosFlags.NoActivate);
            }

            if (ShowWindow(windowHandle, ShowWindowCommand.Minimize))
            {
                _logger.LogDebug("LINE 視窗已在發送後透過 Win32 API 最小化。Handle: {WindowHandle}", windowHandle);
            }
            else
            {
                _logger.LogWarning("MinimizeLineWindowAfterSend 已啟用，但 Win32 API 最小化 LINE 視窗失敗。Handle: {WindowHandle}", windowHandle);
            }
        }

        private async Task<IntPtr> WaitForLineWindowAsync(CancellationToken cancellationToken)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(_options.OperationTimeoutSeconds, 5));
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var processName = GetLineProcessNameWithoutExtension();

            EnsureLineProcessStarted(processName);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lineWindow = FindLineMainWindow(processName);
                if (lineWindow != IntPtr.Zero)
                {
                    return lineWindow;
                }

                await Task.Delay(250, cancellationToken);
            }

            return IntPtr.Zero;
        }

        private void EnsureLineProcessStarted(string processName)
        {
            if (Process.GetProcessesByName(processName).Length > 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.LineExecutablePath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _options.LineExecutablePath,
                    UseShellExecute = true
                });

                _logger.LogInformation("LINE 未執行，已嘗試啟動 LINE。ExecutablePath: {LineExecutablePath}", _options.LineExecutablePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"無法啟動 LINE。請確認 LineExecutablePath 是否正確：{_options.LineExecutablePath}", ex);
            }
        }

        private static IntPtr FindLineMainWindow(string processName)
        {
            var processIds = Process.GetProcessesByName(processName)
                .Select(process =>
                {
                    try
                    {
                        process.Refresh();
                        if (process.MainWindowHandle != IntPtr.Zero && IsWindowVisible(process.MainWindowHandle))
                        {
                            return process.MainWindowHandle;
                        }

                        return IntPtr.Zero;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                })
                .Where(handle => handle != IntPtr.Zero)
                .ToArray();

            if (processIds.Length > 0)
            {
                return processIds[0];
            }

            var candidateProcessIds = Process.GetProcessesByName(processName)
                .Select(process =>
                {
                    try
                    {
                        return process.Id;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                })
                .ToHashSet();

            if (candidateProcessIds.Count == 0)
            {
                return IntPtr.Zero;
            }

            var result = IntPtr.Zero;
            EnumWindows((windowHandle, _) =>
            {
                GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                if (!candidateProcessIds.Contains((int)windowProcessId) || !IsWindowVisible(windowHandle))
                {
                    return true;
                }

                var titleLength = GetWindowTextLength(windowHandle);
                if (titleLength <= 0)
                {
                    return true;
                }

                result = windowHandle;
                return false;
            }, IntPtr.Zero);

            return result;
        }



        private string[] GetTargetChatNames() =>
            _options.TargetChatNames
                .Where(chatName => !string.IsNullOrWhiteSpace(chatName))
                .Select(chatName => chatName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private bool IsAutoHotkeyV1() =>
            string.Equals(_options.AutoHotkeyVersion, "v1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_options.AutoHotkeyVersion, "1", StringComparison.OrdinalIgnoreCase);

        private string GetLineProcessExecutableName()
        {
            var processName = string.IsNullOrWhiteSpace(_options.LineProcessName)
                ? "LINE"
                : _options.LineProcessName.Trim();

            return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName
                : $"{processName}.exe";
        }

        private string GetLineProcessNameWithoutExtension()
        {
            var executableName = GetLineProcessExecutableName();
            return executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? executableName[..^4]
                : executableName;
        }

        private static async Task<(bool HadText, string? Text)> GetClipboardTextAsync(CancellationToken cancellationToken)
        {
            return await RunOnUiThreadAsync<(bool HadText, string? Text)>(() =>
            {
                if (!Clipboard.ContainsText())
                {
                    return (false, null);
                }

                return (true, Clipboard.GetText());
            }, cancellationToken);
        }

        private static async Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
        {
            await RunOnUiThreadAsync(() => Clipboard.SetText(text), cancellationToken);
        }

        private static Task<T> RunOnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                return Task.FromResult(action());
            }

            return dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Send, cancellationToken).Task;
        }

        private static Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken)
        {
            return RunOnUiThreadAsync(() =>
            {
                action();
                return true;
            }, cancellationToken);
        }

        private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

        private static string EscapeAutoHotkeyV2String(string value) =>
            value.Replace("`", "``").Replace("\"", "`\"").Replace("\r", "`r").Replace("\n", "`n");

        private static string EscapeAutoHotkeyV1String(string value) =>
            value.Replace("`", "``").Replace("\"", "\"\"").Replace("\r", "`r").Replace("\n", "`n");


        private static readonly IntPtr HwndTopMost = new(-1);
        private static readonly IntPtr HwndNoTopMost = new(-2);

        private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr lParam);

        [Flags]
        private enum SetWindowPosFlags : uint
        {
            NoSize = 0x0001,
            NoMove = 0x0002,
            NoActivate = 0x0010,
            ShowWindow = 0x0040
        }

        private enum ShowWindowCommand
        {
            Minimize = 6,
            Restore = 9
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            SetWindowPosFlags uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, ShowWindowCommand nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        private void TryDeleteDirectory(string tempDirectory)
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "刪除 AutoHotkey 暫存目錄失敗。TempDirectory: {TempDirectory}", tempDirectory);
            }
        }
    }
}
