// Win10MenuBridge - brings back the Windows 10 right-click menus for the Windows 11 taskbar.
// Plain Win32: WH_MOUSE_LL hook + UI Automation hit-testing + TrackPopupMenu. No XAML/WinUI,
// no injection, no Explorer patching, no drivers, no network access.
// How it works:
//   * A background thread uses UI Automation (read-only) to cache the screen rectangles of the
//     taskbar, the Network button, the Volume button, the Clock, and every other taskbar button.
//   * A low-level mouse hook (separate thread) checks each right-click against that cache.
//     Only right-clicks on Network / Volume / Clock / empty taskbar are swallowed; everything else
//     passes through untouched. If the cache is stale or anything is uncertain, the click passes
//     through and the stock Windows 11 menu appears.
//   * Menus are loaded from the Windows 10 .mui files in .\res (copied as data from a Windows 10 install),
//     with a built-in fallback if they are missing. Commands launch standard Windows panels.
// Exit: tray icon -> Exit (or kill the process). Stock menus come back immediately.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

static class NativeMethods
{
    public const int WH_MOUSE_LL = 14, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    public const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, TPM_BOTTOMALIGN = 0x0020, TPM_LEFTALIGN = 0;
    public const uint MF_BYCOMMAND = 0, MF_BYPOSITION = 0x400, MF_CHECKED = 8, MF_GRAYED = 1, MF_STRING = 0, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    public const uint LOAD_LIBRARY_AS_DATAFILE = 0x2, LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
    [DllImport("user32")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32")] public static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint fl);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr LoadMenu(IntPtr h, IntPtr id);
    [DllImport("user32")] public static extern IntPtr GetSubMenu(IntPtr m, int p);
    [DllImport("user32")] public static extern bool DeleteMenu(IntPtr m, uint id, uint f);
    [DllImport("user32")] public static extern bool RemoveMenu(IntPtr m, uint id, uint f);
    [DllImport("user32")] public static extern int GetMenuItemCount(IntPtr m);
    [DllImport("user32")] public static extern uint GetMenuItemID(IntPtr m, int p);
    [DllImport("user32")] public static extern uint CheckMenuItem(IntPtr m, uint id, uint f);
    [DllImport("user32")] public static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr m, uint f, IntPtr id, string s);
    [DllImport("user32")] public static extern uint TrackPopupMenuEx(IntPtr m, uint f, int x, int y, IntPtr hwnd, IntPtr p);
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32")] public static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32")] public static extern bool PostThreadMessage(uint tid, uint m, IntPtr w, IntPtr l);
    [DllImport("kernel32")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr h; public uint msg; public IntPtr w, l; public uint t; public POINT p; }
    // uxtheme dark-mode opt-in for this process's own menus (ordinals 135/136). Skipped if absent.
    [DllImport("kernel32", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr h, IntPtr ord);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr LoadLibrary(string f);
    public delegate int SetPreferredAppModeFn(int mode);
    public delegate void FlushMenuThemesFn();
}

enum Target { None, Network, Volume, Clock, EmptyTaskbar }

class TaskbarCache
{
    public readonly object Lock = new object();
    public Rectangle Taskbar, Network, Volume, Clock;
    public List<Rectangle> Buttons = new List<Rectangle>();
    public DateTime Stamp = DateTime.MinValue;
    public bool Valid;

    public Target Hover = Target.None;
    public Point HoverPt;
    public DateTime HoverStamp = DateTime.MinValue;

    public static bool Hit(Rectangle r, Point p)
    {
        if (r.IsEmpty) return false;
        r.Inflate(2, 2);
        return r.Contains(p);
    }

    public Target HitTest(int x, int y, out string why)
    {
        lock (Lock)
        {
            Point p = new Point(x, y);
            if ((DateTime.UtcNow - HoverStamp).TotalMilliseconds <= 400
                && Math.Abs(p.X - HoverPt.X) <= 12 && Math.Abs(p.Y - HoverPt.Y) <= 12)
            {
                why = "hover:" + Hover + " age=" + (int)(DateTime.UtcNow - HoverStamp).TotalMilliseconds + "ms";
                return Hover;
            }
            if (!Valid) { why = "cache-invalid"; return Target.None; }
            int age = (int)(DateTime.UtcNow - Stamp).TotalMilliseconds;
            if (age > 30000) { why = "cache-very-stale age=" + age; return Target.None; }
            if (!Hit(Taskbar, p)) { why = "off-taskbar age=" + age; return Target.None; }
            if (Hit(Network, p)) { why = "rect-network age=" + age; return Target.Network; }
            if (Hit(Volume, p)) { why = "rect-volume age=" + age; return Target.Volume; }
            if (Hit(Clock, p)) { why = "rect-clock age=" + age; return Target.Clock; }
            foreach (var b in Buttons)
                if (Hit(b, p)) { why = "rect-other-button age=" + age; return Target.None; }
            why = "rect-empty-taskbar age=" + age;
            return Target.EmptyTaskbar;
        }
    }
}

static class Program
{
    static TaskbarCache cache = new TaskbarCache();
    static NativeMethods.HookProc hookProc;
    static IntPtr hook;
    static uint hookThreadId;
    static AutoResetEvent uiaWake = new AutoResetEvent(false);
    static volatile bool menuOpen;
    static Target swallowedDown = Target.None;
    static MenuHost host;
    static string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    static string logPath;

    public static void Log(string s)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + s + Environment.NewLine); } catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        try { NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2: physical coords everywhere
        logPath = Path.Combine(baseDir, "Win10MenuBridge.log");
        bool created;
        using (var mtx = new Mutex(true, "Local\\Win10MenuBridge_SingleInstance", out created))
        {
            if (!created) return;
            Log("start pid=" + Process.GetCurrentProcess().Id);
            TryEnableDarkMenus();
            Application.EnableVisualStyles();
            host = new MenuHost();

            var uia = new Thread(UiaLoop) { IsBackground = true, Name = "uia" };
            uia.SetApartmentState(ApartmentState.MTA);
            uia.Start();
            var hk = new Thread(HookLoop) { IsBackground = true, Name = "hook" };
            hk.Start();

            Application.Run(host);
            if (hookThreadId != 0) NativeMethods.PostThreadMessage(hookThreadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
            Log("exit");
        }
    }

    static void TryEnableDarkMenus()
    {
        try
        {
            var ux = NativeMethods.LoadLibrary("uxtheme.dll");
            var p135 = NativeMethods.GetProcAddress(ux, new IntPtr(135));
            var p136 = NativeMethods.GetProcAddress(ux, new IntPtr(136));
            if (p135 != IntPtr.Zero)
            {
                var f = (NativeMethods.SetPreferredAppModeFn)Marshal.GetDelegateForFunctionPointer(p135, typeof(NativeMethods.SetPreferredAppModeFn));
                f(1); // AllowDark: follow the Windows app theme
            }
            if (p136 != IntPtr.Zero)
            {
                var g = (NativeMethods.FlushMenuThemesFn)Marshal.GetDelegateForFunctionPointer(p136, typeof(NativeMethods.FlushMenuThemesFn));
                g();
            }
        }
        catch (Exception e) { Log("dark mode skipped: " + e.Message); }
    }

    // ---------- UI Automation cache (read-only) ----------
    static void UiaLoop()
    {
        DateTime lastFull = DateTime.MinValue;
        DateTime lastHoverLog = DateTime.MinValue;
        Target lastLoggedHover = (Target)(-1);
        try
        {
            IntPtr tray0 = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray0 != IntPtr.Zero) { Refresh(tray0); lastFull = DateTime.UtcNow; Log("first refresh ok"); }
        }
        catch (Exception e) { Log("uia first refresh: " + e.Message); }

        while (true)
        {
            try
            {
                IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                NativeMethods.POINT cp; NativeMethods.GetCursorPos(out cp);
                NativeMethods.RECT tr = new NativeMethods.RECT();
                bool haveRect = tray != IntPtr.Zero && NativeMethods.GetWindowRect(tray, out tr);
                Rectangle cachedTb;
                lock (cache.Lock) cachedTb = cache.Taskbar;
                bool nearTaskbar = (haveRect && cp.Y >= tr.T - 40 && cp.Y <= tr.B + 5 && cp.X >= tr.L - 5 && cp.X <= tr.R + 5)
                    || (!cachedTb.IsEmpty && cp.Y >= cachedTb.Top - 40 && cp.Y <= cachedTb.Bottom + 5 && cp.X >= cachedTb.Left - 5 && cp.X <= cachedTb.Right + 5);
                if (tray != IntPtr.Zero && (nearTaskbar || (DateTime.UtcNow - lastFull).TotalSeconds > 2))
                {
                    Refresh(tray);
                    lastFull = DateTime.UtcNow;
                }
                if (tray != IntPtr.Zero && nearTaskbar)
                    UpdateHover(cp.X, cp.Y);
                else
                    lock (cache.Lock) { cache.Hover = Target.None; cache.HoverStamp = DateTime.MinValue; }

                Target hv;
                lock (cache.Lock) hv = cache.Hover;
                if (hv != lastLoggedHover && (DateTime.UtcNow - lastHoverLog).TotalMilliseconds > 250)
                {
                    Log("hover " + hv + " @ " + cp.X + "," + cp.Y);
                    lastLoggedHover = hv; lastHoverLog = DateTime.UtcNow;
                }
            }
            catch (Exception e) { lock (cache.Lock) cache.Valid = false; Log("uia error: " + e.Message); }
            uiaWake.WaitOne(120);
        }
    }

    static bool Starts(string s, string p) { return (s ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase); }

    static Target ClassifyName(string aid, string name, string cls)
    {
        aid = aid ?? ""; name = name ?? ""; cls = cls ?? "";
        if (aid == "StartButton" || aid == "SearchButton" || aid == "TaskViewButton") return Target.None;
        if (aid.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)) return Target.None;
        if (aid == "NotifyItemIcon") return Target.None;
        string first = name.Trim();
        int nl = first.IndexOfAny(new char[] { '\r', '\n' });
        if (nl >= 0) first = first.Substring(0, nl).Trim();
        if (aid == "SystemTrayIcon" || cls.IndexOf("SystemTray", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (Starts(first, "Network") || Starts(first, "Ethernet") || Starts(first, "Wi-Fi") || Starts(first, "WiFi")
                || Starts(first, "WLAN") || Starts(first, "Not connected") || Starts(first, "Internet")
                || name.IndexOf("Internet access", StringComparison.OrdinalIgnoreCase) >= 0)
                return Target.Network;
            if (Starts(first, "Volume") || Starts(first, "Speaker") || Starts(first, "Headphone") || Starts(first, "Headset")
                || Starts(first, "Audio") || Starts(first, "Muted") || Starts(first, "No audio")
                || cls.IndexOf("OmniButtonRight", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0)
                return Target.Volume;
            if (Starts(first, "Clock") || Starts(first, "Date and time")) return Target.Clock;
            return Target.None;
        }
        return Target.None;
    }

    static void UpdateHover(int x, int y)
    {
        Target t = Target.None;
        bool classified = false;
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            var walker = TreeWalker.ControlViewWalker;
            for (int i = 0; i < 8 && el != null; i++)
            {
                var c = el.Current;
                if (c.ControlType == ControlType.Button || (c.AutomationId ?? "") == "SystemTrayIcon")
                {
                    t = ClassifyName(c.AutomationId, c.Name, c.ClassName);
                    classified = true;
                    break;
                }
                el = walker.GetParent(el);
            }
        }
        catch { classified = false; }
        if (!classified || t == Target.None)
        {
            lock (cache.Lock)
            {
                Point p = new Point(x, y);
                if (cache.Valid && !cache.Taskbar.IsEmpty)
                {
                    if (TaskbarCache.Hit(cache.Taskbar, p))
                    {
                        if (TaskbarCache.Hit(cache.Network, p)) t = Target.Network;
                        else if (TaskbarCache.Hit(cache.Volume, p)) t = Target.Volume;
                        else if (TaskbarCache.Hit(cache.Clock, p)) t = Target.Clock;
                        else
                        {
                            bool other = false;
                            foreach (var b in cache.Buttons) if (TaskbarCache.Hit(b, p)) { other = true; break; }
                            t = other ? Target.None : Target.EmptyTaskbar;
                        }
                    }
                }
            }
        }
        lock (cache.Lock) { cache.Hover = t; cache.HoverPt = new Point(x, y); cache.HoverStamp = DateTime.UtcNow; }
    }

    static Rectangle R(System.Windows.Rect r)
    {
        if (r.IsEmpty || double.IsInfinity(r.Width) || double.IsInfinity(r.Height)) return Rectangle.Empty;
        return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    static void Refresh(IntPtr tray)
    {
        var root = AutomationElement.FromHandle(tray);
        var req = new CacheRequest();
        req.Add(AutomationElement.NameProperty);
        req.Add(AutomationElement.AutomationIdProperty);
        req.Add(AutomationElement.ClassNameProperty);
        req.Add(AutomationElement.BoundingRectangleProperty);
        req.Add(AutomationElement.ControlTypeProperty);
        req.AutomationElementMode = AutomationElementMode.None;
        Rectangle taskbar = Rectangle.Empty, net = Rectangle.Empty, vol = Rectangle.Empty, clk = Rectangle.Empty;
        var buttons = new List<Rectangle>();
        using (req.Activate())
        {
            var frame = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskbarFrame"));
            if (frame == null) { lock (cache.Lock) cache.Valid = false; return; }
            taskbar = R(frame.Cached.BoundingRectangle);
            var all = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement e in all)
            {
                var c = e.Cached;
                var rect = R(c.BoundingRectangle);
                if (rect.IsEmpty) continue;
                buttons.Add(rect);
                if (c.AutomationId == "SystemTrayIcon" || (c.ClassName ?? "").IndexOf("SystemTray", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Target kind = ClassifyName(c.AutomationId, c.Name, c.ClassName);
                    if (kind == Target.Network) net = rect;
                    else if (kind == Target.Volume) vol = rect;
                    else if (kind == Target.Clock) clk = rect;
                }
            }
        }
        lock (cache.Lock)
        {
            cache.Taskbar = taskbar; cache.Network = net; cache.Volume = vol; cache.Clock = clk;
            cache.Buttons = buttons; cache.Stamp = DateTime.UtcNow; cache.Valid = !taskbar.IsEmpty;
        }
    }

    // ---------- Low-level mouse hook ----------
    static void HookLoop()
    {
        hookThreadId = NativeMethods.GetCurrentThreadId();
        hookProc = HookCallback;
        hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, hookProc, NativeMethods.GetModuleHandle(null), 0);
        Log("hook installed: " + (hook != IntPtr.Zero));
        NativeMethods.MSG m;
        while (NativeMethods.GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
        if (hook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hook);
    }

    const int WM_MOUSEMOVE = 0x0200;

    static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_MOUSEMOVE)
                {
                    var mv = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                    bool near;
                    lock (cache.Lock)
                    {
                        Rectangle tb = cache.Taskbar; tb.Inflate(40, 40);
                        near = cache.Valid && tb.Contains(mv.pt.X, mv.pt.Y);
                    }
                    if (near) uiaWake.Set();
                }
                else if (msg == NativeMethods.WM_RBUTTONDOWN)
                {
                    swallowedDown = Target.None;
                    if (!menuOpen)
                    {
                        var info = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                        string why;
                        Target t = cache.HitTest(info.pt.X, info.pt.Y, out why);
                        string line = "rdown " + t + " @ " + info.pt.X + "," + info.pt.Y + " " + why;
                        ThreadPool.QueueUserWorkItem(_ => Log(line));
                        if (t != Target.None) { swallowedDown = t; return new IntPtr(1); }
                    }
                }
                else if (msg == NativeMethods.WM_RBUTTONUP && swallowedDown != Target.None)
                {
                    var info = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                    Target t = swallowedDown; swallowedDown = Target.None;
                    int x = info.pt.X, y = info.pt.Y;
                    host.BeginInvoke(new Action(() => host.ShowMenu(t, x, y)));
                    return new IntPtr(1);
                }
            }
        }
        catch { }
        return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }

    public static void SetMenuOpen(bool v) { menuOpen = v; }
}

class MenuHost : Form
{
    NotifyIcon icon;
    string resDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res");

    public MenuHost()
    {
        ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None; Opacity = 0; Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
        var cm = new ContextMenu();
        cm.MenuItems.Add("Win10 Menu Bridge (running)").Enabled = false;
        cm.MenuItems.Add("-");
        cm.MenuItems.Add("Exit (restore Windows 11 menus)", (s, e) => { icon.Visible = false; Application.Exit(); });
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Win10 Menu Bridge", ContextMenu = cm, Visible = true };
        try { icon.Icon = Icon.ExtractAssociatedIcon(Path.Combine(Environment.SystemDirectory, "taskbarcpl.dll")) ?? SystemIcons.Application; } catch { }
    }
    protected override void SetVisibleCore(bool value) { if (!IsHandleCreated) CreateHandle(); base.SetVisibleCore(false); }
    protected override void OnFormClosed(FormClosedEventArgs e) { icon.Visible = false; base.OnFormClosed(e); }

    IntPtr LoadMuiMenu(string file, int id)
    {
        string p = Path.Combine(resDir, file);
        if (!File.Exists(p)) return IntPtr.Zero;
        IntPtr h = NativeMethods.LoadLibraryEx(p, IntPtr.Zero, NativeMethods.LOAD_LIBRARY_AS_DATAFILE | NativeMethods.LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        return NativeMethods.LoadMenu(h, new IntPtr(id)); // module stays loaded (tiny, data-only)
    }

    static IntPtr Fallback(params object[] items)
    {
        IntPtr bar = NativeMethods.CreatePopupMenu(), pop = NativeMethods.CreatePopupMenu();
        for (int i = 0; i < items.Length; i += 2)
        {
            if (items[i] == null) NativeMethods.AppendMenu(pop, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
            else NativeMethods.AppendMenu(pop, NativeMethods.MF_STRING, new IntPtr((int)items[i]), (string)items[i + 1]);
        }
        NativeMethods.AppendMenu(bar, NativeMethods.MF_POPUP, pop, "x");
        return bar;
    }

    static void Remove(IntPtr m, params int[] ids) { foreach (var id in ids) NativeMethods.DeleteMenu(m, (uint)id, NativeMethods.MF_BYCOMMAND); }

    static bool IsSeparator(IntPtr m, int i)
    {
        if (NativeMethods.GetSubMenu(m, i) != IntPtr.Zero) return false;
        uint id = NativeMethods.GetMenuItemID(m, i);
        return id == 0 || id == 0xFFFFFFFF;
    }

    static void TidySeparators(IntPtr m)
    {
        bool prevSep = true;
        for (int i = 0; i < NativeMethods.GetMenuItemCount(m); )
        {
            bool sep = IsSeparator(m, i);
            if (sep && prevSep) { NativeMethods.DeleteMenu(m, (uint)i, NativeMethods.MF_BYPOSITION); continue; }
            prevSep = sep; i++;
        }
        int n = NativeMethods.GetMenuItemCount(m);
        if (n > 0 && IsSeparator(m, n - 1))
            NativeMethods.DeleteMenu(m, (uint)(n - 1), NativeMethods.MF_BYPOSITION);
    }

    static void RemovePopupsContaining(IntPtr menu, params uint[] ids)
    {
        var set = new System.Collections.Generic.HashSet<uint>(ids);
        for (int i = NativeMethods.GetMenuItemCount(menu) - 1; i >= 0; i--)
        {
            IntPtr sub = NativeMethods.GetSubMenu(menu, i);
            if (sub == IntPtr.Zero) continue;
            bool hit = false;
            int n = NativeMethods.GetMenuItemCount(sub);
            for (int j = 0; j < n; j++) if (set.Contains(NativeMethods.GetMenuItemID(sub, j))) { hit = true; break; }
            if (hit) NativeMethods.DeleteMenu(menu, (uint)i, NativeMethods.MF_BYPOSITION);
        }
    }

    static void RemoveEmptyPopups(IntPtr menu)
    {
        for (int i = NativeMethods.GetMenuItemCount(menu) - 1; i >= 0; i--)
        {
            IntPtr sub = NativeMethods.GetSubMenu(menu, i);
            if (sub == IntPtr.Zero) continue;
            TidySeparators(sub);
            if (NativeMethods.GetMenuItemCount(sub) == 0)
                NativeMethods.DeleteMenu(menu, (uint)i, NativeMethods.MF_BYPOSITION);
        }
    }

    static int RegInt(string key, string name, int def)
    {
        try { var v = Registry.GetValue(key, name, null); return v is int ? (int)v : def; } catch { return def; }
    }

    public void ShowMenu(Target t, int x, int y)
    {
        IntPtr bar = IntPtr.Zero, menu = IntPtr.Zero;
        try
        {
            Program.SetMenuOpen(true);
            switch (t)
            {
                case Target.Network:
                    bar = LoadMuiMenu("pnidui.dll.mui", 3014);
                    if (bar == IntPtr.Zero) bar = Fallback(3107, "Troubleshoot problems", 3109, "Open Network && Internet settings");
                    break;
                case Target.Volume:
                    bar = LoadMuiMenu("SndVolSSO.dll.mui", 130);
                    if (bar == IntPtr.Zero) bar = Fallback(40012, "Op&en Sound settings", 40001, "Open Volume &mixer", 40007, "&Sounds", 40008, "&Troubleshoot sound problems");
                    break;
                default:
                    bar = LoadMuiMenu("explorer.exe.mui", 205);
                    if (bar == IntPtr.Zero) bar = Fallback(408, "&Adjust date/time", 421, "&Customize notification icons", null, null,
                        430, "Show Task &View button", null, null, 403, "Casca&de windows", 405, "Show windows stack&ed", 404, "Show windows s&ide by side",
                        407, "&Show the desktop", null, null, 420, "Tas&k Manager", null, null, 413, "Taskbar se&ttings");
                    break;
            }
            menu = NativeMethods.GetSubMenu(bar, 0);
            if (menu == IntPtr.Zero) return;

            if (t == Target.Clock || t == Target.EmptyTaskbar)
            {
                // Items that have no Windows 11 equivalent are removed (Cortana, News, People, Ink, touch kb/pad, Copilot, lock, Exit Explorer, Undo).
                Remove(menu, 446, 447, 448, 625, 626, 627, 628, 640, 449, 435, 437, 436, 438, 26128, 416, 424, 425, 518);
                // News / Cortana are POPUP items (no command id). Deleting their children left a
                // leftover separator, so a "submenu is empty" check used to miss them.
                RemovePopupsContaining(menu, 446, 447, 448, 625, 626, 627, 628, 640);
                RemoveEmptyPopups(menu);
                if (t == Target.EmptyTaskbar) Remove(menu, 408, 421);
                // Search submenu: Win11 SearchboxTaskbarMode 0=hidden 1=icon 2=box. Highlights/hover have no Win11 equivalent.
                Remove(menu, 657, 656);
                int mode = RegInt(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 1);
                NativeMethods.CheckMenuItem(menu, mode == 0 ? 432u : mode == 1 ? 433u : 434u, NativeMethods.MF_CHECKED);
                int tv = RegInt(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", 1);
                if (tv != 0) NativeMethods.CheckMenuItem(menu, 430, NativeMethods.MF_CHECKED);
                for (int i = 0; i < NativeMethods.GetMenuItemCount(menu); i++) { IntPtr s = NativeMethods.GetSubMenu(menu, i); if (s != IntPtr.Zero) TidySeparators(s); }
                TidySeparators(menu);
            }
            else if (t == Target.Volume)
            {
                // Spatial Audio submenu: Windows 10 listed formats dynamically; here "Off" opens the device's spatial sound settings.
            }

            ForceForeground();
            uint cmd = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN | NativeMethods.TPM_LEFTALIGN, x, y, Handle, IntPtr.Zero);
            NativeMethods.PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
            Program.Log("menu " + t + " cmd=" + cmd);
            if (cmd != 0) Run(t, (int)cmd);
        }
        catch (Exception e) { Program.Log("menu error: " + e); }
        finally { if (bar != IntPtr.Zero) NativeMethods.DestroyMenu(bar); Program.SetMenuOpen(false); }
    }

    // The swallowed click never reached any window, so Windows won't let us take the foreground by default.
    // Briefly attach to the current foreground thread's input so the popup gets focus and closes normally
    // on Esc or on a click elsewhere (the same thing a tray icon's own menu needs).
    void ForceForeground()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        uint me = NativeMethods.GetCurrentThreadId();
        uint other = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, IntPtr.Zero);
        bool attached = other != 0 && other != me && NativeMethods.AttachThreadInput(me, other, true);
        bool ok = NativeMethods.SetForegroundWindow(Handle);
        NativeMethods.BringWindowToTop(Handle);
        if (attached) NativeMethods.AttachThreadInput(me, other, false);
        if (!ok) Program.Log("SetForegroundWindow failed (menu may not auto-close)");
    }

    static void Start(string file, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
        catch (Exception e) { Program.Log("launch failed " + file + " " + args + ": " + e.Message); }
    }

    static void ShellCall(string method)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            object sh = Activator.CreateInstance(t);
            t.InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, sh, null);
            Marshal.ReleaseComObject(sh);
        }
        catch (Exception e) { Program.Log("shell call " + method + " failed: " + e.Message); }
    }

    static void SetReg(string key, string name, int val)
    {
        try { Registry.SetValue(key, name, val, RegistryValueKind.DWord); } catch (Exception e) { Program.Log("reg failed: " + e.Message); }
    }

    static void Run(Target t, int cmd)
    {
        string sys = Environment.SystemDirectory;
        switch (cmd)
        {
            // network (pnidui)
            case 3107: Start(Path.Combine(sys, "msdt.exe"), "-skip TRUE -path " + Path.Combine(Environment.GetEnvironmentVariable("windir"), @"diagnostics\system\networking") + " -ep NetworkDiagnosticsPNI"); break;
            case 3109: Start("ms-settings:network-status"); break;
            // sound (SndVolSSO)
            case 40012: Start("ms-settings:sound"); break;
            case 40001: Start(Path.Combine(sys, "SndVol.exe")); break;
            case 40100: Start("ms-settings:sound"); break;
            case 40007: Start(Path.Combine(sys, "rundll32.exe"), "shell32.dll,Control_RunDLL mmsys.cpl,,2"); break;
            case 40008: Start(Path.Combine(sys, "msdt.exe"), "-id AudioPlaybackDiagnostic"); break;
            // taskbar / clock (explorer)
            case 408: Start("ms-settings:dateandtime"); break;
            case 421: Start("ms-settings:taskbar"); break;
            case 432: SetReg(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0); break;
            case 433: SetReg(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 1); break;
            case 434: SetReg(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 2); break;
            case 430:
                int tv = RegInt(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", 1);
                SetReg(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", tv != 0 ? 0 : 1); break;
            case 403: ShellCall("CascadeWindows"); break;
            case 405: ShellCall("TileHorizontally"); break;
            case 404: ShellCall("TileVertically"); break;
            case 407: ShellCall("ToggleDesktop"); break;
            case 420: Start(Path.Combine(sys, "Taskmgr.exe")); break;
            case 413: Start("ms-settings:taskbar"); break;
        }
    }
}
