using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace DesktopAgent;

/// <summary>Low-level desktop operations executed inside the interactive session.</summary>
internal static class DesktopOps
{
    // ---------------------------------------------------------------- state
    /// <summary>True when the input desktop is not the user's Default desktop
    /// (workstation locked or a UAC secure-desktop prompt is up).</summary>
    public static (bool locked, string desktopName) GetLockState()
    {
        IntPtr h = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return (true, "<unavailable>");
        try
        {
            var buf = new byte[256];
            NativeMethods.GetUserObjectInformation(h, NativeMethods.UOI_NAME, buf, (uint)buf.Length, out _);
            string name = Encoding.Unicode.GetString(buf).TrimEnd('\0');
            return (!string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase), name);
        }
        finally { NativeMethods.CloseDesktop(h); }
    }

    /// <summary>Foreground window identity for response digests and conditions.</summary>
    public static (long Hwnd, int Pid, string Process, string Title) ForegroundInfo()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (0, 0, "", "");
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string proc = "";
        try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return ((long)hwnd, (int)pid, proc, sb.ToString());
    }

    public static double GetIdleSeconds()
    {
        var lii = new NativeMethods.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref lii)) return -1;
        ulong now = NativeMethods.GetTickCount64();
        return (now - lii.dwTime) / 1000.0;
    }

    // ---------------------------------------------------------------- monitors
    public static List<Dictionary<string, object>> GetMonitors()
    {
        var list = new List<Dictionary<string, object>>();
        var seen = new HashSet<IntPtr>();
        // Primary source: WinForms screens (DPI-aware process => physical pixels)
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            var d = new Dictionary<string, object>
            {
                ["deviceName"] = s.DeviceName,
                ["primary"] = s.Primary,
                ["x"] = s.Bounds.X,
                ["y"] = s.Bounds.Y,
                ["width"] = s.Bounds.Width,
                ["height"] = s.Bounds.Height,
                ["workX"] = s.WorkingArea.X,
                ["workY"] = s.WorkingArea.Y,
                ["workWidth"] = s.WorkingArea.Width,
                ["workHeight"] = s.WorkingArea.Height,
                ["dpi"] = 96,
                ["scale"] = 1.0,
            };
            list.Add(d);
        }
        // Enrich with real DPI per monitor handle
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _hdc, ref NativeMethods.RECT _rc, IntPtr _data) =>
        {
            if (seen.Contains(hMon)) return true;
            seen.Add(hMon);
            var mi = new NativeMethods.MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                var target = list.FirstOrDefault(d =>
                    (string)d["deviceName"] == mi.szDevice ||
                    ((int)d["x"] == mi.rcMonitor.Left && (int)d["y"] == mi.rcMonitor.Top && (int)d["width"] == mi.rcMonitor.Width));
                if (target != null && NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
                {
                    target["dpi"] = (int)dpiX;
                    target["scale"] = Math.Round(dpiX / 96.0, 3);
                }
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---------------------------------------------------------------- windows
    public sealed record WinInfo(long Hwnd, string Title, string ClassName, int Pid, string ProcessName,
                                 int X, int Y, int Width, int Height, bool Minimized, bool Foreground, bool Cloaked);

    public static List<WinInfo> GetWindows(bool includeCloaked = true)
    {
        var result = new List<WinInfo>();
        IntPtr fg = NativeMethods.GetForegroundWindow();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.Length == 0) return true;
            var cb = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, cb, cb.Capacity);
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = "";
            try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { /* gone */ }
            NativeMethods.GetWindowRect(hWnd, out var rc);
            int cloaked = 0;
            NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int));
            bool isCloaked = cloaked != 0;
            if (isCloaked && !includeCloaked) return true;
            result.Add(new WinInfo(hWnd.ToInt64(), title, cb.ToString(), (int)pid, procName,
                rc.Left, rc.Top, rc.Width, rc.Height, NativeMethods.IsIconic(hWnd), hWnd == fg, isCloaked));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Resolve a window selector: numeric HWND, or case-insensitive title substring.</summary>
    public static WinInfo ResolveWindow(string selector)
    {
        var wins = GetWindows();
        if (long.TryParse(selector, out long hwndVal))
        {
            var byHwnd = wins.FirstOrDefault(w => w.Hwnd == hwndVal);
            if (byHwnd != null) return byHwnd;
            throw new InvalidOperationException($"no visible window with hwnd {hwndVal}");
        }
        var matches = wins.Where(w => w.Title.Contains(selector, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new InvalidOperationException($"no window title matches '{selector}'");
        if (matches.Count > 1)
        {
            var names = string.Join("; ", matches.Select(m => $"[{m.Hwnd}] {m.Title}"));
            throw new InvalidOperationException($"window selector '{selector}' is ambiguous: {names}");
        }
        return matches[0];
    }

    public static bool FocusWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        // Classic workaround for foreground restrictions: tap Alt first.
        SendKeyInput(0x12, false); SendKeyInput(0x12, true); // VK_MENU down/up
        NativeMethods.BringWindowToTop(hwnd);
        NativeMethods.SetForegroundWindow(hwnd);
        Thread.Sleep(120);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    // ---------------------------------------------------------------- precise timing
    /// <summary>Hybrid wait: coarse Sleep for the bulk, spin for the last ~2 ms.
    /// Requires timeBeginPeriod(1) (done at server start) for Sleep granularity.</summary>
    public static void PreciseWait(double ms)
    {
        if (ms <= 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (ms > 4)
        {
            int coarse = (int)(ms - 2.5);
            if (coarse > 0) Thread.Sleep(coarse);
        }
        while (sw.Elapsed.TotalMilliseconds < ms)
        {
            if (ms - sw.Elapsed.TotalMilliseconds > 1.5) Thread.Sleep(0);
            else Thread.SpinWait(64);
        }
    }

    // ---------------------------------------------------------------- screenshot
    public sealed record ShotResult(string Path, long ElapsedMs, int Width, int Height, string Format);

    /// <summary>Capture a screen region. format: "png" | "jpg"; scale 0.1–1.0 shrinks the output.</summary>
    public static ShotResult CaptureScreenshot(int x, int y, int w, int h, string? outPath, string shotsDir,
                                               string format = "png", int jpgQuality = 85, double scale = 1.0)
    {
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"invalid capture size {w}x{h}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool jpg = string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase);
        string ext = jpg ? ".jpg" : ".png";
        Directory.CreateDirectory(shotsDir);
        string path = outPath ?? Path.Combine(shotsDir, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}");
        if (outPath != null && Path.GetExtension(outPath).Length == 0) path = outPath + ext;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);

        using var full = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }
        int ow = Math.Max(1, (int)Math.Round(w * scale));
        int oh = Math.Max(1, (int)Math.Round(h * scale));
        Bitmap toSave = full;
        Bitmap? scaled = null;
        if (scale < 0.999)
        {
            scaled = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, ow, oh);
            }
            toSave = scaled;
        }
        try
        {
            if (jpg)
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpgQuality);
                toSave.Save(path, codec, encParams);
            }
            else
            {
                toSave.Save(path, ImageFormat.Png);
            }
        }
        finally { scaled?.Dispose(); }
        sw.Stop();
        return new ShotResult(path, sw.ElapsedMilliseconds, ow, oh, jpg ? "jpg" : "png");
    }

    public static (int x, int y, int w, int h) VirtualScreenRect() =>
        (NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
         NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
         NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
         NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    // ---------------------------------------------------------------- mouse
    private static (int nx, int ny) ToAbsolute(int x, int y)
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        int nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        return (nx, ny);
    }

    private static NativeMethods.INPUT MouseInput(uint flags, int nx, int ny, uint data = 0) =>
        new() { type = NativeMethods.INPUT_MOUSE, U = new NativeMethods.INPUTUNION { mi = new NativeMethods.MOUSEINPUT { dx = nx, dy = ny, mouseData = data, dwFlags = flags, time = 0, dwExtraInfo = UIntPtr.Zero } } };

    private static void Send(NativeMethods.INPUT[] inputs)
    {
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, NativeMethods.InputSize);
        if (sent != (uint)inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent}/{inputs.Length} events (the target window may be at a higher integrity level)");
    }

    public static void MoveMouse(int x, int y)
    {
        var (nx, ny) = ToAbsolute(x, y);
        Send(new[] { MouseInput(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK, nx, ny) });
    }

    public static void ClickAt(int x, int y, string button = "left", int count = 1)
    {
        (uint down, uint up) = button.ToLowerInvariant() switch
        {
            "right" => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
            "middle" => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP),
        };
        var (nx, ny) = ToAbsolute(x, y);
        Send(new[] { MouseInput(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK, nx, ny) });
        Thread.Sleep(40);
        for (int i = 0; i < count; i++)
        {
            Send(new[] { MouseInput(down, nx, ny), MouseInput(up, nx, ny) });
            if (i + 1 < count) Thread.Sleep(90);
        }
    }

    public static void Drag(int x1, int y1, int x2, int y2, string button = "left")
    {
        (uint down, uint up) = button.ToLowerInvariant() switch
        {
            "right" => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
            "middle" => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP),
        };
        var (ax, ay) = ToAbsolute(x1, y1);
        Send(new[] { MouseInput(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK, ax, ay) });
        Thread.Sleep(40);
        Send(new[] { MouseInput(down, ax, ay) });
        const int steps = 16;
        for (int i = 1; i <= steps; i++)
        {
            int cx = x1 + (x2 - x1) * i / steps;
            int cy = y1 + (y2 - y1) * i / steps;
            var (nx, ny) = ToAbsolute(cx, cy);
            Send(new[] { MouseInput(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK, nx, ny) });
            Thread.Sleep(12);
        }
        Thread.Sleep(40);
        var (bx, by) = ToAbsolute(x2, y2);
        Send(new[] { MouseInput(up, bx, by) });
    }

    public static void ScrollAt(int x, int y, int delta, bool horizontal = false)
    {
        var (nx, ny) = ToAbsolute(x, y);
        Send(new[] { MouseInput(NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK, nx, ny) });
        Thread.Sleep(30);
        uint flag = horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL;
        Send(new[] { MouseInput(flag, nx, ny, unchecked((uint)delta)) });
    }

    /// <summary>Relative mouse move (delta in pixels, raw relative motion).</summary>
    public static void MoveRelative(int dx, int dy)
    {
        Send(new[] { MouseInput(NativeMethods.MOUSEEVENTF_MOVE, dx, dy) });
    }

    /// <summary>Move the cursor along a polyline, spread evenly over durationMs
    /// with the precise timer — for smooth, game-like pointer motion.</summary>
    public static void MousePath(IReadOnlyList<(int x, int y)> points, int durationMs)
    {
        if (points.Count == 0) return;
        if (points.Count == 1 || durationMs <= 0) { MoveMouse(points[^1].x, points[^1].y); return; }
        // Compute cumulative segment lengths
        var cum = new double[points.Count];
        for (int i = 1; i < points.Count; i++)
        {
            double ddx = points[i].x - points[i - 1].x, ddy = points[i].y - points[i - 1].y;
            cum[i] = cum[i - 1] + Math.Sqrt(ddx * ddx + ddy * ddy);
        }
        double total = cum[^1];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            double t = sw.Elapsed.TotalMilliseconds;
            double frac = total <= 0 ? 1 : Math.Min(1, t / durationMs);
            double target = frac * total;
            int seg = 1;
            while (seg < cum.Length - 1 && cum[seg] < target) seg++;
            double segLen = cum[seg] - cum[seg - 1];
            double local = segLen <= 0 ? 0 : (target - cum[seg - 1]) / segLen;
            int cx = (int)Math.Round(points[seg - 1].x + (points[seg].x - points[seg - 1].x) * local);
            int cy = (int)Math.Round(points[seg - 1].y + (points[seg].y - points[seg - 1].y) * local);
            MoveMouse(cx, cy);
            if (frac >= 1) break;
            PreciseWait(4);
        }
    }

    // ---------------------------------------------------------------- keyboard
    private static readonly HashSet<ushort> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home Left Up Right Down
        0x2C, 0x2D, 0x2E, // PrintScreen Insert Delete
        0x5B, 0x5C, 0x5D, // LWin RWin Apps
        0x6F, 0x90, // Numpad Divide, NumLock
        0xA3, 0xA5, // Right Ctrl, Right Alt
    };

    private static NativeMethods.INPUT KeyInput(ushort vk, bool up, bool unicode = false, ushort scan = 0)
    {
        uint flags = 0;
        if (unicode) flags |= NativeMethods.KEYEVENTF_UNICODE;
        else if (ExtendedKeys.Contains(vk)) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        if (up) flags |= NativeMethods.KEYEVENTF_KEYUP;
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = unicode ? (ushort)0 : vk, wScan = unicode ? scan : (ushort)0, dwFlags = flags, time = 0, dwExtraInfo = UIntPtr.Zero } }
        };
    }

    private static void SendKeyInput(ushort vk, bool up) => Send(new[] { KeyInput(vk, up) });

    public static readonly Dictionary<string, ushort> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["bksp"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["ins"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["capslock"] = 0x14, ["numlock"] = 0x90,
        ["pause"] = 0x13, ["scrolllock"] = 0x91,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75,
        ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["f13"] = 0x7C, ["f14"] = 0x7D, ["f15"] = 0x7E, ["f16"] = 0x7F, ["f17"] = 0x80, ["f18"] = 0x81,
        ["f19"] = 0x82, ["f20"] = 0x83, ["f21"] = 0x84, ["f22"] = 0x85, ["f23"] = 0x86, ["f24"] = 0x87,
    };

    internal static ushort ResolveKey(string name)
    {
        string n = name.Trim();
        if (KeyNames.TryGetValue(n, out ushort vk)) return vk;
        if (n.Length == 1)
        {
            char c = char.ToUpperInvariant(n[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return (ushort)c;
        }
        throw new InvalidOperationException($"unknown key name '{name}'");
    }

    /// <summary>Resolve a key token: a name ("ctrl", "f5", "a"), "vk:0x41" / "vk:65", or "scan:0x1E" / "scan:30".
    /// Returns (vk, scan, useScanCode).</summary>
    public static (ushort vk, ushort scan, bool useScan) ResolveKeyToken(string token)
    {
        string t = token.Trim();
        if (t.StartsWith("vk:", StringComparison.OrdinalIgnoreCase))
        {
            string v = t[3..];
            ushort code = (ushort)(v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt16(v[2..], 16) : ushort.Parse(v));
            return (code, 0, false);
        }
        if (t.StartsWith("scan:", StringComparison.OrdinalIgnoreCase))
        {
            string v = t[5..];
            ushort code = (ushort)(v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt16(v[2..], 16) : ushort.Parse(v));
            return (0, code, true);
        }
        return (ResolveKey(t), 0, false);
    }

    private static NativeMethods.INPUT ScanInput(ushort scan, bool up, bool extended)
    {
        uint flags = 0x0008; // KEYEVENTF_SCANCODE
        if (up) flags |= NativeMethods.KEYEVENTF_KEYUP;
        if (extended) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = UIntPtr.Zero } }
        };
    }

    public static void KeyDown(string token)
    {
        var (vk, scan, useScan) = ResolveKeyToken(token);
        Send(new[] { useScan ? ScanInput(scan, false, false) : KeyInput(vk, false) });
    }

    public static void KeyUp(string token)
    {
        var (vk, scan, useScan) = ResolveKeyToken(token);
        Send(new[] { useScan ? ScanInput(scan, true, false) : KeyInput(vk, true) });
    }

    /// <summary>Hold a key down for exactly ms milliseconds (precise timer), then release.
    /// Supports chords ("ctrl") poorly by design — use a single key; modifiers via keydown/keyup around it.</summary>
    public static long HoldKey(string token, int ms)
    {
        var (vk, scan, useScan) = ResolveKeyToken(token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Send(new[] { useScan ? ScanInput(scan, false, false) : KeyInput(vk, false) });
        PreciseWait(ms);
        Send(new[] { useScan ? ScanInput(scan, true, false) : KeyInput(vk, true) });
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    /// <summary>Raw SendInput: an array of low-level events.
    /// Key event:    {"kind":"key","vk":65,"scan":0,"flags":0,"down":true}  (flags are raw KEYEVENTF_* bits; scan used when flags include 0x8)
    /// Mouse event:  {"kind":"mouse","dx":0,"dy":0,"flags":1,"data":0}      (flags are raw MOUSEEVENTF_* bits; dx/dy as given)</summary>
    public static int RawSend(System.Text.Json.Nodes.JsonArray events)
    {
        var inputs = new List<NativeMethods.INPUT>();
        foreach (var node in events)
        {
            var o = node?.AsObject() ?? throw new InvalidOperationException("raw event is not an object");
            string kind = o["kind"]?.GetValue<string>() ?? throw new InvalidOperationException("raw event missing 'kind'");
            if (kind == "key")
            {
                ushort vk = (ushort)(o["vk"]?.GetValue<int>() ?? 0);
                ushort scan = (ushort)(o["scan"]?.GetValue<int>() ?? 0);
                uint flags = (uint)(o["flags"]?.GetValue<int>() ?? 0);
                bool down = o["down"]?.GetValue<bool>() ?? true;
                if (!down) flags |= NativeMethods.KEYEVENTF_KEYUP;
                inputs.Add(new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_KEYBOARD,
                    U = new NativeMethods.INPUTUNION { ki = new NativeMethods.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = UIntPtr.Zero } }
                });
            }
            else if (kind == "mouse")
            {
                uint mflags = (uint)(o["flags"]?.GetValue<int>() ?? 0);
                int mdx, mdy;
                if (o["x"] != null && o["y"] != null)
                {
                    // Pixel coordinates: converted to the normalized absolute
                    // space here, so sequence files can be written in pixels.
                    (mdx, mdy) = ToAbsolute(o["x"]!.GetValue<int>(), o["y"]!.GetValue<int>());
                }
                else
                {
                    mdx = o["dx"]?.GetValue<int>() ?? 0;
                    mdy = o["dy"]?.GetValue<int>() ?? 0;
                }
                inputs.Add(new NativeMethods.INPUT
                {
                    type = NativeMethods.INPUT_MOUSE,
                    U = new NativeMethods.INPUTUNION
                    {
                        mi = new NativeMethods.MOUSEINPUT
                        {
                            dx = mdx,
                            dy = mdy,
                            mouseData = (uint)(o["data"]?.GetValue<int>() ?? 0),
                            dwFlags = mflags,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                });
            }
            else throw new InvalidOperationException($"unknown raw event kind '{kind}'");
        }
        if (inputs.Count > 0) Send(inputs.ToArray());
        return inputs.Count;
    }

    /// <summary>Press a chord like "ctrl+s", "alt+tab", "shift+ctrl+esc", or a single key.</summary>
    public static void PressChord(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new InvalidOperationException("empty key chord");
        var vks = parts.Select(ResolveKey).ToList();
        var inputs = new List<NativeMethods.INPUT>();
        foreach (var vk in vks) inputs.Add(KeyInput(vk, false));
        for (int i = vks.Count - 1; i >= 0; i--) inputs.Add(KeyInput(vks[i], true));
        Send(inputs.ToArray());
    }

    /// <summary>Type Unicode text via SendInput (handles any script). Enter for \n, Tab for \t.</summary>
    public static void TypeText(string text)
    {
        var batch = new List<NativeMethods.INPUT>();
        void Flush()
        {
            if (batch.Count == 0) return;
            Send(batch.ToArray());
            batch.Clear();
            Thread.Sleep(5);
        }
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r')
            {
                if (c == '\r') continue;
                batch.Add(KeyInput(0x0D, false)); batch.Add(KeyInput(0x0D, true));
            }
            else if (c == '\t')
            {
                batch.Add(KeyInput(0x09, false)); batch.Add(KeyInput(0x09, true));
            }
            else
            {
                batch.Add(KeyInput(0, false, unicode: true, scan: c));
                batch.Add(KeyInput(0, true, unicode: true, scan: c));
            }
            if (batch.Count >= 100) Flush();
        }
        Flush();
    }

    // ---------------------------------------------------------------- pixels & images
    /// <summary>Capture an arbitrary screen region as a Bitmap (caller disposes).</summary>
    public static Bitmap CaptureBitmap(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"invalid capture size {w}x{h}");
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static (int R, int G, int B) GetPixel(int x, int y)
    {
        using var bmp = CaptureBitmap(x, y, 1, 1);
        var c = bmp.GetPixel(0, 0);
        return (c.R, c.G, c.B);
    }

    public static string Hex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";

    public static (int R, int G, int B) ParseHex(string hex)
    {
        string s = hex.Trim().TrimStart('#');
        if (s.Length != 6) throw new InvalidOperationException($"color must be #RRGGBB, got '{hex}'");
        return (Convert.ToInt32(s[..2], 16), Convert.ToInt32(s[2..4], 16), Convert.ToInt32(s[4..6], 16));
    }

    public static bool ColorClose((int R, int G, int B) a, (int R, int G, int B) b, int tol) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    /// <summary>Dominant colors of a region: pixels are bucketed 16x16x16 and each
    /// bucket reports its members' average color. Cheap screen "touch" for agents:
    /// verify a fill, a state color, a highlight — without a screenshot round trip.</summary>
    public static List<(string Hex, int Count, double Frac)> ColorHistogram(int x, int y, int w, int h, int top = 8)
    {
        using var bmp = CaptureBitmap(x, y, w, h);
        var buckets = new Dictionary<int, (long SR, long SG, long SB, int N)>();
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int yy = 0; yy < h; yy++)
            {
                int rowOff = yy * stride;
                for (int xx = 0; xx < w; xx++)
                {
                    int i = rowOff + xx * 4;
                    int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    if (buckets.TryGetValue(key, out var v)) buckets[key] = (v.SR + r, v.SG + g, v.SB + b, v.N + 1);
                    else buckets[key] = (r, g, b, 1);
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        double totalPx = (double)w * h;
        return buckets.Values.OrderByDescending(v => v.N).Take(top)
            .Select(v => (Hex((int)(v.SR / v.N), (int)(v.SG / v.N), (int)(v.SB / v.N)), v.N, v.N / totalPx))
            .ToList();
    }

    /// <summary>Fraction of pixels in a region whose color is within tol of the target.</summary>
    public static double ColorFraction(int x, int y, int w, int h, (int R, int G, int B) target, int tol)
    {
        using var bmp = CaptureBitmap(x, y, w, h);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        long hit = 0;
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int yy = 0; yy < h; yy++)
            {
                int rowOff = yy * stride;
                for (int xx = 0; xx < w; xx++)
                {
                    int i = rowOff + xx * 4;
                    if (Math.Abs(buf[i] - target.B) <= tol && Math.Abs(buf[i + 1] - target.G) <= tol && Math.Abs(buf[i + 2] - target.R) <= tol) hit++;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return hit / ((double)w * h);
    }

    /// <summary>Fraction of pixels that differ between two same-size bitmaps
    /// by more than tol in any channel. Used by sequence "changed" checkpoints.</summary>
    public static double DiffFraction(Bitmap a, Bitmap b, int tol = 24)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 1.0;
        int w = a.Width, h = a.Height;
        long diff = 0;
        var da = a.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ba = new byte[da.Stride * h]; var bb = new byte[db.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(da.Scan0, ba, 0, ba.Length);
            System.Runtime.InteropServices.Marshal.Copy(db.Scan0, bb, 0, bb.Length);
            for (int yy = 0; yy < h; yy++)
            {
                int oa = yy * da.Stride, ob = yy * db.Stride;
                for (int xx = 0; xx < w; xx++)
                {
                    int ia = oa + xx * 4, ib = ob + xx * 4;
                    if (Math.Abs(ba[ia] - bb[ib]) > tol || Math.Abs(ba[ia + 1] - bb[ib + 1]) > tol || Math.Abs(ba[ia + 2] - bb[ib + 2]) > tol) diff++;
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
        return diff / ((double)w * h);
    }

    public sealed record TemplateMatch(int X, int Y, int W, int H, double Score);

    private static float[] ToGray(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var gray = new float[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[data.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOff + x * 4;
                    gray[y * w + x] = 0.114f * buf[i] + 0.587f * buf[i + 1] + 0.299f * buf[i + 2];
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return gray;
    }

    /// <summary>Find a template image inside a screen region (grayscale SAD,
    /// coarse-to-fine). Score is 1 - mean abs difference / 255.</summary>
    public static TemplateMatch? FindTemplate(Bitmap template, int rx, int ry, int rw, int rh, double threshold)
    {
        using var region = CaptureBitmap(rx, ry, rw, rh);
        var rg = ToGray(region);
        var tg = ToGray(template);
        int tw = template.Width, th = template.Height;
        if (tw > rw || th > rh) return null;

        double SadAt(int ox, int oy, int step)
        {
            double sad = 0; long n = 0;
            for (int y = 0; y < th; y += step)
            {
                int rRow = (oy + y) * rw, tRow = y * tw;
                for (int x = 0; x < tw; x += step)
                {
                    sad += Math.Abs(rg[rRow + ox + x] - tg[tRow + x]);
                    n++;
                }
            }
            return n == 0 ? double.MaxValue : sad / n;
        }

        int coarse = Math.Max(2, Math.Min(tw, th) / 6);
        double best = double.MaxValue; int bx = -1, by = -1;
        for (int oy = 0; oy + th <= rh; oy += coarse)
            for (int ox = 0; ox + tw <= rw; ox += coarse)
            {
                double s = SadAt(ox, oy, coarse);
                if (s < best) { best = s; bx = ox; by = oy; }
            }
        if (bx < 0) return null;
        // Refine at full sampling around the coarse winner.
        int x0 = Math.Max(0, bx - coarse), x1 = Math.Min(rw - tw, bx + coarse);
        int y0 = Math.Max(0, by - coarse), y1 = Math.Min(rh - th, by + coarse);
        best = double.MaxValue;
        for (int oy = y0; oy <= y1; oy++)
            for (int ox = x0; ox <= x1; ox++)
            {
                double s = SadAt(ox, oy, 1);
                if (s < best) { best = s; bx = ox; by = oy; }
            }
        double score = 1.0 - best / 255.0;
        if (score < threshold) return null;
        return new TemplateMatch(rx + bx, ry + by, tw, th, Math.Round(score, 4));
    }

    /// <summary>Convert physical pixels to the absolute 0..65535 mouse space
    /// (virtual-desktop aware). Raw events may give x/y pixels instead of dx/dy.</summary>
    public static (int Nx, int Ny) AbsoluteFromPixel(int x, int y) => ToAbsolute(x, y);

    // ---------------------------------------------------------------- launch
    public static int LaunchApp(string app, string? args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = app,
            Arguments = args ?? "",
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start '{app}'");
        return p.Id;
    }
}
