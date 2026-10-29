// ============================================================================
// 模块名称: SideButtonMacroService.cs
// 所属区域: Services (后台长效服务层)
// 核心职责:
//   鼠标侧键 → 复制/粘贴 软件级映射服务 (不依赖任何品牌驱动, 对所有发送标准
//   XBUTTON1/XBUTTON2 的鼠标生效: 罗技/雷蛇/蓝牙/2.4G 一视同仁)：
//   1. WH_MOUSE_LL 低级鼠标钩子拦截 WM_XBUTTONDOWN/UP, 吞掉原始"前进/后退"事件
//   2. 默认 前侧键(XBUTTON2) = Ctrl+C, 后侧键(XBUTTON1) = Ctrl+V; 可对调
//   3. 全屏游戏自动停用: 2 秒后台节拍缓存 GameDetector 结果, 钩子回调内只读缓存,
//      绝不在钩子线程做耗时探测 (避免超出 LowLevelHooksTimeout 被系统摘钩)
//   4. 忽略带 LLMHF_INJECTED 标记的注入事件, 防止与其他改键软件互相回环
//   5. 组合键通过 SendInput 在线程池异步注入, 钩子回调立即返回
//   配置键 (HKCU\Software\FerrisPulse): SideButtonMacroEnabled (默认 0),
//   SideButtonMacroSwapped (默认 0), SideButtonMacroPauseInGames (默认 1)
//   线程契约: Start/Stop 必须在拥有消息循环的 UI 线程调用 (低级钩子依赖该线程泵消息)。
// 依赖关系: ConfigManager.cs, GameDetector.cs
//
// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️:
// 1. [契约同步更新]: 任何 AI Agent 在修改按键映射、钩子逻辑或配置键时，【必须】同步更新上方说明。
// 2. [C# 5 兼容铁律]: 严禁使用 C# 6+ 语法。
// 3. [编码格式锁定]: 本文件必须严格保存为【UTF-8 with BOM】编码。
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RazerBatteryTray
{
    public static class SideButtonMacroService
    {
        public const string EnabledKey = "SideButtonMacroEnabled";
        public const string SwappedKey = "SideButtonMacroSwapped";
        public const string PauseInGamesKey = "SideButtonMacroPauseInGames";

        private const int WH_MOUSE_LL = 14;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int XBUTTON1 = 0x0001;   // 后侧键 (浏览器"后退")
        private const int XBUTTON2 = 0x0002;   // 前侧键 (浏览器"前进")
        private const int LLMHF_INJECTED = 0x00000001;

        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_C = 0x43;
        private const ushort VK_V = 0x56;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelMouseProc proc = HookCallback;
        private static IntPtr hookId = IntPtr.Zero;
        private static Timer gameProbeTimer;
        private static volatile bool gameActive;
        private static volatile bool suppressX1Up;
        private static volatile bool suppressX2Up;

        public static bool IsRunning { get { return hookId != IntPtr.Zero; } }
        public static bool Swapped { get; private set; }
        public static bool PauseInGames { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // MOUSEINPUT 是联合体中最大的成员, 必须一并声明, 否则 x64 下 cbSize 偏小导致 SendInput 失败
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>读取注册表配置并按需挂钩; 主窗体启动时在 UI 线程调用一次。</summary>
        public static void LoadFromConfig()
        {
            Swapped = ConfigManager.GetBool(SwappedKey, false);
            PauseInGames = ConfigManager.GetBool(PauseInGamesKey, true);
            SetEnabled(ConfigManager.GetBool(EnabledKey, false), false);
        }

        public static bool SetEnabled(bool enable, bool persist)
        {
            if (persist) ConfigManager.SetBool(EnabledKey, enable);
            if (enable) Start(); else Stop();
            return IsRunning;
        }

        public static void SetSwapped(bool swapped)
        {
            Swapped = swapped;
            ConfigManager.SetBool(SwappedKey, swapped);
        }

        public static void SetPauseInGames(bool pause)
        {
            PauseInGames = pause;
            ConfigManager.SetBool(PauseInGamesKey, pause);
            if (!pause) gameActive = false;
        }

        public static void Start()
        {
            if (hookId != IntPtr.Zero) return;
            try
            {
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    hookId = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch { hookId = IntPtr.Zero; }

            if (hookId != IntPtr.Zero && gameProbeTimer == null)
            {
                gameProbeTimer = new Timer(ProbeGameState, null, 0, 2000);
            }
        }

        public static void Stop()
        {
            if (gameProbeTimer != null)
            {
                gameProbeTimer.Dispose();
                gameProbeTimer = null;
            }
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
            gameActive = false;
            suppressX1Up = false;
            suppressX2Up = false;
        }

        private static void ProbeGameState(object state)
        {
            if (!PauseInGames) { gameActive = false; return; }
            try
            {
                string title;
                gameActive = GameDetector.IsGameRunning(out title);
            }
            catch { gameActive = false; }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                {
                    // MSLLHOOKSTRUCT: POINT pt (8) | mouseData (4) | flags (4) | time | dwExtraInfo
                    int mouseData = Marshal.ReadInt32(lParam, 8);
                    int flags = Marshal.ReadInt32(lParam, 12);
                    int button = (mouseData >> 16) & 0xFFFF;

                    if ((flags & LLMHF_INJECTED) == 0 && (button == XBUTTON1 || button == XBUTTON2))
                    {
                        if (msg == WM_XBUTTONDOWN)
                        {
                            if (!gameActive)
                            {
                                if (button == XBUTTON1) suppressX1Up = true; else suppressX2Up = true;
                                bool isCopy = (button == XBUTTON2) != Swapped;
                                ushort key = isCopy ? VK_C : VK_V;
                                ThreadPool.QueueUserWorkItem(_ => SendCtrlChord(key));
                                return new IntPtr(1);
                            }
                        }
                        else
                        {
                            // 只吞掉"按下时已被接管"的那一次抬起, 保证原始按下/抬起成对出现
                            if (button == XBUTTON1 && suppressX1Up) { suppressX1Up = false; return new IntPtr(1); }
                            if (button == XBUTTON2 && suppressX2Up) { suppressX2Up = false; return new IntPtr(1); }
                        }
                    }
                }
            }
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        private static void SendCtrlChord(ushort vk)
        {
            try
            {
                INPUT[] inputs = new INPUT[4];
                inputs[0] = MakeKey(VK_CONTROL, false);
                inputs[1] = MakeKey(vk, false);
                inputs[2] = MakeKey(vk, true);
                inputs[3] = MakeKey(VK_CONTROL, true);
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            }
            catch { }
        }

        private static INPUT MakeKey(ushort vk, bool keyUp)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.u.ki.wVk = vk;
            input.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
            input.u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
            return input;
        }
    }
}
