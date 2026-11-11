using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TinyHand
{
    /// <summary>The notification-area icon, its menu, the global shortcuts and tinyhand:// automation.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        public const string AutomationWindow = "TinyHand.Automation";

        private readonly Overlay overlay = new Overlay();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenu menu = new ContextMenu();
        private readonly MenuItem toggleItem;
        private readonly MenuItem clearItem;
        private readonly MenuItem soundItem;
        private readonly MenuItem loginItem;
        private readonly MessageWindow automation = new MessageWindow(AutomationWindow);
        private SettingsForm settings;
        private int toggleHotKey;
        private int clearHotKey;
        private Icon currentIcon;

        public TrayApp(string initialCommand)
        {
            HandAsset.PreloadThumbnails(PointerStyle.Hands.Select(s => s.AssetName), null);
            Prefs.RefreshLaunchAtLoginPath();
            RegisterProtocol();

            overlay.ActiveChanged += (s, e) =>
            {
                UpdateIcon();
                WriteState();
            };
            overlay.SoundChanged += (s, e) => WriteState();
            overlay.OpenSettings = ShowSettings;

            toggleItem = new MenuItem("", (s, e) => overlay.Toggle());
            clearItem = new MenuItem("", (s, e) => overlay.ClearInk());
            soundItem = new MenuItem("Tap Sound", (s, e) => overlay.SoundOn = !overlay.SoundOn);
            loginItem = new MenuItem("Open at Login", (s, e) => Prefs.LaunchAtLogin = !Prefs.LaunchAtLogin);
            menu.MenuItems.Add(toggleItem);
            menu.MenuItems.Add(clearItem);
            menu.MenuItems.Add(soundItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(new MenuItem("Settings…", (s, e) => ShowSettings()));
            menu.MenuItems.Add(loginItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(new MenuItem("Quit TinyHand", (s, e) => Quit()));
            menu.Popup += (s, e) =>
            {
                RefreshMenu();
                overlay.SuspendInput(true);
            };
            menu.Collapse += (s, e) => overlay.SuspendInput(false);

            tray.ContextMenu = menu;
            tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) overlay.Toggle();
            };
            UpdateIcon();
            tray.Visible = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            RegisterHotKeys();

            automation.OnMessage = (ref Message m) =>
            {
                if (m.Msg != Native.WM_COPYDATA) return false;
                Native.COPYDATASTRUCT cds = (Native.COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(Native.COPYDATASTRUCT));
                string text = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2);
                // Run it after returning, so the sender isn't kept waiting.
                BeginInvoke(() => Handle(text));
                m.Result = new IntPtr(1);
                return true;
            };

            WriteState();
            if (!Prefs.WelcomeShown)
            {
                Prefs.WelcomeShown = true;
                tray.ShowBalloonTip(8000, "TinyHand is running",
                    "Click the hand in the notification area or press " + Prefs.ToggleShortcut.Display + " to point. Right-click it for the menu.",
                    ToolTipIcon.None);
            }
            if (!string.IsNullOrEmpty(initialCommand))
            {
                BeginInvoke(() => Handle(initialCommand));
            }
        }

        private readonly Timer invoker = new Timer();
        private readonly Queue<Action> pending = new Queue<Action>();

        private void BeginInvoke(Action a)
        {
            pending.Enqueue(a);
            if (!invoker.Enabled)
            {
                invoker.Interval = 1;
                invoker.Tick -= RunPending;
                invoker.Tick += RunPending;
                invoker.Start();
            }
        }

        private void RunPending(object sender, EventArgs e)
        {
            invoker.Stop();
            while (pending.Count > 0)
            {
                pending.Dequeue()();
            }
        }

        private void RefreshMenu()
        {
            toggleItem.Text = (overlay.IsActive ? "Turn Pointer Off" : "Turn Pointer On") + "\t" + Prefs.ToggleShortcut.Display;
            clearItem.Text = "Clear Drawing\t" + Prefs.ClearShortcut.Display;
            soundItem.Checked = overlay.SoundOn;
            loginItem.Checked = Prefs.LaunchAtLogin;
        }

        // ---- Shortcuts ----

        public bool RegisterHotKeys()
        {
            if (toggleHotKey != 0 || clearHotKey != 0) return true;
            Shortcut t = Prefs.ToggleShortcut, c = Prefs.ClearShortcut;
            toggleHotKey = HotKeys.Register(t.Modifiers, t.Key, () => overlay.Toggle());
            clearHotKey = HotKeys.Register(c.Modifiers, c.Key, () => overlay.ClearInk());
            if (toggleHotKey == 0) Log.Write("toggle shortcut " + t.Display + " is taken");
            if (clearHotKey == 0) Log.Write("clear shortcut " + c.Display + " is taken");
            return toggleHotKey != 0 && clearHotKey != 0;
        }

        public void UnregisterHotKeys()
        {
            HotKeys.Unregister(toggleHotKey);
            HotKeys.Unregister(clearHotKey);
            toggleHotKey = 0;
            clearHotKey = 0;
        }

        /// <summary>Saves a new shortcut if it can be registered; otherwise keeps the old one.</summary>
        public bool ApplyShortcut(Shortcut s, bool isToggle)
        {
            UnregisterHotKeys();
            Shortcut old = isToggle ? Prefs.ToggleShortcut : Prefs.ClearShortcut;
            if (isToggle) Prefs.ToggleShortcut = s;
            else Prefs.ClearShortcut = s;
            if (RegisterHotKeys()) return true;
            UnregisterHotKeys();
            if (isToggle) Prefs.ToggleShortcut = old;
            else Prefs.ClearShortcut = old;
            RegisterHotKeys();
            return false;
        }

        // ---- Settings ----

        private void ShowSettings()
        {
            overlay.Exit();
            if (settings == null || settings.IsDisposed)
            {
                settings = new SettingsForm(this, overlay);
            }
            settings.Show();
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }

        private void Quit()
        {
            overlay.Deactivate();
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            UnregisterHotKeys();
            overlay.Dispose();
            tray.Visible = false;
            tray.Dispose();
            automation.Dispose();
            try
            {
                File.Delete(StatePath);
            }
            catch
            {
            }
            CursorHider.Restore();
            base.ExitThreadCore();
        }

        // ---- Tray icon ----

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
            {
                UpdateIcon();
            }
        }

        private static bool TaskbarIsLight()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v != 0;
            }
        }

        private void UpdateIcon()
        {
            bool on = overlay.IsActive;
            Color tint = overlay.IsClickThrough ? Color.FromArgb(255, 149, 0) : TaskbarIsLight() ? Color.Black : Color.White;
            Size size = SystemInformation.SmallIconSize;
            using (Stream s = HandAsset.OpenResource(on ? "menubar_on@3x.png" : "menubar_off@3x.png"))
            using (Bitmap src = s == null ? null : new Bitmap(s))
            using (Bitmap bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    if (src != null)
                    {
                        ColorMatrix cm = new ColorMatrix(new[]
                        {
                            new float[] { 0, 0, 0, 0, 0 },
                            new float[] { 0, 0, 0, 0, 0 },
                            new float[] { 0, 0, 0, 0, 0 },
                            new float[] { 0, 0, 0, 1, 0 },
                            new float[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, 0, 1 },
                        });
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            ia.SetColorMatrix(cm);
                            g.DrawImage(src, new Rectangle(0, 0, size.Width, size.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                        }
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                tray.Icon = icon;
                if (currentIcon != null) currentIcon.Dispose();
                currentIcon = icon;
            }
            string tip;
            if (overlay.IsClickThrough) tip = "TinyHand: clicks go through. Double-tap Shift to tap again";
            else if (on) tip = "TinyHand is on. Click to turn off, right-click for the menu";
            else tip = "TinyHand. Click to turn on, right-click for the menu";
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // ---- Automation: tinyhand:// links and state.json ----

        public static string StatePath
        {
            get
            {
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TinyHand"), "state.json");
            }
        }

        private static void RegisterProtocol()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\tinyhand"))
                {
                    k.SetValue("", "URL:TinyHand");
                    k.SetValue("URL Protocol", "");
                    using (RegistryKey cmd = k.CreateSubKey(@"shell\open\command"))
                    {
                        cmd.SetValue("", "\"" + Application.ExecutablePath + "\" \"%1\"");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("could not register tinyhand:// links: " + e.Message);
            }
        }

        public void Handle(string text)
        {
            string t = (text ?? "").Trim('\0', ' ', '\t', '\r', '\n', '"');
            if (t.StartsWith("tinyhand:", StringComparison.OrdinalIgnoreCase))
            {
                t = t.Substring("tinyhand:".Length);
            }
            t = t.TrimStart('/').TrimEnd('/');
            string[] parts = t.Split('/');
            string command = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]).ToLowerInvariant() : null;
            Log.Write("command: " + command + " " + (arg ?? ""));
            switch (command)
            {
                case "":
                case "settings":
                    ShowSettings();
                    break;
                case "on":
                    overlay.Activate();
                    break;
                case "off":
                    overlay.Deactivate();
                    break;
                case "toggle":
                    overlay.Toggle();
                    break;
                case "clear":
                    overlay.ClearInk();
                    break;
                case "style":
                    {
                        PointerStyle s = PointerStyle.Find(arg);
                        if (s == null)
                        {
                            Log.Write("unknown pointer style: " + (arg ?? "none"));
                            break;
                        }
                        overlay.Pick(s);
                        overlay.Activate();
                        break;
                    }
                case "sound":
                    if (arg == "play") overlay.PlayTapSound();
                    else if (arg == "on") overlay.SoundOn = true;
                    else if (arg == "off") overlay.SoundOn = false;
                    else overlay.SoundOn = !overlay.SoundOn;
                    break;
                case "quit":
                    Quit();
                    break;
                default:
                    Log.Write("unknown command: " + command);
                    break;
            }
        }

        private static string Json(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public void WriteState()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"active\" : ").Append(overlay.IsActive ? "true" : "false").Append(",\n");
                sb.Append("  \"clickThrough\" : ").Append(overlay.IsClickThrough ? "true" : "false").Append(",\n");
                sb.Append("  \"pid\" : ").Append(Process.GetCurrentProcess().Id).Append(",\n");
                sb.Append("  \"soundOn\" : ").Append(overlay.SoundOn ? "true" : "false").Append(",\n");
                sb.Append("  \"style\" : ").Append(Json(overlay.Style.Id)).Append(",\n");
                sb.Append("  \"styles\" : [\n");
                List<PointerStyle> all = PointerStyle.All.ToList();
                for (int i = 0; i < all.Count; i++)
                {
                    PointerStyle s = all[i];
                    sb.Append("    { \"id\" : ").Append(Json(s.Id)).Append(", \"name\" : ").Append(Json(s.Title))
                      .Append(", \"type\" : ").Append(Json(s.IsLaser ? "laser" : "hand")).Append(" }")
                      .Append(i < all.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("  ]\n}\n");
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
            }
            catch (Exception e)
            {
                Log.Write("could not write state: " + e.Message);
            }
        }
    }
}
