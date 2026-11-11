using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TinyHand
{
    /// <summary>Settings, laid out like the macOS app: a sidebar of sections and cards of rows.</summary>
    internal sealed class SettingsForm : Form
    {
        public enum Section { General, Pointers, Appearance, Drawing, Sound, About }

        private static readonly string[] SectionTitles = { "General", "Pointers", "Appearance", "Drawing", "Sound", "About" };
        private static readonly string[] SectionGlyphs = { "", "", "", "", "", "" };
        private static readonly Color[] SectionTints =
        {
            Color.FromArgb(140, 140, 140), Color.FromArgb(140, 107, 242), Color.FromArgb(242, 115, 153),
            Color.FromArgb(245, 148, 51), Color.FromArgb(51, 140, 250), Color.FromArgb(115, 115, 115),
        };

        private const float WinW = 780, MinH = 560, SideW = 210, Pad = 28;
        private static float CX { get { return SideW + Pad; } }
        private static float CW { get { return WinW - SideW - 2 * Pad; } }

        private readonly TrayApp app;
        private readonly Overlay overlay;
        private readonly Surface surface = new Surface();
        private readonly Timer thumbTimer = new Timer();
        private readonly TapSound preview = new TapSound();
        private Theme T;
        private Section section = Section.General;
        private int recording; // 0 = not recording, 1 = toggle shortcut, 2 = clear shortcut
        private string shortcutNote;
        private string pinMessage;
        private ToolbarRow toolbarRow;
        private ToolStripDropDown picker;
        private float contentHeight = MinH;

        private static float scale;

        /// <summary>Design pixels (96 DPI) to device pixels.</summary>
        internal static int Px(float v)
        {
            if (scale == 0)
            {
                // The monitor the window opens on (the one with the mouse), not the system DPI.
                scale = Native.MonitorAt(Native.CursorPos()).Scale;
            }
            return (int)Math.Round(v * scale);
        }

        public SettingsForm(TrayApp app, Overlay overlay)
        {
            this.app = app;
            this.overlay = overlay;
            Px(1);
            Text = "TinyHand Settings";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = false;
            using (Stream s = HandAsset.OpenResource("app.ico"))
            {
                if (s != null) Icon = new Icon(s);
            }
            surface.S = scale;
            surface.Dock = DockStyle.Fill;
            surface.BackgroundDown = StopRecording;
            Controls.Add(surface);
            toolbarRow = new ToolbarRow(this);

            T = Theme.Current();
            BackColor = T.Bg;
            Rebuild();

            Prefs.Changed += OnPrefsChanged;
            SystemEvents.UserPreferenceChanged += OnThemeChanged;
            FormClosed += (s, e) =>
            {
                Prefs.Changed -= OnPrefsChanged;
                SystemEvents.UserPreferenceChanged -= OnThemeChanged;
                thumbTimer.Dispose();
                StopRecording();
            };
            thumbTimer.Interval = 300;
            thumbTimer.Tick += (s, e) =>
            {
                if (PointerStyle.Hands.All(h => HandAsset.CachedThumbnail(h.AssetName) != null))
                {
                    thumbTimer.Stop();
                }
                surface.Invalidate();
            };
            thumbTimer.Start();
        }

        public void ShowSection(Section s)
        {
            section = s;
            StopRecording();
            Rebuild();
        }

        // ---- Window chrome ----

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyChrome();
            FollowDpi();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FollowDpi();
        }

        /// <summary>Draws at the DPI of the monitor the window is on.</summary>
        private void FollowDpi()
        {
            float s = scale;
            try
            {
                uint dpi = GetDpiForWindow(Handle);
                if (dpi > 0) s = dpi / 96f;
            }
            catch (EntryPointNotFoundException)
            {
            }
            Size want = new Size((int)Math.Round(WinW * s), (int)Math.Round(contentHeight * s));
            if (s == scale && surface.S == s && ClientSize == want) return;
            scale = s;
            surface.S = s;
            Rebuild();
        }

        private void ApplyChrome()
        {
            if (!IsHandleCreated) return;
            try
            {
                int dark = T.Dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
                int caption = T.Bg.R | (T.Bg.G << 8) | (T.Bg.B << 16);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR (Windows 11)
            }
            catch
            {
            }
        }

        protected override void WndProc(ref Message m)
        {
            // While recording a shortcut, Alt must not open the window menu.
            if (recording != 0 && m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF100) return;
            base.WndProc(ref m);
            if (m.Msg == 0x02E0) // WM_DPICHANGED: moved to a monitor with another scale
            {
                BeginInvoke(new Action(FollowDpi));
            }
        }

        private void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Theme next = Theme.Current();
            if (next.Dark == T.Dark) return;
            T = next;
            BackColor = T.Bg;
            ApplyChrome();
            Rebuild();
        }

        private void OnPrefsChanged(object sender, EventArgs e)
        {
            if (!IsDisposed && !surface.Dragging) Rebuild();
        }

        // ---- Building ----

        private Widget Add(RectangleF r, Action<Graphics, Widget> paint, Action click = null, string tip = null)
        {
            Widget w = new Widget();
            w.R = r;
            w.Paint = paint;
            w.Click = click;
            w.Clickable = click != null;
            w.Tip = tip;
            return surface.Add(w);
        }

        private void Label(float x, float y, float w, string text, Font f, Color c, bool wrap = false)
        {
            float h = wrap ? Ui.Measure(text, f, w).Height + 2 : f.Height + 2;
            Add(new RectangleF(x, y, w, h), (g, _) => Ui.Text(g, text, f, c, new RectangleF(x, y, w, h), StringAlignment.Near, StringAlignment.Near, wrap));
        }

        private void Rebuild()
        {
            surface.T = T;
            surface.Widgets.Clear();
            surface.KeyHandler = recording != 0 ? (Func<Keys, bool>)HandleKey : null;

            // Sidebar.
            Add(new RectangleF(0, 0, SideW, 4000), (g, w) =>
            {
                using (SolidBrush b = new SolidBrush(T.Sidebar)) g.FillRectangle(b, 0, 0, SideW, surface.Height / surface.S + 1);
            });
            float ny = 16;
            for (int i = 0; i < SectionTitles.Length; i++)
            {
                int idx = i;
                RectangleF r = new RectangleF(8, ny, SideW - 16, 32);
                Add(r, (g, w) =>
                {
                    if ((int)section == idx) Ui.Fill(g, r, 7, T.Selected);
                    else if (w.Hot) Ui.Fill(g, r, 7, Ui.A(T.Selected, 110));
                    RectangleF icon = new RectangleF(r.X + 8, r.Y + 5, 22, 22);
                    Ui.Fill(g, icon, 6, SectionTints[idx]);
                    if (idx == (int)Section.Pointers) Ui.HandGlyph(g, RectangleF.Inflate(icon, -2, -2), Color.White);
                    else Ui.Text(g, SectionGlyphs[idx], Ui.Icon(12), Color.White, icon, StringAlignment.Center, StringAlignment.Center);
                    Ui.Text(g, SectionTitles[idx], Ui.F(13), T.Text, new RectangleF(r.X + 40, r.Y, r.Width - 40, r.Height));
                }, () => ShowSection((Section)idx));
                ny += 36;
            }
            Add(new RectangleF(0, 0, SideW, 30), (g, w) =>
            {
                float y = surface.Height / surface.S - 38;
                Bitmap icon = AppIcon(20);
                if (icon != null) g.DrawImage(icon, new RectangleF(18, y + 5, 20, 20));
                Ui.Text(g, "TinyHand", Ui.F(12, true), T.Text, new RectangleF(46, y, 140, 30));
            });

            // Page.
            Label(CX, 22, CW, section == Section.Pointers ? "Pointers" : SectionTitles[(int)section], Ui.F(22, false, true), T.Text);
            float y0 = 74;
            float end;
            switch (section)
            {
                case Section.Pointers: end = BuildPointers(y0); break;
                case Section.Appearance: end = BuildAppearance(y0); break;
                case Section.Drawing: end = BuildDrawing(y0); break;
                case Section.Sound: end = BuildSound(y0); break;
                case Section.About: end = BuildAbout(y0); break;
                default: end = BuildGeneral(y0); break;
            }
            contentHeight = Math.Max(MinH, end + 24);
            Size want = new Size(Px(WinW), Px(contentHeight));
            if (ClientSize != want) ClientSize = want;
            surface.Rebuilt();
        }

        private static Bitmap appIcon;

        private static Bitmap AppIcon(int size)
        {
            if (appIcon == null)
            {
                using (Stream s = HandAsset.OpenResource("mark-512.png"))
                {
                    if (s == null) return null;
                    using (Bitmap raw = new Bitmap(s))
                    {
                        appIcon = new Bitmap(144, 144);
                        using (Graphics g = Graphics.FromImage(appIcon))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(raw, 0, 0, 144, 144);
                        }
                    }
                }
            }
            return appIcon;
        }

        // ---- Cards and rows ----

        private Widget cardBg;
        private float cardTop;
        private int cardRows;

        private float BeginCard(string title, float y)
        {
            if (title != null)
            {
                Label(CX + 4, y, CW, title, Ui.F(12, true), T.Secondary);
                y += 22;
            }
            cardTop = y;
            cardRows = 0;
            cardBg = Add(RectangleF.Empty, (g, w) => Ui.Fill(g, w.R, 12, T.Card));
            if (!T.Dark) cardBg.Paint = (g, w) =>
            {
                Ui.Fill(g, w.R, 12, T.Card);
                Ui.Stroke(g, w.R, 12, T.Separator, 1);
            };
            return y;
        }

        private float EndCard(float y)
        {
            cardBg.R = new RectangleF(CX, cardTop, CW, y - cardTop);
            return y + 24;
        }

        private float Row(float y, string title, string detail, Widget trailing, bool dim = false)
        {
            if (cardRows > 0)
            {
                float sy = y;
                Add(new RectangleF(CX + 14, sy, CW - 14, 1), (g, w) =>
                {
                    using (SolidBrush b = new SolidBrush(T.Separator)) g.FillRectangle(b, CX + 14, sy, CW - 14, 1);
                });
            }
            cardRows++;
            float tw = trailing == null ? 0 : trailing.R.Width;
            float textW = CW - 28 - tw - (tw > 0 ? 16 : 0);
            float detailH = detail == null ? 0 : Ui.Measure(detail, Ui.F(11), textW).Height;
            float h = detail == null ? 44 : Math.Max(56, 32 + detailH + 10);
            Color tc = dim ? Ui.A(T.Text, 110) : T.Text;
            float ty = detail == null ? y + (h - 18) / 2 : y + 11;
            Label(CX + 14, ty, textW, title, Ui.F(13), tc);
            if (detail != null) Label(CX + 14, y + 31, textW, detail, Ui.F(11), dim ? Ui.A(T.Secondary, 110) : T.Secondary, true);
            if (trailing != null)
            {
                trailing.R = new RectangleF(CX + CW - 14 - tw, y + (h - trailing.R.Height) / 2, tw, trailing.R.Height);
                surface.Add(trailing);
            }
            return y + h;
        }

        private float Hint(float y, string text, Color? color = null)
        {
            Label(CX + 4, y, CW - 8, text, Ui.F(11), color ?? T.Secondary, true);
            return y + Ui.Measure(text, Ui.F(11), CW - 8).Height + 4;
        }

        // ---- Controls ----

        private Widget Toggle(Func<bool> get, Action<bool> set, bool enabled = true)
        {
            Widget w = new Widget();
            w.R = new RectangleF(0, 0, 38, 22);
            w.Clickable = enabled;
            w.Paint = (g, x) =>
            {
                bool on = get();
                int a = enabled ? 255 : 110;
                Ui.Fill(g, x.R, 11, Ui.A(on ? T.Accent : T.ToggleOff, a));
                float kx = on ? x.R.Right - 20 : x.R.X + 2;
                RectangleF knob = new RectangleF(kx, x.R.Y + 2, 18, 18);
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(40 * a / 255, 0, 0, 0))) g.FillEllipse(sh, knob.X, knob.Y + 0.8f, knob.Width, knob.Height);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255))) g.FillEllipse(b, knob);
            };
            w.Click = () => set(!get());
            return w;
        }

        private Widget Dropdown(string[] titles, Func<int> get, Action<int> set)
        {
            float maxW = titles.Max(t => Ui.Measure(t, Ui.F(13)).Width);
            Widget w = new Widget();
            w.R = new RectangleF(0, 0, maxW + 40, 26);
            w.Clickable = true;
            w.Paint = (g, x) =>
            {
                Ui.Fill(g, x.R, 6, x.Hot ? ControlPaint.Light(T.Field, 0.05f) : T.Field);
                Ui.Stroke(g, x.R, 6, T.FieldBorder, 1);
                int i = get();
                string text = i >= 0 && i < titles.Length ? titles[i] : "";
                Ui.Text(g, text, Ui.F(13), T.Text, new RectangleF(x.R.X + 10, x.R.Y, x.R.Width - 30, x.R.Height));
                float cx = x.R.Right - 14, cy = x.R.Y + x.R.Height / 2;
                using (Pen p = new Pen(T.Secondary, 1.4f))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawLines(p, new[] { new PointF(cx - 3.5f, cy - 1.5f), new PointF(cx, cy - 5), new PointF(cx + 3.5f, cy - 1.5f) });
                    g.DrawLines(p, new[] { new PointF(cx - 3.5f, cy + 1.5f), new PointF(cx, cy + 5), new PointF(cx + 3.5f, cy + 1.5f) });
                }
            };
            w.Click = () =>
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ThemedMenuRenderer(T);
                menu.ShowImageMargin = false;
                menu.Font = new Font("Segoe UI", 9.5f);
                int cur = get();
                for (int i = 0; i < titles.Length; i++)
                {
                    int idx = i;
                    ToolStripMenuItem item = new ToolStripMenuItem((idx == cur ? "✓  " : "     ") + titles[i]);
                    item.Padding = new Padding(2, 3, 12, 3);
                    item.Click += (s, e) => set(idx);
                    menu.Items.Add(item);
                }
                menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
                menu.Show(surface, new Point(Px(w.R.X), Px(w.R.Bottom + 4)));
            };
            return w;
        }

        private Widget KeyCaps(bool isToggle)
        {
            int which = isToggle ? 1 : 2;
            Shortcut sc = isToggle ? Prefs.ToggleShortcut : Prefs.ClearShortcut;
            string[] keys = sc.Display.Split('+');
            bool rec = recording == which;
            Font f = Ui.F(12);
            float width;
            if (rec) width = Ui.Measure("Type a shortcut", f).Width + 24;
            else width = keys.Sum(k => Math.Max(26, Ui.Measure(k, f).Width + 14)) + 4 * (keys.Length - 1) + 8;
            Widget w = new Widget();
            w.R = new RectangleF(0, 0, width, 30);
            w.Clickable = true;
            w.Tip = rec ? "Press the new shortcut, or Esc to cancel" : "Click to change";
            w.Paint = (g, x) =>
            {
                Ui.Fill(g, x.R, 7, T.KeyBg);
                if (rec)
                {
                    Ui.Stroke(g, x.R, 7, T.Accent, 1.5f);
                    Ui.Text(g, "Type a shortcut", f, T.Accent, x.R, StringAlignment.Center, StringAlignment.Center);
                    return;
                }
                if (x.Hot) Ui.Stroke(g, x.R, 7, T.FieldBorder, 1);
                float kx = x.R.X + 4;
                foreach (string k in keys)
                {
                    float kw = Math.Max(26, Ui.Measure(k, f).Width + 14);
                    RectangleF cap = new RectangleF(kx, x.R.Y + 4, kw, x.R.Height - 8);
                    Ui.Fill(g, cap, 5, T.KeyCap);
                    if (!T.Dark) Ui.Stroke(g, cap, 5, T.FieldBorder, 1);
                    Ui.Text(g, k, f, T.Text, cap, StringAlignment.Center, StringAlignment.Center);
                    kx += kw + 4;
                }
            };
            w.Click = () =>
            {
                if (recording == which)
                {
                    StopRecording();
                    return;
                }
                recording = which;
                shortcutNote = null;
                if (app != null) app.UnregisterHotKeys();
                surface.Focus();
                Rebuild();
            };
            return w;
        }

        private void StopRecording()
        {
            if (recording == 0) return;
            recording = 0;
            if (app != null) app.RegisterHotKeys();
            if (!IsDisposed) Rebuild();
        }

        private bool HandleKey(Keys keyData)
        {
            if (recording == 0) return false;
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
            {
                return true;
            }
            if (key == Keys.Escape && mods == Keys.None)
            {
                StopRecording();
                return true;
            }
            uint m = 0;
            if ((mods & Keys.Control) != 0) m |= Native.MOD_CONTROL;
            if ((mods & Keys.Alt) != 0) m |= Native.MOD_ALT;
            if ((mods & Keys.Shift) != 0) m |= Native.MOD_SHIFT;
            if (Native.KeyDown(Native.VK_LWIN) || Native.KeyDown(Native.VK_RWIN)) m |= Native.MOD_WIN;
            if ((m & ~Native.MOD_SHIFT) == 0)
            {
                shortcutNote = "Use Ctrl, Alt or Win with the key.";
                Rebuild();
                return true;
            }
            Shortcut s = new Shortcut(m, key);
            bool isToggle = recording == 1;
            recording = 0;
            shortcutNote = null;
            if (app != null && !app.ApplyShortcut(s, isToggle))
            {
                shortcutNote = s.Display + " is used by another app. Pick a different shortcut.";
            }
            if (app != null) app.RegisterHotKeys();
            Rebuild();
            return true;
        }

        // ---- Pages ----

        private float BuildGeneral(float y)
        {
            y = BeginCard("Shortcuts", y);
            y = Row(y, "Turn the pointer on or off", null, KeyCaps(true));
            y = Row(y, "Clear the drawing", null, KeyCaps(false));
            y = EndCard(y);
            if (shortcutNote != null) y = Hint(y - 16, shortcutNote, T.Danger) + 12;

            InteractKey ik = Prefs.Interact;
            y = BeginCard("Clicking for real", y);
            y = Row(y, "Hold to use the app", "Clicks and drags reach the app.",
                Dropdown(new[] { "Alt", "Ctrl", "Off" }, () => ik == InteractKey.Alt ? 0 : ik == InteractKey.Control ? 1 : 2,
                    i => Prefs.Interact = i == 0 ? InteractKey.Alt : i == 1 ? InteractKey.Control : InteractKey.Off));
            if (ik != InteractKey.Off)
            {
                bool keep = Prefs.InteractKeepsHand;
                y = Row(y, "While holding", keep ? "The hand stays, in menus too." : "The normal cursor comes back, in menus too.",
                    Dropdown(new[] { "Show the cursor", "Keep the hand" }, () => keep ? 1 : 0, i => Prefs.InteractKeepsHand = i == 1));
            }
            y = Row(y, "Double-tap Shift to click through",
                "Clicks reach the app and the hand stays. Double-tap again for taps. The tray icon turns orange.",
                Toggle(() => Prefs.DoubleShiftClickThrough, v => Prefs.DoubleShiftClickThrough = v));
            y = EndCard(y);

            y = BeginCard("Startup", y);
            y = Row(y, "Open TinyHand when you sign in", null, Toggle(() => Prefs.LaunchAtLogin, v => Prefs.LaunchAtLogin = v));
            return EndCard(y);
        }

        private float BuildPointers(float y)
        {
            Label(CX + 4, y, 200, "In your toolbar", Ui.F(12, true), T.Secondary);
            float rw = Ui.Measure("Reset", Ui.F(12)).Width;
            RectangleF reset = new RectangleF(CX + CW - 4 - rw, y - 2, rw, 20);
            Add(reset, (g, w) => Ui.Text(g, "Reset", Ui.F(12), w.Hot ? T.Text : T.Secondary, w.R), () =>
            {
                pinMessage = null;
                Prefs.Pinned = PointerStyle.DefaultPinned.Select(PointerStyle.Find).ToList();
            });
            y += 22;
            toolbarRow.R = new RectangleF(CX, y, CW, 78);
            toolbarRow.Clickable = true;
            surface.Add(toolbarRow);
            y += 86;
            bool full = Prefs.Pinned.Count >= Prefs.MaxPinned;
            y = Hint(y, full ? "The toolbar is full. Remove a pointer to add another. Drag to reorder."
                             : "Press + to add a pointer. Drag to reorder, or out to remove.");
            if (pinMessage != null) y = Hint(y, pinMessage, T.Danger);
            y += 22;

            bool show = Prefs.ShowToolbar;
            y = BeginCard("Toolbar", y);
            y = Row(y, "Show the toolbar", show ? "Pops up when the pointer turns on and near the bottom of the screen."
                                                : "Stays hidden. Press Esc to exit, or use the tray icon.",
                Toggle(() => Prefs.ShowToolbar, v => Prefs.ShowToolbar = v));
            y = Row(y, "Ink button", "Change the colour while you present.", Toggle(() => Prefs.ShowInkButton, v => Prefs.ShowInkButton = v));
            return EndCard(y);
        }

        private float BuildAppearance(float y)
        {
            y = BeginCard("Pointer", y);
            Widget slider = new SliderWidget(this);
            y = Row(y, "Size", null, slider);
            y = Row(y, "Shadow under the pointer", null, Toggle(() => Prefs.ShowShadow, v => Prefs.ShowShadow = v));
            return EndCard(y);
        }

        private static readonly double[] FadeValues = { 3, 6, 10, 0 };
        private static readonly string[] FadeTitles = { "After 3 seconds", "After 6 seconds", "After 10 seconds", "Keep until cleared" };

        private float BuildDrawing(float y)
        {
            string ink = Prefs.InkChoice;
            int inkIdx = Array.IndexOf(InkChoices.Ids, ink);
            y = BeginCard("Ink", y);
            y = Row(y, "Ink colour", ink == "match" ? "Matches each pointer." : "All pointers draw in " + InkChoices.Titles[inkIdx].ToLowerInvariant() + ".",
                new SwatchesWidget(this));
            double fade = Prefs.FadeSeconds;
            int fi = Array.FindIndex(FadeValues, v => Math.Abs(v - fade) < 0.01);
            if (fi < 0) fi = fade <= 0 ? 3 : fade <= 4 ? 0 : fade <= 8 ? 1 : 2;
            y = Row(y, "Lines fade", "Clear them any time with " + Prefs.ClearShortcut.Display + ".",
                Dropdown(FadeTitles, () => fi, i => Prefs.FadeSeconds = FadeValues[i]));
            y = EndCard(y);
            return Hint(y - 12, "Hold and drag to draw. Hold Ctrl while drawing to add an arrowhead.");
        }

        private float BuildSound(float y)
        {
            preview.Warm();
            Label(CX + 4, y, 200, "Tap sound", Ui.F(12, true), T.Secondary);
            y += 22;
            bool on = Prefs.HandSound;
            string cur = Prefs.TapSound;
            const int cols = 4;
            const float gap = 8, th = 62;
            float tw = (CW - gap * (cols - 1)) / cols;
            int n = TapSounds.Ids.Length + 1;
            for (int i = 0; i < n; i++)
            {
                int idx = i - 1;
                bool selected = idx < 0 ? !on : on && TapSounds.Ids[idx] == cur;
                string title = idx < 0 ? "Off" : TapSounds.Titles[idx];
                RectangleF r = new RectangleF(CX + (i % cols) * (tw + gap), y + (i / cols) * (th + gap), tw, th);
                Add(r, (g, w) =>
                {
                    if (selected)
                    {
                        Ui.Fill(g, r, 10, Ui.A(T.Accent, T.Dark ? 38 : 28));
                        Ui.Stroke(g, r, 10, T.Accent, 1.5f);
                    }
                    else
                    {
                        Ui.Fill(g, r, 10, w.Hot ? T.Hover : T.Card);
                        if (!T.Dark) Ui.Stroke(g, r, 10, T.Separator, 1);
                    }
                    Color ic = selected ? T.Accent : T.Secondary;
                    float cx = r.X + r.Width / 2, cy = r.Y + 22;
                    if (idx < 0)
                    {
                        Ui.Text(g, "", Ui.Icon(15), ic, new RectangleF(cx - 12, cy - 10, 24, 20), StringAlignment.Center, StringAlignment.Center);
                    }
                    else
                    {
                        float[] bars = { 5, 10, 15, 10, 13, 6 };
                        using (Pen p = new Pen(ic, 1.8f))
                        {
                            p.StartCap = p.EndCap = LineCap.Round;
                            for (int b = 0; b < bars.Length; b++)
                            {
                                float bx = cx - 9 + b * 3.6f;
                                g.DrawLine(p, bx, cy - bars[b] / 2, bx, cy + bars[b] / 2);
                            }
                        }
                    }
                    Ui.Text(g, title, Ui.F(12, selected), selected ? T.Text : T.Secondary,
                        new RectangleF(r.X + 4, r.Y + 36, r.Width - 8, 20), StringAlignment.Center, StringAlignment.Center);
                }, () =>
                {
                    if (idx < 0)
                    {
                        Prefs.HandSound = false;
                        return;
                    }
                    preview.Preview(TapSounds.Ids[idx]);
                    Prefs.TapSound = TapSounds.Ids[idx];
                    Prefs.HandSound = true;
                });
            }
            y += (n + cols - 1) / cols * (th + gap);
            y = Hint(y, "Click a sound to hear it. It plays each time the hand taps.") + 18;
            y = BeginCard(null, y);
            y = Row(y, "Laser dot taps too", "Off by default, for quiet pointing.",
                Toggle(() => Prefs.LaserSound, v => Prefs.LaserSound = v, on), !on);
            return EndCard(y);
        }

        private float BuildAbout(float y)
        {
            RectangleF icon = new RectangleF(CX, y, 72, 72);
            Add(icon, (g, w) =>
            {
                Bitmap b = AppIcon(72);
                if (b != null) g.DrawImage(b, icon);
            });
            Label(CX + 88, y + 6, CW - 88, "TinyHand", Ui.F(18, false, true), T.Text);
            Label(CX + 88, y + 32, CW - 88, "for Windows, version 1.0", Ui.F(12), T.Secondary);
            Label(CX + 88, y + 50, CW - 88, "A Windows port of TinyHand for Mac. Free and open source.", Ui.F(12), T.Secondary);
            y += 96;

            y = BeginCard(null, y);
            y = LinkRow(y, "Windows port", "github.com/hotdogday/tinyhand-windows", "https://github.com/hotdogday/tinyhand-windows");
            y = LinkRow(y, "Original Mac app", "tinyhand.app", "https://tinyhand.app");
            y = LinkRow(y, "Original code by", "Kamran Ahmed (@nilbuild)", "https://github.com/nilbuild");
            y = LinkRow(y, "License", "MIT", "https://github.com/hotdogday/tinyhand-windows/blob/main/LICENSE");
            y = EndCard(y);

            float x = CX + 4;
            foreach (KeyValuePair<string, Action> b in new[]
            {
                new KeyValuePair<string, Action>("Reset all settings…", ResetAll),
                new KeyValuePair<string, Action>("Open log", () =>
                {
                    if (File.Exists(Log.PathName)) Process.Start("notepad.exe", "\"" + Log.PathName + "\"");
                }),
            })
            {
                float bw = Ui.Measure(b.Key, Ui.F(12)).Width;
                RectangleF r = new RectangleF(x, y - 8, bw, 20);
                Add(r, (g, w) => Ui.Text(g, b.Key, Ui.F(12), w.Hot ? T.Text : T.Secondary, r), b.Value);
                x += bw + 24;
            }
            return y + 12;
        }

        private float LinkRow(float y, string title, string detail, string url)
        {
            float rowTop = y;
            Widget link = new Widget();
            float dw = Ui.Measure(detail, Ui.F(12)).Width;
            link.R = new RectangleF(0, 0, dw + 22, 20);
            link.Paint = (g, w) =>
            {
                Ui.Text(g, detail, Ui.F(12), T.Secondary, new RectangleF(w.R.X, w.R.Y, dw + 2, w.R.Height));
                Ui.Text(g, "", Ui.Icon(10), T.Secondary, new RectangleF(w.R.Right - 14, w.R.Y, 14, w.R.Height), StringAlignment.Center, StringAlignment.Center);
            };
            int at = surface.Widgets.Count;
            y = Row(y, title, null, link);
            RectangleF whole = new RectangleF(CX, rowTop, CW, y - rowTop);
            Widget hover = new Widget();
            hover.R = whole;
            hover.Clickable = true;
            hover.Tip = url;
            hover.Paint = (g, w) =>
            {
                if (w.Hot) Ui.Fill(g, whole, 12, Ui.A(T.Hover, 160));
            };
            hover.Click = () => Process.Start(url);
            surface.Widgets.Insert(at, hover);
            hover.Clickable = true;
            return y;
        }

        private void ResetAll()
        {
            if (MessageBox.Show(this, "Shortcuts, toolbar pointers, size, shadow, drawing and sound go back to how they were at first. Open at login stays as it is.",
                "Reset all settings?", MessageBoxButtons.OKCancel, MessageBoxIcon.None) != DialogResult.OK)
            {
                return;
            }
            if (app != null) app.UnregisterHotKeys();
            Prefs.ResetAll();
            if (app != null) app.RegisterHotKeys();
        }

        // ---- Pointer picker (the + slot) ----

        private void OpenPicker(RectangleF anchor)
        {
            if (picker != null) picker.Close();
            Surface ps = new Surface();
            ps.S = scale;
            ps.T = T;
            const int cols = 6;
            const float tw = 62, th = 58, gap = 4, pad = 12;
            List<PointerStyle> all = PointerStyle.All.ToList();
            int rows = (all.Count + cols - 1) / cols;
            float pw = pad * 2 + cols * tw + (cols - 1) * gap;
            float ph = pad * 2 + rows * th + (rows - 1) * gap;
            Action build = null;
            build = () =>
            {
                ps.Widgets.Clear();
                List<PointerStyle> pinned = Prefs.Pinned;
                for (int i = 0; i < all.Count; i++)
                {
                    PointerStyle s = all[i];
                    bool isPinned = pinned.Contains(s);
                    RectangleF r = new RectangleF(pad + (i % cols) * (tw + gap), pad + (i / cols) * (th + gap), tw, th);
                    Widget w = new Widget();
                    w.R = r;
                    w.Clickable = true;
                    w.Tip = isPinned ? s.Title + " is in your toolbar. Click to remove it" : "Click to add " + s.Title + " to the toolbar";
                    w.Paint = (g, x) =>
                    {
                        Ui.Fill(g, r, 10, x.Hot ? T.Hover : Ui.A(T.Hover, 0));
                        Ui.Style(g, s, new RectangleF(r.X + (tw - 40) / 2, r.Y + (th - 40) / 2, 40, 40));
                        if (isPinned)
                        {
                            RectangleF badge = new RectangleF(r.Right - 18, r.Y + 3, 15, 15);
                            using (SolidBrush b = new SolidBrush(T.Accent)) g.FillEllipse(b, badge);
                            Ui.Text(g, "", Ui.Icon(8), Color.White, badge, StringAlignment.Center, StringAlignment.Center);
                        }
                    };
                    w.Click = () =>
                    {
                        List<PointerStyle> p = Prefs.Pinned;
                        pinMessage = null;
                        if (p.Contains(s))
                        {
                            if (p.Count <= 1)
                            {
                                pinMessage = "Keep at least one pointer in the toolbar.";
                                Rebuild();
                                return;
                            }
                            p.Remove(s);
                            Prefs.Pinned = p;
                            build();
                            ps.Rebuilt();
                            return;
                        }
                        if (p.Count >= Prefs.MaxPinned)
                        {
                            pinMessage = "The toolbar is full. Remove one above to add " + s.Title + ".";
                            Rebuild();
                            picker.Close();
                            return;
                        }
                        p.Add(s);
                        Prefs.Pinned = p;
                        picker.Close();
                    };
                    ps.Widgets.Add(w);
                }
            };
            build();
            ps.Size = new Size(Px(pw), Px(ph));
            ps.BackColor = T.Menu;
            ToolStripControlHost host = new ToolStripControlHost(ps);
            host.Margin = Padding.Empty;
            host.Padding = Padding.Empty;
            host.AutoSize = false;
            host.Size = ps.Size;
            picker = new ToolStripDropDown();
            picker.Renderer = new ThemedMenuRenderer(T);
            picker.Padding = new Padding(1);
            picker.Items.Add(host);
            picker.Closed += (s, e) => surface.Invalidate();
            float px = Math.Max(CX, Math.Min(anchor.X + anchor.Width / 2 - pw / 2, WinW - pw - 12));
            picker.Show(surface, new Point(Px(px), Px(anchor.Bottom + 6)));
        }

        // ---- Custom widgets ----

        /// <summary>The toolbar's pointers: drag to reorder or out to remove, × to remove, + to add.</summary>
        private sealed class ToolbarRow : Widget
        {
            private readonly SettingsForm f;
            private PointerStyle dragStyle;
            private List<PointerStyle> order;
            private PointF downAt;
            private bool moved;
            private bool outside;
            private int downSlot = -1;
            private bool downOnBadge;

            public ToolbarRow(SettingsForm form)
            {
                f = form;
            }

            private const float Gap = 6, Inset = 10, SlotH = 58;

            private float SlotW { get { return (R.Width - 2 * Inset - (Prefs.MaxPinned - 1) * Gap) / Prefs.MaxPinned; } }

            private RectangleF Slot(int i)
            {
                return new RectangleF(R.X + Inset + i * (SlotW + Gap), R.Y + Inset, SlotW, SlotH);
            }

            private static RectangleF Badge(RectangleF slot)
            {
                return new RectangleF(slot.Right - 13, slot.Y - 4, 17, 17);
            }

            private int SlotAt(PointF p)
            {
                for (int i = 0; i < Prefs.MaxPinned; i++)
                {
                    if (RectangleF.Inflate(Slot(i), Gap / 2, Gap).Contains(p)) return i;
                }
                return -1;
            }

            public override bool Hit(PointF p)
            {
                return RectangleF.Inflate(R, 0, 6).Contains(p);
            }

            public override void OnPaint(Graphics g, Surface s)
            {
                Theme T = f.T;
                List<PointerStyle> pinned = moved && order != null ? order : Prefs.Pinned;
                bool full = pinned.Count >= Prefs.MaxPinned;
                int alpha = Prefs.ShowToolbar ? 255 : 120;
                Ui.Fill(g, R, 14, T.Card);
                if (!T.Dark) Ui.Stroke(g, R, 14, T.Separator, 1);
                int hoverSlot = moved ? -1 : SlotAt(s.Mouse);
                if (!Hit(s.Mouse)) hoverSlot = -1;
                for (int i = 0; i < Prefs.MaxPinned; i++)
                {
                    RectangleF slot = Slot(i);
                    if (i < pinned.Count)
                    {
                        PointerStyle st = pinned[i];
                        if (moved && st == dragStyle)
                        {
                            Ui.Stroke(g, slot, 10, Ui.A(T.Secondary, 90), 1, true);
                            continue;
                        }
                        Ui.Fill(g, slot, 10, i == hoverSlot ? T.Hover : T.KeyBg);
                        Ui.Style(g, st, new RectangleF(slot.X + (slot.Width - 40) / 2, slot.Y + 9, 40, 40), alpha);
                        if (i == hoverSlot && pinned.Count > 1)
                        {
                            RectangleF b = Badge(slot);
                            bool overBadge = RectangleF.Inflate(b, 2, 2).Contains(s.Mouse);
                            using (SolidBrush br = new SolidBrush(overBadge ? T.Text : T.Secondary)) g.FillEllipse(br, b);
                            Ui.Text(g, "", Ui.Icon(7), T.Bg, b, StringAlignment.Center, StringAlignment.Center);
                        }
                    }
                    else if (i == pinned.Count && !full)
                    {
                        bool hot = i == hoverSlot || (f.picker != null && f.picker.Visible);
                        Ui.Fill(g, slot, 10, Ui.A(T.Text, hot ? 22 : 12));
                        Ui.Stroke(g, slot, 10, Ui.A(T.Text, 45), 1, true);
                        Ui.Text(g, "", Ui.Icon(14), hot ? T.Text : T.Secondary, slot, StringAlignment.Center, StringAlignment.Center);
                    }
                    else
                    {
                        Ui.Fill(g, slot, 10, Ui.A(T.Text, 7));
                    }
                }
                if (moved && dragStyle != null)
                {
                    RectangleF fl = new RectangleF(s.Mouse.X - SlotW / 2, s.Mouse.Y - SlotH / 2, SlotW, SlotH);
                    Ui.Fill(g, new RectangleF(fl.X + 1, fl.Y + 3, fl.Width, fl.Height), 10, Color.FromArgb(60, 0, 0, 0));
                    Ui.Fill(g, fl, 10, T.KeyCap);
                    Ui.Style(g, dragStyle, new RectangleF(fl.X + (fl.Width - 40) / 2, fl.Y + 9, 40, 40), outside ? 140 : 255);
                    if (outside)
                    {
                        RectangleF b = Badge(fl);
                        using (SolidBrush br = new SolidBrush(T.Danger)) g.FillEllipse(br, b);
                        Ui.Text(g, "", Ui.Icon(8), Color.White, b, StringAlignment.Center, StringAlignment.Center);
                    }
                }
            }

            public override void OnDown(PointF p, Surface s)
            {
                downAt = p;
                moved = false;
                outside = false;
                order = null;
                dragStyle = null;
                downSlot = SlotAt(p);
                List<PointerStyle> pinned = Prefs.Pinned;
                downOnBadge = downSlot >= 0 && downSlot < pinned.Count && pinned.Count > 1
                    && RectangleF.Inflate(Badge(Slot(downSlot)), 2, 2).Contains(p);
                if (downSlot >= 0 && downSlot < pinned.Count && !downOnBadge) dragStyle = pinned[downSlot];
            }

            public override void OnDrag(PointF p, Surface s)
            {
                if (dragStyle == null) return;
                if (!moved)
                {
                    if (Math.Abs(p.X - downAt.X) + Math.Abs(p.Y - downAt.Y) < 4) return;
                    moved = true;
                    order = Prefs.Pinned;
                }
                outside = !RectangleF.Inflate(R, 10, 34).Contains(p);
                if (!outside)
                {
                    int target = (int)Math.Floor((p.X - R.X - Inset + Gap / 2) / (SlotW + Gap));
                    target = Math.Max(0, Math.Min(order.Count - 1, target));
                    order.Remove(dragStyle);
                    order.Insert(target, dragStyle);
                }
            }

            public override void OnUp(PointF p, Surface s, bool inside)
            {
                List<PointerStyle> pinned = Prefs.Pinned;
                if (moved)
                {
                    List<PointerStyle> result = order;
                    PointerStyle st = dragStyle;
                    bool remove = outside;
                    moved = false;
                    order = null;
                    dragStyle = null;
                    f.pinMessage = null;
                    if (remove)
                    {
                        if (pinned.Count <= 1)
                        {
                            f.pinMessage = "Keep at least one pointer in the toolbar.";
                            f.Rebuild();
                            return;
                        }
                        pinned.Remove(st);
                        Prefs.Pinned = pinned;
                        return;
                    }
                    Prefs.Pinned = result;
                    return;
                }
                dragStyle = null;
                int slot = SlotAt(p);
                if (slot != downSlot || slot < 0) return;
                if (downOnBadge)
                {
                    f.pinMessage = null;
                    pinned.RemoveAt(slot);
                    Prefs.Pinned = pinned;
                    return;
                }
                if (slot == pinned.Count && pinned.Count < Prefs.MaxPinned)
                {
                    f.OpenPicker(Slot(slot));
                }
            }
        }

        /// <summary>Pointer size, with a small and a big hand at the ends.</summary>
        private sealed class SliderWidget : Widget
        {
            private readonly SettingsForm f;
            private const float TrackW = 180;

            public SliderWidget(SettingsForm form)
            {
                f = form;
                R = new RectangleF(0, 0, TrackW + 52, 24);
                Clickable = true;
            }

            private RectangleF Track { get { return new RectangleF(R.X + 22, R.Y + R.Height / 2 - 2, TrackW, 4); } }

            public override void OnPaint(Graphics g, Surface s)
            {
                Theme T = f.T;
                Ui.HandGlyph(g, new RectangleF(R.X + 2, R.Y + R.Height / 2 - 6, 12, 12), T.Secondary);
                Ui.HandGlyph(g, new RectangleF(R.Right - 20, R.Y + R.Height / 2 - 10, 20, 20), T.Secondary);
                RectangleF t = Track;
                double v = (Prefs.PointerSize - Prefs.PointerSizeMin) / (Prefs.PointerSizeMax - Prefs.PointerSizeMin);
                float kx = t.X + (float)v * t.Width;
                Ui.Fill(g, t, 2, T.ToggleOff);
                Ui.Fill(g, new RectangleF(t.X, t.Y, kx - t.X, t.Height), 2, T.Accent);
                RectangleF knob = new RectangleF(kx - 9, R.Y + R.Height / 2 - 9, 18, 18);
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillEllipse(sh, knob.X, knob.Y + 1, knob.Width, knob.Height);
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, knob);
                if (!T.Dark)
                {
                    using (Pen p = new Pen(T.FieldBorder, 1)) g.DrawEllipse(p, knob);
                }
            }

            private void SetFrom(PointF p)
            {
                RectangleF t = Track;
                double v = Math.Max(0, Math.Min(1, (p.X - t.X) / t.Width));
                double size = Prefs.PointerSizeMin + v * (Prefs.PointerSizeMax - Prefs.PointerSizeMin);
                Prefs.PointerSize = Math.Round(size, 3);
            }

            public override void OnDown(PointF p, Surface s) { SetFrom(p); }
            public override void OnDrag(PointF p, Surface s) { SetFrom(p); }
            public override void OnUp(PointF p, Surface s, bool inside) { f.Rebuild(); }
        }

        /// <summary>The nine ink colours as dots.</summary>
        private sealed class SwatchesWidget : Widget
        {
            private readonly SettingsForm f;
            private const float D = 20, Gap = 8;

            public SwatchesWidget(SettingsForm form)
            {
                f = form;
                int n = InkChoices.Ids.Length;
                R = new RectangleF(0, 0, n * D + (n - 1) * Gap + 4, D + 4);
                Clickable = true;
            }

            private RectangleF Dot(int i)
            {
                return new RectangleF(R.X + 2 + i * (D + Gap), R.Y + 2, D, D);
            }

            private int At(PointF p)
            {
                for (int i = 0; i < InkChoices.Ids.Length; i++)
                {
                    if (RectangleF.Inflate(Dot(i), Gap / 2, 4).Contains(p)) return i;
                }
                return -1;
            }

            public override void OnPaint(Graphics g, Surface s)
            {
                Theme T = f.T;
                string cur = Prefs.InkChoice;
                int hover = Hot ? At(s.Mouse) : -1;
                for (int i = 0; i < InkChoices.Ids.Length; i++)
                {
                    string id = InkChoices.Ids[i];
                    RectangleF d = Dot(i);
                    RectangleF inner = id == cur ? RectangleF.Inflate(d, -3.5f, -3.5f) : RectangleF.Inflate(d, -1, -1);
                    if (id == "match")
                    {
                        Color[] seg = { PointerStyle.Red, Color.FromArgb(255, 204, 0), Color.FromArgb(48, 194, 90), Color.FromArgb(10, 132, 255), Color.FromArgb(135, 84, 242) };
                        for (int k = 0; k < seg.Length; k++)
                        {
                            using (SolidBrush b = new SolidBrush(seg[k])) g.FillPie(b, inner.X, inner.Y, inner.Width, inner.Height, -90 + k * 72, 72.5f);
                        }
                    }
                    else
                    {
                        Color c = InkChoices.ColorOf(id).Value;
                        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, inner);
                        if (id == "white" || (id == "black" && T.Dark))
                        {
                            using (Pen p = new Pen(Ui.A(T.Secondary, 120), 1)) g.DrawEllipse(p, inner);
                        }
                    }
                    if (id == cur)
                    {
                        using (Pen p = new Pen(T.Text, 1.6f)) g.DrawEllipse(p, RectangleF.Inflate(d, 0.5f, 0.5f));
                    }
                    else if (i == hover)
                    {
                        using (Pen p = new Pen(Ui.A(T.Text, 90), 1.2f)) g.DrawEllipse(p, RectangleF.Inflate(d, 0.5f, 0.5f));
                    }
                }
            }

            public override void OnUp(PointF p, Surface s, bool inside)
            {
                int i = At(p);
                if (i >= 0) Prefs.InkChoice = InkChoices.Ids[i];
            }
        }
    }
}
