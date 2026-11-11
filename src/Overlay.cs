using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TinyHand
{
    /// <summary>
    /// Runs the pointer while it's on: follows the mouse, turns clicks into taps and drags into ink, hides
    /// the real cursor (except over the taskbar and menus, or while the interact key is held) and keeps
    /// the toolbar up to date.
    /// </summary>
    internal sealed class Overlay : IDisposable
    {
        private readonly MessageWindow messages = new MessageWindow("TinyHand.Input");
        private readonly InputHook hook;
        private readonly ArmView arm = new ArmView();
        private readonly Ink ink = new Ink();
        private readonly Toolbar toolbar = new Toolbar();
        private readonly TapSound tapSound = new TapSound();
        private readonly Timer timer = new Timer();

        private bool active;
        private Rectangle currentBounds = Rectangle.Empty;
        private Screenish currentScreen;
        private PointF? downPoint;
        private bool normalCursor;
        private bool ownMenuOpen;
        private bool interactHeld;
        private DateTime revealUntil = DateTime.MinValue;
        private DateTime hintUntil = DateTime.MaxValue;
        private DateTime lastTick = DateTime.UtcNow;
        private DateTime stillSince = DateTime.UtcNow;
        private DateTime lastButtonEvent = DateTime.MinValue;
        private DateTime lastMenuScan = DateTime.MinValue;
        private DateTime lastTaskbarScan = DateTime.MinValue;
        private DateTime lastTopmost = DateTime.MinValue;
        private DateTime menuOpeningUntil = DateTime.MinValue;
        private Point lastMouse;
        private List<Rectangle> menuRects = new List<Rectangle>();
        private List<Rectangle> taskbarRects = new List<Rectangle>();
        private int escHotKey;
        private bool fastPace;

        // Cached preferences (the registry isn't read on every tick).
        private int interactVk;
        private bool interactKeepsHand;
        private bool showToolbar;

        private const int BusyInterval = 8;
        private const int IdleInterval = 50;

        public event EventHandler ActiveChanged;
        public event EventHandler SoundChanged;
        public Action OpenSettings;

        public Overlay()
        {
            hook = new InputHook(messages.Handle);
            messages.OnMessage = OnHookMessage;
            timer.Interval = BusyInterval;
            timer.Tick += (s, e) => Tick();

            toolbar.OnPick = Pick;
            toolbar.OnToggleSound = () => SoundOn = !SoundOn;
            toolbar.OnExit = Exit;
            toolbar.OnSettings = () =>
            {
                Exit();
                if (OpenSettings != null) OpenSettings();
            };

            ReadPrefs();
            arm.SetStyle(Prefs.Style);
            UpdateInkColor();
            toolbar.Select(arm.Style);
            SyncSound();

            Prefs.Changed += OnPrefsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        public bool IsActive { get { return active; } }
        public bool IsClickThrough { get { return active && hook.ClickThrough; } }
        public PointerStyle Style { get { return arm.Style; } }

        public bool SoundOn
        {
            get { return arm.Style.IsLaser ? Prefs.LaserSound : Prefs.HandSound; }
            set
            {
                if (arm.Style.IsLaser) Prefs.LaserSound = value;
                else Prefs.HandSound = value;
            }
        }

        private void ReadPrefs()
        {
            InteractKey key = Prefs.Interact;
            interactVk = key == InteractKey.Alt ? Native.VK_MENU : key == InteractKey.Control ? Native.VK_CONTROL : 0;
            interactKeepsHand = Prefs.InteractKeepsHand;
            showToolbar = Prefs.ShowToolbar;
            hook.InteractVk = interactVk;
            hook.DetectDoubleShift = Prefs.DoubleShiftClickThrough;
        }

        private void OnPrefsChanged(object sender, EventArgs e)
        {
            ReadPrefs();
            if (!Prefs.DoubleShiftClickThrough && IsClickThrough)
            {
                SetClickThrough(false);
            }
            arm.ReadPrefs();
            tapSound.Load(Prefs.TapSound);
            PointerStyle saved = Prefs.Style;
            if (saved != arm.Style)
            {
                Pick(saved);
            }
            UpdateInkColor();
            SyncSound();
            toolbar.Relayout();
        }

        private void UpdateInkColor()
        {
            ink.Color = InkChoices.Resolve(Prefs.InkChoice, arm.Style);
        }

        public void Pick(PointerStyle style)
        {
            arm.SetStyle(style);
            Prefs.Style = arm.Style;
            toolbar.Select(arm.Style);
            UpdateInkColor();
            SyncSound();
        }

        /// <summary>Plays the current tap sound once, as a tap would.</summary>
        public void PlayTapSound()
        {
            tapSound.Play();
        }

        private void SyncSound()
        {
            bool on = SoundOn;
            tapSound.Enabled = on;
            toolbar.SetSoundOn(on);
            if (SoundChanged != null) SoundChanged(this, EventArgs.Empty);
        }

        public void Toggle()
        {
            if (active) Deactivate();
            else Activate();
        }

        public void Exit()
        {
            ClearInk();
            Deactivate();
        }

        public void ClearInk()
        {
            ink.Clear();
        }

        public bool Activate()
        {
            if (active) return true;
            active = true;
            currentBounds = Rectangle.Empty;
            tapSound.Warm();
            Native.timeBeginPeriod(1);

            CursorHider.Hide();
            normalCursor = false;
            arm.PointerHidden = false;
            bool installed = hook.Install();
            hook.ClickThrough = false;
            hook.Active = installed;
            lastTick = DateTime.UtcNow;
            stillSince = DateTime.UtcNow;
            Tick();

            revealUntil = DateTime.UtcNow.AddSeconds(3);
            if (!Prefs.HintShown)
            {
                Prefs.HintShown = true;
                List<KeyValuePair<string, string>> parts = new List<KeyValuePair<string, string>>();
                parts.Add(Pair("Click", "to tap"));
                parts.Add(Pair("Drag", "to draw"));
                if (Prefs.Interact != InteractKey.Off) parts.Add(Pair("Hold " + Prefs.InteractName, "to click for real"));
                if (Prefs.DoubleShiftClickThrough) parts.Add(Pair("Double-tap Shift", "to click through"));
                parts.Add(Pair("Esc", "to exit"));
                toolbar.ShowHint(parts);
                revealUntil = DateTime.UtcNow.AddSeconds(10);
                hintUntil = revealUntil;
            }
            SetFast(true);
            timer.Start();
            SetEscEnabled(true);
            Log.Write("activate hook=" + installed);
            if (ActiveChanged != null) ActiveChanged(this, EventArgs.Empty);
            return installed;
        }

        private static KeyValuePair<string, string> Pair(string k, string v)
        {
            return new KeyValuePair<string, string>(k, v);
        }

        public void Deactivate()
        {
            if (!active) return;
            active = false;
            timer.Stop();
            hook.Active = false;
            hook.ClickThrough = false;
            hook.Uninstall();
            Native.timeEndPeriod(1);
            arm.Held = false;
            downPoint = null;
            ink.Clear();
            arm.Hide();
            toolbar.Hide();
            toolbar.HideHint();
            normalCursor = false;
            arm.PointerHidden = false;
            SetEscEnabled(false);
            ownMenuOpen = false;
            CursorHider.Show();
            Log.Write("deactivate");
            if (ActiveChanged != null) ActiveChanged(this, EventArgs.Empty);
        }

        /// <summary>While our own tray menu is open, clicks go to it and the real cursor shows.</summary>
        public void SuspendInput(bool suspended)
        {
            if (!active) return;
            ownMenuOpen = suspended;
            hook.Active = !suspended;
            Tick();
        }

        private void SetClickThrough(bool on)
        {
            if (!active) return;
            hook.ClickThrough = on;
            Log.Write(on ? "click-through on" : "click-through off");
            if (on) Release();
            List<KeyValuePair<string, string>> parts = new List<KeyValuePair<string, string>>();
            if (on)
            {
                parts.Add(Pair("Clicks go through", "the hand stays"));
                parts.Add(Pair("Double-tap Shift", "to tap again"));
            }
            else
            {
                parts.Add(Pair("Taps", "clicks don't reach apps"));
                parts.Add(Pair("Double-tap Shift", "to click through"));
            }
            toolbar.ShowHint(parts);
            hintUntil = DateTime.UtcNow.AddSeconds(1.5);
            revealUntil = hintUntil;
            Wake();
            if (ActiveChanged != null) ActiveChanged(this, EventArgs.Empty);
        }

        private bool OnHookMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case InputHook.MsgMove:
                    hook.MoveHandled();
                    if (!fastPace || (DateTime.UtcNow - lastTick).TotalMilliseconds >= 4)
                    {
                        Wake();
                    }
                    return true;
                case InputHook.MsgDown:
                    if (!active) return true;
                    arm.Press();
                    arm.Held = true;
                    tapSound.Play();
                    toolbar.HideHint();
                    toolbar.CloseInkTray();
                    downPoint = LocalMouse();
                    NoteButtonEvent();
                    return true;
                case InputHook.MsgUp:
                    Release();
                    NoteButtonEvent();
                    return true;
                case InputHook.MsgInteractDown:
                    arm.Press();
                    arm.Held = true;
                    NoteButtonEvent();
                    return true;
                case InputHook.MsgInteractUp:
                    arm.Held = false;
                    NoteButtonEvent();
                    return true;
                case InputHook.MsgPassThroughDown:
                    NoteButtonEvent();
                    menuOpeningUntil = DateTime.UtcNow.AddSeconds(1);
                    return true;
                case InputHook.MsgDoubleShift:
                    if (active && Prefs.DoubleShiftClickThrough) SetClickThrough(!hook.ClickThrough);
                    return true;
            }
            return false;
        }

        private void NoteButtonEvent()
        {
            lastButtonEvent = DateTime.UtcNow;
            Wake();
        }

        private void Wake()
        {
            stillSince = DateTime.UtcNow;
            if (!active) return;
            SetFast(true);
            Tick();
        }

        private void SetFast(bool fast)
        {
            if (fast == fastPace) return;
            fastPace = fast;
            timer.Interval = fast ? BusyInterval : IdleInterval;
        }

        private void Release()
        {
            arm.Held = false;
            downPoint = null;
            ink.End();
        }

        private PointF? LocalMouse()
        {
            if (currentBounds.IsEmpty) return null;
            Point c = Native.CursorPos();
            return new PointF(c.X - currentBounds.X, c.Y - currentBounds.Y);
        }

        private void Drag(PointF p)
        {
            if (!downPoint.HasValue) return;
            PointF start = downPoint.Value;
            if (!ink.IsDrawing)
            {
                float dx = p.X - start.X, dy = p.Y - start.Y;
                if (dx * dx + dy * dy < 16) return;
                ink.Begin(start, arm.InkWidth);
            }
            ink.Arrow = Native.KeyDown(Native.VK_CONTROL);
            ink.Extend(p);
        }

        private void SetEscEnabled(bool enabled)
        {
            if (enabled && active && escHotKey == 0)
            {
                escHotKey = HotKeys.Register(0, Keys.Escape, Exit);
            }
            if (!enabled && escHotKey != 0)
            {
                HotKeys.Unregister(escHotKey);
                escHotKey = 0;
            }
        }

        private void Tick()
        {
            if (!active) return;
            DateTime now = DateTime.UtcNow;
            float dt = (float)(now - lastTick).TotalSeconds;
            lastTick = now;

            Point mouse = Native.CursorPos();
            Screenish mon = Native.MonitorAt(mouse);
            if (mon.Bounds != currentBounds)
            {
                bool moving = !currentBounds.IsEmpty;
                currentBounds = mon.Bounds;
                currentScreen = mon;
                if (moving)
                {
                    Release();
                    revealUntil = now.AddSeconds(1.5);
                }
                ink.SetScreen(mon.Bounds);
                arm.SetScreen(mon.Bounds);
                toolbar.Place(mon);
                lastTopmost = DateTime.MinValue;
            }
            if (mouse != lastMouse)
            {
                lastMouse = mouse;
                stillSince = now;
            }
            if (now > hintUntil)
            {
                hintUntil = DateTime.MaxValue;
                toolbar.HideHint();
            }

            UpdateZones(mouse, mon, now);

            if ((now - lastTopmost).TotalSeconds > 0.5)
            {
                lastTopmost = now;
                ink.BringToTop();
                toolbar.BringToTop();
                arm.BringToTop();
            }

            PointF local = new PointF(mouse.X - currentBounds.X, mouse.Y - currentBounds.Y);
            arm.Aim(local, dt);
            if (downPoint.HasValue)
            {
                Drag(local);
            }
            ink.Render();
            toolbar.Tick(mouse);

            bool still = (now - stillSince).TotalSeconds > 0.5;
            bool busy = arm.Held || arm.IsSettling || ink.IsDrawing || ink.IsAnimating || interactHeld
                || (now - lastButtonEvent).TotalSeconds < 1 || now < revealUntil;
            SetFast(!still || busy);
        }

        private void UpdateZones(Point mouse, Screenish mon, DateTime now)
        {
            Rectangle f = toolbar.Frame;
            Rectangle near = Rectangle.Union(Rectangle.Inflate(f, (int)(80 * mon.Scale), 0), new Rectangle(f.X, f.Y - (int)(90 * mon.Scale), f.Width, f.Height));
            near.Inflate(0, (int)(10 * mon.Scale));
            bool drawing = ink.IsDrawing;
            toolbar.SetRevealed(showToolbar && !drawing && (near.Contains(mouse) || now < revealUntil));

            if ((now - lastTaskbarScan).TotalSeconds > 1)
            {
                lastTaskbarScan = now;
                taskbarRects = ScanTaskbars();
            }
            bool menuLikely = menuRects.Count > 0 || ownMenuOpen || now < menuOpeningUntil
                || (now - lastButtonEvent).TotalSeconds < 2 || taskbarRects.Exists(r => Rectangle.Inflate(r, 0, 40).Contains(mouse));
            double scanEvery = menuLikely ? 0.05 : 0.25;
            if ((now - lastMenuScan).TotalSeconds > scanEvery)
            {
                lastMenuScan = now;
                menuRects = ScanMenus();
            }

            bool overMenu = taskbarRects.Exists(r => r.Contains(mouse)) || menuRects.Exists(r => r.Contains(mouse)) || now < menuOpeningUntil;
            bool interacting = (interactVk != 0 && Native.KeyDown(interactVk)) || hook.InteractPressing;
            interactHeld = interacting;
            bool through = hook.ClickThrough;
            bool normal = !drawing && !interactKeepsHand && ((interacting && !through) || overMenu || ownMenuOpen);
            SetEscEnabled(menuRects.Count == 0 && !ownMenuOpen && !through);

            if (normal != normalCursor)
            {
                normalCursor = normal;
                arm.PointerHidden = normal;
                if (normal) CursorHider.Show();
                else CursorHider.Hide();
            }

            List<Rectangle> rects = new List<Rectangle>(taskbarRects);
            rects.AddRange(menuRects);
            if (toolbar.Revealed) rects.Add(toolbar.Frame);
            hook.PassThrough = rects.ToArray();
        }

        private static readonly string[] FlyoutClasses =
        {
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
            "NotifyIconOverflowWindow", "Shell_InputSwitchTopLevelWindow",
        };

        /// <summary>Open popup menus (#32768) and shell flyouts like Start or the tray overflow.</summary>
        private static List<Rectangle> ScanMenus()
        {
            List<Rectangle> list = new List<Rectangle>();
            IntPtr w = IntPtr.Zero;
            for (int guard = 0; guard < 32; guard++)
            {
                w = Native.FindWindowEx(IntPtr.Zero, w, "#32768", null);
                if (w == IntPtr.Zero) break;
                if (Native.IsWindowVisible(w)) list.Add(Native.WindowRect(w));
            }
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && Array.IndexOf(FlyoutClasses, Native.ClassOf(fg)) >= 0)
            {
                list.Add(Native.WindowRect(fg));
            }
            return list;
        }

        private static List<Rectangle> ScanTaskbars()
        {
            List<Rectangle> list = new List<Rectangle>();
            foreach (string cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr w = IntPtr.Zero;
                for (int guard = 0; guard < 16; guard++)
                {
                    w = Native.FindWindowEx(IntPtr.Zero, w, cls, null);
                    if (w == IntPtr.Zero) break;
                    if (Native.IsWindowVisible(w)) list.Add(Native.WindowRect(w));
                }
            }
            return list;
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect
                || e.Reason == SessionSwitchReason.RemoteDisconnect || e.Reason == SessionSwitchReason.SessionLogoff)
            {
                TurnOffForSystem(e.Reason.ToString());
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                TurnOffForSystem("suspend");
            }
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!active) return;
            currentBounds = Rectangle.Empty;
            Tick();
        }

        private void TurnOffForSystem(string reason)
        {
            if (!active) return;
            Log.Write("turning pointer off: " + reason);
            Deactivate();
        }

        public void Dispose()
        {
            Deactivate();
            Prefs.Changed -= OnPrefsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            timer.Dispose();
            arm.Dispose();
            ink.Dispose();
            toolbar.Dispose();
            messages.Dispose();
        }
    }
}
