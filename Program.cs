using System;
using System.Collections.Generic;
using System.Drawing;
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
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
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
    private LowLevelKeyboardProc? keyboardProc;
    private IntPtr keyboardHook;
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)] private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputGamepad
    {
        public ushort Buttons; public byte LeftTrigger; public byte RightTrigger;
        public short ThumbLX; public short ThumbLY; public short ThumbRX; public short ThumbRY;
    }

    private const int UndoHoldMilliseconds = 700;
    private const int MaximumHistory = 5;
    private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001, XINPUT_GAMEPAD_DPAD_DOWN = 0x0002,
        XINPUT_GAMEPAD_DPAD_LEFT = 0x0004, XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008,
        XINPUT_GAMEPAD_START = 0x0010, XINPUT_GAMEPAD_BACK = 0x0020,
        XINPUT_GAMEPAD_LEFT_THUMB = 0x0040, XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080,
        XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100, XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200,
        XINPUT_GAMEPAD_A = 0x1000, XINPUT_GAMEPAD_B = 0x2000,
        XINPUT_GAMEPAD_X = 0x4000, XINPUT_GAMEPAD_Y = 0x8000;

    private readonly Label totalLabel = new();
    private readonly Label bossLabel = new();
    private Font? totalOwnedFont, bossOwnedFont;
    private readonly NotifyIcon tray = new();
    // 30 ms gives responsive controls while avoiding unnecessary 50 Hz polling.
    private readonly System.Windows.Forms.Timer inputTimer = new() { Interval = 30 };
    private readonly System.Windows.Forms.Timer settingsSaveTimer = new() { Interval = 750 };
    private readonly string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldenRingDeathCounter", "counters.json");
    private int totalDeaths, bossDeaths;
    private string deathBinding = "K:118"; // F7
    private string resetBinding = "K:119"; // F8
    private Color totalTextColor = Color.FromArgb(234, 198, 125), bossTextColor = Color.White;
    private float totalFontSize = 14, bossFontSize = 12;
    private string totalTextTemplate = "MORTES TOTAIS: {total}";
    private string bossTextTemplate = "MORTES NO BOSS: {boss}";
    // Reuse input sets instead of allocating two new HashSets on every poll.
    private HashSet<string> currentInputs = new(StringComparer.Ordinal);
    private HashSet<string> previousInputs = new(StringComparer.Ordinal);
    private bool xinputUnavailable;
    private bool chordLatched, chordTriggered;
    private DateTime? chordStarted;
    private Point dragStart;
    private bool dragging;
    private ControlPanelForm? controlPanel;
    private string? captureTarget;
    private string? capturedBindingToIgnore;

    private sealed class PersistedData
    {
        public int Total { get; set; }
        public int Boss { get; set; }
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
        BackColor = Color.Magenta; TransparencyKey = Color.Magenta; Opacity = 1.0;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(2, 1, 2, 1);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Color.Magenta, Margin = new Padding(0), Padding = new Padding(0) };
        totalLabel.AutoSize = true; totalLabel.BackColor = Color.Magenta; totalOwnedFont = new Font("Segoe UI", totalFontSize, FontStyle.Bold); totalLabel.Font = totalOwnedFont; totalLabel.ForeColor = totalTextColor; totalLabel.Margin = new Padding(0, 0, 0, 3);
        bossLabel.AutoSize = true; bossLabel.BackColor = Color.Magenta; bossOwnedFont = new Font("Segoe UI", bossFontSize, FontStyle.Bold); bossLabel.Font = bossOwnedFont; bossLabel.ForeColor = bossTextColor; bossLabel.Margin = new Padding(0);
        layout.Controls.Add(totalLabel, 0, 0); layout.Controls.Add(bossLabel, 0, 1); Controls.Add(layout);
        LoadData(); RefreshDisplay(); BindDrag(this); BindDrag(totalLabel); BindDrag(bossLabel);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir painel de controle", null, (_, _) => OpenControlPanel());
        menu.Items.Add("Registrar morte", null, (_, _) => AddDeath());
        menu.Items.Add("Zerar mortes do boss", null, (_, _) => ResetBoss());
        menu.Items.Add("Desfazer última alteração", null, (_, _) => UndoLast());
        menu.Items.Add("Sair", null, (_, _) => Close());
        ContextMenuStrip = menu; totalLabel.ContextMenuStrip = menu; bossLabel.ContextMenuStrip = menu;
        tray.Icon = SystemIcons.Application; tray.Text = "Elden Ring Death Counter"; tray.ContextMenuStrip = menu; tray.Visible = true;
        tray.DoubleClick += (_, _) => OpenControlPanel();
        keyboardProc = KeyboardHookCallback;
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
        if (keyboardHook == IntPtr.Zero)
        {
            tray.BalloonTipTitle = "Elden Ring Death Counter";
            tray.BalloonTipText = "Não foi possível ativar a captura global do teclado. Feche e reabra o contador; se necessário, execute como administrador.";
            tray.ShowBalloonTip(4000);
        }
        inputTimer.Tick += (_, _) => PollInputs(); inputTimer.Start();
        settingsSaveTimer.Tick += (_, _) => { settingsSaveTimer.Stop(); SaveData(); };
        FormClosing += (_, _) =>
        {
            inputTimer.Stop(); settingsSaveTimer.Stop();
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            SaveData();
            totalOwnedFont?.Dispose(); bossOwnedFont?.Dispose();
            inputTimer.Dispose(); settingsSaveTimer.Dispose();
            tray.Visible = false; tray.Dispose();
        };
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                lock (keyboardLock)
                {
                    int vk = (int)data.vkCode;
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                    {
                        if (globalKeysDown.Add(vk)) keyboardPressedSincePoll.Add(vk);
                    }
                    else globalKeysDown.Remove(vk);
                }
            }
        }
        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }
    private static string KeyBinding(int vk) => (uint)vk < KeyboardBindingCache.Length ? KeyboardBindingCache[vk] : $"K:{vk}";
    private static string PadBinding(int controller, string button) => PadBindingCache[controller - 1][button];

    private void ReadCurrentInputs()
    {
        // Global low-level keyboard hook works while the game is the foreground window.
        lock (keyboardLock)
        {
            foreach (int vk in globalKeysDown) currentInputs.Add(KeyBinding(vk));
            foreach (int vk in keyboardPressedSincePoll) currentInputs.Add(KeyBinding(vk));
            keyboardPressedSincePoll.Clear();
        }

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
                string otherBinding = captureTarget == "death" ? resetBinding : deathBinding;
                if (newlyPressed == otherBinding)
                {
                    if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Esse botão já está atribuído à outra ação. Aperte um botão diferente.");
                }
                else
                {
                    if (captureTarget == "death") deathBinding = newlyPressed; else resetBinding = newlyPressed;
                    capturedBindingToIgnore = newlyPressed;
                    captureTarget = null; SaveData(); RefreshAll();
                    if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus($"Botão registrado: {FormatBinding(newlyPressed)}");
                }
            }
            SwapInputSnapshots(); return;
        }

        bool deathDown = currentInputs.Contains(deathBinding);
        bool resetDown = currentInputs.Contains(resetBinding);
        DateTime now = DateTime.Now;
        if (deathDown && resetDown)
        {
            if (!chordLatched) { chordLatched = true; chordStarted = now; chordTriggered = false; }
            if (!chordTriggered && chordStarted.HasValue && (now - chordStarted.Value).TotalMilliseconds >= UndoHoldMilliseconds)
            { UndoLast(); chordTriggered = true; }
        }
        else
        {
            if (!chordLatched)
            {
                if (previousInputs.Contains(deathBinding) && !deathDown)
                {
                    if (capturedBindingToIgnore != deathBinding) AddDeath();
                    if (capturedBindingToIgnore == deathBinding) capturedBindingToIgnore = null;
                }
                if (previousInputs.Contains(resetBinding) && !resetDown)
                {
                    if (capturedBindingToIgnore != resetBinding) ResetBoss();
                    if (capturedBindingToIgnore == resetBinding) capturedBindingToIgnore = null;
                }
            }
            if (!deathDown && !resetDown) { chordLatched = false; chordStarted = null; chordTriggered = false; }
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
    internal string ResetBindingDisplay => FormatBinding(resetBinding);
    internal void BeginCapture(string target) { captureTarget = target; if (controlPanel is { IsDisposed: false }) controlPanel.SetStatus("Aguardando botão/tecla... aperte o botão desejado."); }

    private void AddDeath() => ApplyCommand($"Morte registrada ({FormatBinding(deathBinding)})", () => { totalDeaths++; bossDeaths++; });
    private void ResetBoss() => ApplyCommand($"Boss zerado ({FormatBinding(resetBinding)})", () => bossDeaths = 0);
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
        string totalText = RenderTemplate(totalTextTemplate, totalDeaths, bossDeaths);
        string bossText = RenderTemplate(bossTextTemplate, totalDeaths, bossDeaths);
        if (totalLabel.Text != totalText) totalLabel.Text = totalText;
        if (bossLabel.Text != bossText) bossLabel.Text = bossText;
        if (totalLabel.ForeColor != totalTextColor) totalLabel.ForeColor = totalTextColor;
        if (bossLabel.ForeColor != bossTextColor) bossLabel.ForeColor = bossTextColor;
        if (totalOwnedFont == null || totalOwnedFont.SizeInPoints != totalFontSize)
        {
            var next = new Font("Segoe UI", totalFontSize, FontStyle.Bold);
            var old = totalOwnedFont; totalOwnedFont = next; totalLabel.Font = next; old?.Dispose();
        }
        if (bossOwnedFont == null || bossOwnedFont.SizeInPoints != bossFontSize)
        {
            var next = new Font("Segoe UI", bossFontSize, FontStyle.Bold);
            var old = bossOwnedFont; bossOwnedFont = next; bossLabel.Font = next; old?.Dispose();
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
            if (!string.IsNullOrWhiteSpace(saved.DeathBinding)) deathBinding = saved.DeathBinding;
            if (!string.IsNullOrWhiteSpace(saved.ResetBinding)) resetBinding = saved.ResetBinding;
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
            File.WriteAllText(savePath, JsonSerializer.Serialize(new PersistedData { Total = totalDeaths, Boss = bossDeaths, DeathBinding = deathBinding, ResetBinding = resetBinding, History = History, TotalColor = ColorTranslator.ToHtml(totalTextColor), BossColor = ColorTranslator.ToHtml(bossTextColor), TotalFontSize = totalFontSize, BossFontSize = bossFontSize, TotalTextTemplate = totalTextTemplate, BossTextTemplate = bossTextTemplate }, SaveJsonOptions));
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
    private readonly NumericUpDown totalInput = new(), bossInput = new();
    private readonly TextBox totalTextInput = new(), bossTextInput = new();
    private readonly ListBox historyList = new();
    private readonly Label statusLabel = new(), deathBindingLabel = new(), resetBindingLabel = new();
    private readonly Button undoButton = new();

    public ControlPanelForm(CounterOverlayForm owner)
    {
        this.owner = owner; Text = "Elden Ring — Painel de controle"; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = true; ClientSize = new Size(620, 800); Font = new Font("Segoe UI", 9.5f);
        Controls.Add(new Label { Text = "CONTADORES DE MORTES", AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(18, 14) });
        AddCaption("Mortes totais", 20, 55); AddCaption("Mortes no boss atual", 320, 55); ConfigureNumber(totalInput, 20, 78); ConfigureNumber(bossInput, 320, 78); Controls.Add(totalInput); Controls.Add(bossInput);
        var apply = new Button { Text = "Aplicar valores", Location = new Point(20, 116), Size = new Size(140, 30) };
        apply.Click += (_, _) => { owner.EditCounts((int)totalInput.Value, (int)bossInput.Value); SetStatus("Valores atualizados; a alteração entrou no histórico."); }; Controls.Add(apply);
        var reload = new Button { Text = "Recarregar valores", Location = new Point(170, 116), Size = new Size(150, 30) }; reload.Click += (_, _) => ReloadFromOwner(); Controls.Add(reload);

        Controls.Add(new Label { Text = "ATALHOS — clique em Definir e aperte uma tecla ou botão do controle", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 160) });
        deathBindingLabel.Location = new Point(22, 190); deathBindingLabel.Size = new Size(235, 25); Controls.Add(deathBindingLabel);
        var deathCapture = new Button { Text = "Definir botão de morte", Location = new Point(260, 186), Size = new Size(170, 30) }; deathCapture.Click += (_, _) => owner.BeginCapture("death"); Controls.Add(deathCapture);
        resetBindingLabel.Location = new Point(22, 226); resetBindingLabel.Size = new Size(235, 25); Controls.Add(resetBindingLabel);
        var resetCapture = new Button { Text = "Definir botão de zerar boss", Location = new Point(260, 222), Size = new Size(200, 30) }; resetCapture.Click += (_, _) => owner.BeginCapture("reset"); Controls.Add(resetCapture);

        Controls.Add(new Label { Text = "HISTÓRICO — últimas 5 alterações (data e horário)", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 270) });
        historyList.Location = new Point(20, 296); historyList.Size = new Size(580, 155); historyList.HorizontalScrollbar = true; Controls.Add(historyList);
        undoButton.Text = "Desfazer última alteração"; undoButton.Location = new Point(20, 464); undoButton.Size = new Size(190, 32); undoButton.Click += (_, _) => owner.UndoFromPanel(); Controls.Add(undoButton);
        var close = new Button { Text = "Fechar painel", Location = new Point(460, 464), Size = new Size(140, 32) }; close.Click += (_, _) => Hide(); Controls.Add(close);
        Controls.Add(new Label { Text = "TEXTOS DO OVERLAY", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 512) });
        Controls.Add(new Label { Text = "Use {total} e {boss} para mostrar os números. Os textos atuais já vêm preenchidos.", AutoSize = true, Location = new Point(20, 536) });
        AddTextRow("Mortes totais", totalTextInput, 562);
        AddTextRow("Mortes no boss", bossTextInput, 592);
        var applyText = new Button { Text = "Aplicar textos", Location = new Point(160, 622), Size = new Size(130, 28) };
        applyText.Click += (_, _) => { owner.SetTextTemplates(totalTextInput.Text, bossTextInput.Text); SetStatus("Textos do overlay atualizados e salvos."); }; Controls.Add(applyText);
        var defaultText = new Button { Text = "Restaurar textos padrão", Location = new Point(300, 622), Size = new Size(180, 28) };
        defaultText.Click += (_, _) => { totalTextInput.Text = "MORTES TOTAIS: {total}"; bossTextInput.Text = "MORTES NO BOSS: {boss}"; owner.SetTextTemplates(totalTextInput.Text, bossTextInput.Text); SetStatus("Textos padrão restaurados."); }; Controls.Add(defaultText);

        Controls.Add(new Label { Text = "APARÊNCIA DO CONTADOR (fundo transparente)", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 661) });
        AddStyleRow("Mortes totais", true, 690);
        AddStyleRow("Mortes no boss", false, 729);
        statusLabel.Location = new Point(20, 768); statusLabel.Size = new Size(580, 26); statusLabel.ForeColor = Color.DimGray; Controls.Add(statusLabel);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        ReloadFromOwner();
    }
    private void AddCaption(string text, int x, int y) => Controls.Add(new Label { Text = text, AutoSize = true, Location = new Point(x, y) });
    private void AddTextRow(string caption, TextBox input, int y)
    {
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(22, y + 4) });
        input.Location = new Point(160, y); input.Size = new Size(440, 25); input.MaxLength = 200; Controls.Add(input);
    }
    private void AddStyleRow(string caption, bool total, int y)
    {
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(22, y + 5) });
        var colorButton = new Button { Text = "Escolher cor", Location = new Point(180, y), Size = new Size(110, 28) }; Controls.Add(colorButton);
        var size = new NumericUpDown { Location = new Point(315, y + 1), Size = new Size(75, 26), Minimum = 6, Maximum = 72, DecimalPlaces = 0, Value = (decimal)(total ? owner.TotalFontSize : owner.BossFontSize) }; Controls.Add(size);
        Controls.Add(new Label { Text = "tamanho", AutoSize = true, Location = new Point(395, y + 5) });
        colorButton.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = total ? owner.TotalTextColor : owner.BossTextColor, FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) owner.SetTextStyle(total, dialog.Color, (float)size.Value);
        };
        size.ValueChanged += (_, _) => owner.SetTextStyle(total, total ? owner.TotalTextColor : owner.BossTextColor, (float)size.Value);
    }
    private static void ConfigureNumber(NumericUpDown c, int x, int y) { c.Location = new Point(x, y); c.Size = new Size(260, 28); c.Minimum = 0; c.Maximum = 999999; c.ThousandsSeparator = true; c.Font = new Font("Segoe UI", 12); }
    public void ReloadFromOwner()
    {
        totalInput.Value = Math.Clamp(owner.TotalDeaths, (int)totalInput.Minimum, (int)totalInput.Maximum);
        bossInput.Value = Math.Clamp(owner.BossDeaths, (int)bossInput.Minimum, (int)bossInput.Maximum);
        totalTextInput.Text = owner.TotalTextTemplate; bossTextInput.Text = owner.BossTextTemplate;
        RefreshFromOwner();
    }
    public void RefreshFromOwner()
    {
        if (!totalInput.Focused) totalInput.Value = Math.Clamp(owner.TotalDeaths, (int)totalInput.Minimum, (int)totalInput.Maximum);
        if (!bossInput.Focused) bossInput.Value = Math.Clamp(owner.BossDeaths, (int)bossInput.Minimum, (int)bossInput.Maximum);
        deathBindingLabel.Text = "Morte: " + owner.DeathBindingDisplay; resetBindingLabel.Text = "Zerar boss: " + owner.ResetBindingDisplay;
        historyList.BeginUpdate(); historyList.Items.Clear();
        foreach (var e in owner.UndoHistory) historyList.Items.Add($"{e.When:dd/MM/yyyy HH:mm:ss} — {e.Description}");
        historyList.EndUpdate(); undoButton.Enabled = owner.UndoHistory.Count > 0;
    }
    public void SetStatus(string message) => statusLabel.Text = message;
}
