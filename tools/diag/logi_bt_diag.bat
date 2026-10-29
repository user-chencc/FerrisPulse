// 2>nul||@goto :batch
/*
:batch
@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "EXE=%TEMP%\fp_logi_bt_diag.exe"
"%CSC%" /nologo /out:"%EXE%" "%~f0" >"%TEMP%\fp_logi_bt_diag_build.txt" 2>&1
if errorlevel 1 (
  echo Build failed:
  type "%TEMP%\fp_logi_bt_diag_build.txt"
  pause
  exit /b 1
)
"%EXE%" "%~dp0logi_bt_diag_result.txt"
echo.
echo Done. Result saved to: %~dp0logi_bt_diag_result.txt
pause
exit /b 0
*/
// FerrisPulse Logitech Bluetooth HID++ diagnostic. READ-ONLY: it only asks questions, never changes mouse settings.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Diag
{
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr r; }
    [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort Version; }
    [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS
    {
        public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort a, b, c, d, e, f, g, h, i, j;
    }
    [StructLayout(LayoutKind.Sequential)] struct OVERLAPPED { public IntPtr Internal; public IntPtr InternalHigh; public int Offset; public int OffsetHigh; public IntPtr hEvent; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(IntPtr h, StringBuilder sb, int len);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr p, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern IntPtr CreateFile(string n, uint acc, uint share, IntPtr sec, uint disp, uint flags, IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr a, bool manual, bool init, string n);
    [DllImport("kernel32.dll")] static extern bool ResetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr h, byte[] buf, int len, IntPtr written, ref OVERLAPPED o);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(IntPtr h, byte[] buf, int len, IntPtr read, ref OVERLAPPED o);
    [DllImport("kernel32.dll")] static extern int WaitForSingleObject(IntPtr h, int ms);
    [DllImport("kernel32.dll")] static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED o, out int n, bool wait);
    [DllImport("kernel32.dll")] static extern bool CancelIo(IntPtr h);

    static StringBuilder log = new StringBuilder();
    static void L(string s) { Console.WriteLine(s); log.AppendLine(s); }
    static string Hex(byte[] b, int n) { if (b == null) return "(none)"; var sb = new StringBuilder(); for (int i = 0; i < n && i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' '); return sb.ToString().Trim(); }

    class Ep { public string Path; public int Vid, Pid; public string Product; public HIDP_CAPS Caps; }

    static int Main(string[] args)
    {
        string outFile = args.Length > 0 ? args[0] : "logi_bt_diag_result.txt";
        L("FerrisPulse Logitech BT diag v1  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  OS=" + Environment.OSVersion + "  x64=" + Environment.Is64BitProcess);
        List<Ep> eps = Enumerate();
        L("");
        L("== Logitech HID interfaces found: " + eps.Count);
        foreach (Ep e in eps)
        {
            L(string.Format("- VID={0:X4} PID={1:X4} UP={2:X4} U={3:X4} in={4} out={5} feat={6} name='{7}' oldFilterMatch={8}",
                e.Vid, e.Pid, e.Caps.UsagePage, e.Caps.Usage, e.Caps.InputReportByteLength, e.Caps.OutputReportByteLength, e.Caps.FeatureReportByteLength,
                e.Product, e.Path.ToLower().Contains("vid_046d")));
            L("    path=" + e.Path);
        }
        foreach (Ep e in eps)
        {
            bool hidpp = (e.Caps.UsagePage == 0xFF43 || e.Caps.UsagePage == 0xFF00) && e.Caps.OutputReportByteLength >= 7;
            if (!hidpp) continue;
            L("");
            L(string.Format("== Probing HID++ on PID={0:X4} UP={1:X4} out={2}", e.Pid, e.Caps.UsagePage, e.Caps.OutputReportByteLength));
            try { Probe(e); } catch (Exception ex) { L("  exception: " + ex.Message); }
        }
        try { File.WriteAllText(outFile, log.ToString(), Encoding.UTF8); } catch (Exception ex) { Console.WriteLine("Could not save file: " + ex.Message); }
        return 0;
    }

    static List<Ep> Enumerate()
    {
        var list = new List<Ep>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12);
        var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
        for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
        {
            uint req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
            if (req == 0) continue;
            IntPtr buf = Marshal.AllocHGlobal((int)req);
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 5);
            if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero))
            {
                string path = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
                if (path != null && path.ToLower().Contains("046d"))
                {
                    IntPtr h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h != new IntPtr(-1))
                    {
                        var ep = new Ep(); ep.Path = path;
                        var a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a);
                        if (HidD_GetAttributes(h, ref a)) { ep.Vid = a.VendorID; ep.Pid = a.ProductID; }
                        var sb = new StringBuilder(256);
                        ep.Product = HidD_GetProductString(h, sb, sb.Capacity) ? sb.ToString() : "";
                        IntPtr p; if (HidD_GetPreparsedData(h, out p)) { HidP_GetCaps(p, out ep.Caps); HidD_FreePreparsedData(p); }
                        CloseHandle(h);
                        if (ep.Vid == 0x046D) list.Add(ep);
                    }
                }
            }
            Marshal.FreeHGlobal(buf);
        }
        SetupDiDestroyDeviceInfoList(set);
        return list;
    }

    static IntPtr dev; static IntPtr evt; static int outLen, inLen; static byte devIdx = 0xFF;

    static void Probe(Ep e)
    {
        dev = CreateFile(e.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (dev == new IntPtr(-1)) { L("  open read/write FAILED, win32 error=" + Marshal.GetLastWin32Error()); return; }
        evt = CreateEvent(IntPtr.Zero, true, false, null);
        outLen = e.Caps.OutputReportByteLength; inLen = e.Caps.InputReportByteLength;
        try
        {
            byte[] r = Req(0x00, 1, new byte[] { 0, 0, 0x5A });
            if (r == null) { L("  ping: no answer"); return; }
            L(string.Format("  ping OK, HID++ protocol {0}.{1}", r[4], r[5]));

            ushort[] feats = { 0x0001, 0x0005, 0x1000, 0x1004, 0x2201, 0x2202, 0x1B04, 0x2110, 0x2111, 0x2121, 0x8100 };
            var idx = new Dictionary<ushort, byte>();
            foreach (ushort f in feats)
            {
                byte[] fr = Req(0x00, 0, new byte[] { (byte)(f >> 8), (byte)f });
                byte fi = fr != null ? fr[4] : (byte)0;
                idx[f] = fi;
                L(string.Format("  feature {0:X4} -> index {1}", f, fr == null ? "no answer" : (fi == 0 ? "not supported" : "0x" + fi.ToString("X2"))));
            }

            if (idx[0x0005] != 0)
            {
                byte[] c = Req(idx[0x0005], 0, null);
                if (c != null)
                {
                    int len = c[4]; var name = new StringBuilder();
                    for (int off = 0; off < len && off < 64; )
                    {
                        byte[] n = Req(idx[0x0005], 1, new byte[] { (byte)off });
                        if (n == null) break;
                        for (int k = 4; k < n.Length && off < len; k++, off++) name.Append((char)n[k]);
                    }
                    L("  device name: " + name);
                }
            }
            if (idx[0x2201] != 0)
            {
                byte fi = idx[0x2201];
                L("  0x2201 sensorCount: " + Hex(Req(fi, 0, null), 8));
                L("  0x2201 dpiList(sensor0): " + Hex(Req(fi, 1, new byte[] { 0 }), 20));
                L("  0x2201 currentDpi(sensor0): " + Hex(Req(fi, 2, new byte[] { 0 }), 12));
            }
            if (idx[0x2202] != 0)
            {
                byte fi = idx[0x2202];
                L("  0x2202 fn0: " + Hex(Req(fi, 0, null), 12));
                L("  0x2202 fn5(getSensorDpi): " + Hex(Req(fi, 5, new byte[] { 0 }), 16));
            }
            if (idx[0x1B04] != 0)
            {
                byte fi = idx[0x1B04];
                byte[] c = Req(fi, 0, null);
                int count = c != null ? c[4] : 0;
                L("  0x1B04 control count: " + count);
                for (int i = 0; i < count && i < 32; i++)
                {
                    byte[] ci = Req(fi, 1, new byte[] { (byte)i });
                    if (ci == null) { L("    #" + i + " no answer"); continue; }
                    int cid = (ci[4] << 8) | ci[5], tid = (ci[6] << 8) | ci[7];
                    L(string.Format("    #{0} CID=0x{1:X4} TID=0x{2:X4} flags=0x{3:X2} pos={4} group={5} gmask=0x{6:X2} addFlags=0x{7:X2}  raw={8}",
                        i, cid, tid, ci[8], ci[9], ci[10], ci[11], ci[12], Hex(ci, 14)));
                }
            }
            if (idx[0x1004] != 0) L("  0x1004 battery status: " + Hex(Req(idx[0x1004], 1, null), 10));
            if (idx[0x1000] != 0) L("  0x1000 battery level: " + Hex(Req(idx[0x1000], 0, null), 10));
        }
        finally { CloseHandle(evt); CloseHandle(dev); }
    }

    // Sends one HID++ 2.0 request and waits for the matching reply (or an error frame). Returns null on timeout.
    static byte[] Req(byte featIdx, int fn, byte[] pars)
    {
        bool longRep = outLen >= 20;
        byte[] tx = new byte[longRep ? 20 : 7];
        tx[0] = (byte)(longRep ? 0x11 : 0x10); tx[1] = devIdx; tx[2] = featIdx; tx[3] = (byte)((fn << 4) | 0x0A);
        if (pars != null) for (int i = 0; i < pars.Length && 4 + i < tx.Length; i++) tx[4 + i] = pars[i];
        byte[] txPad = new byte[outLen]; Array.Copy(tx, txPad, Math.Min(tx.Length, outLen));
        var o = new OVERLAPPED(); o.hEvent = evt; ResetEvent(evt);
        if (!WriteFile(dev, txPad, txPad.Length, IntPtr.Zero, ref o) && Marshal.GetLastWin32Error() != 997) return null;
        if (WaitForSingleObject(evt, 500) != 0) { CancelIo(dev); return null; }
        int n; GetOverlappedResult(dev, ref o, out n, false);

        DateTime end = DateTime.Now.AddMilliseconds(800);
        while (DateTime.Now < end)
        {
            byte[] rx = new byte[inLen];
            var ro = new OVERLAPPED(); ro.hEvent = evt; ResetEvent(evt);
            if (!ReadFile(dev, rx, rx.Length, IntPtr.Zero, ref ro) && Marshal.GetLastWin32Error() != 997) return null;
            int left = (int)(end - DateTime.Now).TotalMilliseconds; if (left < 1) left = 1;
            if (WaitForSingleObject(evt, left) != 0) { CancelIo(dev); GetOverlappedResult(dev, ref ro, out n, true); return null; }
            GetOverlappedResult(dev, ref ro, out n, false);
            if (n < 7) continue;
            if (rx[2] == 0xFF && rx[3] == featIdx && rx[4] == tx[3]) { L(string.Format("    (error reply for feat 0x{0:X2} fn {1}: code 0x{2:X2})", featIdx, fn, rx[5])); return null; }
            if ((rx[0] == 0x11 || rx[0] == 0x10) && rx[2] == featIdx && rx[3] == tx[3]) return rx;
        }
        return null;
    }
}
