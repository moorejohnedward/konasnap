// KonaSnap - a simple Snagit-style screen capture and markup tool for Windows.
// v2: scrolling capture, panels inside windows, full-text grab, new tools and a fresh look.
// v2.1: in-app updates from GitHub Releases and a crash report that is saved and copied to the clipboard.
// Build with build.bat (uses the C# compiler that ships with Windows / .NET Framework 4).
// C# 5 syntax only, so it compiles with the built-in csc.exe.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("KonaSnap")]
[assembly: System.Reflection.AssemblyProduct("KonaSnap")]
[assembly: System.Reflection.AssemblyCompany("John Moore")]
[assembly: System.Reflection.AssemblyVersion("2.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.1.0.0")]

namespace KonaSnap
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Updater.InstallCrashHandlers();
            Updater.Cleanup();
            string cmd = Taskbar.FindCommand(args);
            bool justUpdated = Array.Exists(args, delegate(string a) { return a.Equals("--updated", StringComparison.OrdinalIgnoreCase); });
            bool created;
            using (var mutex = new Mutex(true, "KonaSnap_SingleInstance_Mutex", out created))
            {
                if (!created && justUpdated)
                {
                    // the old copy is still closing after an update; wait for it
                    try { created = mutex.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    IntPtr h = Native.FindWindow(null, MainForm.AppTitle);
                    if (h != IntPtr.Zero)
                    {
                        if (cmd != null) Taskbar.Send(h, cmd); // e.g. "Grab text" picked from the taskbar menu
                        else { Native.ShowWindow(h, 9); Native.SetForegroundWindow(h); }
                    }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm main;
                try { main = new MainForm(); }
                catch (Exception ex) { Updater.Report(ex, true); return; }
                main.StartupCommand = cmd;
                main.JustUpdated = justUpdated;
                Application.Run(main);
            }
        }
    }

    // ---- In-app updates and crash reports (added in v2.1) ----
    // Updates come from the GitHub Releases page of moorejohnedward/konasnap.
    // Each release carries KonaSnap.exe and KonaSnap.exe.sha256; the download is checked
    // against the hash before the running copy is swapped out.
    static class Updater
    {
        public const string Version = "2.1.0";
        const string Owner = "moorejohnedward", Repo = "konasnap", AssetName = "KonaSnap.exe";

        public class Info { public string Tag, ExeUrl, ShaUrl, Notes, PageUrl; public System.Version Ver; public long Size; }

        public static System.Version Current { get { return new System.Version(Version); } }
        public static string ExePath { get { return Application.ExecutablePath; } }

        static System.Net.WebClient Client()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; } catch { } // TLS 1.2
            var wc = new System.Net.WebClient();
            wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "KonaSnap/" + Version;
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        // Removes the previous copy left behind by the last update.
        public static void Cleanup()
        {
            try { string old = ExePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
            try { string nw = ExePath + ".new"; if (File.Exists(nw)) File.Delete(nw); } catch { }
        }

        static string JsonString(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return null;
            return System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value);
        }

        // Returns null when KonaSnap is up to date. Throws on network errors.
        public static Info Check()
        {
            string json;
            using (var wc = Client())
            {
                wc.Headers[System.Net.HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = wc.DownloadString("https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest");
            }
            var info = new Info();
            info.Tag = JsonString(json, "tag_name");
            info.Notes = JsonString(json, "body") ?? "";
            info.PageUrl = JsonString(json, "html_url");
            if (info.Tag == null) return null;
            System.Version v;
            if (!System.Version.TryParse(info.Tag.TrimStart('v', 'V'), out v)) return null;
            info.Ver = v;
            // each asset block: "name": "...", ... "size": n, ... "browser_download_url": "..."
            foreach (System.Text.RegularExpressions.Match a in System.Text.RegularExpressions.Regex.Matches(json,
                "\"name\"\\s*:\\s*\"([^\"]+)\"[^{}]*?\"size\"\\s*:\\s*(\\d+)[^{}]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string name = a.Groups[1].Value;
                if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) { info.ExeUrl = a.Groups[3].Value; info.Size = long.Parse(a.Groups[2].Value); }
                else if (name.Equals(AssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) info.ShaUrl = a.Groups[3].Value;
            }
            if (info.ExeUrl == null || info.ShaUrl == null) return null;
            return v > Current ? info : null;
        }

        // Downloads, verifies and swaps in the new KonaSnap.exe. Returns true when the swap
        // is done; the caller then restarts KonaSnap.
        public static void Install(Info info, Action<int> progress)
        {
            string exe = ExePath, nw = exe + ".new", old = exe + ".old";
            string expected;
            using (var wc = Client()) expected = wc.DownloadString(info.ShaUrl).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();

            using (var wc = Client())
            {
                var done = new ManualResetEvent(false);
                Exception err = null;
                wc.DownloadProgressChanged += delegate(object s, System.Net.DownloadProgressChangedEventArgs e) { if (progress != null) progress(e.ProgressPercentage); };
                wc.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e) { err = e.Error; done.Set(); };
                wc.DownloadFileAsync(new Uri(info.ExeUrl), nw);
                done.WaitOne();
                if (err != null) throw err;
            }

            if (info.Size > 0 && new FileInfo(nw).Length != info.Size) { File.Delete(nw); throw new Exception("The download was incomplete. Try again."); }
            string actual;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(nw))
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (actual != expected) { File.Delete(nw); throw new Exception("The download didn't match its checksum, so it wasn't installed."); }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);              // Windows lets a running exe be renamed
            try { File.Move(nw, exe); }
            catch { File.Move(old, exe); throw; }
        }

        public static void Restart()
        {
            System.Diagnostics.Process.Start(ExePath, "--updated");
            Application.Exit();
        }

        // ---- crash reports
        public static string LogDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KonaSnap"); } }
        public static string CrashLog { get { return Path.Combine(LogDir, "crash.log"); } }

        public static void InstallCrashHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Report(e.Exception, false); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Report(e.ExceptionObject as Exception, true); };
        }

        static bool reporting;
        public static void Report(Exception ex, bool fatal)
        {
            if (reporting) return;
            reporting = true;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("KonaSnap " + Version + " crash report, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Windows " + Environment.OSVersion.VersionString + (Environment.Is64BitProcess ? ", 64-bit" : ", 32-bit") + ", .NET " + Environment.Version);
                sb.AppendLine("Running from: " + ExePath);
                sb.AppendLine(ex == null ? "(no exception details)" : ex.ToString());
                sb.AppendLine(new string('-', 60));
                string text = sb.ToString();
                try { Directory.CreateDirectory(LogDir); File.AppendAllText(CrashLog, text); } catch { }
                try { Clipboard.SetText(text); } catch { }
                MessageBox.Show(
                    "KonaSnap ran into a problem" + (fatal ? " and has to close." : ". It will try to keep going.") + "\n\n" +
                    (ex == null ? "" : ex.GetType().Name + ": " + ex.Message + "\n\n") +
                    "The full details are copied to your clipboard and saved in:\n" + CrashLog + "\n\nPaste them to Claude to get it fixed.",
                    "KonaSnap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
            finally { reporting = false; }
        }
    }

    // Small "Check for updates" window.
    class UpdateForm : Form
    {
        readonly Label msg;
        readonly ProgressBar bar;
        readonly Button go, close;
        Updater.Info info;

        public UpdateForm(Updater.Info found)
        {
            Text = "KonaSnap updates";
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(440, 230);

            msg = new Label();
            msg.Location = new Point(16, 14);
            msg.Size = new Size(408, 140);
            msg.ForeColor = Theme.Ink;
            Controls.Add(msg);

            bar = new ProgressBar();
            bar.Location = new Point(16, 158);
            bar.Size = new Size(408, 14);
            bar.Visible = false;
            Controls.Add(bar);

            go = new Button();
            go.Text = "Update now";
            go.Size = new Size(120, 32);
            go.Location = new Point(196, 186);
            go.Enabled = false;
            go.Click += delegate { DoInstall(); };
            Controls.Add(go);

            close = new Button();
            close.Text = "Close";
            close.Size = new Size(100, 32);
            close.Location = new Point(324, 186);
            close.Click += delegate { Close(); };
            Controls.Add(close);
            AcceptButton = go;
            CancelButton = close;

            if (found != null) ShowRelease(found);
            else Shown += delegate { RunCheck(); };
        }

        void ShowRelease(Updater.Info found)
        {
            info = found;
            string notes = (found.Notes ?? "").Trim();
            if (notes.Length > 420) notes = notes.Substring(0, 420) + "...";
            msg.Text = "KonaSnap " + found.Ver + " is ready (you have " + Updater.Version + ").\n\n" + notes;
            go.Enabled = true;
            go.Focus();
        }

        void RunCheck()
        {
            msg.Text = "Checking for a newer KonaSnap...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                Updater.Info r = null; Exception err = null;
                try { r = Updater.Check(); } catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (err != null) msg.Text = "Couldn't reach the update server.\n\n" + err.Message;
                        else if (r == null) msg.Text = "You have the latest KonaSnap (" + Updater.Version + ").";
                        else ShowRelease(r);
                    });
                }
                catch { }
            });
        }

        void DoInstall()
        {
            go.Enabled = close.Enabled = false;
            bar.Visible = true;
            msg.Text = "Downloading KonaSnap " + info.Ver + "...";
            var i = info;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception err = null;
                try { Updater.Install(i, delegate(int p) { try { BeginInvoke((MethodInvoker)delegate { bar.Value = Math.Max(0, Math.Min(100, p)); }); } catch { } }); }
                catch (Exception ex) { err = ex; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (err != null)
                        {
                            bar.Visible = false;
                            msg.Text = "The update didn't install. KonaSnap is unchanged.\n\n" + err.Message;
                            go.Enabled = close.Enabled = true;
                            return;
                        }
                        msg.Text = "Installed. Restarting KonaSnap...";
                        Updater.Restart();
                    });
                }
                catch { }
            });
        }
    }

    static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);

        public static Point CursorPos()
        {
            POINT p;
            GetCursorPos(out p);
            return new Point(p.X, p.Y);
        }

        // Turns the mouse wheel where the cursor is. Negative notches scroll down.
        public static void Wheel(int notches)
        {
            var inp = new INPUT[1];
            inp[0].type = 0; // mouse
            inp[0].mi.mouseData = unchecked((uint)(notches * 120));
            inp[0].mi.dwFlags = 0x0800; // MOUSEEVENTF_WHEEL
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        // Visible top-level windows with their handles, top of the z-order first, in screen coordinates.
        public static List<KeyValuePair<IntPtr, Rectangle>> VisibleWindows()
        {
            var list = new List<KeyValuePair<IntPtr, Rectangle>>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsWindowVisible(h) || IsIconic(h)) return true;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                    if ((GetWindowLong(h, -20) & 0x80) != 0) return true; // tool windows
                    if (GetWindowTextLength(h) == 0) return true;
                    var sb = new StringBuilder(64);
                    GetClassName(h, sb, 64);
                    string cls = sb.ToString();
                    if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return true;
                    RECT r;
                    if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(h, out r);
                    var rc = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
                    if (rc.Width > 40 && rc.Height > 40) list.Add(new KeyValuePair<IntPtr, Rectangle>(h, rc));
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    // ------------------------------------------------------------------ taskbar: right-click menu (Jump List) and hover buttons
    static class Taskbar
    {
        // Commands, shared by the right-click menu, the hover buttons and the command line (KonaSnap.exe /text).
        public static readonly string[] Cmds = { "/text", "/scroll", "/area", "/full", "/delay" };
        public static readonly string[] Titles = { "Grab text from a box", "Scrolling capture", "Capture area or window", "Capture full screen", "Capture in 3 seconds" };
        public static readonly string[] Tips = {
            "Click a box to copy every word in it, even the scrolled part",
            "Scroll a page or panel and save one tall picture",
            "Drag an area, or click a window or panel",
            "The monitor your mouse is on",
            "Wait 3 seconds, then capture" };
        public static readonly string[] IconNames = { "textgrab", "scroll", "capture", "screen", "clock" };

        // ---- COM interfaces
        [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IObjectArray
        {
            void GetCount(out uint c);
            void GetAt(uint i, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object o);
        }

        [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IObjectCollection
        {
            void GetCount(out uint c);
            void GetAt(uint i, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object o);
            void AddObject([MarshalAs(UnmanagedType.Interface)] object o);
            void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray a);
            void RemoveObjectAt(uint i);
            void Clear();
        }

        [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICustomDestinationList
        {
            void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string id);
            void BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
            void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, [MarshalAs(UnmanagedType.Interface)] IObjectArray items);
            void AppendKnownCategory(int category);
            void AddUserTasks([MarshalAs(UnmanagedType.Interface)] IObjectArray tasks);
            void CommitList();
            void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object o);
            void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string id);
            void AbortList();
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder f, int cch, IntPtr fd, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string s);
            void GetHotkey(out short k);
            void SetHotkey(short k);
            void GetShowCmd(out int c);
            void SetShowCmd(int c);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int cch, out int i);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string s, int i);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string s, uint r);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string s);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit)]
        struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
            [FieldOffset(16)] public IntPtr pad;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            void GetCount(out uint c);
            void GetAt(uint i, out PROPERTYKEY k);
            void GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
            void SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct THUMBBUTTON
        {
            public uint dwMask;
            public uint iId;
            public uint iBitmap;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
            public uint dwFlags;
        }

        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool full);
            void SetProgressValue(IntPtr hwnd, ulong done, ulong total);
            void SetProgressState(IntPtr hwnd, int flags);
            void RegisterTab(IntPtr tab, IntPtr mdi);
            void UnregisterTab(IntPtr tab);
            void SetTabOrder(IntPtr tab, IntPtr before);
            void SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string desc);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
            void SetThumbnailClip(IntPtr hwnd, IntPtr rect);
        }

        static object Create(string clsid)
        {
            return Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid)));
        }

        // The right-click menu on the taskbar icon. Works even while KonaSnap is closed.
        public static void BuildJumpList()
        {
            try
            {
                string exe = Application.ExecutablePath;
                var list = (ICustomDestinationList)Create("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
                uint slots;
                object removed;
                Guid iidArray = typeof(IObjectArray).GUID;
                list.BeginList(out slots, ref iidArray, out removed);
                var tasks = (IObjectCollection)Create("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
                var titleKey = new PROPERTYKEY();
                titleKey.fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
                titleKey.pid = 2;
                for (int i = 0; i < Cmds.Length; i++)
                {
                    var link = (IShellLinkW)Create("00021401-0000-0000-C000-000000000046");
                    link.SetPath(exe);
                    link.SetArguments(Cmds[i]);
                    link.SetIconLocation(exe, 0);
                    link.SetDescription(Tips[i]);
                    link.SetWorkingDirectory(Path.GetDirectoryName(exe));
                    var store = (IPropertyStore)link;
                    var pv = new PROPVARIANT();
                    pv.vt = 31; // VT_LPWSTR
                    pv.p = Marshal.StringToCoTaskMemUni(Titles[i]);
                    try { store.SetValue(ref titleKey, ref pv); store.Commit(); }
                    finally { Marshal.FreeCoTaskMem(pv.p); }
                    tasks.AddObject(link);
                }
                list.AddUserTasks((IObjectArray)tasks);
                list.CommitList();
            }
            catch { } // the menu is a convenience; KonaSnap works without it
        }

        // Buttons under the little preview that appears when you hover the taskbar icon.
        public const int FirstButtonId = 100;

        public static void AddThumbButtons(IntPtr hwnd)
        {
            try
            {
                var tb = (ITaskbarList3)Create("56FDF344-FD6D-11d0-958A-006097C9A090");
                tb.HrInit();
                int sz = SystemInformation.SmallIconSize.Width;
                var buttons = new THUMBBUTTON[Cmds.Length];
                for (int i = 0; i < Cmds.Length; i++)
                {
                    buttons[i].dwMask = 2 | 4 | 8; // icon, tooltip, flags
                    buttons[i].iId = (uint)(FirstButtonId + i);
                    buttons[i].hIcon = Icons.Get(IconNames[i], sz, Theme.Lagoon).GetHicon();
                    buttons[i].szTip = Titles[i];
                    buttons[i].dwFlags = 0; // enabled
                }
                tb.ThumbBarAddButtons(hwnd, (uint)buttons.Length, buttons);
            }
            catch { }
        }

        // ---- second copy of KonaSnap (started from the menu) hands its command to the running one
        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref COPYDATASTRUCT l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
        public const int WM_COPYDATA = 0x004A;
        static readonly IntPtr Tag = new IntPtr(0x4B534E50); // "KSNP"

        public static void Send(IntPtr hwnd, string cmd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try { AllowSetForegroundWindow(pid); } catch { }
            var cds = new COPYDATASTRUCT();
            cds.dwData = Tag;
            cds.cbData = (cmd.Length + 1) * 2;
            cds.lpData = Marshal.StringToHGlobalUni(cmd);
            try { SendMessage(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds); }
            finally { Marshal.FreeHGlobal(cds.lpData); }
        }

        public static string Receive(Message m)
        {
            try
            {
                var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
                if (cds.dwData != Tag || cds.lpData == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(cds.lpData);
            }
            catch { return null; }
        }

        public static string FindCommand(string[] args)
        {
            foreach (var a in args)
            {
                string x = a.Trim().ToLowerInvariant().Replace('-', '/');
                if (!x.StartsWith("/")) x = "/" + x;
                foreach (var c in Cmds) if (x == c) return c;
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ look and feel
    static class Theme
    {
        public static readonly Color Ocean = Color.FromArgb(11, 79, 108);
        public static readonly Color OceanDeep = Color.FromArgb(6, 44, 62);
        public static readonly Color Lagoon = Color.FromArgb(0, 174, 239);
        public static readonly Color Reef = Color.FromArgb(0, 166, 150);
        public static readonly Color Sunset = Color.FromArgb(255, 112, 67);
        public static readonly Color Ink = Color.FromArgb(14, 39, 51);
        public static readonly Color Muted = Color.FromArgb(85, 112, 128);
        public static readonly Color Foam = Color.FromArgb(242, 248, 251);
        public static readonly Color Line = Color.FromArgb(205, 228, 236);
        public static readonly Color Slate = Color.FromArgb(36, 44, 52);
        public static readonly Color SelectFill = Color.FromArgb(218, 242, 253);

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

        // A soft shadow under a rounded shape.
        public static void SoftShadow(Graphics g, RectangleF r, float rad, int strength, float spread)
        {
            for (int i = (int)spread; i >= 1; i--)
            {
                var rr = RectangleF.Inflate(r, i, i);
                rr.Offset(0, i * 0.5f);
                int a = (int)(strength * (1f - i / (spread + 1f)) / spread * 2.2f);
                using (var p = Round(rr, rad + i))
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, a)), 0, 0, 0)))
                    g.FillPath(b, p);
            }
        }
    }

    // Small line-art icons drawn in code, so there are no image files to ship.
    static class Icons
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();

        public static Bitmap Get(string name, int size, Color c)
        {
            size = Math.Max(8, size);
            string key = name + "|" + size + "|" + c.ToArgb();
            Bitmap b;
            if (cache.TryGetValue(key, out b)) return b;
            b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.ScaleTransform(size / 24f, size / 24f);
                try { Draw(g, name, c); } catch { }
            }
            cache[key] = b;
            return b;
        }

        static void Draw(Graphics g, string name, Color c)
        {
            using (var p = new Pen(c, 2f))
            using (var br = new SolidBrush(c))
            using (var soft = new SolidBrush(Theme.Alpha(c, 70)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                switch (name)
                {
                    case "arrow":
                        g.DrawLine(p, 5, 19, 14, 10);
                        g.FillPolygon(br, new PointF[] { new PointF(19.5f, 4.5f), new PointF(10.5f, 6.5f), new PointF(17.5f, 13.5f) });
                        break;
                    case "line":
                        g.DrawLine(p, 5, 19, 19, 5);
                        g.FillEllipse(br, 3.2f, 17.2f, 3.6f, 3.6f);
                        g.FillEllipse(br, 17.2f, 3.2f, 3.6f, 3.6f);
                        break;
                    case "box":
                        using (var r = Theme.Round(new RectangleF(4, 6, 16, 12), 2.5f)) g.DrawPath(p, r);
                        break;
                    case "ellipse":
                        g.DrawEllipse(p, 3.5f, 5.5f, 17, 13);
                        break;
                    case "highlight":
                        g.FillRectangle(soft, 3, 15, 18, 6);
                        g.DrawPolygon(p, new PointF[] { new PointF(8, 14), new PointF(14.5f, 4), new PointF(19, 7), new PointF(12.5f, 17) });
                        g.DrawLine(p, 8, 14, 7, 17.5f);
                        g.DrawLine(p, 7, 17.5f, 12.5f, 17);
                        break;
                    case "pen":
                        g.DrawBezier(p, 4, 17, 8, 4, 13, 23, 20, 7);
                        break;
                    case "text":
                        g.DrawLine(p, 6, 5.5f, 18, 5.5f);
                        g.DrawLine(p, 12, 5.5f, 12, 19);
                        g.DrawLine(p, 9.5f, 19, 14.5f, 19);
                        break;
                    case "step":
                        g.FillEllipse(br, 3, 3, 18, 18);
                        using (var f = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("1", f, Brushes.White, new RectangleF(3, 3.5f, 18, 18), sf);
                        }
                        break;
                    case "blur":
                        for (int i = 0; i < 3; i++)
                            for (int j = 0; j < 3; j++)
                                using (var bb = new SolidBrush(Theme.Alpha(c, (i + j) % 2 == 0 ? 230 : 90)))
                                    g.FillRectangle(bb, 4 + i * 5.5f, 4 + j * 5.5f, 5f, 5f);
                        break;
                    case "spotlight":
                        using (var path = new GraphicsPath())
                        {
                            path.FillMode = FillMode.Alternate;
                            path.AddRectangle(new RectangleF(3, 3, 18, 18));
                            path.AddEllipse(7, 7, 10, 10);
                            g.FillPath(soft, path);
                        }
                        g.DrawEllipse(p, 7, 7, 10, 10);
                        break;
                    case "magnify":
                        g.DrawEllipse(p, 3.5f, 3.5f, 12, 12);
                        using (var hp = new Pen(c, 3.2f)) { hp.StartCap = LineCap.Round; hp.EndCap = LineCap.Round; g.DrawLine(hp, 14, 14, 20, 20); }
                        g.DrawLine(p, 9.5f, 7, 9.5f, 12);
                        g.DrawLine(p, 7, 9.5f, 12, 9.5f);
                        break;
                    case "crop":
                        g.DrawLines(p, new PointF[] { new PointF(7, 3), new PointF(7, 17), new PointF(21, 17) });
                        g.DrawLines(p, new PointF[] { new PointF(3, 7), new PointF(17, 7), new PointF(17, 21) });
                        break;
                    case "undo":
                        g.DrawBezier(p, 19, 18, 19, 9, 13, 8.5f, 6, 9);
                        g.DrawLine(p, 6, 9, 10, 5); g.DrawLine(p, 6, 9, 10, 13);
                        break;
                    case "redo":
                        g.DrawBezier(p, 5, 18, 5, 9, 11, 8.5f, 18, 9);
                        g.DrawLine(p, 18, 9, 14, 5); g.DrawLine(p, 18, 9, 14, 13);
                        break;
                    case "copy":
                        using (var r = Theme.Round(new RectangleF(9, 3.5f, 11, 13), 2f)) g.DrawPath(p, r);
                        using (var r = Theme.Round(new RectangleF(4, 8, 11, 13), 2f)) { g.FillPath(Brushes.White, r); g.DrawPath(p, r); }
                        break;
                    case "copytext":
                        using (var r = Theme.Round(new RectangleF(5, 3, 14, 18), 2f)) g.DrawPath(p, r);
                        g.DrawLine(p, 8.5f, 8, 15.5f, 8); g.DrawLine(p, 8.5f, 12, 15.5f, 12); g.DrawLine(p, 8.5f, 16, 13, 16);
                        break;
                    case "save":
                    case "saveas":
                        using (var r = Theme.Round(new RectangleF(4, 4, 16, 16), 2.5f)) g.DrawPath(p, r);
                        g.DrawRectangle(p, 8, 4, 7, 5);
                        g.DrawRectangle(p, 7.5f, 13, 9, 7);
                        if (name == "saveas") { g.FillEllipse(Brushes.White, 14, 13, 9, 9); g.DrawLine(p, 18.5f, 15, 18.5f, 20); g.DrawLine(p, 16, 17.5f, 21, 17.5f); }
                        break;
                    case "fit":
                        g.DrawLines(p, new PointF[] { new PointF(4, 9), new PointF(4, 4), new PointF(9, 4) });
                        g.DrawLines(p, new PointF[] { new PointF(15, 4), new PointF(20, 4), new PointF(20, 9) });
                        g.DrawLines(p, new PointF[] { new PointF(20, 15), new PointF(20, 20), new PointF(15, 20) });
                        g.DrawLines(p, new PointF[] { new PointF(9, 20), new PointF(4, 20), new PointF(4, 15) });
                        break;
                    case "actual":
                        using (var f = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("1:1", f, br, new RectangleF(0, 1, 24, 22), sf);
                        }
                        using (var r = Theme.Round(new RectangleF(2.5f, 5, 19, 14), 2.5f)) g.DrawPath(p, r);
                        break;
                    case "capture":
                    case "textgrab":
                        g.DrawLines(p, new PointF[] { new PointF(3, 8), new PointF(3, 3), new PointF(8, 3) });
                        g.DrawLines(p, new PointF[] { new PointF(16, 3), new PointF(21, 3), new PointF(21, 8) });
                        g.DrawLines(p, new PointF[] { new PointF(21, 16), new PointF(21, 21), new PointF(16, 21) });
                        g.DrawLines(p, new PointF[] { new PointF(8, 21), new PointF(3, 21), new PointF(3, 16) });
                        if (name == "capture") g.FillEllipse(br, 9.5f, 9.5f, 5, 5);
                        else { g.DrawLine(p, 8, 8, 16, 8); g.DrawLine(p, 12, 8, 12, 17); }
                        break;
                    case "scroll":
                        using (var r = Theme.Round(new RectangleF(5, 2.5f, 14, 19), 2.5f)) g.DrawPath(p, r);
                        g.DrawLine(p, 12, 7, 12, 16);
                        g.DrawLine(p, 12, 16, 9, 13); g.DrawLine(p, 12, 16, 15, 13);
                        break;
                    case "screen":
                        using (var r = Theme.Round(new RectangleF(3, 4, 18, 12), 2f)) g.DrawPath(p, r);
                        g.DrawLine(p, 12, 16, 12, 19.5f); g.DrawLine(p, 8, 20, 16, 20);
                        break;
                    case "clock":
                        g.DrawEllipse(p, 3, 3, 18, 18);
                        g.DrawLines(p, new PointF[] { new PointF(12, 7), new PointF(12, 12), new PointF(15.5f, 14) });
                        break;
                    case "folder":
                        using (var path = new GraphicsPath())
                        {
                            path.AddLines(new PointF[] { new PointF(3, 7), new PointF(3, 19), new PointF(21, 19), new PointF(21, 9), new PointF(11, 9), new PointF(9, 6), new PointF(4, 6) });
                            path.CloseFigure();
                            g.DrawPath(p, path);
                        }
                        break;
                    case "new":
                        g.DrawEllipse(p, 3, 3, 18, 18);
                        g.DrawLine(p, 12, 8, 12, 16); g.DrawLine(p, 8, 12, 16, 12);
                        break;
                }
            }
        }
    }

    // Big rounded button with an icon, a subtitle, a shortcut pill and a smooth hover glow.
    class NiceButton : Control
    {
        public string Title = "", Sub = "", IconName = "", Key = "";
        public bool Primary;
        readonly float S;
        float hover;
        bool over, down;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        readonly Font titleFont, subFont, keyFont;

        public NiceButton(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            titleFont = new Font("Segoe UI Semibold", 10.5f);
            subFont = new Font("Segoe UI", 8.5f);
            keyFont = new Font("Segoe UI Semibold", 8f);
            anim.Interval = 15;
            anim.Tick += delegate
            {
                float target = over ? 1f : 0f;
                hover += (target - hover) * 0.28f;
                if (Math.Abs(target - hover) < 0.02f) { hover = target; anim.Stop(); }
                Invalidate();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Dispose(); titleFont.Dispose(); subFont.Dispose(); keyFont.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnMouseEnter(EventArgs e) { over = true; anim.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; down = false; anim.Start(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float pad = 4 * S;
            var r = new RectangleF(pad, pad * 0.5f, Width - pad * 2 - 1, Height - pad * 2);
            float rad = 10 * S;

            Color top, bottom, border, tc, sc;
            if (Primary)
            {
                top = Theme.Mix(Theme.Ocean, Theme.Lagoon, 0.10f + 0.25f * hover);
                bottom = Theme.Mix(Theme.OceanDeep, Theme.Reef, 0.25f + 0.35f * hover);
                border = Theme.Mix(Theme.Ocean, Theme.Lagoon, 0.3f * hover);
                tc = Color.White; sc = Color.FromArgb(205, 232, 244);
            }
            else
            {
                top = Theme.Mix(Color.White, Color.FromArgb(236, 249, 255), hover);
                bottom = Theme.Mix(Theme.Foam, Color.FromArgb(220, 243, 252), hover);
                border = Theme.Mix(Theme.Line, Theme.Lagoon, hover);
                tc = Theme.Ink; sc = Theme.Muted;
            }
            if (down) { top = Theme.Mix(top, Color.Black, 0.08f); bottom = Theme.Mix(bottom, Color.Black, 0.08f); }

            Theme.SoftShadow(g, r, rad, (int)(18 + 30 * hover), 3 * S);
            using (var path = Theme.Round(r, rad))
            {
                using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, 90f)) g.FillPath(lg, path);
                using (var bp = new Pen(border, 1f)) g.DrawPath(bp, path);
            }

            // icon bubble
            float d = r.Height - 16 * S;
            var circ = new RectangleF(r.X + 9 * S, r.Y + (r.Height - d) / 2, d, d);
            Color bubble = Primary ? Color.FromArgb((int)(45 + 25 * hover), 255, 255, 255) : Theme.Mix(Color.FromArgb(222, 241, 249), Color.FromArgb(200, 236, 250), hover);
            using (var bb = new SolidBrush(bubble)) g.FillEllipse(bb, circ);
            int isz = (int)(d * 0.6f);
            var ico = Icons.Get(IconName, isz, Primary ? Color.White : Theme.Ocean);
            g.DrawImage(ico, circ.X + (d - isz) / 2f, circ.Y + (d - isz) / 2f, isz, isz);

            // shortcut pill
            float right = r.Right - 10 * S;
            if (Key.Length > 0)
            {
                var ks = g.MeasureString(Key, keyFont);
                var pill = new RectangleF(right - ks.Width - 12 * S, r.Y + (r.Height - ks.Height - 6 * S) / 2, ks.Width + 12 * S, ks.Height + 6 * S);
                using (var pp = Theme.Round(pill, pill.Height / 2))
                {
                    using (var pb = new SolidBrush(Primary ? Color.FromArgb(40, 255, 255, 255) : Color.White)) g.FillPath(pb, pp);
                    using (var pn = new Pen(Primary ? Color.FromArgb(90, 255, 255, 255) : Theme.Line)) g.DrawPath(pn, pp);
                }
                using (var kb = new SolidBrush(Primary ? Color.White : Theme.Ocean)) g.DrawString(Key, keyFont, kb, pill.X + 6 * S, pill.Y + 3 * S);
                right = pill.X - 8 * S;
            }

            // title and subtitle
            float tx = circ.Right + 11 * S;
            var th = g.MeasureString("Ag", titleFont).Height;
            var sh = Sub.Length > 0 ? g.MeasureString("Ag", subFont).Height : 0f;
            float ty = r.Y + (r.Height - th - sh + 2 * S) / 2;
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                using (var tb = new SolidBrush(tc)) g.DrawString(Title, titleFont, tb, new RectangleF(tx, ty, Math.Max(10, right - tx), th), sf);
                if (Sub.Length > 0)
                    using (var sb = new SolidBrush(sc)) g.DrawString(Sub, subFont, sb, new RectangleF(tx, ty + th - 2 * S, Math.Max(10, right - tx), sh), sf);
            }
        }
    }

    // The ocean-gradient band at the top of the main window.
    class HeaderBand : Control
    {
        readonly float S;
        public HeaderBand(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rc = ClientRectangle;
            using (var lg = new LinearGradientBrush(rc, Theme.OceanDeep, Theme.Reef, 15f)) g.FillRectangle(lg, rc);
            // gentle waves
            for (int i = 0; i < 3; i++)
            {
                float y = rc.Height * (0.62f + i * 0.13f);
                using (var wp = new Pen(Color.FromArgb(28 - i * 6, 255, 255, 255), 2f * S))
                    g.DrawBezier(wp, -10, y, rc.Width * 0.3f, y - 14 * S, rc.Width * 0.6f, y + 14 * S, rc.Width + 10, y - 4 * S);
            }
            // logo tile
            float L = 42 * S;
            var tile = new RectangleF(16 * S, (rc.Height - L) / 2, L, L);
            using (var tp = Theme.Round(tile, 10 * S))
            {
                using (var tb = new SolidBrush(Color.FromArgb(55, 255, 255, 255))) g.FillPath(tb, tp);
                using (var tpn = new Pen(Color.FromArgb(90, 255, 255, 255))) g.DrawPath(tpn, tp);
            }
            var ico = Icons.Get("capture", (int)(L * 0.62f), Color.White);
            g.DrawImage(ico, tile.X + (L - ico.Width) / 2f, tile.Y + (L - ico.Height) / 2f);
            using (var dot = new SolidBrush(Theme.Sunset)) g.FillEllipse(dot, tile.X + L / 2 - 4 * S, tile.Y + L / 2 - 4 * S, 8 * S, 8 * S);

            using (var tf = new Font("Segoe UI Semibold", 17f))
            using (var sf = new Font("Segoe UI", 9f))
            {
                float x = tile.Right + 12 * S;
                var th = g.MeasureString("KonaSnap", tf);
                float y = (rc.Height - th.Height - 16 * S) / 2;
                g.DrawString("KonaSnap", tf, Brushes.White, x, y);
                using (var b = new SolidBrush(Color.FromArgb(215, 236, 246)))
                    g.DrawString("Capture  •  Scroll  •  Mark up  •  Copy text", sf, b, x + 2 * S, y + th.Height - 4 * S);
            }
        }
    }

    // ------------------------------------------------------------------ text recognition (Windows built-in OCR)
    static class Ocr
    {
        // Runs Windows.Media.Ocr through Windows PowerShell, so no extra SDK or download is needed.
        const string Script = @"param([string]$Path)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $type) { $t = $asTaskGeneric.MakeGenericMethod($type).Invoke($null, @($op)); $null = $t.Wait(-1); $t.Result }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$max = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
if ($decoder.PixelWidth -gt $max -or $decoder.PixelHeight -gt $max) {
  $s = [Math]::Min($max / $decoder.PixelWidth, $max / $decoder.PixelHeight)
  $tr = [Windows.Graphics.Imaging.BitmapTransform]::new()
  $tr.ScaledWidth = [uint32][Math]::Floor($decoder.PixelWidth * $s)
  $tr.ScaledHeight = [uint32][Math]::Floor($decoder.PixelHeight * $s)
  $tr.InterpolationMode = [Windows.Graphics.Imaging.BitmapInterpolationMode]::Fant
  $bitmap = Await ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied, $tr, [Windows.Graphics.Imaging.ExifOrientationMode]::IgnoreExifOrientation, [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
} else {
  $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
}
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($engine -eq $null) { [Console]::Error.WriteLine('NO_OCR_LANGUAGE'); exit 2 }
$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
$stream.Dispose()
foreach ($line in $result.Lines) { [Console]::Out.WriteLine($line.Text) }
";

        // Long images (like scrolling captures) are read in slices, cut on blank rows so no line of text is split.
        public static string Run(Bitmap src, out string error)
        {
            error = null;
            if (src.Height <= 2400) return RunOne(src, out error);
            var cuts = SliceRows(src, 1600);
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                var rect = new Rectangle(0, cuts[i], src.Width, cuts[i + 1] - cuts[i]);
                if (rect.Height < 2) continue;
                string e, part;
                using (var piece = src.Clone(rect, PixelFormat.Format32bppArgb)) part = RunOne(piece, out e);
                if (e != null && error == null) error = e;
                if (part.Length > 0) { if (sb.Length > 0) sb.Append('\n'); sb.Append(part); }
                if (e != null && e.StartsWith("Windows has no text")) break;
            }
            return sb.ToString();
        }

        static List<int> SliceRows(Bitmap src, int target)
        {
            bool[] plain = Px.PlainRows(src);
            int h = src.Height;
            var cuts = new List<int>();
            cuts.Add(0);
            int pos = 0;
            while (h - pos > target + target / 3)
            {
                int want = pos + target, cut = want;
                for (int d = 0; d < target / 4; d++)
                {
                    if (want - d > pos + 50 && plain[want - d]) { cut = want - d; break; }
                    if (want + d < h - 50 && plain[want + d]) { cut = want + d; break; }
                }
                cuts.Add(cut);
                pos = cut;
            }
            cuts.Add(h);
            return cuts;
        }

        static string RunOne(Bitmap src, out string error)
        {
            error = null;
            string dir = Path.Combine(Path.GetTempPath(), "KonaSnap");
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "ocr.ps1");
            File.WriteAllText(script, Script, new UTF8Encoding(true));
            string imgPath = Path.Combine(dir, "ocr-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                // Small text reads better when the image is enlarged first.
                int longest = Math.Max(src.Width, src.Height);
                float k = longest < 1800 ? 2f : 1f;
                using (var big = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)), PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(big))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, big.Width, big.Height);
                    }
                    big.Save(imgPath, ImageFormat.Png);
                }

                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" \"" + imgPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } error = "Reading the text took too long."; return ""; }
                    string err = errTask.Result;
                    if (err.Contains("NO_OCR_LANGUAGE"))
                        error = "Windows has no text-recognition language installed. Add one in Settings > Time & language > Language & region.";
                    else if (p.ExitCode != 0 && output.Trim().Length == 0)
                        error = "Windows couldn't read text from this image." + (err.Trim().Length > 0 ? " (" + FirstLine(err) + ")" : "");
                    return output.Replace("\r\n", "\n").Trim();
                }
            }
            catch (Exception ex)
            {
                error = "Text reading failed: " + ex.Message;
                return "";
            }
            finally
            {
                try { File.Delete(imgPath); } catch { }
            }
        }

        static string FirstLine(string s)
        {
            s = s.Trim();
            int i = s.IndexOf('\n');
            return i > 0 ? s.Substring(0, i).Trim() : s;
        }

        // Reads text on a background thread, then calls done(text, error) on the UI thread.
        public static void RunAsync(Control owner, Bitmap bmp, Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                string text = Run(bmp, out err);
                bmp.Dispose();
                try { owner.BeginInvoke((MethodInvoker)delegate { done(text, err); }); } catch { }
            });
        }

        public static void ShowResult(string text, string err, float S)
        {
            ShowResult(text, err, S, null);
        }

        public static void ShowResult(string text, string err, float S, string how)
        {
            if (err != null && text.Length == 0) { MessageBox.Show(err, MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (text.Length == 0) { MessageBox.Show("No text was found in that area.", MainForm.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { Clipboard.SetText(text.Replace("\n", "\r\n")); } catch { }
            var f = new TextResultForm(text, S, how);
            f.Show();
            f.Activate();
        }
    }

    class TextResultForm : Form
    {
        public TextResultForm(string text, float S, string how)
        {
            Text = "KonaSnap - Text";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size((int)(720 * S), (int)(560 * S));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var tb = new TextBox();
            tb.Multiline = true;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.Dock = DockStyle.Fill;
            tb.BorderStyle = BorderStyle.None;
            tb.Font = new Font("Segoe UI", 11f);
            tb.Text = text.Replace("\n", "\r\n");

            var holder = new Panel();
            holder.Dock = DockStyle.Fill;
            holder.Padding = new Padding((int)(12 * S));
            holder.BackColor = Color.White;
            holder.Controls.Add(tb);

            var bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.FlowDirection = FlowDirection.RightToLeft;
            bottom.AutoSize = true;
            bottom.Padding = new Padding((int)(8 * S));

            bottom.BackColor = Theme.Foam;
            var close = new Button();
            close.Text = "Close";
            close.AutoSize = true;
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderColor = Theme.Line;
            close.BackColor = Color.White;
            close.ForeColor = Theme.Ink;
            close.Padding = new Padding((int)(10 * S), (int)(3 * S), (int)(10 * S), (int)(3 * S));
            close.Click += delegate { Close(); };
            var copy = new Button();
            copy.Text = "Copy";
            copy.AutoSize = true;
            copy.FlatStyle = FlatStyle.Flat;
            copy.FlatAppearance.BorderColor = Theme.Ocean;
            copy.BackColor = Theme.Ocean;
            copy.ForeColor = Color.White;
            copy.Font = new Font("Segoe UI Semibold", 10f);
            copy.Padding = new Padding((int)(14 * S), (int)(3 * S), (int)(14 * S), (int)(3 * S));
            var note = new Label();
            note.AutoSize = true;
            note.ForeColor = Color.FromArgb(85, 112, 128);
            note.Margin = new Padding(3, (int)(8 * S), (int)(12 * S), 3);
            note.Text = "Copied to the clipboard. Paste it into your Google Doc with Ctrl+V.";
            copy.Click += delegate
            {
                try { Clipboard.SetText(tb.Text.Length > 0 ? tb.Text : " "); note.Text = "Copied again, including your edits."; } catch { }
            };
            var gdoc = new Button();
            gdoc.Text = "New Google Doc";
            gdoc.AutoSize = true;
            gdoc.FlatStyle = FlatStyle.Flat;
            gdoc.FlatAppearance.BorderColor = Theme.Reef;
            gdoc.BackColor = Theme.Reef;
            gdoc.ForeColor = Color.White;
            gdoc.Font = new Font("Segoe UI Semibold", 10f);
            gdoc.Padding = new Padding((int)(14 * S), (int)(3 * S), (int)(14 * S), (int)(3 * S));
            gdoc.Click += delegate
            {
                try { Clipboard.SetText(tb.Text.Length > 0 ? tb.Text : " "); } catch { }
                try { System.Diagnostics.Process.Start("https://docs.new"); note.Text = "A new Google Doc is opening. Click in it and press Ctrl+V."; }
                catch (Exception ex) { note.Text = "Couldn't open the browser: " + ex.Message; }
            };
            bottom.Controls.Add(close);
            bottom.Controls.Add(copy);
            bottom.Controls.Add(gdoc);
            bottom.Controls.Add(note);

            var top = new Label();
            top.Dock = DockStyle.Top;
            top.AutoSize = false;
            top.Height = (int)(34 * S);
            top.TextAlign = ContentAlignment.MiddleLeft;
            top.Padding = new Padding((int)(12 * S), 0, 0, 0);
            top.BackColor = Theme.Ocean;
            top.ForeColor = Color.White;
            top.Font = new Font("Segoe UI Semibold", 9.5f);
            int words = text.Split(new char[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
            top.Text = words + " words" + (how != null ? "  •  " + how : "");

            Controls.Add(holder);
            Controls.Add(top);
            Controls.Add(bottom);
            AcceptButton = copy;
        }
    }

    // ------------------------------------------------------------------ main window
    class MainForm : Form
    {
        public const string AppTitle = "KonaSnap";
        public static MainForm Instance;
        public static string CaptureDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KonaSnap");
        const int HK_PRTSC = 1, HK_CTRLSHIFTX = 2, HK_TEXT = 3, HK_SCROLL = 4;
        bool busy;
        NiceButton regionBtn, textBtn, scrollBtn;
        public float S = 1f; // DPI scale
        public string StartupCommand;
        public bool JustUpdated;
        LinkLabel updateLink;
        readonly uint WM_TASKBARBUTTONCREATED = Native.RegisterWindowMessage("TaskbarButtonCreated");

        public MainForm()
        {
            Instance = this;
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            Text = AppTitle;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var flow = new FlowLayoutPanel();
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.Padding = new Padding((int)(12 * S), (int)(12 * S), (int)(12 * S), (int)(10 * S));
            flow.BackColor = Color.White;

            regionBtn = Btn("Capture area or window", "Drag an area, or click a window or panel", "capture", true, delegate { StartCapture(0, false); });
            scrollBtn = Btn("Scrolling capture", "One tall image of a long page or panel", "scroll", false, delegate { StartCapture(0, false, false, true); });
            textBtn = Btn("Grab text", "Click any box: every word, even scrolled", "textgrab", false, delegate { StartCapture(0, false, true); });
            flow.Controls.Add(regionBtn);
            flow.Controls.Add(scrollBtn);
            flow.Controls.Add(textBtn);
            flow.Controls.Add(Btn("Full screen", "The monitor your mouse is on", "screen", false, delegate { StartCapture(0, true); }));
            flow.Controls.Add(Btn("Capture in 3 seconds", "Time to open a menu first", "clock", false, delegate { StartCapture(3000, false); }));
            flow.Controls.Add(Btn("Open captures folder", "Pictures\\KonaSnap", "folder", false, delegate { OpenFolder(); }));

            var tip = new Label();
            tip.AutoSize = true;
            tip.MaximumSize = new Size((int)(370 * S), 0);
            tip.ForeColor = Theme.Muted;
            tip.Font = new Font("Segoe UI", 9f);
            tip.Margin = new Padding((int)(4 * S), (int)(4 * S), 3, 3);
            tip.Text = "In the picker, the mouse wheel steps between a whole window and the panels inside it. Every capture is copied to the clipboard and saved in Pictures\\KonaSnap.";
            flow.Controls.Add(tip);

            updateLink = new LinkLabel();
            updateLink.AutoSize = true;
            updateLink.Font = new Font("Segoe UI", 9f);
            updateLink.LinkColor = Theme.Ocean;
            updateLink.ActiveLinkColor = Theme.Lagoon;
            updateLink.Margin = new Padding((int)(4 * S), (int)(2 * S), 3, 3);
            updateLink.Text = "KonaSnap " + Updater.Version + "  \u00b7  Check for updates";
            updateLink.LinkArea = new LinkArea(updateLink.Text.Length - "Check for updates".Length, "Check for updates".Length);
            updateLink.LinkClicked += delegate { ShowUpdates(pendingUpdate); };
            flow.Controls.Add(updateLink);

            var header = new HeaderBand(S);
            Controls.Add(header);
            Controls.Add(flow);
            var pref = flow.PreferredSize;
            header.Bounds = new Rectangle(0, 0, pref.Width, (int)(74 * S));
            flow.Location = new Point(0, header.Height);
            ClientSize = new Size(pref.Width, header.Height + pref.Height);
        }

        NiceButton Btn(string title, string sub, string icon, bool primary, EventHandler click)
        {
            var b = new NiceButton(S);
            b.Title = title;
            b.Sub = sub;
            b.IconName = icon;
            b.Primary = primary;
            b.Size = new Size((int)(380 * S), (int)(60 * S));
            b.Margin = new Padding(0, 0, 0, (int)(2 * S));
            b.Click += click;
            return b;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var keys = new List<string>();
            if (Native.RegisterHotKey(Handle, HK_PRTSC, 0x4000, 0x2C)) keys.Add("PrtScn");
            if (Native.RegisterHotKey(Handle, HK_CTRLSHIFTX, 0x0002 | 0x0004 | 0x4000, (uint)Keys.X)) keys.Add("Ctrl+Shift+X");
            if (keys.Count > 0) regionBtn.Key = keys[0];
            if (Native.RegisterHotKey(Handle, HK_TEXT, 0x0002 | 0x0001 | 0x4000, (uint)Keys.T)) textBtn.Key = "Ctrl+Alt+T";
            if (Native.RegisterHotKey(Handle, HK_SCROLL, 0x0002 | 0x0001 | 0x4000, (uint)Keys.S)) scrollBtn.Key = "Ctrl+Alt+S";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Taskbar.BuildJumpList();
            if (JustUpdated) updateLink.Text = "Updated to KonaSnap " + Updater.Version + "  \u00b7  Check for updates";
            if (JustUpdated) updateLink.LinkArea = new LinkArea(updateLink.Text.Length - "Check for updates".Length, "Check for updates".Length);
            CheckForUpdateQuietly();
            if (StartupCommand != null)
            {
                string c = StartupCommand;
                StartupCommand = null;
                BeginInvoke((MethodInvoker)delegate { RunCommand(c); });
            }
        }

        Updater.Info pendingUpdate;

        // Looks for a new version in the background; if there is one, the link at the bottom says so.
        void CheckForUpdateQuietly()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Updater.Info r = null;
                try { r = Updater.Check(); } catch { }
                if (r == null) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        pendingUpdate = r;
                        string t = "Update ready: KonaSnap " + r.Ver;
                        updateLink.Text = t;
                        updateLink.LinkArea = new LinkArea(0, t.Length);
                        updateLink.LinkColor = Theme.Sunset;
                    });
                }
                catch { }
            });
        }

        void ShowUpdates(Updater.Info found)
        {
            using (var f = new UpdateForm(found)) f.ShowDialog(this);
        }

        public void RunCommand(string cmd)
        {
            switch (cmd)
            {
                case "/text": StartCapture(0, false, true); break;
                case "/scroll": StartCapture(0, false, false, true); break;
                case "/area": StartCapture(0, false); break;
                case "/full": StartCapture(0, true); break;
                case "/delay": StartCapture(3000, false); break;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (WM_TASKBARBUTTONCREATED != 0 && m.Msg == (int)WM_TASKBARBUTTONCREATED)
            {
                Taskbar.AddThumbButtons(Handle);
            }
            else if (m.Msg == 0x0111 && ((m.WParam.ToInt64() >> 16) & 0xFFFF) == 0x1800) // hover-preview button clicked
            {
                int i = (int)(m.WParam.ToInt64() & 0xFFFF) - Taskbar.FirstButtonId;
                if (i >= 0 && i < Taskbar.Cmds.Length) { string c = Taskbar.Cmds[i]; BeginInvoke((MethodInvoker)delegate { RunCommand(c); }); }
                return;
            }
            else if (m.Msg == Taskbar.WM_COPYDATA)
            {
                string c = Taskbar.Receive(m);
                if (c != null) { BeginInvoke((MethodInvoker)delegate { RunCommand(c); }); m.Result = new IntPtr(1); return; }
            }
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_TEXT) StartCapture(0, false, true);
                else if (id == HK_SCROLL) StartCapture(0, false, false, true);
                else StartCapture(0, false);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            foreach (Form f in new List<Form>(EnumerateForms()))
            {
                var ed = f as EditorForm;
                if (ed != null) ed.SaveIfDirty();
            }
            Native.UnregisterHotKey(Handle, HK_PRTSC);
            Native.UnregisterHotKey(Handle, HK_CTRLSHIFTX);
            Native.UnregisterHotKey(Handle, HK_TEXT);
            Native.UnregisterHotKey(Handle, HK_SCROLL);
            base.OnFormClosing(e);
        }

        static IEnumerable<Form> EnumerateForms()
        {
            var list = new List<Form>();
            foreach (Form f in Application.OpenForms) list.Add(f);
            return list;
        }

        void OpenFolder()
        {
            Directory.CreateDirectory(CaptureDir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + CaptureDir + "\"");
        }

        public void StartCapture(int delayMs, bool full)
        {
            StartCapture(delayMs, full, false, false);
        }

        public void StartCapture(int delayMs, bool full, bool textOnly)
        {
            StartCapture(delayMs, full, textOnly, false);
        }

        public void StartCapture(int delayMs, bool full, bool textOnly, bool scroll)
        {
            if (busy) return;
            busy = true;
            var hidden = new List<Form>();
            foreach (Form f in EnumerateForms())
                if (f.Visible && !(f is RegionForm)) { hidden.Add(f); f.Hide(); }
            var t = new System.Windows.Forms.Timer();
            // Wait only as long as needed: a moment for hidden KonaSnap windows to leave the screen, or the countdown.
            t.Interval = Math.Max(hidden.Count > 0 ? 140 : 1, delayMs);
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                bool done = false;
                Action<EditorForm> finish = delegate(EditorForm editor)
                {
                    if (done) return;
                    done = true;
                    foreach (var f in hidden) if (!f.IsDisposed) f.Show();
                    if (editor != null) { editor.Show(); editor.Activate(); }
                    busy = false;
                };
                try { DoCapture(full, textOnly, scroll, finish); }
                catch (Exception ex) { MessageBox.Show("Capture failed: " + ex.Message, AppTitle); finish(null); }
            };
            t.Start();
        }

        void DoCapture(bool full, bool textOnly, bool scroll, Action<EditorForm> finish)
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            var shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb); // premultiplied: fastest format for GDI+ to draw
            using (var g = Graphics.FromImage(shot))
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);

            Rectangle sel;
            AutomationElement element = null;
            if (full)
            {
                var b = Screen.FromPoint(Cursor.Position).Bounds;
                sel = new Rectangle(b.X - vs.X, b.Y - vs.Y, b.Width, b.Height);
            }
            else
            {
                string hint = scroll
                    ? "Click the page or panel to scroll  •  Wheel: bigger / smaller  •  Esc cancels"
                    : textOnly
                        ? "Click a box to copy ALL its words (even the scrolled part)  •  Wheel: bigger / smaller  •  or drag"
                        : "Drag an area  •  Click a window  •  Wheel: panels inside it  •  Esc cancels";
                using (var rf = new RegionForm(shot, vs, Native.VisibleWindows(), hint, scroll || textOnly))
                {
                    if (rf.ShowDialog() != DialogResult.OK) { shot.Dispose(); finish(null); return; }
                    sel = rf.Selection;
                    element = rf.SelectedElement;
                }
            }
            sel.Intersect(new Rectangle(0, 0, vs.Width, vs.Height));
            if (sel.Width < 2 || sel.Height < 2) { shot.Dispose(); finish(null); return; }

            if (scroll)
            {
                shot.Dispose();
                var screenRect = new Rectangle(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height);
                RunScroll(screenRect, finish);
                return;
            }

            Bitmap img = shot.Clone(sel, PixelFormat.Format32bppArgb);
            shot.Dispose();
            if (textOnly)
            {
                var screenRect = new Rectangle(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height);
                RunTextGrab(element, screenRect, img, finish);
                return;
            }
            // Open the editor right away; the PNG is written in the background and the clipboard filled just after.
            string path = NewCapturePath();
            var forDisk = img.Clone(new Rectangle(0, 0, img.Width, img.Height), PixelFormat.Format32bppArgb);
            var forClip = img.Clone(new Rectangle(0, 0, img.Width, img.Height), PixelFormat.Format32bppArgb);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { lock (SaveLock) forDisk.Save(path, ImageFormat.Png); }
                catch (Exception ex) { Updater.Report(ex, false); }
                finally { forDisk.Dispose(); }
            });
            finish(new EditorForm(img, path));
            BeginInvoke((MethodInvoker)delegate
            {
                try { Clipboard.SetImage(forClip); } catch { }
                forClip.Dispose();
            });
        }

        // Saves to disk never overlap (background save of a new capture vs. the editor saving the same file).
        public static readonly object SaveLock = new object();

        static string NewCapturePath()
        {
            Directory.CreateDirectory(CaptureDir);
            string baseName = "Capture " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
            string path = Path.Combine(CaptureDir, baseName + ".png");
            int n = 2;
            while (File.Exists(path)) { path = Path.Combine(CaptureDir, baseName + " (" + n + ").png"); n++; }
            return path;
        }

        void RunScroll(Rectangle screenRect, Action<EditorForm> finish)
        {
            var ind = new ScrollIndicator(screenRect, S);
            ind.Show();
            var th = new Thread(delegate()
            {
                Bitmap result = null;
                string note = null, err = null;
                Thread.Sleep(250); // let the picker fade away
                try
                {
                    result = ScrollCap.Run(screenRect, delegate(string msg)
                    {
                        try { ind.BeginInvoke((MethodInvoker)delegate { ind.SetText(msg); }); } catch { }
                    }, out note);
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try { ind.Close(); } catch { }
                        if (result == null)
                        {
                            if (err != null) MessageBox.Show("Scrolling capture failed: " + err, AppTitle);
                            finish(null);
                            return;
                        }
                        string path = SaveNew(result);
                        try { Clipboard.SetImage(result); } catch { }
                        var ed = new EditorForm(result, path);
                        ed.Note((note != null ? note + "  " : "") + "Scrolling capture: " + result.Width + " x " + result.Height + ". Copied and saved.");
                        finish(ed);
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        // Text from a box: 1) ask the app for the words directly (exact, includes the scrolled-off part);
        // 2) if the app won't share them, scroll the box from the top and read the words off the screen;
        // 3) for a dragged area, read just that area.
        void RunTextGrab(AutomationElement element, Rectangle screenRect, Bitmap picture, Action<EditorForm> finish)
        {
            float s = S;
            ScrollIndicator ind = null;
            var th = new Thread(delegate()
            {
                string text = null, err = null, how = null;
                try
                {
                    if (element != null)
                    {
                        text = TextGrab.Direct(element);
                        if (!string.IsNullOrEmpty(text)) how = "Read straight from the page (exact words, including the part scrolled out of view)";
                    }
                    if (string.IsNullOrEmpty(text) && element != null && screenRect.Height >= 120 && screenRect.Width >= 120)
                    {
                        try { Invoke((MethodInvoker)delegate { ind = new ScrollIndicator(screenRect, s); ind.SetText("Reading the box..."); ind.Show(); }); } catch { }
                        Thread.Sleep(200);
                        string note;
                        Bitmap tall = ScrollCap.Run(screenRect, delegate(string msg)
                        {
                            try { if (ind != null) ind.BeginInvoke((MethodInvoker)delegate { ind.SetText(msg); }); } catch { }
                        }, out note, true);
                        if (ind != null) try { ind.BeginInvoke((MethodInvoker)delegate { ind.SetText("Reading the words..."); }); } catch { }
                        if (tall != null)
                        {
                            text = Ocr.Run(tall, out err);
                            tall.Dispose();
                            how = "Scrolled the box and read the words from the screen. Check for small reading mistakes";
                        }
                    }
                    if (string.IsNullOrEmpty(text))
                    {
                        text = Ocr.Run(picture, out err);
                        how = "Read from the picture of that area";
                    }
                }
                catch (Exception ex) { err = "Text grab failed: " + ex.Message; }
                picture.Dispose();
                string final = text ?? "";
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (ind != null) try { ind.Close(); } catch { }
                        finish(null);
                        Ocr.ShowResult(final, err, s, how);
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.MTA);
            th.Start();
        }

        static string SaveNew(Bitmap img)
        {
            string path = NewCapturePath();
            lock (SaveLock) img.Save(path, ImageFormat.Png);
            return path;
        }
    }

    // ------------------------------------------------------------------ area / window / panel picker
    class RegionForm : Form
    {
        readonly Bitmap shot;
        Bitmap dim;
        readonly Rectangle vs;
        readonly List<Rectangle> wins = new List<Rectangle>();
        readonly List<IntPtr> winHandles = new List<IntPtr>();
        readonly List<Rectangle> screens = new List<Rectangle>();
        readonly string hint;
        readonly bool smartDefault;
        Point start, mouse;
        bool dragging;
        Rectangle drag, hover;
        public Rectangle Selection;
        public AutomationElement SelectedElement;
        readonly Font labelFont = new Font("Segoe UI Semibold", 10f);
        readonly Font smallFont = new Font("Segoe UI", 8.5f);

        // Panels inside the window under the mouse, found with Windows UI Automation (client coordinates).
        List<Uia.Node> chain;
        int chainIndex = -1, userLevel = -1;
        IntPtr chainWin;
        bool querying, closed;
        Point queriedAt = new Point(-99999, -99999);
        readonly HashSet<IntPtr> retried = new HashSet<IntPtr>();
        readonly System.Windows.Forms.Timer restTimer = new System.Windows.Forms.Timer();

        const int LoupeCells = 15, LoupeZoom = 8;

        public RegionForm(Bitmap shot, Rectangle vs, List<KeyValuePair<IntPtr, Rectangle>> winsScreen, string hint, bool smartDefault)
        {
            this.shot = shot;
            this.vs = vs;
            this.hint = hint;
            this.smartDefault = smartDefault;
            foreach (var kv in winsScreen) { var l = kv.Value; l.Offset(-vs.X, -vs.Y); wins.Add(l); winHandles.Add(kv.Key); }
            foreach (var s in Screen.AllScreens) { var b = s.Bounds; b.Offset(-vs.X, -vs.Y); screens.Add(b); }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Bounds = vs;
            restTimer.Interval = 110;
            restTimer.Tick += delegate { restTimer.Stop(); QueryPanels(); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Bounds = vs;
            dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dim))
            using (var b = new SolidBrush(Color.FromArgb(110, 4, 24, 34)))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(shot, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                g.FillRectangle(b, 0, 0, dim.Width, dim.Height);
            }
            mouse = PointToClient(Cursor.Position);
            hover = WindowAt(mouse);
            restTimer.Start();
            Activate();
        }

        protected override void Dispose(bool disposing)
        {
            closed = true;
            if (disposing) { restTimer.Dispose(); if (dim != null) dim.Dispose(); labelFont.Dispose(); smallFont.Dispose(); }
            base.Dispose(disposing);
        }

        Rectangle Current { get { return dragging ? drag : hover; } }

        string CurrentKind
        {
            get
            {
                if (dragging || chain == null || chainIndex < 0) return null;
                return chainIndex == 0 ? "Window" : (chain[chainIndex].Kind ?? "Panel");
            }
        }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        int WindowIndexAt(Point p)
        {
            for (int i = 0; i < wins.Count; i++) if (wins[i].Contains(p)) return i;
            return -1;
        }

        Rectangle WindowAt(Point p)
        {
            var all = new Rectangle(0, 0, vs.Width, vs.Height);
            int i = WindowIndexAt(p);
            if (i >= 0) return Rectangle.Intersect(wins[i], all);
            foreach (var s in screens) if (s.Contains(p)) return s;
            return Rectangle.Empty;
        }

        // ---------------- panel detection (runs off the UI thread)
        void QueryPanels()
        {
            if (closed || querying || dragging) return;
            Point p = mouse;
            if (p == queriedAt) return;
            int wi = WindowIndexAt(p);
            if (wi < 0) return;
            IntPtr h = winHandles[wi];
            var clip = Rectangle.Intersect(wins[wi], new Rectangle(0, 0, vs.Width, vs.Height));
            clip.Offset(vs.X, vs.Y);
            var screenPt = new Point(p.X + vs.X, p.Y + vs.Y);
            querying = true;
            queriedAt = p;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Uia.Node> c = Uia.ChainAt(h, screenPt, clip, 2500);
                try { BeginInvoke((MethodInvoker)delegate { querying = false; ApplyChain(c, h); }); }
                catch { }
            });
        }

        void ApplyChain(List<Uia.Node> c, IntPtr h)
        {
            if (closed || dragging) return;
            foreach (var n in c) n.Rect.Offset(-vs.X, -vs.Y);
            int wi = WindowIndexAt(mouse);
            bool fresh = mouse != queriedAt;
            if (wi < 0 || winHandles[wi] != h || c.Count == 0 || !c[c.Count - 1].Rect.Contains(mouse))
            {
                if (fresh) { restTimer.Stop(); restTimer.Start(); }
                return;
            }
            // Browsers build their accessibility tree on first request; look once more a moment later.
            if (c.Count <= 3 && !retried.Contains(h))
            {
                retried.Add(h);
                queriedAt = new Point(-99999, -99999);
                restTimer.Stop(); restTimer.Interval = 700; restTimer.Start();
                restTimer.Tick += ResetInterval;
            }
            var old = Current;
            chain = c;
            chainWin = h;
            if (userLevel >= 0) chainIndex = Math.Min(userLevel, c.Count - 1);
            else chainIndex = smartDefault ? Uia.DefaultIndex(c) : 0;
            hover = chain[chainIndex].Rect;
            Changed(old, Current);
            if (fresh) { restTimer.Stop(); restTimer.Start(); }
        }

        void ResetInterval(object sender, EventArgs e)
        {
            restTimer.Interval = 110;
            restTimer.Tick -= ResetInterval;
        }

        // ---------------- painting
        // Repaints only what changed between the old and new highlight: the area that switched between
        // dim and bright, the borders and handles of both, and their size labels. Repainting both whole
        // rectangles on every mouse move is what made dragging a big area feel heavy.
        void Changed(Rectangle a, Rectangle b)
        {
            if (a == b) return;
            using (var reg = new Region(Rectangle.Empty))
            {
                bool hasA = a.Width > 0 && a.Height > 0, hasB = b.Width > 0 && b.Height > 0;
                if (hasA) reg.Union(a);
                if (hasB) reg.Xor(b);
                foreach (var r in new Rectangle[] { a, b })
                {
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    const int t = 12;
                    reg.Union(new Rectangle(r.X - t, r.Y - t, r.Width + 2 * t, 2 * t));      // top edge
                    reg.Union(new Rectangle(r.X - t, r.Bottom - t, r.Width + 2 * t, 2 * t)); // bottom edge
                    reg.Union(new Rectangle(r.X - t, r.Y - t, 2 * t, r.Height + 2 * t));     // left edge
                    reg.Union(new Rectangle(r.Right - t, r.Y - t, 2 * t, r.Height + 2 * t)); // right edge
                    reg.Union(new Rectangle(r.X - 6, r.Y - 64, 420, 128));                    // size label (above or inside)
                }
                Invalidate(reg);
            }
        }

        void Dirty(Rectangle r)
        {
            if (r.Width <= 0 && r.Height <= 0) return;
            r.Inflate(280, 70);
            Invalidate(r);
        }

        Rectangle LoupeRect(Point p)
        {
            int size = LoupeCells * LoupeZoom;
            int x = p.X + 26, y = p.Y + 26;
            var scr = Rectangle.Empty;
            foreach (var s in screens) if (s.Contains(p)) scr = s;
            if (scr.IsEmpty) scr = new Rectangle(0, 0, vs.Width, vs.Height);
            if (x + size + 6 > scr.Right) x = p.X - 26 - size;
            if (y + size + 40 > scr.Bottom) y = p.Y - 26 - size - 34;
            return new Rectangle(x, y, size, size + 30);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(dim, clip, clip, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var r = Current;
            if (r.Width > 0 && r.Height > 0)
            {
                var vis = Rectangle.Intersect(r, clip);
                if (vis.Width > 0 && vis.Height > 0)
                {
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.SmoothingMode = SmoothingMode.None;
                    g.DrawImage(shot, vis, vis, GraphicsUnit.Pixel);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                }
                using (var glow = new Pen(Color.FromArgb(70, Theme.Lagoon), 6)) g.DrawRectangle(glow, r.X - 2, r.Y - 2, r.Width + 3, r.Height + 3);
                using (var p = new Pen(Theme.Lagoon, 2)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                // corner handles
                int hs = 7;
                foreach (var c in new Point[] { new Point(r.Left, r.Top), new Point(r.Right - 1, r.Top), new Point(r.Left, r.Bottom - 1), new Point(r.Right - 1, r.Bottom - 1) })
                {
                    var hr = new Rectangle(c.X - hs / 2, c.Y - hs / 2, hs, hs);
                    g.FillRectangle(Brushes.White, hr);
                    using (var hp = new Pen(Theme.Lagoon, 1.5f)) g.DrawRectangle(hp, hr);
                }
                string label = r.Width + " × " + r.Height;
                string kind = CurrentKind;
                if (kind != null) label += "   " + kind;
                var sz = g.MeasureString(label, labelFont);
                int lx = r.X, ly = r.Y - (int)sz.Height - 14;
                if (ly < 0) ly = r.Y + 8;
                var box = new RectangleF(lx, ly, sz.Width + 16, sz.Height + 6);
                using (var pp = Theme.Round(box, box.Height / 2))
                using (var b = new LinearGradientBrush(box, Theme.Ocean, Theme.Lagoon, 0f))
                    g.FillPath(b, pp);
                g.DrawString(label, labelFont, Brushes.White, lx + 8, ly + 3);
            }

            // hint banner at the top of each screen
            foreach (var s in screens)
            {
                var sz = g.MeasureString(hint, labelFont);
                var box = new RectangleF(s.X + (s.Width - sz.Width) / 2 - 18, s.Y + 18, sz.Width + 36, sz.Height + 14);
                if (!Rectangle.Round(box).IntersectsWith(clip)) continue;
                Theme.SoftShadow(g, box, box.Height / 2, 60, 4);
                using (var pp = Theme.Round(box, box.Height / 2))
                using (var b = new LinearGradientBrush(box, Color.FromArgb(235, Theme.OceanDeep), Color.FromArgb(235, Theme.Reef), 0f))
                    g.FillPath(b, pp);
                g.DrawString(hint, labelFont, Brushes.White, box.X + 18, box.Y + 7);
            }

            DrawLoupe(g, clip);
        }

        void DrawLoupe(Graphics g, Rectangle clip)
        {
            var lr = LoupeRect(mouse);
            if (!lr.IntersectsWith(clip)) return;
            int size = LoupeCells * LoupeZoom;
            var src = new Rectangle(mouse.X - LoupeCells / 2, mouse.Y - LoupeCells / 2, LoupeCells, LoupeCells);
            var dst = new Rectangle(lr.X, lr.Y, size, size);
            Theme.SoftShadow(g, new RectangleF(lr.X, lr.Y, lr.Width, lr.Height), 10, 70, 5);
            using (var path = Theme.Round(new RectangleF(lr.X, lr.Y, lr.Width, lr.Height), 10))
            {
                using (var bg = new SolidBrush(Theme.OceanDeep)) g.FillPath(bg, path);
                var st = g.Save();
                using (var inner = Theme.Round(new RectangleF(dst.X, dst.Y, dst.Width, dst.Height), 10)) g.SetClip(inner);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.FillRectangle(Brushes.Black, dst);
                g.DrawImage(shot, dst, src, GraphicsUnit.Pixel);
                using (var gp = new Pen(Color.FromArgb(28, 255, 255, 255)))
                    for (int i = 1; i < LoupeCells; i++)
                    {
                        g.DrawLine(gp, dst.X + i * LoupeZoom, dst.Y, dst.X + i * LoupeZoom, dst.Bottom);
                        g.DrawLine(gp, dst.X, dst.Y + i * LoupeZoom, dst.Right, dst.Y + i * LoupeZoom);
                    }
                int c0 = (LoupeCells / 2) * LoupeZoom;
                using (var cp = new Pen(Theme.Lagoon, 2)) g.DrawRectangle(cp, dst.X + c0, dst.Y + c0, LoupeZoom, LoupeZoom);
                g.Restore(st);
                using (var bp = new Pen(Color.FromArgb(120, Theme.Lagoon), 1.5f)) g.DrawPath(bp, path);
            }
            Color px = Color.Black;
            if (mouse.X >= 0 && mouse.Y >= 0 && mouse.X < shot.Width && mouse.Y < shot.Height) px = shot.GetPixel(mouse.X, mouse.Y);
            var sw = new Rectangle(lr.X + 8, lr.Bottom - 22, 14, 14);
            using (var b = new SolidBrush(px)) g.FillRectangle(b, sw);
            g.DrawRectangle(Pens.White, sw);
            string info = (mouse.X + vs.X) + ", " + (mouse.Y + vs.Y) + "   #" + px.R.ToString("X2") + px.G.ToString("X2") + px.B.ToString("X2");
            g.DrawString(info, smallFont, Brushes.White, lr.X + 28, lr.Bottom - 24);
        }

        // ---------------- input
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
            if (e.Button != MouseButtons.Left) return;
            Dirty(Current);
            start = e.Location;
            dragging = true;
            drag = new Rectangle(e.Location, Size.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var oldLoupe = LoupeRect(mouse);
            mouse = e.Location;
            var old = Current;
            if (dragging) drag = Norm(start, e.Location);
            else
            {
                if (chain != null)
                {
                    int wi = WindowIndexAt(e.Location);
                    bool sameWin = wi >= 0 && winHandles[wi] == chainWin;
                    if (!sameWin || !chain[chainIndex].Rect.Contains(e.Location))
                    {
                        chain = null; chainIndex = -1;
                        if (!sameWin) userLevel = -1;
                    }
                }
                hover = chain != null ? chain[chainIndex].Rect : WindowAt(e.Location);
                restTimer.Stop();
                restTimer.Start();
            }
            if (old != Current) Changed(old, Current);
            var nl = LoupeRect(mouse);
            oldLoupe.Inflate(8, 8); nl.Inflate(8, 8);
            Invalidate(oldLoupe);
            Invalidate(nl);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (dragging || chain == null) return;
            int ni = Math.Max(0, Math.Min(chain.Count - 1, chainIndex + (e.Delta > 0 ? -1 : 1)));
            if (ni == chainIndex) return;
            var old = Current;
            chainIndex = ni;
            userLevel = ni;
            hover = chain[ni].Rect;
            Changed(old, Current);
        }

        void Choose(Rectangle r)
        {
            Selection = r;
            SelectedElement = (chain != null && chainIndex > 0 && chain[chainIndex].Rect == r) ? chain[chainIndex].El : null;
            DialogResult = DialogResult.OK;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false;
            var r = Norm(start, e.Location);
            if (r.Width < 5 && r.Height < 5)
            {
                r = chain != null && chain[chainIndex].Rect.Contains(e.Location) ? chain[chainIndex].Rect : WindowAt(e.Location);
                if (r.Width < 2 || r.Height < 2) { Invalidate(); return; }
                Choose(r);
                return;
            }
            if (r.Width < 2 || r.Height < 2) { Invalidate(); return; }
            Selection = r;
            SelectedElement = null;
            DialogResult = DialogResult.OK;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter && hover.Width > 1) Choose(hover);
            base.OnKeyDown(e);
        }
    }

    // ------------------------------------------------------------------ pixels
    static class Px
    {
        public static int[] Read(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new int[b.Width * b.Height];
                for (int y = 0; y < b.Height; y++)
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), px, y * b.Width, b.Width);
                return px;
            }
            finally { b.UnlockBits(data); }
        }

        // Rows that are one flat color (safe places to cut an image).
        public static bool[] PlainRows(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            var px = Read(b);
            var plain = new bool[h];
            for (int y = 0; y < h; y++)
            {
                int o = y * w, first = px[o];
                bool same = true;
                for (int x = 1; x < w; x++) if (px[o + x] != first) { same = false; break; }
                plain[y] = same;
            }
            return plain;
        }
    }

    // ------------------------------------------------------------------ panels inside windows (Windows UI Automation)
    static class Uia
    {
        public class Node
        {
            public Rectangle Rect;
            public AutomationElement El;
            public bool Scrollable, IsDocument;
            public string Kind;
        }

        static CacheRequest NewCache()
        {
            var cr = new CacheRequest();
            cr.Add(AutomationElement.BoundingRectangleProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(ScrollPattern.VerticallyScrollableProperty);
            return cr;
        }

        static Rectangle ToRect(System.Windows.Rect r)
        {
            if (r.IsEmpty || double.IsInfinity(r.Width) || double.IsInfinity(r.Height) || double.IsNaN(r.X)) return Rectangle.Empty;
            return Rectangle.FromLTRB((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        }

        static string Friendly(ControlType ct)
        {
            if (ct == null) return "Panel";
            if (ct == ControlType.Document) return "Page";
            if (ct == ControlType.Pane || ct == ControlType.Group || ct == ControlType.Custom) return "Panel";
            if (ct == ControlType.Edit) return "Text box";
            if (ct == ControlType.List) return "List";
            if (ct == ControlType.Table || ct == ControlType.DataGrid) return "Table";
            if (ct == ControlType.Image) return "Picture";
            if (ct == ControlType.Text) return "Text";
            try { string s = ct.LocalizedControlType; if (!string.IsNullOrEmpty(s)) return char.ToUpper(s[0]) + s.Substring(1); } catch { }
            return "Panel";
        }

        // Everything under the point, from the whole window down to the smallest piece. Screen coordinates.
        public static List<Node> ChainAt(IntPtr hwnd, Point pt, Rectangle windowRect, int budgetMs)
        {
            var chain = new List<Node>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var cr = NewCache();
                AutomationElement el;
                using (cr.Activate()) el = AutomationElement.FromHandle(hwnd);
                var root = new Node();
                root.Rect = windowRect; root.El = el; root.Kind = "Window";
                chain.Add(root);
                Rectangle cur = windowRect;
                for (int depth = 0; depth < 70 && sw.ElapsedMilliseconds < budgetMs; depth++)
                {
                    AutomationElementCollection kids;
                    using (cr.Activate()) kids = el.FindAll(TreeScope.Children, Condition.TrueCondition);
                    AutomationElement best = null;
                    Rectangle bestRect = Rectangle.Empty;
                    long bestArea = long.MaxValue;
                    foreach (AutomationElement k in kids)
                    {
                        Rectangle r;
                        try { r = ToRect(k.Cached.BoundingRectangle); } catch { continue; }
                        if (!r.Contains(pt)) continue;
                        r.Intersect(cur);
                        if (r.Width < 4 || r.Height < 4) continue;
                        long a = (long)r.Width * r.Height;
                        if (a < bestArea) { bestArea = a; best = k; bestRect = r; }
                    }
                    if (best == null) break;
                    el = best;
                    cur = bestRect;
                    var n = new Node();
                    n.Rect = bestRect;
                    n.El = best;
                    try { object v = best.GetCachedPropertyValue(ScrollPattern.VerticallyScrollableProperty, true); n.Scrollable = v is bool && (bool)v; } catch { }
                    try { var ct = best.Cached.ControlType; n.Kind = Friendly(ct); n.IsDocument = ct == ControlType.Document; } catch { n.Kind = "Panel"; }
                    if (n.Scrollable) n.Kind = "Scrolling " + n.Kind.ToLower();
                    var last = chain[chain.Count - 1];
                    if (chain.Count > 1 && last.Rect == n.Rect)
                    {
                        // Same area as the piece above it: keep one entry (the outer one) so the wheel always changes something.
                        if (n.Scrollable && !last.Scrollable) last.Kind = n.Kind;
                        last.Scrollable |= n.Scrollable;
                        last.IsDocument |= n.IsDocument;
                        continue;
                    }
                    if (chain.Count == 1 && last.Rect == n.Rect) { last.El = best; continue; }
                    chain.Add(n);
                }
            }
            catch { }
            return chain;
        }

        // The best first guess for "the panel I mean": the innermost scrolling area, else the page, else the window.
        public static int DefaultIndex(List<Node> c)
        {
            for (int i = c.Count - 1; i > 0; i--)
                if (c[i].Scrollable && c[i].Rect.Width >= 120 && c[i].Rect.Height >= 100) return i;
            for (int i = c.Count - 1; i > 0; i--)
                if (c[i].IsDocument && c[i].Rect.Width >= 120 && c[i].Rect.Height >= 100) return i;
            return 0;
        }

        // All the text inside a panel, including parts scrolled out of view.
        public static string TextOf(AutomationElement el)
        {
            string s = null;
            try { s = ViaTextPattern(el); } catch { }
            if (string.IsNullOrEmpty(s) || s.Trim().Length == 0) { try { s = ViaWalk(el); } catch { } }
            return Tidy(s);
        }

        static string ViaTextPattern(AutomationElement el)
        {
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement host = el;
            for (int up = 0; host != null && up < 30; up++)
            {
                object p;
                if (host.TryGetCurrentPattern(TextPattern.Pattern, out p))
                {
                    var tp = (TextPattern)p;
                    System.Windows.Automation.Text.TextPatternRange range = up == 0 ? tp.DocumentRange : tp.RangeFromChild(el);
                    return range.GetText(-1);
                }
                host = walker.GetParent(host);
            }
            return null;
        }

        static string ViaWalk(AutomationElement el)
        {
            var cr = new CacheRequest();
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(AutomationElement.BoundingRectangleProperty);
            AutomationElementCollection all;
            using (cr.Activate()) all = el.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var sb = new StringBuilder();
            double lastTop = double.NaN, lastH = 0;
            var recent = new List<string>();
            foreach (AutomationElement e in all)
            {
                ControlType ct;
                string name;
                System.Windows.Rect r;
                try { ct = e.Cached.ControlType; name = e.Cached.Name; r = e.Cached.BoundingRectangle; } catch { continue; }
                if (ct != ControlType.Text && ct != ControlType.RadioButton && ct != ControlType.CheckBox) continue;
                if (string.IsNullOrEmpty(name)) continue;
                name = name.Trim();
                if (name.Length == 0 || recent.Contains(name)) continue;
                recent.Add(name);
                if (recent.Count > 4) recent.RemoveAt(0);
                bool hasRect = !r.IsEmpty && !double.IsInfinity(r.Height);
                bool newLine = double.IsNaN(lastTop) || !hasRect || Math.Abs(r.Top - lastTop) > Math.Max(4.0, lastH * 0.5);
                if (sb.Length > 0) sb.Append(newLine ? "\n" : " ");
                sb.Append(name);
                if (hasRect) { lastTop = r.Top; lastH = r.Height; }
            }
            return sb.ToString();
        }

        static string Tidy(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\uFFFC", "").Replace('\u00A0', ' ');
            var lines = s.Split('\n');
            var sb = new StringBuilder();
            int blanks = 0;
            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0) { blanks++; continue; }
                if (sb.Length > 0) sb.Append(blanks > 0 ? "\n\n" : "\n");
                blanks = 0;
                sb.Append(line);
            }
            return sb.ToString().Trim();
        }
    }

    // Copies a panel's text straight from the app (exact words, even the part scrolled out of view),
    // and falls back to reading the picture when the app doesn't share its text.
    static class TextGrab
    {
        public static string Direct(AutomationElement el)
        {
            string best = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var box = new string[1];
                var th = new Thread(delegate() { try { box[0] = Uia.TextOf(el); } catch { } });
                th.IsBackground = true;
                th.Start();
                if (!th.Join(12000)) break; // the app isn't answering; fall back to reading the screen
                string t = box[0];
                if (t != null && (best == null || t.Length > best.Length)) best = t;
                if (best != null && best.Length >= 60 && attempt > 0) break;
                Thread.Sleep(700);
            }
            return best;
        }

        public static void RunAsync(Control owner, AutomationElement el, Bitmap picture, Action<string, string> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var box = new string[1];
                var th = new Thread(delegate() { try { box[0] = Uia.TextOf(el); } catch { } });
                th.IsBackground = true;
                th.Start();
                string text = th.Join(15000) ? box[0] : null;
                string err = null;
                if (string.IsNullOrEmpty(text)) text = Ocr.Run(picture, out err);
                picture.Dispose();
                string final = text ?? "";
                try { owner.BeginInvoke((MethodInvoker)delegate { done(final, err); }); } catch { }
            });
        }
    }

    // ------------------------------------------------------------------ scrolling capture
    static class ScrollCap
    {
        class Rows
        {
            public ulong[] Hash; public bool[] Info;
            public byte[] Prof; public int Cols; public bool[] Busy; // blurry per-row brightness profile, for fuzzy matching
        }
        const int MaxHeight = 30000;
        static StringBuilder log;

        static void Log(string s) { if (log != null) log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s); }

        static Bitmap Grab(Rectangle r)
        {
            var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return b;
        }

        // One fingerprint per row (ignoring the scrollbar strip on the right), plus which rows are distinctive,
        // plus a coarse brightness profile so rows still match when the browser draws text a hair differently.
        static Rows Hash(Bitmap b, int x1)
        {
            int w = b.Width, h = b.Height;
            var px = Px.Read(b);
            var rows = new Rows();
            rows.Hash = new ulong[h];
            rows.Info = new bool[h];
            int cols = Math.Max(1, Math.Min(160, x1 / 3));
            rows.Cols = cols;
            rows.Prof = new byte[h * cols];
            rows.Busy = new bool[h];
            var count = new Dictionary<ulong, int>();
            var sum = new int[cols];
            var cnt = new int[cols];
            for (int y = 0; y < h; y++)
            {
                ulong hv = 14695981039346656037UL;
                int o = y * w;
                Array.Clear(sum, 0, cols);
                Array.Clear(cnt, 0, cols);
                for (int x = 0; x < x1; x++)
                {
                    int p = px[o + x];
                    hv ^= (uint)p; hv *= 1099511628211UL;
                    int lum = (((p >> 16) & 255) * 77 + ((p >> 8) & 255) * 150 + (p & 255) * 29) >> 8;
                    int c = x * cols / x1;
                    sum[c] += lum; cnt[c]++;
                }
                rows.Hash[y] = hv;
                int mn = 255, mx = 0, po = y * cols;
                for (int c = 0; c < cols; c++)
                {
                    int v = cnt[c] > 0 ? sum[c] / cnt[c] : 0;
                    rows.Prof[po + c] = (byte)v;
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                }
                rows.Busy[y] = mx - mn > 10;
                int n;
                count.TryGetValue(hv, out n);
                count[hv] = n + 1;
            }
            int common = Math.Max(2, h / 60);
            for (int y = 0; y < h; y++) rows.Info[y] = count[rows.Hash[y]] <= common;
            return rows;
        }

        static bool Similar(Rows a, Rows b)
        {
            int h = a.Hash.Length, same = 0;
            for (int y = 0; y < h; y++) if (a.Hash[y] == b.Hash[y]) same++;
            return same >= h * 0.99;
        }

        // Waits until the picture stops changing (smooth scrolling has finished).
        static Bitmap Settled(Rectangle r, int x1, out Rows rows)
        {
            Thread.Sleep(140);
            Bitmap a = Grab(r);
            Rows ra = Hash(a, x1);
            for (int t = 0; t < 20; t++)
            {
                Thread.Sleep(90);
                Bitmap b = Grab(r);
                Rows rb = Hash(b, x1);
                bool same = Similar(ra, rb);
                a.Dispose();
                a = b; ra = rb;
                if (same) break;
            }
            rows = ra;
            return a;
        }

        // After a scroll: wait for the page to START moving (busy pages can take a moment), then for it to stop.
        static Bitmap AfterScroll(Rectangle r, int x1, Rows before, out Rows rows, out bool changed)
        {
            changed = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Bitmap cur = null;
            Rows cr = null;
            while (sw.ElapsedMilliseconds < 1600)
            {
                Thread.Sleep(60);
                if (cur != null) cur.Dispose();
                cur = Grab(r);
                cr = Hash(cur, x1);
                if (!Similar(before, cr)) { changed = true; break; }
            }
            if (!changed) { rows = cr; return cur; }
            cur.Dispose();
            return Settled(r, x1, out rows);
        }

        static void Edges(Rows a, Rows b, out int head, out int foot)
        {
            int h = a.Hash.Length, max = (int)(h * 0.35);
            head = 0;
            while (head < max && a.Hash[head] == b.Hash[head]) head++;
            foot = 0;
            while (foot < max && a.Hash[h - 1 - foot] == b.Hash[h - 1 - foot]) foot++;
        }

        // Exact match: how far the content moved up between two frames, or -1 if they can't be lined up.
        static int Offset(Rows a, Rows b, int head, int foot, int expected)
        {
            int h = a.Hash.Length, lo = head, hi = h - foot, body = hi - lo;
            if (body < 24) return -1;
            int best = -1, bestOk = 0;
            for (int d = 1; d <= body - 12; d++)
            {
                int ok = 0, bad = 0;
                for (int i = lo; i + d < hi; i++)
                {
                    int j = i + d;
                    if (!a.Info[j] && !b.Info[i]) continue;
                    if (a.Hash[j] == b.Hash[i]) ok++;
                    else { bad++; if (bad > 8 && bad * 6 > ok + bad) break; }
                }
                if (ok < 4 || bad * 8 > ok + bad) continue;
                if (ok > bestOk || (ok == bestOk && expected > 0 && Math.Abs(d - expected) < Math.Abs(best - expected)))
                {
                    bestOk = ok;
                    best = d;
                }
            }
            return best;
        }

        // Fuzzy match on the brightness profiles: tolerant of anti-aliasing and tiny rendering differences.
        static int FuzzyOffset(Rows a, Rows b, int head, int foot, int expected, out double score)
        {
            score = 999;
            int h = a.Busy.Length, lo = head, hi = h - foot, body = hi - lo, cols = a.Cols;
            if (body < 40) return -1;
            int maxD = body - 30;
            var means = new double[maxD + 1];
            double bestMean = double.MaxValue;
            int best = -1;
            for (int d = 1; d <= maxD; d++)
            {
                means[d] = double.MaxValue;
                long tot = 0;
                int n = 0;
                double cut = bestMean * 2.5 + 2;
                bool abort = false;
                for (int i = lo; i + d < hi; i += 2)
                {
                    int j = i + d;
                    if (!a.Busy[j] && !b.Busy[i]) continue;
                    int pa = j * cols, pb = i * cols, rs = 0;
                    for (int c = 0; c < cols; c++) { int df = a.Prof[pa + c] - b.Prof[pb + c]; rs += df < 0 ? -df : df; }
                    tot += rs;
                    n++;
                    if (n >= 24 && bestMean < double.MaxValue && tot / (double)(n * cols) > cut) { abort = true; break; }
                }
                if (abort || n < 12) continue;
                means[d] = tot / (double)(n * cols);
                if (means[d] < bestMean) { bestMean = means[d]; best = d; }
            }
            if (best < 0) return -1;
            // When the page repeats itself, prefer the fit nearest the distance we expect the wheel to move.
            if (expected > 0)
            {
                int near = -1;
                double nearMean = double.MaxValue;
                for (int d = Math.Max(1, expected * 3 / 4); d <= Math.Min(maxD, expected * 5 / 4); d++)
                    if (means[d] < nearMean) { nearMean = means[d]; near = d; }
                if (near > 0 && nearMean <= bestMean + 0.6) { best = near; bestMean = nearMean; }
            }
            double second = double.MaxValue;
            for (int d = 1; d <= maxD; d++) if (Math.Abs(d - best) > 4 && means[d] < second) second = means[d];
            score = bestMean;
            if (bestMean <= 3.0) return best;               // clearly a good fit
            if (bestMean > 6.0) return -1;                  // nothing lines up
            return second - bestMean >= 1.5 ? best : -1;    // so-so fit: only if it's clearly the best one
        }

        static bool Moved(Point where)
        {
            var p = Native.CursorPos();
            return Math.Abs(p.X - where.X) > 40 || Math.Abs(p.Y - where.Y) > 40;
        }

        static void SaveDebug(Bitmap a, Bitmap b)
        {
            try
            {
                string dir = Path.Combine(MainForm.CaptureDir, "debug");
                Directory.CreateDirectory(dir);
                if (a != null) a.Save(Path.Combine(dir, "scroll-before.png"), ImageFormat.Png);
                if (b != null) b.Save(Path.Combine(dir, "scroll-after.png"), ImageFormat.Png);
            }
            catch { }
        }

        static void WriteLog()
        {
            try
            {
                Directory.CreateDirectory(MainForm.CaptureDir);
                File.WriteAllText(Path.Combine(MainForm.CaptureDir, "scroll-log.txt"), log.ToString());
            }
            catch { }
        }

        public static Bitmap Run(Rectangle r, Action<string> progress, out string note)
        {
            return Run(r, progress, out note, false);
        }

        public static Bitmap Run(Rectangle r, Action<string> progress, out string note, bool fromTop)
        {
            log = new StringBuilder();
            try { return RunInner(r, progress, out note, fromTop); }
            catch (Exception ex) { Log("ERROR " + ex); throw; }
            finally { WriteLog(); }
        }

        static Bitmap RunInner(Rectangle r, Action<string> progress, out string note, bool fromTop)
        {
            note = null;
            int w = r.Width, h = r.Height;
            int x1 = Math.Max(1, w - Math.Min(w / 8, 30));
            Log("KonaSnap scrolling capture. Area " + r + "  fromTop=" + fromTop + "  screens:");
            foreach (var s in Screen.AllScreens) Log("   " + s.DeviceName + " " + s.Bounds + (s.Primary ? " primary" : ""));
            var home = Native.CursorPos();

            // Where to turn the wheel: the middle first, then other spots (the middle might be over an image
            // or a small text box that scrolls on its own).
            var spots = new Point[] {
                new Point(r.X + w / 2, r.Y + h / 2), new Point(r.X + w / 2, r.Y + h / 3), new Point(r.X + w / 3, r.Y + h / 2),
                new Point(r.X + w * 2 / 3, r.Y + h / 2), new Point(r.X + w / 2, r.Y + h * 2 / 3), new Point(r.X + 20, r.Y + h / 2) };
            var center = spots[0];
            Native.SetCursorPos(center.X, center.Y);
            Thread.Sleep(200);
            Log("Cursor set to " + center + ", reads back as " + Native.CursorPos());

            if (fromTop)
            {
                if (progress != null) progress("Going to the top of the box...");
                Rows last;
                Settled(r, x1, out last).Dispose();
                int same = 0, ups = 0;
                for (int i = 0; i < 60 && same < 2; i++)
                {
                    Native.SetCursorPos(center.X, center.Y);
                    Native.Wheel(15);
                    Rows now;
                    bool ch;
                    AfterScroll(r, x1, last, out now, out ch).Dispose();
                    if (!ch) same++; else { same = 0; ups++; }
                    last = now;
                }
                Log("Scrolled up to the top in " + ups + " steps.");
            }

            Rows firstRows;
            Bitmap first = Settled(r, x1, out firstRows);
            Bitmap prev = first;
            Rows pr = firstRows;
            var pieces = new List<Bitmap>();
            int firstKeep = h, endRow = h, total = 0, notches = 1, stalls = 0, expected = 0;
            bool started = false, userMoved = false, spotFound = false;
            int spotIndex = 0;

            try
            {
                for (int step = 0; step < 400; step++)
                {
                    if (Moved(center)) { userMoved = true; note = "Stopped because the mouse moved."; Log("Mouse moved to " + Native.CursorPos() + "; stopping."); break; }
                    Native.SetCursorPos(center.X, center.Y);
                    Native.Wheel(-notches);
                    Rows cr;
                    bool changed;
                    Bitmap cur = AfterScroll(r, x1, pr, out cr, out changed);
                    if (Moved(center)) { cur.Dispose(); userMoved = true; note = "Stopped because the mouse moved."; Log("Mouse moved; stopping."); break; }

                    if (!changed)
                    {
                        cur.Dispose();
                        if (!spotFound && spotIndex + 1 < spots.Length)
                        {
                            // Nothing moved: try turning the wheel over a different spot.
                            spotIndex++;
                            center = spots[spotIndex];
                            Log("Step " + step + ": no movement; trying wheel at " + center);
                            continue;
                        }
                        Log("Step " + step + ": no movement (stall " + (stalls + 1) + ").");
                        if (++stalls >= 2) break; // reached the bottom
                        continue;
                    }
                    stalls = 0;
                    spotFound = true;

                    int head, foot;
                    Edges(pr, cr, out head, out foot);
                    int d = Offset(pr, cr, head, foot, expected);
                    string how = "exact";
                    if (d <= 0)
                    {
                        // A sticky header or footer may have just popped in: try again ignoring the top, then the bottom, strip.
                        int head2 = Math.Max(head, h / 5);
                        d = Offset(pr, cr, head2, foot, expected);
                        if (d > 0) head = head2;
                        else
                        {
                            int foot2 = Math.Max(foot, h / 6);
                            d = Offset(pr, cr, head2, foot2, expected);
                            if (d > 0) { head = head2; foot = foot2; }
                        }
                    }
                    if (d <= 0)
                    {
                        double sc;
                        d = FuzzyOffset(pr, cr, head, foot, expected, out sc);
                        how = "fuzzy " + sc.ToString("0.00");
                        if (d <= 0)
                        {
                            int head2 = Math.Max(head, h / 5);
                            d = FuzzyOffset(pr, cr, head2, foot, expected, out sc);
                            how = "fuzzy-trimmed " + sc.ToString("0.00");
                            if (d > 0) head = head2;
                        }
                    }
                    Log("Step " + step + ": wheel " + notches + " at " + center + "; head " + head + " foot " + foot + "; moved " + d + " px (" + how + ")");
                    if (d <= 0)
                    {
                        if (notches > 1)
                        {
                            // Scrolled too far in one go: go back and take smaller steps.
                            cur.Dispose();
                            Native.SetCursorPos(center.X, center.Y);
                            Native.Wheel(notches);
                            Rows br;
                            bool ch2;
                            AfterScroll(r, x1, cr, out br, out ch2).Dispose();
                            notches = Math.Max(1, notches / 2);
                            expected = 0;
                            if (!Similar(pr, br)) { note = "Stopped: the page moved in a way I couldn't follow."; Log("Couldn't return to the last position; stopping."); break; }
                            continue;
                        }
                        SaveDebug(prev, cur);
                        cur.Dispose();
                        note = "Stopped: couldn't line up the next part of the page.";
                        Log("Couldn't line up; saved debug\\scroll-before.png and scroll-after.png.");
                        break;
                    }

                    if (!started)
                    {
                        started = true;
                        firstKeep = h - foot;
                        endRow = firstKeep;
                    }
                    int from = Math.Max(0, endRow - d), to = h - foot;
                    if (to > from)
                    {
                        pieces.Add(cur.Clone(new Rectangle(0, from, w, to - from), PixelFormat.Format32bppArgb));
                        total += to - from;
                        endRow = to;
                    }
                    else endRow = Math.Max(0, endRow - d);

                    int body = h - head - foot;
                    int oldNotches = notches;
                    if (d < body * 0.3 && notches < 12)
                        notches = Math.Max(notches + 1, Math.Min(12, (int)Math.Round(notches * body * 0.5 / Math.Max(1, d))));
                    expected = d * notches / oldNotches;

                    if (prev != first) prev.Dispose();
                    prev = cur;
                    pr = cr;
                    if (progress != null) progress("Captured " + (firstKeep + total) + " px so far...");
                    if (firstKeep + total >= MaxHeight) { note = "Stopped at the maximum length."; break; }
                }
            }
            finally
            {
                if (!userMoved) Native.SetCursorPos(home.X, home.Y);
            }

            if (!started)
            {
                if (prev != first) prev.Dispose();
                if (note == null) note = "That area didn't scroll when KonaSnap turned the mouse wheel, so this is a normal capture.";
                Log("Result: " + note);
                return first;
            }

            int tail = h - endRow;
            int outH = firstKeep + total + tail;
            Log("Result: " + w + " x " + outH + (note != null ? "  (" + note + ")" : ""));
            var result = new Bitmap(w, outH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                int y = 0;
                g.DrawImage(first, new Rectangle(0, 0, w, firstKeep), new Rectangle(0, 0, w, firstKeep), GraphicsUnit.Pixel);
                y += firstKeep;
                foreach (var p in pieces) { g.DrawImage(p, new Rectangle(0, y, w, p.Height), new Rectangle(0, 0, w, p.Height), GraphicsUnit.Pixel); y += p.Height; }
                if (tail > 0) g.DrawImage(prev, new Rectangle(0, y, w, tail), new Rectangle(0, endRow, w, tail), GraphicsUnit.Pixel);
            }
            foreach (var p in pieces) p.Dispose();
            if (prev != first) prev.Dispose();
            first.Dispose();
            return result;
        }
    }

    // Small "working on it" pill that sits beside (never on top of) the area being scrolled.
    class ScrollIndicator : Form
    {
        readonly Rectangle avoid;
        readonly float S;
        string text = "Scrolling capture...";
        readonly Font font = new Font("Segoe UI Semibold", 10f);

        public ScrollIndicator(Rectangle avoid, float scale)
        {
            this.avoid = avoid;
            S = scale;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.OceanDeep;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // no-activate, tool window
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Place(); }

        public void SetText(string s) { text = s; Place(); Invalidate(); }

        void Place()
        {
            string full = text + "   Move the mouse to stop";
            Size sz;
            using (var g = CreateGraphics()) { var m = g.MeasureString(full, font); sz = new Size((int)m.Width + (int)(44 * S), (int)m.Height + (int)(16 * S)); }
            var cands = new List<Point>();
            cands.Add(new Point(avoid.X + (avoid.Width - sz.Width) / 2, avoid.Bottom + (int)(10 * S)));
            cands.Add(new Point(avoid.X + (avoid.Width - sz.Width) / 2, avoid.Top - sz.Height - (int)(10 * S)));
            cands.Add(new Point(avoid.Right + (int)(10 * S), avoid.Top));
            cands.Add(new Point(avoid.Left - sz.Width - (int)(10 * S), avoid.Top));
            foreach (var s in Screen.AllScreens) cands.Add(new Point(s.WorkingArea.Right - sz.Width - 12, s.WorkingArea.Bottom - sz.Height - 12));
            foreach (var p in cands)
            {
                var rc = new Rectangle(p, sz);
                if (rc.IntersectsWith(avoid)) continue;
                bool onScreen = false;
                foreach (var s in Screen.AllScreens) if (s.Bounds.Contains(rc)) onScreen = true;
                if (!onScreen) continue;
                Bounds = rc;
                Opacity = 0.96;
                using (var path = Theme.Round(new RectangleF(0, 0, sz.Width, sz.Height), sz.Height / 2f)) Region = new Region(path);
                return;
            }
            Opacity = 0; // nowhere outside the area to show it
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rc = ClientRectangle;
            using (var lg = new LinearGradientBrush(rc, Theme.OceanDeep, Theme.Reef, 0f)) g.FillRectangle(lg, rc);
            float d = 10 * S;
            using (var dot = new SolidBrush(Theme.Sunset)) g.FillEllipse(dot, 16 * S, (rc.Height - d) / 2f, d, d);
            g.DrawString(text, font, Brushes.White, 32 * S, 8 * S);
            var tw = g.MeasureString(text, font).Width;
            using (var b = new SolidBrush(Color.FromArgb(200, 225, 240))) g.DrawString("   Move the mouse to stop", font, b, 32 * S + tw - 6 * S, 8 * S);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) font.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ annotations
    abstract class Ann
    {
        public Color C;
        public float W;
        public abstract void Draw(Graphics g, Bitmap baseImg);
        protected static Color Shadow { get { return Color.FromArgb(70, 0, 0, 0); } }
    }

    // Tapered arrow: thin at the tail, wide at the head, like a hand-drawn pointer.
    class ArrowAnn : Ann
    {
        public Point A, B;

        PointF[] Shape(float ox, float oy)
        {
            float dx = B.X - A.X, dy = B.Y - A.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return null;
            float ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
            float head = Math.Min(len * 0.6f, 10f + W * 4.2f);
            float hw = head * 0.62f;
            float neck = Math.Max(1.6f, W * 0.75f);
            float tail = Math.Max(0.8f, W * 0.25f);
            float bx = B.X - ux * head, by = B.Y - uy * head;
            return new PointF[] {
                new PointF(A.X + nx * tail + ox, A.Y + ny * tail + oy),
                new PointF(bx + nx * neck + ox, by + ny * neck + oy),
                new PointF(bx + nx * hw + ox, by + ny * hw + oy),
                new PointF(B.X + ox, B.Y + oy),
                new PointF(bx - nx * hw + ox, by - ny * hw + oy),
                new PointF(bx - nx * neck + ox, by - ny * neck + oy),
                new PointF(A.X - nx * tail + ox, A.Y - ny * tail + oy) };
        }

        public override void Draw(Graphics g, Bitmap baseImg)
        {
            var sh = Shape(2, 2);
            if (sh == null) return;
            using (var b = new SolidBrush(Shadow)) g.FillPolygon(b, sh);
            var pts = Shape(0, 0);
            using (var b = new SolidBrush(C)) g.FillPolygon(b, pts);
            using (var p = new Pen(Theme.Mix(C, Color.Black, 0.25f), 1f)) { p.LineJoin = LineJoin.Round; g.DrawPolygon(p, pts); }
        }
    }

    class LineAnn : Ann
    {
        public Point A, B;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            if (A == B) return;
            using (var p = new Pen(Shadow, W)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, A.X + 2, A.Y + 2, B.X + 2, B.Y + 2); }
            using (var p = new Pen(C, W)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, A, B); }
        }
    }

    class BoxAnn : Ann
    {
        public Rectangle R;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            float rad = Math.Min(6f + W, Math.Min(R.Width, R.Height) / 3f);
            var sr = new RectangleF(R.X + 2, R.Y + 2, R.Width, R.Height);
            using (var path = Theme.Round(sr, rad)) using (var p = new Pen(Shadow, W)) { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
            using (var path = Theme.Round(new RectangleF(R.X, R.Y, R.Width, R.Height), rad)) using (var p = new Pen(C, W)) { p.LineJoin = LineJoin.Round; g.DrawPath(p, path); }
        }
    }

    class EllipseAnn : Ann
    {
        public Rectangle R;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            if (R.Width < 2 || R.Height < 2) return;
            using (var p = new Pen(Shadow, W)) g.DrawEllipse(p, R.X + 2, R.Y + 2, R.Width, R.Height);
            using (var p = new Pen(C, W)) g.DrawEllipse(p, R);
        }
    }

    class HighlightAnn : Ann
    {
        public Rectangle R;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            var st = g.Save();
            g.CompositingQuality = CompositingQuality.HighQuality;
            using (var path = Theme.Round(new RectangleF(R.X, R.Y, R.Width, R.Height), Math.Min(4f, R.Height / 3f)))
            using (var b = new SolidBrush(Color.FromArgb(105, C))) g.FillPath(b, path);
            g.Restore(st);
        }
    }

    class PenAnn : Ann
    {
        public List<Point> Pts = new List<Point>();
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            if (Pts.Count < 2) return;
            var pts = Pts.ToArray();
            using (var p = new Pen(C, W))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                if (pts.Length > 3) g.DrawCurve(p, pts, 0.4f); else g.DrawLines(p, pts);
            }
        }
    }

    class TextAnn : Ann
    {
        public Point P;
        public string S;
        public static float PxFor(float w) { return 14f + w * 3.5f; }
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            using (var f = new Font("Segoe UI", PxFor(W), FontStyle.Bold, GraphicsUnit.Pixel))
            {
                var sz = g.MeasureString(S, f);
                var box = new RectangleF(P.X, P.Y, sz.Width + 18, sz.Height + 10);
                float rad = Math.Min(10f, box.Height / 2.5f);
                var shadow = box; shadow.Offset(2, 3);
                using (var sp = Theme.Round(shadow, rad)) using (var b = new SolidBrush(Color.FromArgb(55, 0, 0, 0))) g.FillPath(b, sp);
                using (var path = Theme.Round(box, rad))
                {
                    using (var b = new SolidBrush(Color.FromArgb(246, 255, 255, 255))) g.FillPath(b, path);
                    using (var p = new Pen(C, 2.5f)) g.DrawPath(p, path);
                }
                using (var b = new SolidBrush(C)) g.DrawString(S, f, b, P.X + 9, P.Y + 5);
            }
        }
    }

    class StepAnn : Ann
    {
        public Point P;
        public int N;
        public static float DiaFor(float w) { return 26f + w * 3f; }
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            float d = DiaFor(W);
            var r = new RectangleF(P.X - d / 2, P.Y - d / 2, d, d);
            using (var b = new SolidBrush(Shadow)) g.FillEllipse(b, r.X + 2, r.Y + 2, d, d);
            using (var b = new LinearGradientBrush(r, Theme.Mix(C, Color.White, 0.18f), Theme.Mix(C, Color.Black, 0.12f), 90f)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.White, 2.2f)) g.DrawEllipse(p, r);
            using (var f = new Font("Segoe UI", d * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                bool light = C.R * 0.299 + C.G * 0.587 + C.B * 0.114 > 170;
                g.DrawString(N.ToString(), f, light ? Brushes.Black : Brushes.White, r, sf);
            }
        }
    }

    class BlurAnn : Ann
    {
        public Rectangle R;
        Bitmap cache; Rectangle cacheR; Bitmap cacheOf;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            var r = Rectangle.Intersect(R, new Rectangle(0, 0, baseImg.Width, baseImg.Height));
            if (r.Width < 2 || r.Height < 2) return;
            const int block = 12;
            int sw = Math.Max(1, r.Width / block), sh = Math.Max(1, r.Height / block);
            if (cache == null || cacheR != r || cacheOf != baseImg)
            {
                if (cache != null) cache.Dispose();
                cache = new Bitmap(sw, sh);
                using (var sg = Graphics.FromImage(cache))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    sg.DrawImage(baseImg, new Rectangle(0, 0, sw, sh), r, GraphicsUnit.Pixel);
                }
                cacheR = r; cacheOf = baseImg;
            }
            var small = cache;
            {
                var st = g.Save();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(small, r, new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
                g.Restore(st);
            }
        }
    }

    // Dims everything except the chosen area so it pops.
    class SpotlightAnn : Ann
    {
        public Rectangle R;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            if (R.Width < 2 || R.Height < 2) return;
            float rad = Math.Min(14f, Math.Min(R.Width, R.Height) / 4f);
            using (var hole = Theme.Round(new RectangleF(R.X, R.Y, R.Width, R.Height), rad))
            using (var rgn = new Region(new Rectangle(-10, -10, baseImg.Width + 20, baseImg.Height + 20)))
            {
                rgn.Exclude(hole);
                using (var b = new SolidBrush(Color.FromArgb(150, 8, 20, 28))) g.FillRegion(b, rgn);
                using (var p = new Pen(Color.FromArgb(220, 255, 255, 255), 2f)) g.DrawPath(p, hole);
            }
        }
    }

    // A round zoom lens showing the area at twice the size.
    class MagnifyAnn : Ann
    {
        public Rectangle R;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            float d = Math.Max(R.Width, R.Height);
            if (d < 8) return;
            float cx = R.X + R.Width / 2f, cy = R.Y + R.Height / 2f;
            var lens = new RectangleF(cx - d / 2, cy - d / 2, d, d);
            var src = new RectangleF(cx - d / 4, cy - d / 4, d / 2, d / 2);
            using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(b, lens.X + 3, lens.Y + 4, d, d);
            var st = g.Save();
            using (var clip = new GraphicsPath()) { clip.AddEllipse(lens); g.SetClip(clip); }
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.FillEllipse(Brushes.White, lens);
            g.DrawImage(baseImg, lens, src, GraphicsUnit.Pixel);
            g.Restore(st);
            using (var p = new Pen(Color.White, Math.Max(3f, W + 2f))) g.DrawEllipse(p, lens);
            using (var p = new Pen(C, Math.Max(2f, W))) g.DrawEllipse(p, lens);
        }
    }

    class CropAnn : Ann
    {
        public Rectangle R;
        public override void Draw(Graphics g, Bitmap baseImg)
        {
            using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                var all = new Region(new Rectangle(0, 0, baseImg.Width, baseImg.Height));
                all.Exclude(R);
                g.FillRegion(b, all);
                all.Dispose();
            }
            using (var p = new Pen(Color.White, 2)) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, R); }
        }
    }

    // ------------------------------------------------------------------ editor
    class Canvas : Panel
    {
        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }

    // Dark backdrop behind the image, with a soft drop shadow under it.
    class Backdrop : Panel
    {
        public Control Target;
        public Backdrop()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var lg = new LinearGradientBrush(ClientRectangle.Width > 0 && ClientRectangle.Height > 0 ? ClientRectangle : new Rectangle(0, 0, 1, 1), Color.FromArgb(44, 54, 63), Color.FromArgb(28, 34, 41), 90f))
                g.FillRectangle(lg, ClientRectangle);
            if (Target != null && Target.Visible)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.SoftShadow(g, new RectangleF(Target.Left, Target.Top, Target.Width, Target.Height), 2, 120, 10);
            }
        }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    }

    class SnapColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.SelectFill; } }
        public override Color MenuItemBorder { get { return Theme.Lagoon; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Foam; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Foam; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Foam; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.SelectFill; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.SelectFill; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.SelectFill; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.SelectFill; } }
    }

    // Toolbar and status bar styling: soft rounded hover, lagoon-blue "selected" tool.
    class SnapRenderer : ToolStripProfessionalRenderer
    {
        public SnapRenderer() : base(new SnapColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var rc = new Rectangle(Point.Empty, e.ToolStrip.Size);
            if (e.ToolStrip is StatusStrip)
            {
                using (var lg = new LinearGradientBrush(rc, Theme.OceanDeep, Theme.Ocean, 0f)) e.Graphics.FillRectangle(lg, rc);
                return;
            }
            if (e.ToolStrip is ToolStripDropDown) { base.OnRenderToolStripBackground(e); return; }
            using (var lg = new LinearGradientBrush(rc, Color.White, Theme.Foam, 90f)) e.Graphics.FillRectangle(lg, rc);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) { base.OnRenderToolStripBorder(e); return; }
            if (e.ToolStrip is StatusStrip) return;
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }

        void Pill(Graphics g, ToolStripItem item, bool on)
        {
            bool hot = item.Selected || item.Pressed;
            if (!on && !hot) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1.5f, 2.5f, item.Width - 3, item.Height - 5);
            Color fill = on ? Theme.SelectFill : Color.FromArgb(234, 245, 250);
            if (item.Pressed) fill = Theme.Mix(fill, Theme.Lagoon, 0.15f);
            using (var path = Theme.Round(r, 7))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(on ? Theme.Lagoon : Theme.Line)) g.DrawPath(p, path);
            }
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var b = e.Item as ToolStripButton;
            Pill(e.Graphics, e.Item, b != null && b.Checked);
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        {
            Pill(e.Graphics, e.Item, false);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) { base.OnRenderSeparator(e); return; }
            int x = e.Item.Width / 2, y0 = 8, y1 = e.Item.Height - 8;
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, x, y0, x, y1);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.ToolStrip is StatusStrip) e.TextColor = Color.White;
            else if (!(e.ToolStrip is ToolStripDropDown)) e.TextColor = Theme.Ink;
            base.OnRenderItemText(e);
        }
    }

    // A grid of round color swatches for the Color drop-down.
    class SwatchGrid : Control
    {
        public static readonly Color[] Colors = new Color[] {
            Color.FromArgb(215, 38, 30), Color.FromArgb(255, 112, 67), Color.FromArgb(245, 124, 0), Color.FromArgb(255, 193, 7), Color.FromArgb(255, 230, 0), Color.FromArgb(124, 179, 66),
            Color.FromArgb(46, 160, 67), Color.FromArgb(0, 166, 150), Color.FromArgb(0, 174, 239), Color.FromArgb(30, 110, 230), Color.FromArgb(11, 79, 108), Color.FromArgb(94, 53, 177),
            Color.FromArgb(142, 68, 173), Color.FromArgb(233, 30, 140), Color.FromArgb(255, 128, 171), Color.FromArgb(121, 85, 72), Color.FromArgb(120, 130, 140), Color.Black,
            Color.White };
        public static readonly string[] Names = new string[] {
            "Red", "Kona sunset", "Orange", "Amber", "Yellow", "Lime",
            "Green", "Reef teal", "Lagoon blue", "Royal blue", "Ocean blue", "Indigo",
            "Purple", "Hibiscus pink", "Plumeria pink", "Lava brown", "Gray", "Black",
            "White" };
        const int Cols = 6;
        readonly float S;
        int hot = -1;
        public Color Selected;
        public event Action<Color> Picked;
        public event Action MoreClicked;
        readonly ToolTip tip = new ToolTip();

        public SwatchGrid(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            int rows = (Colors.Length + Cols - 1) / Cols;
            Size = new Size((int)((Cols * 34 + 16) * S), (int)((rows * 34 + 16 + 30) * S));
            Cursor = Cursors.Hand;
            BackColor = Color.White;
        }

        RectangleF Cell(int i)
        {
            float c = 34 * S;
            return new RectangleF(8 * S + (i % Cols) * c + 4 * S, 8 * S + (i / Cols) * c + 4 * S, c - 8 * S, c - 8 * S);
        }

        RectangleF MoreRect()
        {
            int rows = (Colors.Length + Cols - 1) / Cols;
            return new RectangleF(8 * S, 8 * S + rows * 34 * S + 2 * S, Width - 16 * S, 26 * S);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            for (int i = 0; i < Colors.Length; i++)
            {
                var r = Cell(i);
                if (i == hot) r.Inflate(2 * S, 2 * S);
                using (var b = new SolidBrush(Colors[i])) g.FillEllipse(b, r);
                using (var p = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawEllipse(p, r);
                if (Colors[i].ToArgb() == Selected.ToArgb())
                {
                    var o = RectangleF.Inflate(r, 3.5f * S, 3.5f * S);
                    using (var p = new Pen(Theme.Lagoon, 2f * S)) g.DrawEllipse(p, o);
                }
            }
            var m = MoreRect();
            using (var path = Theme.Round(m, 6 * S))
            {
                using (var b = new SolidBrush(hot == 999 ? Theme.SelectFill : Theme.Foam)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Line)) g.DrawPath(p, path);
            }
            using (var f = new Font("Segoe UI", 9f))
            using (var sf = new StringFormat())
            using (var b = new SolidBrush(Theme.Ocean))
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString("More colors...", f, b, m, sf);
            }
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < Colors.Length; i++) { var r = Cell(i); r.Inflate(3, 3); if (r.Contains(p)) return i; }
            if (MoreRect().Contains(p)) return 999;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hot)
            {
                hot = h;
                tip.SetToolTip(this, h >= 0 && h < Names.Length ? Names[h] : "");
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h == 999) { if (MoreClicked != null) MoreClicked(); }
            else if (h >= 0 && Picked != null) Picked(Colors[h]);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }

    class EditorForm : Form
    {
        enum Tool { Arrow, Line, Box, Ellipse, Highlight, Pen, Text, Step, Spotlight, Magnify, Blur, Crop }
        class State { public Bitmap Img; public List<Ann> Anns; }

        Bitmap img;
        string path;
        List<Ann> anns = new List<Ann>();
        readonly Stack<State> undo = new Stack<State>();
        readonly Stack<State> redo = new Stack<State>();
        Tool tool = Tool.Arrow;
        Color color = Color.FromArgb(215, 38, 30);
        float width = 4f;
        float zoom = 1f;
        bool dirty;
        Ann preview;
        bool mouseDown;
        Point downPt;
        TextBox textBox;
        Point textPos;
        bool committing;

        readonly Canvas canvas = new Canvas();
        readonly Backdrop scroller = new Backdrop();
        readonly ToolStrip bar = new ToolStrip();
        readonly StatusStrip statusStrip = new StatusStrip();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel zoomLabel = new ToolStripStatusLabel();
        readonly Dictionary<Tool, ToolStripButton> toolButtons = new Dictionary<Tool, ToolStripButton>();
        ToolStripDropDownButton colorBtn, widthBtn;
        readonly Dictionary<Tool, string> toolIcons = new Dictionary<Tool, string>();
        readonly Dictionary<Tool, Color> toolColors = new Dictionary<Tool, Color>();
        SwatchGrid swatches;
        readonly float S;

        public EditorForm(Bitmap image, string savedPath)
        {
            img = image;
            path = savedPath;
            S = MainForm.Instance != null ? MainForm.Instance.S : 1f;
            Text = "KonaSnap - " + Path.GetFileName(path);
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.White;

            foreach (Tool t in Enum.GetValues(typeof(Tool))) toolColors[t] = color;
            toolColors[Tool.Highlight] = Color.FromArgb(255, 230, 0);
            toolColors[Tool.Magnify] = Theme.Lagoon;
            BuildToolbar();

            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Text = "Copied to clipboard and saved to " + path;
            statusStrip.Items.Add(status);
            statusStrip.Items.Add(zoomLabel);
            statusStrip.Renderer = new SnapRenderer();
            statusStrip.SizingGrip = false;
            statusStrip.Padding = new Padding((int)(8 * S), 2, (int)(8 * S), 2);
            status.ForeColor = Color.White;
            zoomLabel.ForeColor = Color.White;

            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            scroller.BackColor = Theme.Slate;
            scroller.Target = canvas;
            scroller.Controls.Add(canvas);
            scroller.Resize += delegate { CenterCanvas(); };
            scroller.MouseWheel += OnWheel;

            canvas.BackColor = Color.White;
            canvas.Cursor = Cursors.Cross;
            canvas.Paint += delegate(object s, PaintEventArgs e) { PaintCanvas(e.Graphics); };
            canvas.MouseDown += OnDown;
            canvas.MouseMove += OnMove;
            canvas.MouseUp += OnUp;
            canvas.MouseWheel += OnWheel;

            Controls.Add(scroller);
            Controls.Add(bar);
            Controls.Add(statusStrip);

            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int w = Math.Min(wa.Width - 80, Math.Max((int)(1180 * S), img.Width + (int)(60 * S)));
            int h = Math.Min(wa.Height - 80, Math.Max((int)(600 * S), img.Height + (int)(160 * S)));
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
            Shown += delegate { ZoomFit(); };
            SelectTool(Tool.Arrow);
        }

        // ---------------- toolbar
        void BuildToolbar()
        {
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Padding = new Padding((int)(8 * S), (int)(4 * S), (int)(8 * S), (int)(4 * S));
            bar.ImageScalingSize = new Size((int)(22 * S), (int)(22 * S));
            bar.Font = new Font("Segoe UI", 8.5f);
            bar.Renderer = new SnapRenderer();
            bar.ShowItemToolTips = true;

            AddTool(Tool.Arrow, "Arrow", "arrow", "Arrow (A)");
            AddTool(Tool.Line, "Line", "line", "Straight line (L)");
            AddTool(Tool.Box, "Box", "box", "Rounded box (R)");
            AddTool(Tool.Ellipse, "Circle", "ellipse", "Circle / oval (E)");
            AddTool(Tool.Highlight, "Highlight", "highlight", "Highlighter (H)");
            AddTool(Tool.Pen, "Pen", "pen", "Freehand pen (P)");
            AddTool(Tool.Text, "Text", "text", "Text label (T)");
            AddTool(Tool.Step, "Step", "step", "Numbered step (N)");
            AddTool(Tool.Spotlight, "Spotlight", "spotlight", "Spotlight: dim everything else (S)");
            AddTool(Tool.Magnify, "Magnify", "magnify", "Magnifier lens (M)");
            AddTool(Tool.Blur, "Blur", "blur", "Blur / pixelate private info (B)");
            AddTool(Tool.Crop, "Crop", "crop", "Crop (C)");
            bar.Items.Add(new ToolStripSeparator());

            colorBtn = new ToolStripDropDownButton("Color");
            colorBtn.ToolTipText = "Color for the current tool";
            colorBtn.TextImageRelation = TextImageRelation.ImageAboveText;
            colorBtn.ImageScaling = ToolStripItemImageScaling.None;
            var dd = new ToolStripDropDown();
            dd.Renderer = new SnapRenderer();
            dd.Padding = Padding.Empty;
            swatches = new SwatchGrid(S);
            var host = new ToolStripControlHost(swatches);
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            host.AutoSize = false;
            host.Size = swatches.Size;
            dd.Items.Add(host);
            swatches.Picked += delegate(Color c) { dd.Close(); SetColor(c); };
            swatches.MoreClicked += delegate
            {
                dd.Close();
                using (var cd = new ColorDialog()) { cd.Color = color; cd.FullOpen = true; if (cd.ShowDialog(this) == DialogResult.OK) SetColor(cd.Color); }
            };
            colorBtn.DropDown = dd;
            bar.Items.Add(colorBtn);

            widthBtn = new ToolStripDropDownButton("Size");
            widthBtn.ToolTipText = "Line thickness and text size";
            widthBtn.TextImageRelation = TextImageRelation.ImageAboveText;
            widthBtn.ImageScaling = ToolStripItemImageScaling.None;
            widthBtn.DropDown.Renderer = new SnapRenderer();
            foreach (int v in new int[] { 2, 4, 6, 10, 14 })
            {
                int vv = v;
                string label = vv == 2 ? "Thin" : vv == 4 ? "Medium" : vv == 6 ? "Thick" : vv == 10 ? "Extra thick" : "Heavy";
                var item = new ToolStripMenuItem(label, SizeSample(vv, Theme.Ocean));
                item.ImageScaling = ToolStripItemImageScaling.None;
                item.Click += delegate { SetWidth(vv); };
                widthBtn.DropDownItems.Add(item);
            }
            bar.Items.Add(widthBtn);

            // Right side, listed right-to-left.
            AddAction("New", "new", "Take a new capture (Ctrl+N)", delegate { if (MainForm.Instance != null) MainForm.Instance.StartCapture(0, false); });
            AddSep();
            AddAction("100%", "actual", "Actual size (Ctrl+1)", delegate { SetZoom(1f); });
            AddAction("Fit", "fit", "Zoom to fit (Ctrl+0)", delegate { ZoomFit(); });
            AddSep();
            AddAction("Save as", "saveas", "Save a copy as PNG or JPG", delegate { SaveAs(); });
            AddAction("Save", "save", "Save (Ctrl+S)", delegate { Save(); });
            AddAction("Copy text", "copytext", "Read the words in this image and copy them (Ctrl+Shift+C)", delegate { CopyText(); });
            AddAction("Copy", "copy", "Copy the picture (Ctrl+C)", delegate { CopyToClipboard(); });
            AddSep();
            AddAction("Redo", "redo", "Redo (Ctrl+Y)", delegate { Redo(); });
            AddAction("Undo", "undo", "Undo (Ctrl+Z)", delegate { Undo(); });

            SetColor(color);
            SetWidth(width);
        }

        Bitmap Swatch(Color c)
        {
            int s = (int)(22 * S);
            var b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(2 * S, 2 * S, s - 4 * S - 1, s - 4 * S - 1);
                using (var br = new SolidBrush(c)) g.FillEllipse(br, r);
                using (var p = new Pen(Color.White, 2f * S)) g.DrawEllipse(p, RectangleF.Inflate(r, -1.5f * S, -1.5f * S));
                using (var p = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawEllipse(p, r);
            }
            return b;
        }

        Bitmap SizeSample(float w, Color c)
        {
            int bw = (int)(40 * S), bh = (int)(22 * S);
            var b = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(c, Math.Max(1f, w * S * 0.8f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, 6 * S, bh / 2f, bw - 6 * S, bh / 2f);
                }
            }
            return b;
        }

        void AddTool(Tool t, string text, string icon, string tip)
        {
            var b = new ToolStripButton(text, Icons.Get(icon, (int)(22 * S), Theme.Ocean));
            b.TextImageRelation = TextImageRelation.ImageAboveText;
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.ToolTipText = tip;
            b.Padding = new Padding((int)(3 * S), (int)(2 * S), (int)(3 * S), (int)(1 * S));
            b.Margin = new Padding(1, 1, 1, 2);
            b.Click += delegate { SelectTool(t); };
            toolButtons[t] = b;
            toolIcons[t] = icon;
            bar.Items.Add(b);
        }

        void AddAction(string text, string icon, string tip, EventHandler click)
        {
            var b = new ToolStripButton(text, Icons.Get(icon, (int)(22 * S), Theme.Ocean));
            b.TextImageRelation = TextImageRelation.ImageAboveText;
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.ToolTipText = tip;
            b.Padding = new Padding((int)(3 * S), (int)(2 * S), (int)(3 * S), (int)(1 * S));
            b.Margin = new Padding(1, 1, 1, 2);
            b.Alignment = ToolStripItemAlignment.Right;
            b.Click += click;
            bar.Items.Add(b);
        }

        void AddSep()
        {
            var s = new ToolStripSeparator();
            s.Alignment = ToolStripItemAlignment.Right;
            bar.Items.Add(s);
        }

        void SetColor(Color c)
        {
            color = c;
            toolColors[tool] = c;
            if (swatches != null) { swatches.Selected = c; swatches.Invalidate(); }
            var old = colorBtn.Image;
            colorBtn.Image = Swatch(c);
            if (old != null) old.Dispose();
            if (textBox != null) textBox.ForeColor = c;
            UpdateSizeImage();
        }

        void SetWidth(float w)
        {
            width = w;
            UpdateSizeImage();
        }

        void UpdateSizeImage()
        {
            if (widthBtn == null) return;
            var old = widthBtn.Image;
            widthBtn.Image = SizeSample(width, color.ToArgb() == Color.White.ToArgb() ? Theme.Muted : color);
            if (old != null) old.Dispose();
        }

        public void Note(string s) { status.Text = s; }

        void SelectTool(Tool t)
        {
            CommitText();
            if (preview is CropAnn) { preview = null; canvas.Invalidate(); }
            tool = t;
            foreach (var kv in toolButtons)
            {
                bool on = kv.Key == t;
                kv.Value.Checked = on;
                kv.Value.Image = Icons.Get(toolIcons[kv.Key], (int)(22 * S), on ? Color.FromArgb(0, 128, 190) : Theme.Ocean);
            }
            Color c;
            if (toolColors.TryGetValue(t, out c)) SetColor(c);
            canvas.Cursor = t == Tool.Text ? Cursors.IBeam : Cursors.Cross;
            string hint = "";
            switch (t)
            {
                case Tool.Arrow: hint = "Drag to draw an arrow. It points where you let go."; break;
                case Tool.Line: hint = "Drag to draw a straight line. Hold Shift to keep it level or at 45°."; break;
                case Tool.Box: hint = "Drag to draw a rounded box."; break;
                case Tool.Ellipse: hint = "Drag to draw a circle or oval. Hold Shift for a perfect circle."; break;
                case Tool.Highlight: hint = "Drag over an area to highlight it. Pick any color."; break;
                case Tool.Pen: hint = "Drag to draw freehand. Lines are smoothed for you."; break;
                case Tool.Text: hint = "Click where the label goes, type, then press Enter."; break;
                case Tool.Step: hint = "Click to drop numbered steps: 1, 2, 3..."; break;
                case Tool.Spotlight: hint = "Drag around what matters; everything else is dimmed."; break;
                case Tool.Magnify: hint = "Drag over a small detail to show it twice as big in a lens."; break;
                case Tool.Blur: hint = "Drag over anything private to pixelate it."; break;
                case Tool.Crop: hint = "Drag the area to keep."; break;
            }
            status.Text = hint;
        }

        // ---------------- zoom & layout
        void SetZoom(float z)
        {
            zoom = Math.Max(0.1f, Math.Min(8f, z));
            canvas.Size = new Size(Math.Max(1, (int)(img.Width * zoom)), Math.Max(1, (int)(img.Height * zoom)));
            zoomLabel.Text = (int)Math.Round(zoom * 100) + "%";
            CenterCanvas();
            canvas.Invalidate();
        }

        void ZoomFit()
        {
            int pad = (int)(24 * S);
            float zx = (scroller.ClientSize.Width - pad) / (float)img.Width;
            float zy = (scroller.ClientSize.Height - pad) / (float)img.Height;
            SetZoom(Math.Min(1f, Math.Min(zx, zy)));
        }

        void CenterCanvas()
        {
            int pad = (int)(12 * S);
            int x = Math.Max(pad, (scroller.ClientSize.Width - canvas.Width) / 2);
            int y = Math.Max(pad, (scroller.ClientSize.Height - canvas.Height) / 2);
            canvas.Location = new Point(x + scroller.AutoScrollPosition.X, y + scroller.AutoScrollPosition.Y);
            scroller.Invalidate();
        }

        void OnWheel(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == 0) return;
            SetZoom(e.Delta > 0 ? zoom * 1.25f : zoom / 1.25f);
            var hme = e as HandledMouseEventArgs;
            if (hme != null) hme.Handled = true;
        }

        // ---------------- painting
        // The picture as shown at the current zoom, kept ready so drawing an arrow or box only has to
        // copy pixels instead of rescaling the whole capture on every mouse move.
        Bitmap view;
        Bitmap viewOf;
        float viewZoom;

        Bitmap View()
        {
            if (view != null && viewOf == img && viewZoom == zoom) return view;
            if (view != null) { view.Dispose(); view = null; }
            int w = Math.Max(1, (int)Math.Round(img.Width * zoom)), h = Math.Max(1, (int)Math.Round(img.Height * zoom));
            if ((long)w * h > 60000000L) return null; // very deep zoom on a huge capture: draw directly instead
            try
            {
                var v = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var vg = Graphics.FromImage(v))
                {
                    vg.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    vg.PixelOffsetMode = PixelOffsetMode.Half;
                    vg.DrawImage(img, new Rectangle(0, 0, w, h), new Rectangle(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
                }
                view = v; viewOf = img; viewZoom = zoom;
                return view;
            }
            catch (OutOfMemoryException) { return null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && view != null) { view.Dispose(); view = null; }
            base.Dispose(disposing);
        }

        void PaintCanvas(Graphics g)
        {
            var v = View();
            if (v != null)
            {
                var clip = Rectangle.Intersect(Rectangle.Round(g.ClipBounds), new Rectangle(0, 0, v.Width, v.Height));
                g.CompositingMode = CompositingMode.SourceCopy;
                if (clip.Width > 0 && clip.Height > 0) g.DrawImage(v, clip, clip, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                g.ScaleTransform(zoom, zoom);
            }
            else
            {
                g.ScaleTransform(zoom, zoom);
                g.InterpolationMode = zoom < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, 0, 0, img.Width, img.Height);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var a in anns) a.Draw(g, img);
            if (preview != null) preview.Draw(g, img);
        }

        Bitmap Render()
        {
            var b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.DrawImage(img, 0, 0, img.Width, img.Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                foreach (var a in anns) a.Draw(g, img);
            }
            return b;
        }

        // ---------------- mouse
        Point ImgPt(Point p)
        {
            return new Point((int)(p.X / zoom), (int)(p.Y / zoom));
        }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        void OnDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (textBox != null) { CommitText(); return; }
            downPt = ImgPt(e.Location);
            switch (tool)
            {
                case Tool.Text:
                    BeginText(e.Location);
                    return;
                case Tool.Step:
                    {
                        int n = 1;
                        foreach (var a in anns) if (a is StepAnn) n++;
                        var s = new StepAnn();
                        s.P = downPt; s.N = n; s.C = color; s.W = width;
                        AddAnn(s);
                        return;
                    }
                case Tool.Arrow: { var a = new ArrowAnn(); a.A = downPt; a.B = downPt; preview = a; break; }
                case Tool.Line: { var a = new LineAnn(); a.A = downPt; a.B = downPt; preview = a; break; }
                case Tool.Box: preview = new BoxAnn(); break;
                case Tool.Ellipse: preview = new EllipseAnn(); break;
                case Tool.Spotlight: preview = new SpotlightAnn(); break;
                case Tool.Magnify: preview = new MagnifyAnn(); break;
                case Tool.Highlight: preview = new HighlightAnn(); break;
                case Tool.Blur: preview = new BlurAnn(); break;
                case Tool.Crop: preview = new CropAnn(); break;
                case Tool.Pen: { var p = new PenAnn(); p.Pts.Add(downPt); preview = p; break; }
            }
            if (preview != null) { preview.C = color; preview.W = width; }
            mouseDown = true;
            canvas.Capture = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!mouseDown || preview == null) return;
            var p = ImgPt(e.Location);
            p.X = Math.Max(0, Math.Min(img.Width, p.X));
            p.Y = Math.Max(0, Math.Min(img.Height, p.Y));
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (shift && (preview is LineAnn || preview is ArrowAnn))
            {
                double ang = Math.Atan2(p.Y - downPt.Y, p.X - downPt.X);
                double snap = Math.Round(ang / (Math.PI / 4)) * (Math.PI / 4);
                double len = Math.Sqrt((p.X - downPt.X) * (double)(p.X - downPt.X) + (p.Y - downPt.Y) * (double)(p.Y - downPt.Y));
                p = new Point(downPt.X + (int)Math.Round(Math.Cos(snap) * len), downPt.Y + (int)Math.Round(Math.Sin(snap) * len));
            }
            if (shift && (preview is EllipseAnn || preview is BoxAnn))
            {
                int side = Math.Max(Math.Abs(p.X - downPt.X), Math.Abs(p.Y - downPt.Y));
                p = new Point(downPt.X + (p.X >= downPt.X ? side : -side), downPt.Y + (p.Y >= downPt.Y ? side : -side));
            }
            var r = Norm(downPt, p);
            if (preview is ArrowAnn) ((ArrowAnn)preview).B = p;
            else if (preview is LineAnn) ((LineAnn)preview).B = p;
            else if (preview is EllipseAnn) ((EllipseAnn)preview).R = r;
            else if (preview is SpotlightAnn) ((SpotlightAnn)preview).R = r;
            else if (preview is MagnifyAnn) ((MagnifyAnn)preview).R = r;
            else if (preview is BoxAnn) ((BoxAnn)preview).R = r;
            else if (preview is HighlightAnn) ((HighlightAnn)preview).R = r;
            else if (preview is BlurAnn) ((BlurAnn)preview).R = r;
            else if (preview is CropAnn) ((CropAnn)preview).R = r;
            else if (preview is PenAnn) ((PenAnn)preview).Pts.Add(p);
            canvas.Invalidate();
        }

        void OnUp(object sender, MouseEventArgs e)
        {
            if (!mouseDown) return;
            mouseDown = false;
            canvas.Capture = false;
            var a = preview;
            preview = null;
            if (a == null) return;
            if (a is CropAnn) { DoCrop(((CropAnn)a).R); return; }
            bool ok = true;
            if (a is ArrowAnn) { var x = (ArrowAnn)a; ok = Math.Abs(x.A.X - x.B.X) + Math.Abs(x.A.Y - x.B.Y) > 4; }
            else if (a is LineAnn) { var x = (LineAnn)a; ok = Math.Abs(x.A.X - x.B.X) + Math.Abs(x.A.Y - x.B.Y) > 4; }
            else if (a is BoxAnn) ok = ((BoxAnn)a).R.Width > 3 && ((BoxAnn)a).R.Height > 3;
            else if (a is EllipseAnn) ok = ((EllipseAnn)a).R.Width > 3 && ((EllipseAnn)a).R.Height > 3;
            else if (a is SpotlightAnn) ok = ((SpotlightAnn)a).R.Width > 3 && ((SpotlightAnn)a).R.Height > 3;
            else if (a is MagnifyAnn) ok = Math.Max(((MagnifyAnn)a).R.Width, ((MagnifyAnn)a).R.Height) > 8;
            else if (a is HighlightAnn) ok = ((HighlightAnn)a).R.Width > 3 && ((HighlightAnn)a).R.Height > 3;
            else if (a is BlurAnn) ok = ((BlurAnn)a).R.Width > 3 && ((BlurAnn)a).R.Height > 3;
            else if (a is PenAnn) ok = ((PenAnn)a).Pts.Count > 1;
            if (ok) AddAnn(a); else canvas.Invalidate();
        }

        // ---------------- text entry
        void BeginText(Point screenPt)
        {
            textPos = ImgPt(screenPt);
            textBox = new TextBox();
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Font = new Font("Segoe UI", Math.Max(8f, TextAnn.PxFor(width) * zoom), FontStyle.Bold, GraphicsUnit.Pixel);
            textBox.ForeColor = color;
            textBox.Location = screenPt;
            textBox.Width = (int)(320 * S);
            textBox.KeyDown += delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; CommitText(); }
                else if (ke.KeyCode == Keys.Escape) { ke.SuppressKeyPress = true; CancelText(); }
            };
            textBox.LostFocus += delegate { CommitText(); };
            canvas.Controls.Add(textBox);
            textBox.Focus();
            status.Text = "Type your label, then press Enter. Esc cancels.";
        }

        void CommitText()
        {
            if (textBox == null || committing) return;
            committing = true;
            string s = textBox.Text.Trim();
            var tb = textBox;
            textBox = null;
            canvas.Controls.Remove(tb);
            tb.Dispose();
            if (s.Length > 0)
            {
                var t = new TextAnn();
                t.P = textPos; t.S = s; t.C = color; t.W = width;
                AddAnn(t);
            }
            committing = false;
            canvas.Focus();
        }

        void CancelText()
        {
            if (textBox == null) return;
            committing = true;
            var tb = textBox;
            textBox = null;
            canvas.Controls.Remove(tb);
            tb.Dispose();
            committing = false;
        }

        // ---------------- edits, undo
        State Snapshot()
        {
            var s = new State();
            s.Img = img;
            s.Anns = new List<Ann>(anns);
            return s;
        }

        void Restore(State s)
        {
            img = s.Img;
            anns = new List<Ann>(s.Anns);
            SetZoom(zoom);
        }

        void AddAnn(Ann a)
        {
            undo.Push(Snapshot());
            redo.Clear();
            anns.Add(a);
            dirty = true;
            canvas.Invalidate();
        }

        void DoCrop(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, img.Width, img.Height));
            if (r.Width < 4 || r.Height < 4) { canvas.Invalidate(); return; }
            undo.Push(Snapshot());
            redo.Clear();
            using (var flat = Render()) img = flat.Clone(r, PixelFormat.Format32bppArgb);
            anns = new List<Ann>();
            dirty = true;
            ZoomFit();
            status.Text = "Cropped to " + r.Width + " x " + r.Height + ". Undo brings it back.";
        }

        void Undo()
        {
            CommitText();
            if (undo.Count == 0) return;
            redo.Push(Snapshot());
            Restore(undo.Pop());
            dirty = true;
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Push(Snapshot());
            Restore(redo.Pop());
            dirty = true;
        }

        // ---------------- output
        void CopyToClipboard()
        {
            CommitText();
            using (var b = Render())
            {
                try { Clipboard.SetImage(b); status.Text = "Copied to clipboard. Paste it anywhere with Ctrl+V."; }
                catch (Exception ex) { status.Text = "Couldn't copy: " + ex.Message; }
            }
        }

        bool reading;
        void CopyText()
        {
            CommitText();
            if (reading) return;
            reading = true;
            status.Text = "Reading text...";
            UseWaitCursor = true;
            Ocr.RunAsync(this, Render(), delegate(string text, string err)
            {
                reading = false;
                UseWaitCursor = false;
                status.Text = text.Length > 0 ? "Text copied to the clipboard." : (err ?? "No text found.");
                Ocr.ShowResult(text, err, S);
            });
        }

        void Save()
        {
            CommitText();
            try
            {
                using (var b = Render()) lock (MainForm.SaveLock) b.Save(path, ImageFormat.Png);
                dirty = false;
                status.Text = "Saved to " + path;
            }
            catch (Exception ex) { status.Text = "Couldn't save: " + ex.Message; }
        }

        public void SaveIfDirty()
        {
            if (dirty) Save();
        }

        void SaveAs()
        {
            CommitText();
            using (var d = new SaveFileDialog())
            {
                d.Filter = "PNG image|*.png|JPEG image|*.jpg";
                d.InitialDirectory = Path.GetDirectoryName(path);
                d.FileName = Path.GetFileNameWithoutExtension(path);
                if (d.ShowDialog(this) != DialogResult.OK) return;
                using (var b = Render())
                {
                    if (d.FilterIndex == 2)
                    {
                        using (var flat = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb))
                        {
                            using (var g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImage(b, 0, 0, b.Width, b.Height); }
                            flat.Save(d.FileName, ImageFormat.Jpeg);
                        }
                    }
                    else b.Save(d.FileName, ImageFormat.Png);
                }
                status.Text = "Saved a copy to " + d.FileName;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveIfDirty();
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (textBox != null) { base.OnKeyDown(e); return; }
            bool handled = true;
            if (e.Control && e.Shift && e.KeyCode == Keys.C) CopyText();
            else if (e.Control && e.KeyCode == Keys.Z) Undo();
            else if (e.Control && e.KeyCode == Keys.Y) Redo();
            else if (e.Control && e.KeyCode == Keys.C) CopyToClipboard();
            else if (e.Control && e.KeyCode == Keys.S) Save();
            else if (e.Control && e.KeyCode == Keys.N) { if (MainForm.Instance != null) MainForm.Instance.StartCapture(0, false); }
            else if (e.Control && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0)) ZoomFit();
            else if (e.Control && (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1)) SetZoom(1f);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.A) SelectTool(Tool.Arrow);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.R) SelectTool(Tool.Box);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.L) SelectTool(Tool.Line);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.E) SelectTool(Tool.Ellipse);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.S) SelectTool(Tool.Spotlight);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.M) SelectTool(Tool.Magnify);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.H) SelectTool(Tool.Highlight);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.P) SelectTool(Tool.Pen);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.T) SelectTool(Tool.Text);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.N) SelectTool(Tool.Step);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.B) SelectTool(Tool.Blur);
            else if (!e.Control && !e.Alt && e.KeyCode == Keys.C) SelectTool(Tool.Crop);
            else if (e.KeyCode == Keys.Escape && preview != null) { preview = null; mouseDown = false; canvas.Invalidate(); }
            else handled = false;
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }
    }
}
