// ============================================================================
// 模块名称: LogitechButtonDiversionHelper.cs
// 所属区域: Hardware (硬件驱动层)
// 核心职责:
//   罗技 HID++ 2.0 按键接管 (Feature 0x1B04 REPROG_CONTROLS_V4)：把鼠标顶部按键
//   (CID 0x00C4, SmartShift/滚轮模式切换键) 临时 divert 给本程序，按下时注入 Win+Tab。
//   1. 后台扫描线程每 10 秒枚举 046D 长报文 HID++ 端点 (含蓝牙 "vid&02046d" 路径)，
//      每个端点一个会话线程；蓝牙/有线直连 devIdx=0xFF，接收器 (PID C5xx) 探测 1..6
//   2. 会话线程单线程收发：未匹配的应答一律当作事件分发，读缓冲/OVERLAPPED 均放在
//      非托管内存 (异步 I/O 完成时 GC 不能搬动它们)
//   3. divert 是易失状态 (断开/休眠/切换蓝牙主机后丢失)，以下时机重新下发：
//      0x1D4B 无线状态事件、鼠标静止 20 秒后再次移动 (NotifyMouseActivity)、每 60 秒兜底；
//      曾接管成功的设备下发失败 (正在重连) 时改为每 5 秒重试
//   4. 只接管 0x1B04 getCidInfo 标记可 divert (flags & 0x20) 的 0x00C4；没有该键的设备
//      (如 G502 X) 不做任何改动
//   5. 停用/退出时撤销 divert (flags=0x02)，顶部键恢复原厂功能
//   协议实测 (MX Anywhere 3S 蓝牙, 046D:B037): HID++ 4.5, 0xFF43 / 20 字节端点,
//   0x1B04 @0x0A, CID 0x00C4 flags=0x31 (可 divert)。
//   配置键 (HKCU\Software\FerrisPulse): LogiTopButtonWinTabEnabled (默认 0)
//   软件 ID 固定 0x0B，与主轮询 (0x08) 区分，互不抢应答。
// 依赖关系: LogitechDeviceHelper.cs (SetupAPI/HidD 声明与路径判定), SideButtonMacroService.cs (SendChord),
//   ConfigManager.cs
//
// ⚠️ AI 维护准则 (AI MAINTENANCE DIRECTIVE - DO NOT REMOVE) ⚠️:
// 1. [契约同步更新]: 任何 AI Agent 在修改接管的 CID、报文结构或重新下发时机时，【必须】同步更新上方说明。
// 2. [C# 5 兼容铁律]: 严禁使用 C# 6+ 语法。
// 3. [编码格式锁定]: 本文件必须严格保存为【UTF-8 with BOM】编码。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RazerBatteryTray
{
    public static class LogitechButtonDiversionHelper
    {
        public const string EnabledKey = "LogiTopButtonWinTabEnabled";
        public const ushort CidTopButton = 0x00C4;

        private const byte SoftwareId = 0x0B;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_TAB = 0x09;

        private static readonly object sessionLock = new object();
        private static readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> failedPaths = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool running;
        private static Thread scanThread;
        private static long lastActivityTicks;
        private static int reassertGeneration;

        public static bool IsRunning { get { return running; } }

        public static void LoadFromConfig()
        {
            if (ConfigManager.GetBool(EnabledKey, false)) Start();
        }

        public static void SetEnabled(bool enable)
        {
            ConfigManager.SetBool(EnabledKey, enable);
            if (enable) Start(); else Stop();
        }

        public static void Start()
        {
            if (running) return;
            running = true;
            scanThread = new Thread(ScanLoop);
            scanThread.IsBackground = true;
            scanThread.Name = "LogiButtonDiversionScan";
            scanThread.Start();
        }

        /// <summary>停止并撤销所有 divert；最多等待约 1.5 秒让会话线程把按键还给鼠标。</summary>
        public static void Stop()
        {
            if (!running) return;
            running = false;
            List<Session> snapshot;
            lock (sessionLock)
            {
                snapshot = new List<Session>(sessions.Values);
            }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(1500);
            foreach (Session s in snapshot)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left > 0 && s.Worker != null) s.Worker.Join(left);
            }
        }

        /// <summary>RawInput 鼠标事件入口：静止 20 秒以上后的第一次移动视为"唤醒"，触发重新下发 divert。</summary>
        public static void NotifyMouseActivity()
        {
            long now = DateTime.UtcNow.Ticks;
            long prev = Interlocked.Exchange(ref lastActivityTicks, now);
            if (running && now - prev > TimeSpan.FromSeconds(20).Ticks)
            {
                Interlocked.Increment(ref reassertGeneration);
            }
        }

        private static void ScanLoop()
        {
            // 快速 停用→启用 时旧扫描线程看到 running 又变回 true，用线程身份让它自行退出
            while (running && Thread.CurrentThread == scanThread)
            {
                try { ScanOnce(); } catch { }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(500);
            }
        }

        private static void ScanOnce()
        {
            Guid hidGuid;
            LogitechDeviceHelper.HidD_GetHidGuid(out hidGuid);
            IntPtr devInfo = LogitechDeviceHelper.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12);
            if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return;

            try
            {
                var ifData = new LogitechDeviceHelper.SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                uint memberIdx = 0;
                while (running && LogitechDeviceHelper.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, memberIdx++, ref ifData))
                {
                    uint reqSize;
                    LogitechDeviceHelper.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    if (reqSize == 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 5);
                        if (!LogitechDeviceHelper.SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detail, reqSize, out reqSize, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
                        if (!LogitechDeviceHelper.IsLogitechHidPath(path)) continue;
                        TryAttach(path);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                        ifData.cbSize = Marshal.SizeOf(ifData);
                    }
                }
            }
            finally
            {
                LogitechDeviceHelper.SetupDiDestroyDeviceInfoList(devInfo);
            }
        }

        private static void TryAttach(string path)
        {
            lock (sessionLock)
            {
                if (sessions.ContainsKey(path)) return;
                DateTime failedAt;
                if (failedPaths.TryGetValue(path, out failedAt) && DateTime.UtcNow - failedAt < TimeSpan.FromSeconds(60)) return;
            }

            IntPtr h = LogitechDeviceHelper.CreateFile(path, 0, 0x03, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return;

            int pid = 0;
            LogitechDeviceHelper.HIDP_CAPS caps = new LogitechDeviceHelper.HIDP_CAPS();
            bool capsOk = false;
            try
            {
                var attr = new LogitechDeviceHelper.HIDD_ATTRIBUTES();
                attr.Size = Marshal.SizeOf(attr);
                if (!LogitechDeviceHelper.HidD_GetAttributes(h, ref attr) || attr.VendorID != 0x046D) return;
                pid = attr.ProductID;

                IntPtr preparsed;
                if (LogitechDeviceHelper.HidD_GetPreparsedData(h, out preparsed))
                {
                    capsOk = LogitechDeviceHelper.HidP_GetCaps(preparsed, out caps) == 0x00110000;
                    LogitechDeviceHelper.HidD_FreePreparsedData(preparsed);
                }
            }
            finally
            {
                LogitechDeviceHelper.CloseHandle(h);
            }

            if (!capsOk) return;
            if (caps.UsagePage != 0xFF00 && caps.UsagePage != 0xFF43) return;
            if (caps.InputReportByteLength < 20 || caps.OutputReportByteLength < 20) return;
            if (pid == 0x0ABA) return; // G PRO X 耳机: 无按键可接管, 且有专属通道

            Session s = new Session();
            s.Path = path;
            s.InLen = caps.InputReportByteLength;
            s.OutLen = caps.OutputReportByteLength;
            s.DevIndices = (pid >= 0xC500 && pid <= 0xC5FF)
                ? new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }
                : new byte[] { 0xFF };
            s.Worker = new Thread(s.Run);
            s.Worker.IsBackground = true;
            s.Worker.Name = "LogiButtonDiversion " + pid.ToString("X4");

            lock (sessionLock)
            {
                if (!running || sessions.ContainsKey(path)) return;
                sessions[path] = s;
            }
            s.Worker.Start();
        }

        private static void OnSessionEnded(Session s, bool openFailed)
        {
            lock (sessionLock)
            {
                Session cur;
                if (sessions.TryGetValue(s.Path, out cur) && cur == s) sessions.Remove(s.Path);
                if (openFailed) failedPaths[s.Path] = DateTime.UtcNow;
            }
        }

        private static void OnTopButtonPressed()
        {
            ThreadPool.QueueUserWorkItem(_ => SideButtonMacroService.SendChord(VK_LWIN, VK_TAB));
        }

        private sealed class DevState
        {
            public bool Probed;
            public bool Unsupported;
            public byte ReprogIdx;
            public byte StatusIdx;
            public bool Diverted;
            public bool EverDiverted;
            public bool Pressed;
        }

        private sealed class Session
        {
            public string Path;
            public int InLen;
            public int OutLen;
            public byte[] DevIndices;
            public Thread Worker;

            private IntPtr handle = IntPtr.Zero;
            private IntPtr readEvt, writeEvt, readBuf, writeBuf, readOvl, writeOvl;
            private bool readPending;
            private bool reassertRequested;
            private readonly Dictionary<byte, DevState> devs = new Dictionary<byte, DevState>();

            public void Run()
            {
                bool openFailed = false;
                try
                {
                    handle = CreateFileW(Path, 0xC0000000, 0x03, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                    {
                        handle = IntPtr.Zero;
                        openFailed = true;
                        return;
                    }
                    readEvt = CreateEvent(IntPtr.Zero, true, false, null);
                    writeEvt = CreateEvent(IntPtr.Zero, true, false, null);
                    readBuf = Marshal.AllocHGlobal(InLen);
                    writeBuf = Marshal.AllocHGlobal(OutLen);
                    readOvl = Marshal.AllocHGlobal(OverlappedSize);
                    writeOvl = Marshal.AllocHGlobal(OverlappedSize);

                    for (int i = 0; i < DevIndices.Length; i++) devs[DevIndices[i]] = new DevState();

                    DateTime nextAssert = DateTime.UtcNow.AddSeconds(AssertAll() ? 5 : 60);
                    int seenGen = Thread.VolatileRead(ref reassertGeneration);

                    while (running)
                    {
                        bool fatal;
                        byte[] rep = ReadReport(1000, out fatal);
                        if (fatal) break;
                        if (rep != null) Dispatch(rep);

                        int gen = Thread.VolatileRead(ref reassertGeneration);
                        if (reassertRequested || gen != seenGen || DateTime.UtcNow >= nextAssert)
                        {
                            reassertRequested = false;
                            seenGen = gen;
                            nextAssert = DateTime.UtcNow.AddSeconds(AssertAll() ? 5 : 60);
                        }
                    }
                }
                catch { }
                finally
                {
                    try { ReleaseAll(); } catch { }
                    Cleanup();
                    OnSessionEnded(this, openFailed);
                }
            }

            /// <summary>下发 divert；返回 true 表示曾经接管成功的设备这次失败了 (多半正在重连)，需要尽快重试。</summary>
            private bool AssertAll()
            {
                bool retrySoon = false;
                foreach (byte d in DevIndices)
                {
                    if (!running) return false;
                    DevState st = devs[d];
                    if (st.Unsupported) continue;
                    bool ok = st.Probed || Probe(d, st);
                    byte[] rx = ok ? Request(d, st.ReprogIdx, 3, new byte[] { (byte)(CidTopButton >> 8), (byte)CidTopButton, 0x03, 0x00, 0x00 }, 500) : null;
                    st.Diverted = (rx != null);
                    if (rx != null)
                    {
                        st.EverDiverted = true;
                    }
                    else
                    {
                        if (ok) st.Probed = false; // 离线或换了设备: 下次重新探测特性表
                        if (st.EverDiverted && !st.Unsupported) retrySoon = true;
                    }
                }
                return retrySoon;
            }

            private bool Probe(byte d, DevState st)
            {
                byte[] ping = Request(d, 0x00, 1, new byte[] { 0x00, 0x00, 0x5A }, 400);
                if (ping == null) return false;

                byte[] f = Request(d, 0x00, 0, new byte[] { 0x1B, 0x04 }, 400);
                if (f == null) return false;
                if (f[4] == 0) { st.Unsupported = true; return false; }
                st.ReprogIdx = f[4];

                byte[] fs = Request(d, 0x00, 0, new byte[] { 0x1D, 0x4B }, 400);
                st.StatusIdx = (fs != null) ? fs[4] : (byte)0;

                byte[] cnt = Request(d, st.ReprogIdx, 0, null, 400);
                if (cnt == null) return false;
                bool found = false;
                for (int i = 0; i < cnt[4] && i < 32; i++)
                {
                    byte[] info = Request(d, st.ReprogIdx, 1, new byte[] { (byte)i }, 400);
                    if (info == null) return false;
                    int cid = (info[4] << 8) | info[5];
                    if (cid == CidTopButton && (info[8] & 0x20) != 0) { found = true; break; }
                }
                if (!found) { st.Unsupported = true; return false; }
                st.Probed = true;
                return true;
            }

            private void ReleaseAll()
            {
                if (handle == IntPtr.Zero) return;
                foreach (KeyValuePair<byte, DevState> kv in devs)
                {
                    if (!kv.Value.Diverted) continue;
                    kv.Value.Diverted = false;
                    Request(kv.Key, kv.Value.ReprogIdx, 3, new byte[] { (byte)(CidTopButton >> 8), (byte)CidTopButton, 0x02, 0x00, 0x00 }, 300);
                }
            }

            private void Dispatch(byte[] rep)
            {
                if (rep.Length < 4 || rep[0] != 0x11) return;
                DevState st;
                if (!devs.TryGetValue(rep[1], out st) || !st.Probed) return;

                // 事件帧: function 0 / software id 0
                if (rep[3] != 0x00) return;
                if (rep[2] == st.ReprogIdx)
                {
                    // divertedButtonsEvent: rep[4..11] = 当前按住的最多 4 个 CID (大端)
                    bool pressed = false;
                    for (int k = 4; k + 1 < rep.Length && k < 12; k += 2)
                    {
                        if (((rep[k] << 8) | rep[k + 1]) == CidTopButton) pressed = true;
                    }
                    if (pressed && !st.Pressed) OnTopButtonPressed();
                    st.Pressed = pressed;
                }
                else if (st.StatusIdx != 0 && rep[2] == st.StatusIdx)
                {
                    // 0x1D4B 无线状态: 设备重连后 divert 已丢失
                    reassertRequested = true;
                }
            }

            private byte[] Request(byte devIdx, byte featIdx, int fn, byte[] pars, int timeoutMs)
            {
                byte[] tx = new byte[OutLen];
                tx[0] = 0x11;
                tx[1] = devIdx;
                tx[2] = featIdx;
                tx[3] = (byte)((fn << 4) | SoftwareId);
                if (pars != null) Array.Copy(pars, 0, tx, 4, Math.Min(pars.Length, OutLen - 4));
                if (!Write(tx)) return null;

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return null;
                    bool fatal;
                    byte[] rx = ReadReport(left, out fatal);
                    if (fatal) throw new IOException("HID++ endpoint closed");
                    if (rx == null || rx.Length < 7) continue;
                    if (rx[0] == 0x11 && rx[1] == devIdx && rx[2] == featIdx && rx[3] == tx[3]) return rx;
                    if (rx[1] == devIdx && rx[2] == 0xFF && rx[3] == featIdx && rx[4] == tx[3]) return null;
                    Dispatch(rx);
                }
            }

            private bool Write(byte[] tx)
            {
                ZeroOverlapped(writeOvl, writeEvt);
                Marshal.Copy(tx, 0, writeBuf, tx.Length);
                if (!WriteFile(handle, writeBuf, tx.Length, IntPtr.Zero, writeOvl))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) return false;
                }
                if (WaitForSingleObject(writeEvt, 500) != 0)
                {
                    CancelIoEx(handle, writeOvl);
                    int n0;
                    GetOverlappedResult(handle, writeOvl, out n0, true);
                    return false;
                }
                int n;
                return GetOverlappedResult(handle, writeOvl, out n, false);
            }

            // 读请求可以跨多次调用保持挂起 (超时不取消)，下次调用继续等待同一个读请求
            private byte[] ReadReport(int timeoutMs, out bool fatal)
            {
                fatal = false;
                if (!readPending)
                {
                    ZeroOverlapped(readOvl, readEvt);
                    if (!ReadFile(handle, readBuf, InLen, IntPtr.Zero, readOvl))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING) { fatal = true; return null; }
                    }
                    readPending = true;
                }
                if (WaitForSingleObject(readEvt, timeoutMs) != 0) return null;
                readPending = false;
                int n;
                if (!GetOverlappedResult(handle, readOvl, out n, false)) { fatal = true; return null; }
                if (n <= 0) return null;
                byte[] rx = new byte[n];
                Marshal.Copy(readBuf, rx, 0, n);
                return rx;
            }

            private void Cleanup()
            {
                if (handle != IntPtr.Zero)
                {
                    if (readPending)
                    {
                        CancelIoEx(handle, readOvl);
                        int n;
                        GetOverlappedResult(handle, readOvl, out n, true);
                        readPending = false;
                    }
                    CloseHandle(handle);
                    handle = IntPtr.Zero;
                }
                if (readEvt != IntPtr.Zero) { CloseHandle(readEvt); readEvt = IntPtr.Zero; }
                if (writeEvt != IntPtr.Zero) { CloseHandle(writeEvt); writeEvt = IntPtr.Zero; }
                if (readBuf != IntPtr.Zero) { Marshal.FreeHGlobal(readBuf); readBuf = IntPtr.Zero; }
                if (writeBuf != IntPtr.Zero) { Marshal.FreeHGlobal(writeBuf); writeBuf = IntPtr.Zero; }
                if (readOvl != IntPtr.Zero) { Marshal.FreeHGlobal(readOvl); readOvl = IntPtr.Zero; }
                if (writeOvl != IntPtr.Zero) { Marshal.FreeHGlobal(writeOvl); writeOvl = IntPtr.Zero; }
            }
        }

        // OVERLAPPED: Internal, InternalHigh (ULONG_PTR) + Offset, OffsetHigh (DWORD) + hEvent
        private static readonly int OverlappedSize = IntPtr.Size * 3 + 8;
        private const int ERROR_IO_PENDING = 997;

        private static void ZeroOverlapped(IntPtr ovl, IntPtr evt)
        {
            for (int i = 0; i < OverlappedSize; i++) Marshal.WriteByte(ovl, i, 0);
            Marshal.WriteIntPtr(ovl, IntPtr.Size * 2 + 8, evt);
            ResetEvent(evt);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToRead, IntPtr lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, IntPtr lpBuffer, int nNumberOfBytesToWrite, IntPtr lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr hFile, IntPtr lpOverlapped, out int lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);
    }
}
