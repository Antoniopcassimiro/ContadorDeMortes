using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003, RIDEV_REMOVE = 0x00000001, RIDEV_INPUTSINK = 0x00000100;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const ushort RI_KEY_BREAK = 0x0001;
    private const int WS_EX_LAYERED = 0x00080000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const uint BI_RGB = 0, DIB_RGB_COLORS = 0, ULW_ALPHA = 2, AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Width, Height; public SIZE(int width, int height) { Width = width; Height = height; } }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
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
    // Keeps very short presses visible to the next poll, even if press/release happens between ticks.
    private readonly HashSet<int> keyboardPressedSincePoll = new();
    private readonly object keyboardLock = new();
    private static readonly JsonSerializerOptions SaveJsonOptions = new() { WriteIndented = true };
    private static readonly string[] KeyboardBindingCache = Enumerable.Range(0, 256).Select(v => $"K:{v}").ToArray();
    private static readonly string[] PadButtonNames = { "A", "B", "X", "Y", "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "Start", "Back", "LeftStick", "RightStick", "LB", "RB", "LT", "RT" };
    private static readonly Dictionary<string, string>[] PadBindingCache = Enumerable.Range(1, 4)
        .Select(controller => PadButtonNames.ToDictionary(button => button, button => $"P{controller}:{button}", StringComparer.Ordinal))
        .ToArray();
    private bool rawInputRegistered;
    private IntPtr rawInputBuffer = IntPtr.Zero;
    private uint rawInputBufferSize;
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)] private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputGamepad
    {
        public ushort Buttons; public byte LeftTrigger; public byte RightTrigger;
        public short ThumbLX; public short ThumbLY; public short ThumbRX; public short ThumbRY;
    }

    private const int ResetBossHoldMilliseconds = 2000;
    private const int UndoHoldMilliseconds = 4000;
    private const int MaximumHistory = 5;
    private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001, XINPUT_GAMEPAD_DPAD_DOWN = 0x0002,
        XINPUT_GAMEPAD_DPAD_LEFT = 0x0004, XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008,
        XINPUT_GAMEPAD_START = 0x0010, XINPUT_GAMEPAD_BACK = 0x0020,
        XINPUT_GAMEPAD_LEFT_THUMB = 0x0040, XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080,
        XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100, XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200,
        XINPUT_GAMEPAD_A = 0x1000, XINPUT_GAMEPAD_B = 0x2000,
        XINPUT_GAMEPAD_X = 0x4000, XINPUT_GAMEPAD_Y = 0x8000;

    private Font? totalOwnedFont, bossOwnedFont;
    private readonly NotifyIcon tray = new();
    // 30 ms gives responsive controls while avoiding unnecessary 50 Hz polling.
    private readonly System.Windows.Forms.Timer inputTimer = new() { Interval = 30 };
    private readonly System.Windows.Forms.Timer settingsSaveTimer = new() { Interval = 750 };
    // Reassert topmost only twice per second so games that reorder windows cannot bury the overlay.
    private readonly System.Windows.Forms.Timer topmostTimer = new() { Interval = 500 };
    private readonly string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldenRingDeathCounter", "counters.json");
    private int totalDeaths, bossDeaths;
    private string deathBinding = "K:118"; // F7; one button controls all actions
    private Color totalTextColor = Color.FromArgb(234, 198, 125), bossTextColor = Color.White;
    private float totalFontSize = 14, bossFontSize = 12;
    private string totalTextTemplate = "MORTES TOTAIS: {total}";
    private string bossTextTemplate = "MORTES NO BOSS: {boss}";
    // Reuse input sets instead of allocating two new HashSets on every poll.
    private HashSet<string> currentInputs = new(StringComparer.Ordinal);
    private HashSet<string> previousInputs = new(StringComparer.Ordinal);
    private bool xinputUnavailable;
    private DateTime? buttonPressStarted;
    private bool holdActionTriggered;
    private Point dragStart;
    private bool dragging;
    private ControlPanelForm? controlPanel;
    private string? captureTarget;
    private string? capturedBindingToIgnore;

    private sealed class PersistedData
    {
        public int Total { get; set; }
        public int Boss { get; set; }
        public string MainBinding { get; set; } = "";
        // Legacy fields keep old settings files readable during migration.
        public string DeathBinding { get; set; } = "K:118";
        public string ResetBinding { get; set; } = "K:119";
        public List<UndoEntry> History { get; set; } = new();
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
    }

    public CounterOverlayForm()
    {
        Text = "Elden Ring — Contador de mortes";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; Location = new Point(32, 80);
        AutoScaleMode = AutoScaleMode.None;
        LoadData();
        RefreshDisplay();
        BindDrag(this);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir painel de controle", null, (_, _) => OpenControlPanel());
        menu.Items.Add("Registrar morte", null, (_, _) => AddDeath());
        menu.Items.Add("Zerar mortes do boss", null, (_, _) => ResetBoss());
        menu.Items.Add("Desfazer última alteração", null, (_, _) => UndoLast());
        menu.Items.Add("Sair", null, (_, _) => Close());
        ContextMenuStrip = menu;
        tray.Icon = SystemIcons.Application; tray.Text = "Elden Ring Death Counter"; tray.ContextMenuStrip = menu; tray.Visible = true;
        tray.DoubleClick += (_, _) => OpenControlPanel();

        // Register the keyboard as Raw Input with RIDEV_INPUTSINK. Windows delivers
        // WM_INPUT even when Elden Ring, rather than this overlay, has focus.
        Shown += (_, _) => RegisterGlobalKeyboardInput();
        inputTimer.Tick += (_, _) => PollInputs(); inputTimer.Start();
        topmostTimer.Tick += (_, _) => KeepOverlayOnTop(); topmostTimer.Start();
        settingsSaveTimer.Tick += (_, _) => { settingsSaveTimer.Stop(); SaveData(); };
        FormClosing += (_, _) =>
        {
            inputTimer.Stop(); settingsSaveTimer.Stop(); topmostTimer.Stop();
            if (rawInputRegistered)
            {
                var removeDevice = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero };
                RegisterRawInputDevices(new[] { removeDevice }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            if (rawInputBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(rawInputBuffer); rawInputBuffer = IntPtr.Zero; }
            SaveData();
            totalOwnedFont?.Dispose(); bossOwnedFont?.Dispose();
            inputTimer.Dispose(); settingsSaveTimer.Dispose(); topmostTimer.Dispose();
            tray.Visible = false; tray.Dispose();
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
        if (m.Msg == WM_INPUT)
        {
            ProcessRawKeyboardInput(m.LParam);
            // Windows requires DefWindowProc cleanup for foreground RIM_INPUT packets.
            if ((m.WParam.ToInt64() & 0xFF) == 0) DefWindowProc(Handle, m.Msg, m.WParam, m.LParam);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private void RegisterGlobalKeyboardInput()
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // Generic Desktop Controls
            usUsage = 0x06,     // Keyboard
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = Handle
        };
        rawInputRegistered = RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        tray.BalloonTipTitle = "Elden Ring Death Counter";
        if (rawInputRegistered)
        {
            tray.BalloonTipText = "Captura global do teclado ativada. Os atalhos funcionam mesmo com o jogo em primeiro plano.";
            tray.ShowBalloonTip(1800);
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            tray.BalloonTipText = $"Falha ao registrar entrada global do teclado (erro Windows {error}). Os comandos do teclado podem não funcionar em segundo plano.";
            tray.ShowBalloonTip(4000);
        }
    }

    private void ProcessRawKeyboardInput(IntPtr rawHandle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        uint queryResult = GetRawInputData(rawHandle, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (queryResult == uint.MaxValue || size < headerSize + Marshal.SizeOf<RAWKEYBOARD>() || size > 4096) return;
        if (rawInputBuffer == IntPtr.Zero || rawInputBufferSize < size)
        {
            if (rawInputBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rawInputBuffer);
            rawInputBuffer = Marshal.AllocHGlobal((int)size);
            rawInputBufferSize = size;
        }

        uint actualSize = size;
        uint result = GetRawInputData(rawHandle, RID_INPUT, rawInputBuffer, ref actualSize, headerSize);
        if (result == uint.MaxValue || result < headerSize + Marshal.SizeOf<RAWKEYBOARD>()) return;
        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(rawInputBuffer);
        if (header.dwType != RIM_TYPEKEYBOARD) return;
        var keyboard = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(rawInputBuffer, (int)headerSize));
        int vk = keyboard.VKey;
        if (vk <= 0 || vk >= 255) return;

        lock (keyboardLock)
        {
            if ((keyboard.Flags & RI_KEY_BREAK) == 0)
            {
                // Remember a press even when key-down and key-up arrive between polling ticks.
                if (globalKeysDown.Add(vk)) keyboardPressedSincePoll.Add(vk);
            }
            else globalKeysDown.Remove(vk);
        }
    }

    private static string KeyBinding(int vk) => (uint)vk < KeyboardBindingCache.Length ? KeyboardBindingCache[vk] : $"K:{vk}";
    private static string PadBinding(int controller, string button) => PadBindingCache[controller - 1][button];

    private void ReadCurrentInputs()
    {
        // Raw Input is registered with RIDEV_INPUTSINK, so keyboard state remains available
        // while another application (including the game) owns focus.
        int deathVk = GetKeyboardVirtualKey(deathBinding);
        bool deathPressQueued = false;
        lock (keyboardLock)
        {
            foreach (int vk in globalKeysDown) currentInputs.Add(KeyBinding(vk));
            foreach (int vk in keyboardPressedSincePoll) currentInputs.Add(KeyBinding(vk));
            if (deathVk >= 0) deathPressQueued = keyboardPressedSincePoll.Contains(deathVk);
            keyboardPressedSincePoll.Clear();
        }

        // Cheap backup for the assigned key; also clears stale held-key state.
        SyncBoundKeyboardKey(deathBinding, deathVk, deathPressQueued);

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
            string? newlyPressed = null;
            foreach (string input in currentInputs)
            {
                if (!previousInputs.Contains(input)) { newlyPressed = input; break; }
            }
            if (newlyPressed != null)
            {
                deathBinding = newlyPressed;
                capturedBindingToIgnore = newlyPressed;
                captureTarget = null; SaveData(); RefreshAll();
                if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus($"Botão único registrado: {FormatBinding(newlyPressed)}");
            }
            SwapInputSnapshots(); return;
        }

        bool buttonDown = currentInputs.Contains(deathBinding);
        bool wasButtonDown = previousInputs.Contains(deathBinding);
        DateTime now = DateTime.Now;

        // One button, three mutually exclusive gestures:
        // tap (<2 s) = death; release after 2-<4 s = reset boss; hold 4 s = undo.
        // Reset is intentionally delayed until release so a 4-second hold performs only undo.
        if (buttonDown && !wasButtonDown)
        {
            buttonPressStarted = now;
            holdActionTriggered = false;
        }

        if (buttonDown && buttonPressStarted.HasValue && !holdActionTriggered &&
            (now - buttonPressStarted.Value).TotalMilliseconds >= UndoHoldMilliseconds)
        {
            UndoLast();
            holdActionTriggered = true;
            if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Última alteração desfeita (botão mantido por 4 segundos). ");
        }

        if (!buttonDown && wasButtonDown)
        {
            if (capturedBindingToIgnore == deathBinding)
            {
                capturedBindingToIgnore = null;
            }
            else if (!holdActionTriggered && buttonPressStarted.HasValue)
            {
                double heldMs = (now - buttonPressStarted.Value).TotalMilliseconds;
                if (heldMs >= ResetBossHoldMilliseconds)
                {
                    ResetBoss();
                    if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Mortes do boss zeradas (botão mantido por 2 segundos). ");
                }
                else
                {
                    AddDeath();
                }
            }
            buttonPressStarted = null;
            holdActionTriggered = false;
        }
        SwapInputSnapshots();
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
    internal void BeginCapture(string target = "death") { captureTarget = "death"; if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Aguardando botão/tecla única... aperte o botão desejado."); }

    private void AddDeath() => ApplyCommand($"Morte registrada ({FormatBinding(deathBinding)})", () => { totalDeaths++; bossDeaths++; });
    private void ResetBoss() => ApplyCommand($"Mortes do boss zeradas ({FormatBinding(deathBinding)})", () => bossDeaths = 0);
    private void SetCounts(int total, int boss) => ApplyCommand($"Contadores editados: total {totalDeaths} → {total}; boss {bossDeaths} → {boss}", () => { totalDeaths = Math.Max(0, total); bossDeaths = Math.Max(0, boss); });
    private void ApplyCommand(string description, Action action)
    {
        int oldTotal = totalDeaths, oldBoss = bossDeaths; action();
        if (oldTotal == totalDeaths && oldBoss == bossDeaths) { RefreshAll(); return; }
        History.Insert(0, new UndoEntry { Description = description, PreviousTotal = oldTotal, PreviousBoss = oldBoss, When = DateTime.Now });
        if (History.Count > MaximumHistory) History.RemoveRange(MaximumHistory, History.Count - MaximumHistory);
        SaveData(); RefreshAll();
    }
    private readonly List<UndoEntry> History = new();
    private void UndoLast()
    {
        if (History.Count == 0) { tray.BalloonTipTitle = "Elden Ring Death Counter"; tray.BalloonTipText = "Não há alterações no histórico para desfazer."; tray.ShowBalloonTip(1500); return; }
        var entry = History[0]; History.RemoveAt(0); totalDeaths = Math.Max(0, entry.PreviousTotal); bossDeaths = Math.Max(0, entry.PreviousBoss);
        SaveData(); RefreshAll(); if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus($"Desfeito: {entry.Description}");
    }
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
        const float lineGap = 2.0f;
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
                tray.BalloonTipTitle = "Elden Ring Death Counter";
                tray.BalloonTipText = $"Falha ao atualizar o overlay transparente (erro Windows {Marshal.GetLastWin32Error()}).";
                tray.ShowBalloonTip(2500);
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
            var saved = JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(savePath)); if (saved == null) return;
            totalDeaths = Math.Max(0, saved.Total); bossDeaths = Math.Max(0, saved.Boss);
            // Prefer the new single binding; migrate old settings using their death key.
            if (!string.IsNullOrWhiteSpace(saved.MainBinding)) deathBinding = saved.MainBinding;
            else if (!string.IsNullOrWhiteSpace(saved.DeathBinding)) deathBinding = saved.DeathBinding;
            try { totalTextColor = ColorTranslator.FromHtml(saved.TotalColor); } catch { }
            try { bossTextColor = ColorTranslator.FromHtml(saved.BossColor); } catch { }
            totalFontSize = Math.Clamp(saved.TotalFontSize, 6, 72); bossFontSize = Math.Clamp(saved.BossFontSize, 6, 72);
            totalTextTemplate = saved.TotalTextTemplate ?? "MORTES TOTAIS: {total}";
            bossTextTemplate = saved.BossTextTemplate ?? "MORTES NO BOSS: {boss}";
            History.Clear(); History.AddRange(saved.History ?? new List<UndoEntry>());
            if (History.Count > MaximumHistory) History.RemoveRange(MaximumHistory, History.Count - MaximumHistory);
        }
        catch { totalDeaths = 0; bossDeaths = 0; History.Clear(); }
    }
    private void SaveData()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            File.WriteAllText(savePath, JsonSerializer.Serialize(new PersistedData { Total = totalDeaths, Boss = bossDeaths, MainBinding = deathBinding, DeathBinding = deathBinding, ResetBinding = deathBinding, History = History, TotalColor = ColorTranslator.ToHtml(totalTextColor), BossColor = ColorTranslator.ToHtml(bossTextColor), TotalFontSize = totalFontSize, BossFontSize = bossFontSize, TotalTextTemplate = totalTextTemplate, BossTextTemplate = bossTextTemplate }, SaveJsonOptions));
        }
        catch (Exception ex) { tray.BalloonTipTitle = "Falha ao salvar contador"; tray.BalloonTipText = ex.Message; tray.ShowBalloonTip(1500); }
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
    private readonly Label statusLabel = new(), deathBindingLabel = new();
    private readonly Button undoButton = new(), totalColorButton = new(), bossColorButton = new();

    public ControlPanelForm(CounterOverlayForm owner)
    {
        this.owner = owner;
        Text = "Elden Ring — Painel de controle";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        MinimumSize = new Size(680, 590);
        ClientSize = new Size(900, 900);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
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
        sections.Padding = new Padding(12, 10, 12, 10);
        sections.Margin = Padding.Empty;
        sections.SizeChanged += (_, _) => ResizeSections();
        sections.ClientSizeChanged += (_, _) => ResizeSections();

        var statusBar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0), Margin = Padding.Empty };
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.AutoEllipsis = true;
        statusLabel.ForeColor = Color.DimGray;
        statusBar.Controls.Add(statusLabel);

        root.Controls.Add(sections, 0, 0);
        root.Controls.Add(statusBar, 0, 1);
        Controls.Add(root);

        BuildCounterSection();
        BuildBindingsSection();
        BuildHistorySection();
        BuildTextSection();
        BuildAppearanceSection();
        ResizeSections();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        ReloadFromOwner();
    }

    private GroupBox NewSection(string title, int height, out TableLayoutPanel layout)
    {
        var group = new GroupBox
        {
            Text = title,
            Height = height,
            Width = Math.Max(500, sections.ClientSize.Width - 32),
            Margin = new Padding(3, 3, 3, 8),
            Padding = new Padding(8)
        };
        layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(8, 18, 8, 8),
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
        int width = Math.Max(500, sections.ClientSize.Width - sections.Padding.Horizontal - 8);
        foreach (Control control in sections.Controls)
            control.Width = width;
    }

    private void BuildCounterSection()
    {
        NewSection("CONTADORES DE MORTES", 150, out var layout);
        layout.ColumnCount = 2;
        layout.RowCount = 3;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(MakeLabel("Mortes totais"), 0, 0);
        layout.Controls.Add(MakeLabel("Mortes no boss atual"), 1, 0);
        ConfigureNumber(totalInput);
        ConfigureNumber(bossInput);
        layout.Controls.Add(totalInput, 0, 1);
        layout.Controls.Add(bossInput, 1, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var apply = new Button { Text = "Aplicar valores", AutoSize = true, MinimumSize = new Size(130, 30), Margin = new Padding(0, 0, 8, 0) };
        apply.Click += (_, _) => { owner.EditCounts((int)totalInput.Value, (int)bossInput.Value); SetStatus("Valores atualizados; a alteração entrou no histórico."); };
        var reload = new Button { Text = "Recarregar valores", AutoSize = true, MinimumSize = new Size(145, 30), Margin = new Padding(0) };
        reload.Click += (_, _) => ReloadFromOwner();
        actions.Controls.Add(apply);
        actions.Controls.Add(reload);
        layout.Controls.Add(actions, 0, 2);
        layout.SetColumnSpan(actions, 2);
    }

    private void BuildBindingsSection()
    {
        NewSection("BOTÃO ÚNICO — TOQUE E SEGURE", 112, out var layout);
        layout.ColumnCount = 2;
        layout.RowCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        deathBindingLabel.Dock = DockStyle.Fill;
        deathBindingLabel.TextAlign = ContentAlignment.MiddleLeft;
        deathBindingLabel.AutoEllipsis = true;
        deathBindingLabel.Margin = new Padding(0, 0, 8, 0);
        var capture = new Button { Text = "Definir botão único", Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4) };
        capture.Click += (_, _) => owner.BeginCapture();
        var instructions = new Label
        {
            Text = "Toque: +1 morte    |    Segure 2 s: zerar boss    |    Segure 4 s: desfazer",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Padding.Empty
        };
        layout.Controls.Add(deathBindingLabel, 0, 0);
        layout.Controls.Add(capture, 1, 0);
        layout.Controls.Add(instructions, 0, 1);
        layout.SetColumnSpan(instructions, 2);
    }

    private void BuildHistorySection()
    {
        NewSection("HISTÓRICO — ÚLTIMAS 5 ALTERAÇÕES", 225, out var layout);
        layout.RowCount = 2;
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        historyList.Dock = DockStyle.Fill;
        historyList.HorizontalScrollbar = true;
        historyList.IntegralHeight = false;
        historyList.Margin = new Padding(0, 0, 0, 6);
        layout.Controls.Add(historyList, 0, 0);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        undoButton.Text = "Desfazer última alteração";
        undoButton.Dock = DockStyle.Fill;
        undoButton.Margin = new Padding(0, 0, 6, 0);
        undoButton.Click += (_, _) => owner.UndoFromPanel();
        var close = new Button { Text = "Fechar painel", Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 0) };
        close.Click += (_, _) => Hide();
        actions.Controls.Add(undoButton, 0, 0);
        actions.Controls.Add(close, 1, 0);
        layout.Controls.Add(actions, 0, 1);
    }

    private void BuildTextSection()
    {
        NewSection("TEXTOS DO OVERLAY", 215, out var layout);
        layout.ColumnCount = 2;
        layout.RowCount = 4;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var applyText = new Button { Text = "Aplicar textos", AutoSize = true, MinimumSize = new Size(125, 30), Margin = new Padding(0, 0, 8, 0) };
        applyText.Click += (_, _) => { owner.SetTextTemplates(totalTextInput.Text, bossTextInput.Text); SetStatus("Textos do overlay atualizados e salvos."); };
        var defaultText = new Button { Text = "Restaurar textos padrão", AutoSize = true, MinimumSize = new Size(180, 30), Margin = new Padding(0) };
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
        NewSection("APARÊNCIA DO CONTADOR", 145, out var layout);
        layout.ColumnCount = 4;
        layout.RowCount = 2;
        layout.ColumnStyles.Clear();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        totalColorButton.Text = "Escolher cor";
        totalColorButton.Dock = DockStyle.Fill;
        totalColorButton.Margin = new Padding(3, 2, 5, 3);
        totalSizeInput.Minimum = 6; totalSizeInput.Maximum = 72; totalSizeInput.DecimalPlaces = 0; totalSizeInput.Dock = DockStyle.Fill;
        totalSizeInput.Margin = new Padding(2, 4, 3, 4);
        var totalSizeLabel = MakeLabel("Tamanho (pt)"); totalSizeLabel.Dock = DockStyle.Fill; totalSizeLabel.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(MakeLabel("Mortes totais"), 0, 0);
        layout.Controls.Add(totalColorButton, 1, 0);
        layout.Controls.Add(totalSizeLabel, 2, 0);
        layout.Controls.Add(totalSizeInput, 3, 0);

        bossColorButton.Text = "Escolher cor";
        bossColorButton.Dock = DockStyle.Fill;
        bossColorButton.Margin = new Padding(3, 2, 5, 3);
        bossSizeInput.Minimum = 6; bossSizeInput.Maximum = 72; bossSizeInput.DecimalPlaces = 0; bossSizeInput.Dock = DockStyle.Fill;
        bossSizeInput.Margin = new Padding(2, 4, 3, 4);
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
        Margin = new Padding(0, 1, 4, 1)
    };

    private static void ConfigureNumber(NumericUpDown control)
    {
        control.Dock = DockStyle.Fill;
        control.Minimum = 0;
        control.Maximum = 999999;
        control.ThousandsSeparator = true;
        control.Font = new Font("Segoe UI", 12);
        control.Margin = new Padding(0, 1, 8, 4);
    }

    private static void ConfigureTextBox(TextBox input)
    {
        input.Dock = DockStyle.Fill;
        input.MaxLength = 200;
        input.Margin = new Padding(0, 4, 2, 4);
        input.MinimumSize = new Size(100, 26);
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
        deathBindingLabel.Text = "Botão configurado: " + owner.DeathBindingDisplay;
        historyList.BeginUpdate();
        historyList.Items.Clear();
        foreach (var entry in owner.UndoHistory)
            historyList.Items.Add($"{entry.When:dd/MM/yyyy HH:mm:ss} — {entry.Description}");
        historyList.EndUpdate();
        undoButton.Enabled = owner.UndoHistory.Count > 0;
    }

    public void SetStatus(string message) => statusLabel.Text = message;
}
