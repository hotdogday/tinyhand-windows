using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TinyHand
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Native.EnableDpiAwareness();

            if (args.Length > 0 && args[0] == "--render-test")
            {
                return RenderTest.Run(args.Length > 1 ? args[1] : "render-test.png");
            }
            if (args.Length > 0 && args[0] == "--ink-test")
            {
                return RenderTest.InkClear(args.Length > 1 ? args[1] : "ink-test.png");
            }
            if (args.Length > 0 && args[0] == "--settings-test")
            {
                return RenderTest.Settings(args.Length > 1 ? args[1] : "settings-test.png");
            }

            string command = args.Length > 0 ? string.Join(" ", args) : null;
            bool first;
            using (Mutex mutex = new Mutex(true, "Local\\TinyHand-SingleInstance", out first))
            {
                if (!first)
                {
                    // Already running: hand the command over (no command = open Settings) and leave.
                    return Forward(command ?? "tinyhand://settings") ? 0 : 1;
                }

                // A previous crash could have left the pointer hidden.
                CursorHider.Restore();
                Application.ThreadException += (s, e) => Crash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
                AppDomain.CurrentDomain.ProcessExit += (s, e) => CursorHider.Restore();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Log.Write("launch " + Application.ExecutablePath);

                Application.Run(new TrayApp(command));
                CursorHider.Restore();
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static void Crash(Exception e)
        {
            CursorHider.Restore();
            Log.Write("crash: " + e);
        }

        private static bool Forward(string command)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                IntPtr target = Native.FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, TrayApp.AutomationWindow);
                if (target != IntPtr.Zero)
                {
                    IntPtr text = Marshal.StringToHGlobalUni(command);
                    try
                    {
                        Native.COPYDATASTRUCT cds = new Native.COPYDATASTRUCT();
                        cds.dwData = new IntPtr(1);
                        cds.cbData = (command.Length + 1) * 2;
                        cds.lpData = text;
                        Native.SendMessage(target, Native.WM_COPYDATA, IntPtr.Zero, ref cds);
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(text);
                    }
                }
                Thread.Sleep(100); // the first instance may still be starting
            }
            return false;
        }
    }

    /// <summary>Renders the arm, laser and toolbar off screen into a PNG, to check the drawing code.</summary>
    internal static class RenderTest
    {
        public static int Run(string outPath)
        {
            LayeredWindow.Offscreen = true;
            Rectangle screen = new Rectangle(0, 0, 1600, 900);
            Bitmap sheet = new Bitmap(screen.Width * 2, screen.Height * 2, PixelFormat.Format32bppArgb);
            using (Graphics sg = Graphics.FromImage(sheet))
            {
                sg.Clear(Color.FromArgb(236, 238, 242));
                string[] styles = { "lavender", "robot", "glove", "laser" };
                PointF[] cursors = { new PointF(500, 300), new PointF(1200, 250), new PointF(200, 650), new PointF(800, 450) };
                for (int i = 0; i < styles.Length; i++)
                {
                    using (ArmView arm = new ArmView())
                    {
                        arm.SetScreen(screen);
                        arm.SetStyle(PointerStyle.Find(styles[i]));
                        for (int n = 0; n < 200; n++) arm.Aim(cursors[i], 1 / 60f);
                        if (i == 1)
                        {
                            arm.Press();
                            arm.Held = true;
                            arm.Aim(new PointF(cursors[i].X + 1, cursors[i].Y), 1 / 60f);
                        }
                        Bitmap canvas = GetCanvas(arm);
                        int ox = (i % 2) * screen.Width, oy = (i / 2) * screen.Height;
                        using (Pen p = new Pen(Color.Red, 2))
                        {
                            sg.DrawImage(canvas, ox, oy, screen.Width, screen.Height);
                            sg.DrawLine(p, ox + cursors[i].X - 10, oy + cursors[i].Y, ox + cursors[i].X + 10, oy + cursors[i].Y);
                            sg.DrawLine(p, ox + cursors[i].X, oy + cursors[i].Y - 10, ox + cursors[i].X, oy + cursors[i].Y + 10);
                            sg.DrawRectangle(Pens.Gray, ox, oy, screen.Width - 1, screen.Height - 1);
                        }
                        sg.DrawString(styles[i] + (i == 1 ? " (pressed)" : ""), new Font("Segoe UI", 14), Brushes.Black, ox + 10, oy + 10);
                    }
                }
            }
            sheet.Save(outPath, ImageFormat.Png);
            Console.WriteLine("rendered " + outPath);
            return 0;
        }

        public static int Settings(string outPath)
        {
            Application.EnableVisualStyles();
            HandAsset.PreloadThumbnails(PointerStyle.Hands.Select(s => s.AssetName), null);
            Thread.Sleep(2500);
            string mode = Environment.GetEnvironmentVariable("TH_THEME");
            Theme.Override = mode == "light" ? false : true;
            using (Overlay overlay = new Overlay())
            using (SettingsForm form = new SettingsForm(null, overlay))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-20000, -20000);
                form.ShowInTaskbar = false;
                form.Show();
                Array sections = Enum.GetValues(typeof(SettingsForm.Section));
                foreach (SettingsForm.Section s in sections)
                {
                    form.ShowSection(s);
                    for (int i = 0; i < 6; i++)
                    {
                        Application.DoEvents();
                        Thread.Sleep(30);
                    }
                    Control surface = form.Controls[0];
                    using (Bitmap b = new Bitmap(surface.Width, surface.Height))
                    {
                        surface.DrawToBitmap(b, new Rectangle(0, 0, surface.Width, surface.Height));
                        b.Save(outPath.Replace(".png", "-" + s.ToString().ToLowerInvariant() + ".png"), ImageFormat.Png);
                    }
                }
                form.Close();
            }
            return 0;
        }

        /// <summary>Draws a stroke, clears, draws another: only the second may remain.</summary>
        public static int InkClear(string outPath)
        {
            LayeredWindow.Offscreen = true;
            using (Ink ink = new Ink())
            {
                ink.SetScreen(new Rectangle(0, 0, 800, 450));
                ink.Color = PointerStyle.Red;
                ink.Begin(new PointF(100, 100), 8);
                for (int i = 1; i <= 30; i++) ink.Extend(new PointF(100 + i * 10, 100 + i * 4));
                ink.Render();
                ink.End();
                ink.Clear();
                ink.Color = PointerStyle.Purple;
                ink.Begin(new PointF(150, 350), 8);
                for (int i = 1; i <= 30; i++) ink.Extend(new PointF(150 + i * 15, 350 - i * 3));
                ink.Render();
                System.Reflection.FieldInfo f = typeof(Ink).GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Bitmap canvas = ((LayeredWindow)f.GetValue(ink)).Canvas;
                int red = 0, purple = 0;
                for (int y = 0; y < canvas.Height; y += 2)
                {
                    for (int x = 0; x < canvas.Width; x += 2)
                    {
                        Color c = canvas.GetPixel(x, y);
                        if (c.A > 200 && c.R > 200 && c.B < 80) red++;
                        if (c.A > 200 && c.B > 200 && c.R < 160) purple++;
                    }
                }
                using (Bitmap sheet = new Bitmap(canvas.Width, canvas.Height))
                using (Graphics g = Graphics.FromImage(sheet))
                {
                    g.Clear(Color.White);
                    g.DrawImage(canvas, 0, 0);
                    sheet.Save(outPath, ImageFormat.Png);
                }
                System.IO.File.WriteAllText(outPath + ".txt", "old red pixels=" + red + " new purple pixels=" + purple);
                return red == 0 && purple > 0 ? 0 : 1;
            }
        }

        private static Bitmap GetCanvas(ArmView arm)
        {
            System.Reflection.FieldInfo f = typeof(ArmView).GetField("window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return ((LayeredWindow)f.GetValue(arm)).Canvas;
        }
    }
}
