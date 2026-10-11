using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace EldenRingDeathCounter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new CounterOverlayForm());
    }
}

internal sealed class CounterOverlayForm : Form
{
    private const int WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WS_EX_LAYERED = 0x00080000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const uint BI_RGB = 0, DIB_RGB_COLORS = 0, ULW_ALPHA = 2, AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Width, Height; public SIZE(int width, int height) { Width = width; Height = height; } }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hmod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)] private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);

    private readonly HashSet<int> globalKeysDown = new();
    // Preserves short key presses and every key-down edge, including two taps between poll ticks.
    private readonly HashSet<int> keyboardPressedSincePoll = new();
    private readonly Queue<(int VirtualKey, DateTime TimestampUtc)> pendingKeyboardPresses = new();
    private readonly List<(string Binding, DateTime TimestampUtc)> keyboardPressesThisPoll = new();
    private readonly object keyboardLock = new();
    private static readonly JsonSerializerOptions SaveJsonOptions = new() { WriteIndented = true };
    private static readonly string[] KeyboardBindingCache = Enumerable.Range(0, 256).Select(v => $"K:{v}").ToArray();
    private static readonly string[] PadButtonNames = { "A", "B", "X", "Y", "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "Start", "Back", "LeftStick", "RightStick", "LB", "RB", "LT", "RT" };
    private static readonly Dictionary<string, string>[] PadBindingCache = Enumerable.Range(1, 4)
        .Select(controller => PadButtonNames.ToDictionary(button => button, button => $"P{controller}:{button}", StringComparer.Ordinal))
        .ToArray();
    // Keep the delegate rooted for the lifetime of the global hook.
    private LowLevelKeyboardProc? keyboardHookProc;
    private IntPtr keyboardHookHandle = IntPtr.Zero;
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)] private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputGamepad
    {
        public ushort Buttons; public byte LeftTrigger; public byte RightTrigger;
        public short ThumbLX; public short ThumbLY; public short ThumbRX; public short ThumbRY;
    }

    private const int DoubleTapWindowMilliseconds = 450;
    private const int UndoChordHoldMilliseconds = 1000;
    private const int MaximumHistory = 5;
    private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001, XINPUT_GAMEPAD_DPAD_DOWN = 0x0002,
        XINPUT_GAMEPAD_DPAD_LEFT = 0x0004, XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008,
        XINPUT_GAMEPAD_START = 0x0010, XINPUT_GAMEPAD_BACK = 0x0020,
        XINPUT_GAMEPAD_LEFT_THUMB = 0x0040, XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080,
        XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100, XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200,
        XINPUT_GAMEPAD_A = 0x1000, XINPUT_GAMEPAD_B = 0x2000,
        XINPUT_GAMEPAD_X = 0x4000, XINPUT_GAMEPAD_Y = 0x8000;

    private Font? totalOwnedFont, bossOwnedFont;
    // 30 ms gives responsive controls while avoiding unnecessary 50 Hz polling.
    private readonly System.Windows.Forms.Timer inputTimer = new() { Interval = 30 };
    private readonly System.Windows.Forms.Timer settingsSaveTimer = new() { Interval = 750 };
    // Reassert topmost only twice per second so games that reorder windows cannot bury the overlay.
    private readonly System.Windows.Forms.Timer topmostTimer = new() { Interval = 500 };
    private readonly string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldenRingDeathCounter", "counters.json");
    // The TXT defaults to the executable's folder and can be changed from the control panel.
    private string logDirectory = AppContext.BaseDirectory;
    private string lastStatusMessage = "Pronto.";
    private bool reportGeneratedDuringThisRun;
    private readonly List<BossResetAuditEntry> AuditEntries = new();
    private int totalDeaths, bossDeaths;
    private string deathBinding = "K:118"; // F7: double tap adds a death
    private string resetBossBinding = "K:119"; // F8: double tap resets boss deaths
    private Color totalTextColor = Color.FromArgb(234, 198, 125), bossTextColor = Color.White;
    private float totalFontSize = 14, bossFontSize = 12;
    private string totalTextTemplate = "MORTES TOTAIS: {total}";
    private string bossTextTemplate = "MORTES NO BOSS: {boss}";
    // Reuse input sets instead of allocating two new HashSets on every poll.
    private HashSet<string> currentInputs = new(StringComparer.Ordinal);
    private HashSet<string> previousInputs = new(StringComparer.Ordinal);
    private bool xinputUnavailable;
    private DateTime? lastDeathTapUtc;
    private DateTime? lastBossResetTapUtc;
    private DateTime? undoChordStartedUtc;
    private bool undoChordTriggered;
    private Point dragStart;
    private bool dragging;
    private ControlPanelForm? controlPanel;
    private string? captureTarget;

    private sealed class PersistedData
    {
        public int Total { get; set; }
        public int Boss { get; set; }
        public string MainBinding { get; set; } = "";
        // DeathBinding and ResetBinding are separate in the current version.
        public string DeathBinding { get; set; } = "K:118";
        public string ResetBinding { get; set; } = "K:119";
        public List<UndoEntry> History { get; set; } = new();
        public List<BossResetAuditEntry> BossResetAuditEntries { get; set; } = new();
        public string LogDirectory { get; set; } = "";
        public string TotalColor { get; set; } = "#EAC67D";
        public string BossColor { get; set; } = "#FFFFFF";
        public float TotalFontSize { get; set; } = 14;
        public float BossFontSize { get; set; } = 12;
        public string TotalTextTemplate { get; set; } = "MORTES TOTAIS: {total}";
        public string BossTextTemplate { get; set; } = "MORTES NO BOSS: {boss}";
    }
    internal sealed class UndoEntry
    {
        public string Description { get; set; } = "";
        public int PreviousTotal { get; set; }
        public int PreviousBoss { get; set; }
        public DateTime When { get; set; }
        public string? AuditId { get; set; }
    }
    internal sealed class BossResetAuditEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime When { get; set; }
        public int TotalAtReset { get; set; }
        public int BossBeforeReset { get; set; }
        public string? ExportedFilePath { get; set; }
        public bool IsUndone { get; set; }
    }

    public CounterOverlayForm()
    {
        Text = "Elden Ring — Contador de mortes";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = true; TopMost = true;
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.Manual; Location = new Point(32, 80);
        AutoScaleMode = AutoScaleMode.None;
        LoadData();
        TryRepairUndoneLogRecords();
        RefreshDisplay();
        BindDrag(this);

        // No system-tray icon or overlay context menu: the application has a taskbar entry,
        // and a right-click directly on the counter opens the control panel.
        ContextMenuStrip = null;
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) OpenControlPanel(); };

        // A low-level keyboard hook receives physical key transitions even while the game
        // or another application has focus. The callback only queues events; shortcut logic
        // remains on the normal WinForms timer/message-loop thread.
        Shown += (_, _) => InstallGlobalKeyboardHook();
        inputTimer.Tick += (_, _) => PollInputs(); inputTimer.Start();
        topmostTimer.Tick += (_, _) => KeepOverlayOnTop(); topmostTimer.Start();
        settingsSaveTimer.Tick += (_, _) => { settingsSaveTimer.Stop(); SaveData(); };
        FormClosing += (_, _) =>
        {
            inputTimer.Stop(); settingsSaveTimer.Stop(); topmostTimer.Stop();
            if (keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboardHookHandle);
                keyboardHookHandle = IntPtr.Zero;
            }
            keyboardHookProc = null;
            ExportPendingBossResetLog(manual: false);
            TryRepairUndoneLogRecords();
            SaveData();
            totalOwnedFont?.Dispose(); bossOwnedFont?.Dispose();
            inputTimer.Dispose(); settingsSaveTimer.Dispose(); topmostTimer.Dispose();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* Per-pixel layered bitmap owns the background. */ }

    private void KeepOverlayOnTop()
    {
        if (!IsDisposed && IsHandleCreated && Visible)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
    protected override void OnPaint(PaintEventArgs e) { /* Updated only when text/counters change. */ }

    protected override void WndProc(ref Message m)
    {
        // Windows sends these messages during Start > Shut down/restart. Persist synchronously
        // before Windows can close this process, in addition to the normal FormClosing path.
        if (m.Msg == WM_QUERYENDSESSION)
        {
            ExportPendingBossResetLog(manual: false);
            TryRepairUndoneLogRecords();
            SaveData();
            m.Result = new IntPtr(1); // allow shutdown after synchronously exporting the report
            return;
        }
        if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero)
        {
            ExportPendingBossResetLog(manual: false); // guarded against duplicate exports
            TryRepairUndoneLogRecords();
            SaveData();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private void InstallGlobalKeyboardHook()
    {
        if (keyboardHookHandle != IntPtr.Zero) return;

        keyboardHookProc = LowLevelKeyboardHookCallback;
        IntPtr moduleHandle = GetModuleHandle(null);
        keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardHookProc, moduleHandle, 0);

        if (keyboardHookHandle != IntPtr.Zero)
            SetStatusOrRemember("Teclado global ativado. Os atalhos continuam funcionando com o jogo em primeiro plano.");
        else
        {
            int error = Marshal.GetLastWin32Error();
            SetStatusOrRemember($"Falha ao ativar a captura global do teclado (erro Windows {error}).");
        }
    }

    private IntPtr LowLevelKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Do not perform rendering, I/O or shortcut actions inside the hook. Queue only the
        // key edge and let PollInputs process it after the callback returns.
        if (nCode >= 0)
        {
            int message = unchecked((int)wParam.ToInt64());
            bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;
            if (isDown || isUp)
            {
                try
                {
                    var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)key.vkCode;
                    if (vk > 0 && vk < 255)
                    {
                        lock (keyboardLock)
                        {
                            if (isDown)
                            {
                                // Add() filters OS key-repeat; a release followed by a new press
                                // is recorded even when both happen between timer ticks.
                                if (globalKeysDown.Add(vk))
                                {
                                    keyboardPressedSincePoll.Add(vk);
                                    pendingKeyboardPresses.Enqueue((vk, DateTime.UtcNow));
                                    while (pendingKeyboardPresses.Count > 512)
                                        pendingKeyboardPresses.Dequeue();
                                }
                            }
                            else
                            {
                                globalKeysDown.Remove(vk);
                            }
                        }
                    }
                }
                catch
                {
                    // Never allow a managed exception to escape through the native hook.
                }
            }
        }

        return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);
    }

    private static string KeyBinding(int vk) => (uint)vk < KeyboardBindingCache.Length ? KeyboardBindingCache[vk] : $"K:{vk}";
    private static string PadBinding(int controller, string button) => PadBindingCache[controller - 1][button];

    private void ReadCurrentInputs()
    {
        // The global keyboard hook remains active while the game has focus. Drain every queued
        // key-down edge so two quick taps cannot collapse into one HashSet entry.
        int deathVk = GetKeyboardVirtualKey(deathBinding);
        int resetVk = GetKeyboardVirtualKey(resetBossBinding);
        bool deathPressQueued = false, resetPressQueued = false;
        keyboardPressesThisPoll.Clear();
        lock (keyboardLock)
        {
            foreach (int vk in globalKeysDown) currentInputs.Add(KeyBinding(vk));
            foreach (int vk in keyboardPressedSincePoll) currentInputs.Add(KeyBinding(vk));
            if (deathVk >= 0) deathPressQueued = keyboardPressedSincePoll.Contains(deathVk);
            if (resetVk >= 0) resetPressQueued = keyboardPressedSincePoll.Contains(resetVk);
            while (pendingKeyboardPresses.Count > 0)
            {
                var press = pendingKeyboardPresses.Dequeue();
                keyboardPressesThisPoll.Add((KeyBinding(press.VirtualKey), press.TimestampUtc));
            }
            keyboardPressedSincePoll.Clear();
        }

        // The async state query is a small backup for the two configured keys.
        SyncBoundKeyboardKey(deathBinding, deathVk, deathPressQueued);
        SyncBoundKeyboardKey(resetBossBinding, resetVk, resetPressQueued);

        if (xinputUnavailable) return;
        for (int i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState((uint)i, out var state) != 0) continue;
                ushort b = state.Gamepad.Buttons;
                AddPadButton(i + 1, b, XINPUT_GAMEPAD_A, "A"); AddPadButton(i + 1, b, XINPUT_GAMEPAD_B, "B");
                AddPadButton(i + 1, b, XINPUT_GAMEPAD_X, "X"); AddPadButton(i + 1, b, XINPUT_GAMEPAD_Y, "Y");
                AddPadButton(i + 1, b, XINPUT_GAMEPAD_DPAD_UP, "DPadUp"); AddPadButton(i + 1, b, XINPUT_GAMEPAD_DPAD_DOWN, "DPadDown");
                AddPadButton(i + 1, b, XINPUT_GAMEPAD_DPAD_LEFT, "DPadLeft"); AddPadButton(i + 1, b, XINPUT_GAMEPAD_DPAD_RIGHT, "DPadRight");
                AddPadButton(i + 1, b, XINPUT_GAMEPAD_START, "Start"); AddPadButton(i + 1, b, XINPUT_GAMEPAD_BACK, "Back");
                AddPadButton(i + 1, b, XINPUT_GAMEPAD_LEFT_THUMB, "LeftStick"); AddPadButton(i + 1, b, XINPUT_GAMEPAD_RIGHT_THUMB, "RightStick");
                AddPadButton(i + 1, b, XINPUT_GAMEPAD_LEFT_SHOULDER, "LB"); AddPadButton(i + 1, b, XINPUT_GAMEPAD_RIGHT_SHOULDER, "RB");
                if (state.Gamepad.LeftTrigger > 120) currentInputs.Add(PadBinding(i + 1, "LT"));
                if (state.Gamepad.RightTrigger > 120) currentInputs.Add(PadBinding(i + 1, "RT"));
            }
            catch (DllNotFoundException) { xinputUnavailable = true; break; }
            catch (EntryPointNotFoundException) { xinputUnavailable = true; break; }
        }
    }

    private static int GetKeyboardVirtualKey(string binding) =>
        binding.StartsWith("K:", StringComparison.Ordinal) && int.TryParse(binding.AsSpan(2), out int vk) ? vk : -1;

    private void SyncBoundKeyboardKey(string binding, int vk, bool pressQueuedThisTick)
    {
        if (vk < 0) return;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (down) currentInputs.Add(binding);
        else if (!pressQueuedThisTick)
        {
            currentInputs.Remove(binding);
            lock (keyboardLock) globalKeysDown.Remove(vk);
        }
    }

    private void AddPadButton(int controller, ushort buttons, ushort mask, string name)
    {
        if ((buttons & mask) != 0) currentInputs.Add(PadBinding(controller, name));
    }

    private void SwapInputSnapshots()
    {
        (previousInputs, currentInputs) = (currentInputs, previousInputs);
        currentInputs.Clear();
    }

    private void PollInputs()
    {
        ReadCurrentInputs();

        if (captureTarget != null)
        {
            string target = captureTarget;
            string? newlyPressed = keyboardPressesThisPoll.Count > 0
                ? keyboardPressesThisPoll[0].Binding
                : currentInputs.FirstOrDefault(input => input.StartsWith("P", StringComparison.Ordinal) && !previousInputs.Contains(input));

            if (!string.IsNullOrEmpty(newlyPressed))
            {
                if ((target == "death" && newlyPressed == resetBossBinding) ||
                    (target == "reset" && newlyPressed == deathBinding))
                {
                    // Leave capture active so the user can immediately press a different key.
                    if (controlPanel is { IsDisposed: false })
                        controlPanel.SetStatus("As duas teclas precisam ser diferentes. Pressione outra tecla/botão.");
                }
                else
                {
                    if (target == "death") deathBinding = newlyPressed;
                    else resetBossBinding = newlyPressed;
                    captureTarget = null;
                    lastDeathTapUtc = null;
                    lastBossResetTapUtc = null;
                    SaveData(); RefreshAll();
                    if (controlPanel is { IsDisposed: false })
                        controlPanel.SetStatus(target == "death"
                            ? $"Tecla de morte configurada: {FormatBinding(newlyPressed)}"
                            : $"Tecla de reset configurada: {FormatBinding(newlyPressed)}");
                }
            }
            SwapInputSnapshots();
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        // Use the physical state for keyboard bindings. currentInputs also includes one-poll
        // press events, which must not accidentally be mistaken for keys still being held.
        bool bothBindingsDown = IsBindingPhysicallyDown(deathBinding) && IsBindingPhysicallyDown(resetBossBinding);

        if (bothBindingsDown)
        {
            // The two-key chord takes priority over taps. Clear partial double-taps so starting
            // an undo gesture never accidentally completes a death/reset action.
            if (!undoChordStartedUtc.HasValue)
            {
                undoChordStartedUtc = nowUtc;
                undoChordTriggered = false;
                lastDeathTapUtc = null;
                lastBossResetTapUtc = null;
            }
            else if (!undoChordTriggered && (nowUtc - undoChordStartedUtc.Value).TotalMilliseconds >= UndoChordHoldMilliseconds)
            {
                UndoLast();
                undoChordTriggered = true;
                if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Última alteração desfeita (as duas teclas ficaram pressionadas por 1 segundo).");
            }
        }
        else
        {
            undoChordStartedUtc = null;
            undoChordTriggered = false;

            foreach (var press in keyboardPressesThisPoll)
                HandleShortcutPress(press.Binding, press.TimestampUtc);

            // XInput is polled; process only a newly pressed controller button, never a held state.
            foreach (string input in currentInputs)
            {
                if (input.StartsWith("P", StringComparison.Ordinal) && !previousInputs.Contains(input))
                    HandleShortcutPress(input, nowUtc);
            }
        }

        SwapInputSnapshots();
    }

    private bool IsBindingPhysicallyDown(string binding)
    {
        int vk = GetKeyboardVirtualKey(binding);
        if (vk >= 0) return (GetAsyncKeyState(vk) & 0x8000) != 0;
        return currentInputs.Contains(binding); // controller buttons are sampled from XInput
    }

    private void HandleShortcutPress(string binding, DateTime timestampUtc)
    {
        if (binding == deathBinding)
        {
            double elapsedMs = lastDeathTapUtc.HasValue
                ? (timestampUtc - lastDeathTapUtc.Value).TotalMilliseconds
                : double.PositiveInfinity;
            if (elapsedMs >= 0 && elapsedMs <= DoubleTapWindowMilliseconds)
            {
                lastDeathTapUtc = null;
                AddDeath();
                if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Morte registrada: duplo toque na tecla de morte.");
            }
            else
            {
                lastDeathTapUtc = timestampUtc;
                if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Primeiro toque registrado. Pressione a tecla de morte novamente em até 450 ms.");
            }
        }
        else if (binding == resetBossBinding)
        {
            double elapsedMs = lastBossResetTapUtc.HasValue
                ? (timestampUtc - lastBossResetTapUtc.Value).TotalMilliseconds
                : double.PositiveInfinity;
            if (elapsedMs >= 0 && elapsedMs <= DoubleTapWindowMilliseconds)
            {
                lastBossResetTapUtc = null;
                ResetBoss();
                if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Mortes do boss zeradas: duplo toque na tecla de reset.");
            }
            else
            {
                lastBossResetTapUtc = timestampUtc;
                if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Primeiro toque registrado. Pressione a tecla de reset novamente em até 450 ms.");
            }
        }
    }

    internal static string FormatBinding(string binding)
    {
        if (binding.StartsWith("K:") && int.TryParse(binding[2..], out int vk))
        {
            string name = Enum.IsDefined(typeof(Keys), vk) ? ((Keys)vk).ToString() : $"Tecla {vk}";
            return $"Teclado: {name}";
        }
        if (binding.StartsWith("P"))
        {
            int colon = binding.IndexOf(':');
            if (colon > 1) return $"Controle {binding[1..colon]}: {binding[(colon + 1)..]}";
        }
        return binding;
    }
    internal string DeathBindingDisplay => FormatBinding(deathBinding);
    internal string ResetBossBindingDisplay => FormatBinding(resetBossBinding);
    internal void BeginCapture(string target = "death")
    {
        captureTarget = target == "reset" ? "reset" : "death";
        if (controlPanel is { IsDisposed: false })
            controlPanel.SetStatus(captureTarget == "death"
                ? "Aguardando a tecla/botão que registrará mortes..."
                : "Aguardando uma tecla/botão diferente para zerar o boss...");
    }

    private void AddDeath() => ApplyCommand($"Morte registrada (duplo toque em {FormatBinding(deathBinding)})", () => { totalDeaths++; bossDeaths++; });
    private void ResetBoss()
    {
        var audit = new BossResetAuditEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            When = DateTime.Now,
            TotalAtReset = totalDeaths,
            BossBeforeReset = bossDeaths
        };
        AuditEntries.Add(audit);
        // A reset is an undoable command even when the boss counter was already zero:
        // undoing it also removes its audit record from pending/exported TXT files.
        ApplyCommand($"Mortes do boss zeradas (duplo toque em {FormatBinding(resetBossBinding)})",
            () => bossDeaths = 0, audit.Id, recordEvenIfUnchanged: true);
    }

    private void ExportPendingBossResetLog(bool manual)
    {
        var pending = AuditEntries
            .Where(entry => !entry.IsUndone && string.IsNullOrWhiteSpace(entry.ExportedFilePath))
            .OrderBy(entry => entry.When)
            .ToList();

        // Manual export always creates a report. At shutdown, create one empty report on the
        // first close of this run, but don't create a duplicate after a manual export.
        if (pending.Count == 0 && !manual && reportGeneratedDuringThisRun)
            return;

        string? createdPath = null;
        try
        {
            string directory = string.IsNullOrWhiteSpace(logDirectory) ? AppContext.BaseDirectory : logDirectory;
            Directory.CreateDirectory(directory);
            DateTime reportCreatedAt = DateTime.Now;
            string stem = $"historico-boss_{reportCreatedAt:yyyy-MM-dd_HH-mm-ss}";

            for (int suffix = 0; suffix < 10000; suffix++)
            {
                string filename = suffix == 0 ? $"{stem}.txt" : $"{stem}_{suffix:000}.txt";
                string candidate = Path.Combine(directory, filename);
                FileStream stream;
                try
                {
                    stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // A report with that timestamp already exists. Never overwrite it.
                    continue;
                }

                using (stream)
                {
                    createdPath = candidate;
                    using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true))
                    {
                        WriteBossResetReport(writer, pending, reportCreatedAt, pending.Count == 0);
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                break;
            }

            if (createdPath == null)
                throw new IOException("Não foi possível criar um nome exclusivo para o arquivo TXT.");

            foreach (var entry in pending)
                entry.ExportedFilePath = createdPath;

            reportGeneratedDuringThisRun = true;
            PruneAuditEntries();
            SaveData();
            SetStatusOrRemember(pending.Count == 0
                ? $"TXT criado sem resets pendentes: {createdPath}"
                : $"TXT criado com {pending.Count} reset(s): {createdPath}");
        }
        catch (Exception ex)
        {
            // Keep the reset events pending on failure so the next manual attempt or shutdown retries.
            if (!string.IsNullOrWhiteSpace(createdPath))
            {
                try { File.Delete(createdPath); } catch { }
            }
            SetStatusOrRemember($"Falha ao gerar o histórico TXT: {ex.Message}");
        }
    }

    private static void WriteBossResetReport(StreamWriter writer, IReadOnlyCollection<BossResetAuditEntry> entries,
        DateTime createdAt, bool isEmptyReport)
    {
        writer.WriteLine("HISTÓRICO DE RESETS DE BOSS — ELDEN RING DEATH COUNTER");
        writer.WriteLine($"Arquivo criado em: {createdAt:dd/MM/yyyy HH:mm:ss}");
        writer.WriteLine("Cada registro representa um comando para zerar as mortes do boss.");
        writer.WriteLine(new string('-', 76));

        if (isEmptyReport)
        {
            writer.WriteLine("Nenhum reset de boss estava pendente no momento da geração deste arquivo.");
            return;
        }

        foreach (var entry in entries.Where(entry => !entry.IsUndone).OrderBy(entry => entry.When))
        {
            writer.WriteLine();
            writer.WriteLine($"Data e hora do reset: {entry.When:dd/MM/yyyy HH:mm:ss}");
            writer.WriteLine("Ação: mortes do boss zeradas");
            writer.WriteLine($"Mortes totais no momento do reset: {entry.TotalAtReset}");
            writer.WriteLine($"Mortes no boss antes do reset: {entry.BossBeforeReset}");
            writer.WriteLine("Mortes no boss depois do reset: 0");
            writer.WriteLine(new string('-', 76));
        }
    }

    private bool RewriteExportedLogFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                SetStatusOrRemember($"O reset foi desfeito, mas o TXT exportado não foi encontrado para atualizar: {filePath}");
                return false;
            }

            var remaining = AuditEntries
                .Where(entry => !entry.IsUndone && string.Equals(entry.ExportedFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.When)
                .ToList();
            DateTime createdAt;
            try { createdAt = File.GetCreationTime(filePath); }
            catch { createdAt = DateTime.Now; }

            // Rewriting an existing report is intentional only when undoing a reset. Normal
            // report generation always uses CreateNew and never replaces an older report.
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true))
            {
                WriteBossResetReport(writer, remaining, createdAt, isEmptyReport: remaining.Count == 0);
                if (remaining.Count == 0)
                    writer.WriteLine("O registro original foi removido porque a ação correspondente foi desfeita.");
                writer.Flush();
            }
            stream.Flush(true);
            return true;
        }
        catch (Exception ex)
        {
            SetStatusOrRemember($"O reset foi desfeito, mas não foi possível atualizar o TXT: {ex.Message}");
            return false;
        }
    }

    private void TryRepairUndoneLogRecords()
    {
        bool changed = false;
        foreach (var entry in AuditEntries.Where(entry => entry.IsUndone && !string.IsNullOrWhiteSpace(entry.ExportedFilePath)).ToList())
        {
            if (RewriteExportedLogFile(entry.ExportedFilePath!))
            {
                AuditEntries.Remove(entry);
                changed = true;
            }
        }
        if (changed) SaveData();
    }

    private void PruneAuditEntries()
    {
        var undoableAuditIds = new HashSet<string>(History
            .Where(entry => !string.IsNullOrWhiteSpace(entry.AuditId))
            .Select(entry => entry.AuditId!), StringComparer.Ordinal);

        // If any entry from a report can still be undone (or a failed rewrite is pending),
        // keep every record belonging to that same file. Otherwise rebuilding that file later
        // could accidentally erase older records that have already fallen out of the undo stack.
        var reportPathsNeeded = new HashSet<string>(AuditEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ExportedFilePath) &&
                (entry.IsUndone || undoableAuditIds.Contains(entry.Id)))
            .Select(entry => entry.ExportedFilePath!), StringComparer.OrdinalIgnoreCase);

        AuditEntries.RemoveAll(entry =>
            (entry.IsUndone && string.IsNullOrWhiteSpace(entry.ExportedFilePath)) ||
            (!entry.IsUndone && !string.IsNullOrWhiteSpace(entry.ExportedFilePath) &&
                !reportPathsNeeded.Contains(entry.ExportedFilePath)));
    }

    internal string LogDirectory => string.IsNullOrWhiteSpace(logDirectory) ? AppContext.BaseDirectory : logDirectory;
    internal string StatusMessage => lastStatusMessage;

    internal void SetLogDirectory(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Escolha uma pasta válida.");
            string fullPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(fullPath);
            logDirectory = fullPath;
            SaveData();
            SetStatusOrRemember($"Destino dos TXT alterado para: {logDirectory}");
            if (controlPanel is { IsDisposed: false, Visible: true }) controlPanel.RefreshLogDirectory();
        }
        catch (Exception ex)
        {
            SetStatusOrRemember($"Não foi possível alterar o destino do TXT: {ex.Message}");
        }
    }

    internal void GenerateLogFromPanel() => ExportPendingBossResetLog(manual: true);

    private void SetStatusOrRemember(string message)
    {
        lastStatusMessage = message;
        if (controlPanel is { IsDisposed: false, Visible: true })
            controlPanel.SetStatus(message);
    }

    private void SetCounts(int total, int boss) => ApplyCommand($"Contadores editados: total {totalDeaths} → {total}; boss {bossDeaths} → {boss}", () => { totalDeaths = Math.Max(0, total); bossDeaths = Math.Max(0, boss); });
    private void ApplyCommand(string description, Action action, string? auditId = null, bool recordEvenIfUnchanged = false)
    {
        int oldTotal = totalDeaths, oldBoss = bossDeaths;
        action();
        if (oldTotal == totalDeaths && oldBoss == bossDeaths && !recordEvenIfUnchanged)
        {
            RefreshAll();
            return;
        }

        History.Insert(0, new UndoEntry
        {
            Description = description,
            PreviousTotal = oldTotal,
            PreviousBoss = oldBoss,
            When = DateTime.Now,
            AuditId = auditId
        });
        if (History.Count > MaximumHistory)
            History.RemoveRange(MaximumHistory, History.Count - MaximumHistory);
        PruneAuditEntries();
        SaveData();
        RefreshAll();
    }

    private readonly List<UndoEntry> History = new();

    private void UndoLast()
    {
        if (History.Count == 0)
        {
            SetStatusOrRemember("Não há alterações no histórico para desfazer.");
            return;
        }

        var entry = History[0];
        History.RemoveAt(0);
        totalDeaths = Math.Max(0, entry.PreviousTotal);
        bossDeaths = Math.Max(0, entry.PreviousBoss);

        bool logRewriteFailed = false;
        if (!string.IsNullOrWhiteSpace(entry.AuditId))
        {
            var audit = AuditEntries.FirstOrDefault(candidate => candidate.Id == entry.AuditId);
            if (audit != null)
            {
                string? exportedPath = audit.ExportedFilePath;
                if (string.IsNullOrWhiteSpace(exportedPath))
                {
                    // It has not been exported, so simply omit it from the next report.
                    AuditEntries.Remove(audit);
                }
                else
                {
                    // Mark as undone until the already-created TXT can be rewritten successfully.
                    audit.IsUndone = true;
                    if (RewriteExportedLogFile(exportedPath))
                        AuditEntries.Remove(audit);
                    else
                        logRewriteFailed = true;
                }
            }
        }

        PruneAuditEntries();
        SaveData();
        RefreshAll();
        if (!logRewriteFailed)
            SetStatusOrRemember($"Desfeito: {entry.Description}");
    }
    internal void ExitApplication() => Close();

    private void OpenControlPanel()
    {
        if (controlPanel == null || controlPanel.IsDisposed) controlPanel = new ControlPanelForm(this);
        controlPanel.ReloadFromOwner(); controlPanel.Show(); controlPanel.BringToFront(); controlPanel.Activate();
    }
    private static string RenderTemplate(string template, int total, int boss) =>
        template.Replace("{total}", total.ToString(), StringComparison.OrdinalIgnoreCase)
                .Replace("{boss}", boss.ToString(), StringComparison.OrdinalIgnoreCase);

    private void RefreshDisplay()
    {
        if (totalOwnedFont == null || totalOwnedFont.SizeInPoints != totalFontSize)
        {
            var next = new Font("Segoe UI", totalFontSize, FontStyle.Bold, GraphicsUnit.Point);
            var old = totalOwnedFont; totalOwnedFont = next; old?.Dispose();
        }
        if (bossOwnedFont == null || bossOwnedFont.SizeInPoints != bossFontSize)
        {
            var next = new Font("Segoe UI", bossFontSize, FontStyle.Bold, GraphicsUnit.Point);
            var old = bossOwnedFont; bossOwnedFont = next; old?.Dispose();
        }

        string totalText = RenderTemplate(totalTextTemplate, totalDeaths, bossDeaths);
        string bossText = RenderTemplate(bossTextTemplate, totalDeaths, bossDeaths);
        using var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        measureGraphics.PageUnit = GraphicsUnit.Pixel;
        using var totalPath = CreateTextPath(totalText, totalOwnedFont, measureGraphics);
        using var bossPath = CreateTextPath(bossText, bossOwnedFont, measureGraphics);
        RectangleF totalBounds = totalPath.GetBounds();
        RectangleF bossBounds = bossPath.GetBounds();

        const float margin = 2.5f;
        float lineGap = Math.Max(14f, Math.Max(totalFontSize, bossFontSize) * 0.55f);
        float contentWidth = Math.Max(totalBounds.Width, bossBounds.Width);
        float contentHeight = totalBounds.Height + lineGap + bossBounds.Height;
        int bitmapWidth = Math.Clamp((int)Math.Ceiling(contentWidth + margin * 2 + 2), 1, 16000);
        int bitmapHeight = Math.Clamp((int)Math.Ceiling(contentHeight + margin * 2 + 2), 1, 4000);

        TranslatePathTo(totalPath, margin - totalBounds.X, margin - totalBounds.Y);
        TranslatePathTo(bossPath, margin - bossBounds.X, margin + totalBounds.Height + lineGap - bossBounds.Y);

        using var bitmap = new Bitmap(bitmapWidth, bitmapHeight, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var outline = new Pen(Color.Black, 1.15f) { LineJoin = LineJoin.Round })
        using (var totalBrush = new SolidBrush(totalTextColor))
        using (var bossBrush = new SolidBrush(bossTextColor))
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            if (totalPath.PointCount > 0) { g.DrawPath(outline, totalPath); g.FillPath(totalBrush, totalPath); }
            if (bossPath.PointCount > 0) { g.DrawPath(outline, bossPath); g.FillPath(bossBrush, bossPath); }
        }

        Size = new Size(bitmapWidth, bitmapHeight);
        PresentLayeredBitmap(bitmap);
    }

    private static GraphicsPath CreateTextPath(string text, Font font, Graphics graphics)
    {
        var path = new GraphicsPath();
        if (!string.IsNullOrEmpty(text))
        {
            float emSizePixels = font.SizeInPoints * graphics.DpiY / 72f;
            path.AddString(text, font.FontFamily, (int)font.Style, emSizePixels, PointF.Empty, StringFormat.GenericTypographic);
        }
        return path;
    }

    private static void TranslatePathTo(GraphicsPath path, float x, float y)
    {
        if (path.PointCount == 0) return;
        using var transform = new Matrix();
        transform.Translate(x, y);
        path.Transform(transform);
    }

    private void PresentLayeredBitmap(Bitmap bitmap)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return;
        IntPtr memoryDc = IntPtr.Zero, dib = IntPtr.Zero, oldObject = IntPtr.Zero;
        BitmapData? bitmapData = null;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return;
            var info = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = bitmap.Width, biHeight = -bitmap.Height,
                biPlanes = 1, biBitCount = 32, biCompression = BI_RGB, biSizeImage = (uint)(bitmap.Width * bitmap.Height * 4)
            };
            dib = CreateDIBSection(screenDc, ref info, DIB_RGB_COLORS, out IntPtr dibPixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || dibPixels == IntPtr.Zero) return;
            oldObject = SelectObject(memoryDc, dib);
            bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            int rowBytes = checked(bitmap.Width * 4);
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr sourceRow = bitmapData.Stride >= 0
                    ? IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride)
                    : IntPtr.Add(bitmapData.Scan0, (bitmap.Height - 1 - y) * -bitmapData.Stride);
                IntPtr destinationRow = IntPtr.Add(dibPixels, y * rowBytes);
                CopyMemory(destinationRow, sourceRow, (UIntPtr)(uint)rowBytes);
            }
            var destination = new POINT(Left, Top);
            var source = new POINT(0, 0);
            var size = new SIZE(bitmap.Width, bitmap.Height);
            var blend = new BLENDFUNCTION { BlendOp = (byte)AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = (byte)AC_SRC_ALPHA };
            if (!UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA))
            {
                SetStatusOrRemember($"Falha ao atualizar o overlay transparente (erro Windows {Marshal.GetLastWin32Error()}).");
            }
            else
            {
                // UpdateLayeredWindow updates pixels/position; explicitly restore TOPMOST z-order too.
                KeepOverlayOnTop();
            }
        }
        finally
        {
            if (bitmapData != null) bitmap.UnlockBits(bitmapData);
            if (oldObject != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, oldObject);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal Color TotalTextColor => totalTextColor;
    internal Color BossTextColor => bossTextColor;
    internal float TotalFontSize => totalFontSize;
    internal float BossFontSize => bossFontSize;
    internal string TotalTextTemplate => totalTextTemplate;
    internal string BossTextTemplate => bossTextTemplate;
    internal void SetTextStyle(bool total, Color color, float size)
    {
        size = Math.Clamp(size, 6, 72);
        if (total) { totalTextColor = color; totalFontSize = size; }
        else { bossTextColor = color; bossFontSize = size; }
        RefreshDisplay(); ScheduleSettingsSave();
    }
    internal void SetTextTemplates(string total, string boss)
    {
        totalTextTemplate = total ?? ""; bossTextTemplate = boss ?? "";
        RefreshDisplay(); SaveData();
    }
    private void ScheduleSettingsSave()
    {
        settingsSaveTimer.Stop(); settingsSaveTimer.Start();
    }
    private void RefreshAll()
    {
        RefreshDisplay();
        // The hidden panel is reloaded when opened, so do not rebuild its history on every death.
        if (controlPanel is { IsDisposed: false, Visible: true }) controlPanel.RefreshFromOwner();
    }
    internal int TotalDeaths => totalDeaths;
    internal int BossDeaths => bossDeaths;
    internal IReadOnlyList<UndoEntry> UndoHistory => History;
    internal void EditCounts(int total, int boss) => SetCounts(total, boss);
    internal void UndoFromPanel() => UndoLast();

    private void LoadData()
    {
        try
        {
            if (!File.Exists(savePath)) return;
            var saved = JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(savePath));
            if (saved == null) return;

            totalDeaths = Math.Max(0, saved.Total);
            bossDeaths = Math.Max(0, saved.Boss);
            if (!string.IsNullOrWhiteSpace(saved.DeathBinding)) deathBinding = saved.DeathBinding;
            else if (!string.IsNullOrWhiteSpace(saved.MainBinding)) deathBinding = saved.MainBinding;
            if (!string.IsNullOrWhiteSpace(saved.ResetBinding) && saved.ResetBinding != deathBinding)
                resetBossBinding = saved.ResetBinding;
            else
                resetBossBinding = deathBinding == "K:119" ? "K:118" : "K:119";

            try { totalTextColor = ColorTranslator.FromHtml(saved.TotalColor); } catch { }
            try { bossTextColor = ColorTranslator.FromHtml(saved.BossColor); } catch { }
            totalFontSize = Math.Clamp(saved.TotalFontSize, 6, 72);
            bossFontSize = Math.Clamp(saved.BossFontSize, 6, 72);
            totalTextTemplate = saved.TotalTextTemplate ?? "MORTES TOTAIS: {total}";
            bossTextTemplate = saved.BossTextTemplate ?? "MORTES NO BOSS: {boss}";

            History.Clear();
            History.AddRange(saved.History ?? new List<UndoEntry>());
            if (History.Count > MaximumHistory)
                History.RemoveRange(MaximumHistory, History.Count - MaximumHistory);

            if (!string.IsNullOrWhiteSpace(saved.LogDirectory))
            {
                try { logDirectory = Path.GetFullPath(saved.LogDirectory); }
                catch { logDirectory = AppContext.BaseDirectory; }
            }
            else
                logDirectory = AppContext.BaseDirectory;

            AuditEntries.Clear();
            AuditEntries.AddRange(saved.BossResetAuditEntries ?? new List<BossResetAuditEntry>());
            PruneAuditEntries();
        }
        catch
        {
            totalDeaths = 0;
            bossDeaths = 0;
            History.Clear();
            AuditEntries.Clear();
            logDirectory = AppContext.BaseDirectory;
        }
    }

    private void SaveData()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            var data = new PersistedData
            {
                Total = totalDeaths,
                Boss = bossDeaths,
                MainBinding = deathBinding,
                DeathBinding = deathBinding,
                ResetBinding = resetBossBinding,
                History = History,
                BossResetAuditEntries = AuditEntries,
                LogDirectory = LogDirectory,
                TotalColor = ColorTranslator.ToHtml(totalTextColor),
                BossColor = ColorTranslator.ToHtml(bossTextColor),
                TotalFontSize = totalFontSize,
                BossFontSize = bossFontSize,
                TotalTextTemplate = totalTextTemplate,
                BossTextTemplate = bossTextTemplate
            };
            string tempPath = savePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(data, SaveJsonOptions));
            File.Move(tempPath, savePath, overwrite: true);
        }
        catch (Exception ex)
        {
            SetStatusOrRemember($"Falha ao salvar as configurações: {ex.Message}");
        }
    }
    private void BindDrag(Control control)
    {
        control.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; } };
        control.MouseMove += (_, e) => { if (dragging && e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y); };
        control.MouseUp += (_, _) => dragging = false;
    }
}

internal sealed class ControlPanelForm : Form
{
    private readonly CounterOverlayForm owner;
    private readonly FlowLayoutPanel sections = new();
    private readonly NumericUpDown totalInput = new(), bossInput = new();
    private readonly NumericUpDown totalSizeInput = new(), bossSizeInput = new();
    private readonly TextBox totalTextInput = new(), bossTextInput = new();
    private readonly ListBox historyList = new();
    private readonly Label statusLabel = new(), deathBindingLabel = new(), resetBindingLabel = new();
    private readonly Button undoButton = new(), totalColorButton = new(), bossColorButton = new();
    private readonly TextBox logDirectoryInput = new();
    private readonly Button browseLogDirectoryButton = new(), openLogDirectoryButton = new(), generateLogButton = new();

    public ControlPanelForm(CounterOverlayForm owner)
    {
        this.owner = owner;
        Text = "Elden Ring — Painel de controle";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        MinimumSize = new Size(900, 700);
        ClientSize = new Size(1180, 900);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11.0f);
        KeyPreview = true;

        // Use a real responsive layout instead of placing every control at a fixed X/Y.
        // The sections resize with the window; the vertical panel scrolls on short displays.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        sections.Dock = DockStyle.Fill;
        sections.AutoScroll = true;
        sections.FlowDirection = FlowDirection.TopDown;
        sections.WrapContents = false;
        sections.Padding = new Padding(24, 18, 24, 22);
        sections.Margin = Padding.Empty;
        sections.SizeChanged += (_, _) => ResizeSections();
        sections.ClientSizeChanged += (_, _) => ResizeSections();

        var statusBar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 0), Margin = Padding.Empty };
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.AutoEllipsis = true;
        statusLabel.ForeColor = Color.DimGray;
        statusLabel.Font = new Font("Segoe UI", 10.5f);
        statusBar.Controls.Add(statusLabel);

        root.Controls.Add(sections, 0, 0);
        root.Controls.Add(statusBar, 0, 1);
        Controls.Add(root);

        BuildCounterSection();
        BuildBindingsSection();
        BuildHistorySection();
        BuildLogSection();
        BuildTextSection();
        BuildAppearanceSection();
        ResizeSections();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        ReloadFromOwner();
        SetStatus(owner.StatusMessage);
    }

    private GroupBox NewSection(string title, int height, out TableLayoutPanel layout)
    {
        var group = new GroupBox
        {
            Text = title,
            Height = height,
            Width = Math.Max(500, sections.ClientSize.Width - 40),
            Margin = new Padding(6, 8, 6, 12),
            Padding = new Padding(14)
        };
        layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            // GroupBox already reserves space for its caption; keep a modest inner padding.
            Padding = new Padding(14, 12, 14, 12),
            ColumnCount = 1,
            RowCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        group.Controls.Add(layout);
        sections.Controls.Add(group);
        return group;
    }

    private void ResizeSections()
    {
        // ClientSize already excludes a visible scrollbar, so leave only the padding/margins.
        int width = Math.Max(500, sections.ClientSize.Width - sections.Padding.Horizontal - 24);
        foreach (Control control in sections.Controls)
            control.Width = width;
    }

    private void BuildCounterSection()
    {
        NewSection("CONTADORES DE MORTES", 286, out var layout);
        layout.ColumnCount = 2;
        layout.RowCount = 3;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));

        layout.Controls.Add(MakeLabel("Mortes totais"), 0, 0);
        layout.Controls.Add(MakeLabel("Mortes no boss atual"), 1, 0);
        ConfigureNumber(totalInput);
        ConfigureNumber(bossInput);
        layout.Controls.Add(totalInput, 0, 1);
        layout.Controls.Add(bossInput, 1, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 8, 0, 6), Padding = Padding.Empty };
        var apply = new Button { Text = "Aplicar valores", AutoSize = true, MinimumSize = new Size(160, 42), Margin = new Padding(0, 0, 12, 0), Padding = new Padding(8, 4, 8, 4) };
        apply.Click += (_, _) => { owner.EditCounts((int)totalInput.Value, (int)bossInput.Value); SetStatus("Valores atualizados; a alteração entrou no histórico."); };
        var reload = new Button { Text = "Recarregar valores", AutoSize = true, MinimumSize = new Size(180, 42), Margin = new Padding(0), Padding = new Padding(8, 4, 8, 4) };
        reload.Click += (_, _) => ReloadFromOwner();
        actions.Controls.Add(apply);
        actions.Controls.Add(reload);
        layout.Controls.Add(actions, 0, 2);
        layout.SetColumnSpan(actions, 2);
    }

    private void BuildBindingsSection()
    {
        NewSection("ATALHOS — DUPLO TOQUE E DESFAZER", 320, out var layout);
        layout.ColumnCount = 2;
        layout.RowCount = 3;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));

        ConfigureBindingLabel(deathBindingLabel);
        ConfigureBindingLabel(resetBindingLabel);

        var captureDeath = new Button
        {
            Text = "Configurar tecla de morte",
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 5, 4, 5),
            MinimumSize = new Size(0, 42)
        };
        captureDeath.Click += (_, _) => owner.BeginCapture("death");
        var captureReset = new Button
        {
            Text = "Configurar tecla de reset",
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 5, 4, 5),
            MinimumSize = new Size(0, 42)
        };
        captureReset.Click += (_, _) => owner.BeginCapture("reset");

        var instructions = new Label
        {
            Text = "MORTES: pressione a primeira tecla duas vezes em até 450 ms para adicionar 1 morte.\n" +
                   "ZERAR BOSS: pressione a segunda tecla duas vezes em até 450 ms.\n" +
                   "DESFAZER: mantenha as duas teclas pressionadas juntas por 1 segundo.",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            Margin = new Padding(4, 8, 4, 8),
            Font = new Font("Segoe UI", 10.5f)
        };

        layout.Controls.Add(deathBindingLabel, 0, 0);
        layout.Controls.Add(captureDeath, 1, 0);
        layout.Controls.Add(resetBindingLabel, 0, 1);
        layout.Controls.Add(captureReset, 1, 1);
        layout.Controls.Add(instructions, 0, 2);
        layout.SetColumnSpan(instructions, 2);
    }

    private static void ConfigureBindingLabel(Label label)
    {
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = false;
        label.AutoSize = false;
        label.Margin = new Padding(4, 6, 10, 6);
        label.Padding = new Padding(4, 0, 4, 0);
    }

    private void BuildHistorySection()
    {
        NewSection("HISTÓRICO — ÚLTIMAS 5 ALTERAÇÕES", 430, out var layout);
        layout.RowCount = 2;
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        historyList.Dock = DockStyle.Fill;
        historyList.HorizontalScrollbar = true;
        historyList.IntegralHeight = false;
        historyList.Margin = new Padding(2, 4, 2, 12);
        historyList.Font = new Font("Segoe UI", 10.5f);
        historyList.DrawMode = DrawMode.OwnerDrawFixed;
        historyList.IntegralHeight = false;
        UpdateHistoryItemHeight();
        historyList.HandleCreated += (_, _) => UpdateHistoryItemHeight();
        historyList.FontChanged += (_, _) => UpdateHistoryItemHeight();
        DpiChanged += (_, _) => UpdateHistoryItemHeight();
        historyList.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();

            string itemText = historyList.Items[e.Index]?.ToString() ?? string.Empty;
            // Keep the text away from row edges and reserve enough vertical room at high DPI.
            Rectangle textBounds = Rectangle.Inflate(e.Bounds, -10, -5);
            TextRenderer.DrawText(e.Graphics, itemText, historyList.Font, textBounds, historyList.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            using (var separator = new Pen(SystemColors.ControlLight))
                e.Graphics.DrawLine(separator, e.Bounds.Left + 8, e.Bounds.Bottom - 1, e.Bounds.Right - 8, e.Bounds.Bottom - 1);
            e.DrawFocusRectangle();
        };
        layout.Controls.Add(historyList, 0, 0);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        undoButton.Text = "Desfazer última alteração";
        undoButton.Dock = DockStyle.Fill;
        undoButton.Margin = new Padding(0, 2, 8, 2);
        undoButton.MinimumSize = new Size(0, 42);
        undoButton.Click += (_, _) => owner.UndoFromPanel();
        var close = new Button { Text = "Fechar painel", Dock = DockStyle.Fill, Margin = new Padding(8, 2, 0, 2), MinimumSize = new Size(0, 42) };
        close.Click += (_, _) => Hide();
        actions.Controls.Add(undoButton, 0, 0);
        actions.Controls.Add(close, 1, 0);
        layout.Controls.Add(actions, 0, 1);
    }

    private void BuildLogSection()
    {
        NewSection("HISTÓRICO EM TXT", 330, out var layout);
        layout.ColumnCount = 3;
        layout.RowCount = 4;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334f));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var help = MakeLabel("Os arquivos são criados com data e hora no nome; arquivos anteriores não são substituídos.");
        help.AutoEllipsis = true;
        layout.Controls.Add(help, 0, 0);
        layout.SetColumnSpan(help, 3);

        logDirectoryInput.ReadOnly = true;
        logDirectoryInput.Dock = DockStyle.Fill;
        logDirectoryInput.Font = new Font("Segoe UI", 10.5f);
        logDirectoryInput.Margin = new Padding(4, 6, 4, 8);
        logDirectoryInput.MinimumSize = new Size(100, 36);
        layout.Controls.Add(logDirectoryInput, 0, 1);
        layout.SetColumnSpan(logDirectoryInput, 3);

        browseLogDirectoryButton.Text = "Alterar destino";
        browseLogDirectoryButton.Dock = DockStyle.Fill;
        browseLogDirectoryButton.Margin = new Padding(4, 4, 6, 4);
        browseLogDirectoryButton.MinimumSize = new Size(0, 48);
        browseLogDirectoryButton.Click += (_, _) => ChooseLogDirectory();

        openLogDirectoryButton.Text = "Abrir pasta";
        openLogDirectoryButton.Dock = DockStyle.Fill;
        openLogDirectoryButton.Margin = new Padding(6, 4, 6, 4);
        openLogDirectoryButton.MinimumSize = new Size(0, 48);
        openLogDirectoryButton.Click += (_, _) => OpenLogDirectory();

        generateLogButton.Text = "Gerar TXT agora";
        generateLogButton.Dock = DockStyle.Fill;
        generateLogButton.Margin = new Padding(6, 4, 4, 4);
        generateLogButton.MinimumSize = new Size(0, 48);
        generateLogButton.Click += (_, _) =>
        {
            owner.GenerateLogFromPanel();
            RefreshLogDirectory();
        };

        layout.Controls.Add(browseLogDirectoryButton, 0, 2);
        layout.Controls.Add(openLogDirectoryButton, 1, 2);
        layout.Controls.Add(generateLogButton, 2, 2);

        var exitApplicationButton = new Button
        {
            Text = "Encerrar programa e salvar TXT",
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 4, 4, 4),
            MinimumSize = new Size(0, 44)
        };
        exitApplicationButton.Click += (_, _) => owner.ExitApplication();
        layout.Controls.Add(exitApplicationButton, 0, 3);
        layout.SetColumnSpan(exitApplicationButton, 3);
    }

    private void ChooseLogDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha a pasta onde os relatórios TXT serão salvos.",
            SelectedPath = owner.LogDirectory,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            owner.SetLogDirectory(dialog.SelectedPath);
            RefreshLogDirectory();
        }
    }

    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(owner.LogDirectory);
            Process.Start(new ProcessStartInfo { FileName = owner.LogDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Não foi possível abrir a pasta: {ex.Message}");
        }
    }

    public void RefreshLogDirectory() => logDirectoryInput.Text = owner.LogDirectory;

    private void UpdateHistoryItemHeight()
    {
        if (historyList.IsDisposed) return;
        try
        {
            using Graphics graphics = historyList.CreateGraphics();
            int textHeight = (int)Math.Ceiling(historyList.Font.GetHeight(graphics));
            int scaledPadding = (int)Math.Ceiling(18.0 * Math.Max(96, DeviceDpi) / 96.0);
            historyList.ItemHeight = Math.Max(48, textHeight + scaledPadding);
        }
        catch
        {
            // Safe fallback: a row is deliberately much taller than the default line height.
            historyList.ItemHeight = 48;
        }
    }

    private void BuildTextSection()
    {
        NewSection("TEXTOS DO OVERLAY", 370, out var layout);
        layout.ColumnCount = 2;
        layout.RowCount = 4;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));

        var help = MakeLabel("Use {total} e {boss} para inserir os números. Os textos atuais já vêm preenchidos.");
        help.Dock = DockStyle.Fill;
        help.AutoSize = false;
        help.AutoEllipsis = true;
        layout.Controls.Add(help, 0, 0);
        layout.SetColumnSpan(help, 2);
        layout.Controls.Add(MakeLabel("Mortes totais"), 0, 1);
        layout.Controls.Add(MakeLabel("Mortes no boss"), 0, 2);
        ConfigureTextBox(totalTextInput);
        ConfigureTextBox(bossTextInput);
        layout.Controls.Add(totalTextInput, 1, 1);
        layout.Controls.Add(bossTextInput, 1, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 5, 0, 5), Padding = Padding.Empty };
        var applyText = new Button { Text = "Aplicar textos", AutoSize = true, MinimumSize = new Size(155, 42), Margin = new Padding(0, 0, 12, 0), Padding = new Padding(8, 4, 8, 4) };
        applyText.Click += (_, _) => { owner.SetTextTemplates(totalTextInput.Text, bossTextInput.Text); SetStatus("Textos do overlay atualizados e salvos."); };
        var defaultText = new Button { Text = "Restaurar textos padrão", AutoSize = true, MinimumSize = new Size(220, 42), Margin = new Padding(0), Padding = new Padding(8, 4, 8, 4) };
        defaultText.Click += (_, _) =>
        {
            totalTextInput.Text = "MORTES TOTAIS: {total}";
            bossTextInput.Text = "MORTES NO BOSS: {boss}";
            owner.SetTextTemplates(totalTextInput.Text, bossTextInput.Text);
            SetStatus("Textos padrão restaurados.");
        };
        actions.Controls.Add(applyText);
        actions.Controls.Add(defaultText);
        layout.Controls.Add(actions, 0, 3);
        layout.SetColumnSpan(actions, 2);
    }

    private void BuildAppearanceSection()
    {
        NewSection("APARÊNCIA DO CONTADOR", 270, out var layout);
        layout.ColumnCount = 4;
        layout.RowCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));

        totalColorButton.Text = "Escolher cor";
        totalColorButton.Dock = DockStyle.Fill;
        totalColorButton.Margin = new Padding(4, 6, 6, 6);
        totalSizeInput.Minimum = 6; totalSizeInput.Maximum = 72; totalSizeInput.DecimalPlaces = 0; totalSizeInput.Dock = DockStyle.Fill;
        totalSizeInput.Margin = new Padding(4, 8, 4, 8);
        totalSizeInput.Font = new Font("Segoe UI", 11.0f);
        var totalSizeLabel = MakeLabel("Tamanho (pt)"); totalSizeLabel.Dock = DockStyle.Fill; totalSizeLabel.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(MakeLabel("Mortes totais"), 0, 0);
        layout.Controls.Add(totalColorButton, 1, 0);
        layout.Controls.Add(totalSizeLabel, 2, 0);
        layout.Controls.Add(totalSizeInput, 3, 0);

        bossColorButton.Text = "Escolher cor";
        bossColorButton.Dock = DockStyle.Fill;
        bossColorButton.Margin = new Padding(4, 6, 6, 6);
        bossSizeInput.Minimum = 6; bossSizeInput.Maximum = 72; bossSizeInput.DecimalPlaces = 0; bossSizeInput.Dock = DockStyle.Fill;
        bossSizeInput.Margin = new Padding(4, 8, 4, 8);
        bossSizeInput.Font = new Font("Segoe UI", 11.0f);
        var bossSizeLabel = MakeLabel("Tamanho (pt)"); bossSizeLabel.Dock = DockStyle.Fill; bossSizeLabel.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(MakeLabel("Mortes no boss"), 0, 1);
        layout.Controls.Add(bossColorButton, 1, 1);
        layout.Controls.Add(bossSizeLabel, 2, 1);
        layout.Controls.Add(bossSizeInput, 3, 1);

        totalColorButton.Click += (_, _) => ChooseColor(true);
        bossColorButton.Click += (_, _) => ChooseColor(false);
        totalSizeInput.ValueChanged += (_, _) => owner.SetTextStyle(true, owner.TotalTextColor, (float)totalSizeInput.Value);
        bossSizeInput.ValueChanged += (_, _) => owner.SetTextStyle(false, owner.BossTextColor, (float)bossSizeInput.Value);
    }

    private void ChooseColor(bool total)
    {
        using var dialog = new ColorDialog
        {
            Color = total ? owner.TotalTextColor : owner.BossTextColor,
            FullOpen = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (total)
        {
            totalColorButton.BackColor = dialog.Color;
            totalColorButton.ForeColor = GetContrastingTextColor(dialog.Color);
            owner.SetTextStyle(true, dialog.Color, (float)totalSizeInput.Value);
        }
        else
        {
            bossColorButton.BackColor = dialog.Color;
            bossColorButton.ForeColor = GetContrastingTextColor(dialog.Color);
            owner.SetTextStyle(false, dialog.Color, (float)bossSizeInput.Value);
        }
    }

    private static Color GetContrastingTextColor(Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) >= 145 ? Color.Black : Color.White;

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        Margin = new Padding(4, 5, 12, 5),
        Padding = new Padding(2, 0, 2, 0)
    };

    private static void ConfigureNumber(NumericUpDown control)
    {
        control.Dock = DockStyle.Fill;
        control.Minimum = 0;
        control.Maximum = 999999;
        control.ThousandsSeparator = true;
        control.Font = new Font("Segoe UI", 12.0f);
        control.Margin = new Padding(2, 8, 16, 8);
    }

    private static void ConfigureTextBox(TextBox input)
    {
        input.Dock = DockStyle.Fill;
        input.MaxLength = 200;
        input.Margin = new Padding(2, 8, 12, 8);
        input.MinimumSize = new Size(100, 42);
        input.Font = new Font("Segoe UI", 11.5f);
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
    }

    public void ReloadFromOwner()
    {
        totalInput.Value = Math.Clamp(owner.TotalDeaths, (int)totalInput.Minimum, (int)totalInput.Maximum);
        bossInput.Value = Math.Clamp(owner.BossDeaths, (int)bossInput.Minimum, (int)bossInput.Maximum);
        totalTextInput.Text = owner.TotalTextTemplate;
        bossTextInput.Text = owner.BossTextTemplate;
        totalSizeInput.Value = Math.Clamp((decimal)owner.TotalFontSize, totalSizeInput.Minimum, totalSizeInput.Maximum);
        bossSizeInput.Value = Math.Clamp((decimal)owner.BossFontSize, bossSizeInput.Minimum, bossSizeInput.Maximum);
        UpdateColorButton(totalColorButton, owner.TotalTextColor);
        UpdateColorButton(bossColorButton, owner.BossTextColor);
        RefreshLogDirectory();
        RefreshFromOwner();
    }

    private static void UpdateColorButton(Button button, Color color)
    {
        button.BackColor = color;
        button.ForeColor = GetContrastingTextColor(color);
    }

    public void RefreshFromOwner()
    {
        if (!totalInput.Focused) totalInput.Value = Math.Clamp(owner.TotalDeaths, (int)totalInput.Minimum, (int)totalInput.Maximum);
        if (!bossInput.Focused) bossInput.Value = Math.Clamp(owner.BossDeaths, (int)bossInput.Minimum, (int)bossInput.Maximum);
        deathBindingLabel.Text = "TECLA DE MORTE (duplo toque): " + owner.DeathBindingDisplay;
        resetBindingLabel.Text = "TECLA PARA ZERAR O BOSS (duplo toque): " + owner.ResetBossBindingDisplay;
        historyList.BeginUpdate();
        historyList.Items.Clear();
        foreach (var entry in owner.UndoHistory)
            historyList.Items.Add($"{entry.When:dd/MM/yyyy HH:mm:ss} — {entry.Description}");
        historyList.EndUpdate();
        undoButton.Enabled = owner.UndoHistory.Count > 0;
    }

    public void SetStatus(string message) => statusLabel.Text = message;
}
