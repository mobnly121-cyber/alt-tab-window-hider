using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Vanish
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "Local\\VanishWindowHider", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "Vanish is already running. Look for its icon in the notification area.",
                        "Vanish",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new VanishContext());
                GC.KeepAlive(mutex);
            }
        }
    }

    internal sealed class VanishContext : ApplicationContext
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int VkTab = 0x09;
        private const int VkT = 0x54;
        private const int VkU = 0x55;
        private const int LlkhfAltDown = 0x20;
        private const int SwHide = 0;
        private const int SwShowNormal = 1;
        private const int SwShowMaximized = 3;
        private const int SwShow = 5;
        private const int GaRoot = 2;
        private const int TriplePressWindowMs = 850;

        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem pauseItem;
        private readonly LowLevelKeyboardProc hookProcedure;
        private readonly List<HiddenWindow> hiddenWindows = new List<HiddenWindow>();
        private readonly HashSet<int> keysCurrentlyDown = new HashSet<int>();
        private readonly uint ownProcessId;
        private IntPtr keyboardHook = IntPtr.Zero;
        private Timer recoveryMenuTimer;
        private ContextMenuStrip recoveryMenu;
        private int repeatedKey;
        private int repeatedKeyCount;
        private long lastRepeatedKeyTicks;
        private bool paused;
        private bool shuttingDown;

        public VanishContext()
        {
            ownProcessId = (uint)Process.GetCurrentProcess().Id;
            hookProcedure = KeyboardHookCallback;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Restore last hidden window", null, delegate { RestoreLastWindow(true); });
            menu.Items.Add("Restore all hidden windows", null, delegate { RestoreAllWindows(true); });
            menu.Items.Add(new ToolStripSeparator());

            pauseItem = new ToolStripMenuItem("Pause hiding");
            pauseItem.CheckOnClick = true;
            pauseItem.CheckedChanged += delegate
            {
                paused = pauseItem.Checked;
                ResetRepeatedKeyState();
                UpdateTrayText();
            };
            menu.Items.Add(pauseItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit (restore all)", null, delegate { ExitSafely(); });

            trayIcon = new NotifyIcon();
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { RestoreLastWindow(true); };
            UpdateTrayText();

            keyboardHook = SetWindowsHookEx(WhKeyboardLl, hookProcedure, GetModuleHandle(null), 0);
            if (keyboardHook == IntPtr.Zero)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Vanish could not start its keyboard listener.");
            }

            trayIcon.ShowBalloonTip(
                2500,
                "Vanish is ready",
                "Alt+Tab hides the current window. U U U restores the latest; T T T opens the recovery menu.",
                ToolTipIcon.Info);
        }

        private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return KeyboardHookCallbackCore(code, wParam, lParam);
            }
            catch
            {
                // A global hook must never let an exception escape into Windows.
                return CallNextHookEx(keyboardHook, code, wParam, lParam);
            }
        }

        private IntPtr KeyboardHookCallbackCore(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !shuttingDown)
            {
                int message = wParam.ToInt32();
                var data = (KeyboardHookData)Marshal.PtrToStructure(lParam, typeof(KeyboardHookData));
                int key = unchecked((int)data.VirtualKeyCode);
                bool isDown = message == WmKeyDown || message == WmSysKeyDown;
                bool isUp = message == WmKeyUp || message == WmSysKeyUp;

                if (isUp)
                {
                    keysCurrentlyDown.Remove(key);
                }
                else if (isDown)
                {
                    bool isFreshPress = keysCurrentlyDown.Add(key);

                    if (!paused && key == VkTab && (data.Flags & LlkhfAltDown) != 0)
                    {
                        ResetRepeatedKeyState();
                        if (isFreshPress)
                        {
                            HideForegroundWindow();
                        }

                        // Do not consume Alt+Tab. Windows should still perform its normal
                        // app-switching behavior after Vanish hides the active window.
                    }
                    else if (isFreshPress && (key == VkU || key == VkT) && NoModifierKeysAreDown())
                    {
                        RegisterRepeatedKeyPress(key);
                    }
                    else if (key != VkU && key != VkT)
                    {
                        ResetRepeatedKeyState();
                    }
                }
            }

            return CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        private bool HideForegroundWindow()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }

            window = GetAncestor(window, GaRoot);
            if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window))
            {
                return false;
            }

            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId == ownProcessId || IsShellWindow(window))
            {
                return false;
            }

            WindowPlacement placement = new WindowPlacement();
            placement.Length = Marshal.SizeOf(typeof(WindowPlacement));
            int showCommand = SwShow;
            if (GetWindowPlacement(window, ref placement))
            {
                showCommand = placement.ShowCommand == SwShowMaximized ? SwShowMaximized : SwShowNormal;
            }

            HiddenWindow existing = null;
            for (int index = hiddenWindows.Count - 1; index >= 0; index--)
            {
                if (hiddenWindows[index].Handle == window)
                {
                    existing = hiddenWindows[index];
                    hiddenWindows.RemoveAt(index);
                    break;
                }
            }

            string title = GetWindowTitle(window);
            if (existing == null)
            {
                existing = new HiddenWindow(window, title, showCommand);
            }
            else
            {
                existing.Title = title;
                existing.ShowCommand = showCommand;
            }

            if (!ShowWindowAsync(window, SwHide))
            {
                return false;
            }

            hiddenWindows.Add(existing);
            UpdateTrayText();
            return true;
        }

        private void RegisterRepeatedKeyPress(int key)
        {
            long now = Environment.TickCount & int.MaxValue;
            long elapsed = now - lastRepeatedKeyTicks;
            if (repeatedKey != key
                || repeatedKeyCount == 0
                || elapsed < 0
                || elapsed > TriplePressWindowMs)
            {
                repeatedKey = key;
                repeatedKeyCount = 1;
            }
            else
            {
                repeatedKeyCount++;
            }

            lastRepeatedKeyTicks = now;
            if (repeatedKeyCount >= 3)
            {
                ResetRepeatedKeyState();
                if (key == VkU)
                {
                    RestoreLastWindow(true);
                }
                else
                {
                    QueueRecoveryMenu();
                }
            }
        }

        private void ResetRepeatedKeyState()
        {
            repeatedKey = 0;
            repeatedKeyCount = 0;
            lastRepeatedKeyTicks = 0;
        }

        private void QueueRecoveryMenu()
        {
            if (recoveryMenuTimer != null)
            {
                recoveryMenuTimer.Stop();
                recoveryMenuTimer.Dispose();
            }

            recoveryMenuTimer = new Timer();
            recoveryMenuTimer.Interval = 1;
            recoveryMenuTimer.Tick += delegate
            {
                recoveryMenuTimer.Stop();
                recoveryMenuTimer.Dispose();
                recoveryMenuTimer = null;
                ShowRecoveryMenu();
            };
            recoveryMenuTimer.Start();
        }

        private void ShowRecoveryMenu()
        {
            RemoveClosedWindowsFromHistory();

            DisposePreviousRecoveryMenu();
            recoveryMenu = new ContextMenuStrip();
            var heading = new ToolStripMenuItem("Choose a window to recover");
            heading.Enabled = false;
            recoveryMenu.Items.Add(heading);
            recoveryMenu.Items.Add(new ToolStripSeparator());

            if (hiddenWindows.Count == 0)
            {
                var emptyItem = new ToolStripMenuItem("No hidden windows");
                emptyItem.Enabled = false;
                recoveryMenu.Items.Add(emptyItem);
            }
            else
            {
                for (int index = hiddenWindows.Count - 1; index >= 0; index--)
                {
                    HiddenWindow target = hiddenWindows[index];
                    string label = string.IsNullOrWhiteSpace(target.Title)
                        ? "Untitled window"
                        : target.Title.Trim();
                    if (label.Length > 80)
                    {
                        label = label.Substring(0, 77) + "...";
                    }

                    recoveryMenu.Items.Add(label, null, delegate
                    {
                        RestoreSpecificWindow(target, true);
                    });
                }

                recoveryMenu.Items.Add(new ToolStripSeparator());
                recoveryMenu.Items.Add("Recover all hidden windows", null, delegate
                {
                    RestoreAllWindows(true);
                });
            }

            recoveryMenu.Show(Cursor.Position);
        }

        private void DisposePreviousRecoveryMenu()
        {
            if (recoveryMenu == null)
            {
                return;
            }

            // Never dispose a ContextMenuStrip from inside its Closed event.
            // Windows Forms can still reference the menu while unwinding that event.
            if (recoveryMenu.Visible)
            {
                recoveryMenu.Close();
            }
            recoveryMenu.Dispose();
            recoveryMenu = null;
        }

        private void RemoveClosedWindowsFromHistory()
        {
            for (int index = hiddenWindows.Count - 1; index >= 0; index--)
            {
                if (!IsWindow(hiddenWindows[index].Handle))
                {
                    hiddenWindows.RemoveAt(index);
                }
            }
            UpdateTrayText();
        }

        private bool RestoreSpecificWindow(HiddenWindow target, bool activate)
        {
            int index = hiddenWindows.IndexOf(target);
            if (index >= 0)
            {
                hiddenWindows.RemoveAt(index);
            }

            if (!IsWindow(target.Handle))
            {
                UpdateTrayText();
                return false;
            }

            ShowWindowAsync(target.Handle, target.ShowCommand);
            if (activate)
            {
                SetForegroundWindow(target.Handle);
            }
            UpdateTrayText();
            return true;
        }

        private static bool NoModifierKeysAreDown()
        {
            const int VkShift = 0x10;
            const int VkControl = 0x11;
            const int VkMenu = 0x12;
            const int VkLWin = 0x5B;
            const int VkRWin = 0x5C;

            return !IsKeyDown(VkShift)
                && !IsKeyDown(VkControl)
                && !IsKeyDown(VkMenu)
                && !IsKeyDown(VkLWin)
                && !IsKeyDown(VkRWin);
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private bool RestoreLastWindow(bool activate)
        {
            while (hiddenWindows.Count > 0)
            {
                int index = hiddenWindows.Count - 1;
                HiddenWindow hidden = hiddenWindows[index];
                hiddenWindows.RemoveAt(index);

                if (!IsWindow(hidden.Handle))
                {
                    continue;
                }

                ShowWindowAsync(hidden.Handle, hidden.ShowCommand);
                if (activate)
                {
                    SetForegroundWindow(hidden.Handle);
                }

                UpdateTrayText();
                return true;
            }

            UpdateTrayText();
            return false;
        }

        private void RestoreAllWindows(bool activateLast)
        {
            IntPtr lastRestored = IntPtr.Zero;
            for (int index = 0; index < hiddenWindows.Count; index++)
            {
                HiddenWindow hidden = hiddenWindows[index];
                if (IsWindow(hidden.Handle))
                {
                    ShowWindowAsync(hidden.Handle, hidden.ShowCommand);
                    lastRestored = hidden.Handle;
                }
            }

            hiddenWindows.Clear();
            if (activateLast && lastRestored != IntPtr.Zero)
            {
                SetForegroundWindow(lastRestored);
            }
            UpdateTrayText();
        }

        private void ExitSafely()
        {
            if (shuttingDown)
            {
                return;
            }

            shuttingDown = true;
            RestoreAllWindows(false);
            if (recoveryMenuTimer != null)
            {
                recoveryMenuTimer.Stop();
                recoveryMenuTimer.Dispose();
                recoveryMenuTimer = null;
            }
            DisposePreviousRecoveryMenu();
            if (keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }

            trayIcon.Visible = false;
            trayIcon.Dispose();
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            if (!shuttingDown)
            {
                ExitSafely();
                return;
            }
            base.ExitThreadCore();
        }

        private void UpdateTrayText()
        {
            if (trayIcon == null)
            {
                return;
            }

            string state = paused ? "paused" : "active";
            trayIcon.Text = string.Format("Vanish — {0}; {1} hidden", state, hiddenWindows.Count);
        }

        private static bool IsShellWindow(IntPtr window)
        {
            string className = GetWindowClass(window);
            return string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal)
                || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal)
                || string.Equals(className, "Progman", StringComparison.Ordinal)
                || string.Equals(className, "WorkerW", StringComparison.Ordinal)
                || string.Equals(className, "DV2ControlHost", StringComparison.Ordinal);
        }

        private static string GetWindowTitle(IntPtr window)
        {
            var text = new StringBuilder(512);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        private static string GetWindowClass(IntPtr window)
        {
            var text = new StringBuilder(256);
            GetClassName(window, text, text.Capacity);
            return text.ToString();
        }

        private sealed class HiddenWindow
        {
            public HiddenWindow(IntPtr handle, string title, int showCommand)
            {
                Handle = handle;
                Title = title;
                ShowCommand = showCommand;
            }

            public IntPtr Handle { get; private set; }
            public string Title { get; set; }
            public int ShowCommand { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardHookData
        {
            public uint VirtualKeyCode;
            public uint ScanCode;
            public int Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rectangle
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCommand;
            public Point MinPosition;
            public Point MaxPosition;
            public Rectangle NormalPosition;
        }

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelKeyboardProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, int flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
    }
}
