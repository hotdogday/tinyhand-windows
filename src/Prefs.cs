using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TinyHand
{
    internal sealed class PointerStyle
    {
        public readonly string Id;
        public readonly string Title;
        public readonly Color Ink;

        private PointerStyle(string id, string title, Color ink)
        {
            Id = id;
            Title = title;
            Ink = ink;
        }

        public bool IsLaser { get { return Id == "laser"; } }
        public string AssetName { get { return IsLaser ? null : "hand_" + Id; } }

        public static readonly Color Purple = Color.FromArgb(135, 84, 242);
        public static readonly Color Red = Color.FromArgb(255, 41, 31);

        private static Color Hex(int v)
        {
            return Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        public static readonly PointerStyle Laser = new PointerStyle("laser", "Laser dot", Red);

        public static readonly PointerStyle[] Hands = new[]
        {
            new PointerStyle("lavender", "Lavender toy", Purple),
            new PointerStyle("glove", "Cursor glove", Purple),
            new PointerStyle("coral", "Coral vinyl", Red),
            new PointerStyle("plush", "Plush", Hex(0x2F86F0)),
            new PointerStyle("robot", "Robot", Hex(0x16A87A)),
            new PointerStyle("pink", "Bubblegum", Hex(0xEE4F98)),
            new PointerStyle("blue", "Sky", Hex(0x2F86F0)),
            new PointerStyle("mint", "Mint", Hex(0x16A87A)),
            new PointerStyle("lemon", "Lemon", Hex(0xF2B705)),
            new PointerStyle("black", "Midnight", Hex(0x1D1D21)),
            new PointerStyle("chrome", "Chrome", Hex(0x6B7380)),
            new PointerStyle("holo", "Holo", Purple),
            new PointerStyle("fuzzy", "Fuzzy", Purple),
            new PointerStyle("wood", "Wood", Hex(0x9A5E2C)),
            new PointerStyle("marble", "Marble", Hex(0x6B7380)),
            new PointerStyle("terrazzo", "Terrazzo", Hex(0xF0561D)),
            new PointerStyle("glass", "Glass", Hex(0x1597E5)),
            new PointerStyle("jelly", "Jelly", Hex(0xA33FE6)),
            new PointerStyle("pastel", "Pastel", Hex(0xEE4F98)),
            new PointerStyle("pixel", "Pixel", Hex(0x1D1D21)),
            new PointerStyle("lowpoly", "Low poly", Purple),
            new PointerStyle("knit", "Knit", Purple),
            new PointerStyle("skeleton", "Skeleton", Hex(0x9A5E2C)),
            new PointerStyle("astro", "Astronaut", Hex(0x2F86F0)),
            new PointerStyle("space", "Space", Red),
            new PointerStyle("monster", "Monster", Hex(0x3E9E2E)),
            new PointerStyle("water", "Water", Hex(0x1597E5)),
            new PointerStyle("lava", "Lava", Hex(0xF0561D)),
            new PointerStyle("grass", "Grass", Hex(0x3E9E2E)),
            new PointerStyle("cactus", "Cactus", Hex(0x3E9E2E)),
            new PointerStyle("choco", "Chocolate", Hex(0x9A5E2C)),
            new PointerStyle("cloud", "Cloud", Hex(0x2F86F0)),
            new PointerStyle("mech", "Mech", Hex(0xF0561D)),
            new PointerStyle("neon", "Neon", Hex(0xE0309A)),
            new PointerStyle("leaf", "Leaf", Hex(0x3E9E2E)),
            new PointerStyle("cookie", "Gingerbread", Hex(0x9A5E2C)),
        };

        public static IEnumerable<PointerStyle> All
        {
            get { return Hands.Concat(new[] { Laser }); }
        }

        public static PointerStyle Find(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            id = id.ToLowerInvariant();
            if (id == "dot")
            {
                return Laser;
            }
            return All.FirstOrDefault(s => s.Id == id);
        }

        public static readonly string[] DefaultPinned = { "lavender", "glove", "coral", "plush", "robot", "laser" };

        public override string ToString()
        {
            return Title;
        }
    }

    internal sealed class Shortcut
    {
        public readonly uint Modifiers; // MOD_*
        public readonly Keys Key;

        public Shortcut(uint modifiers, Keys key)
        {
            Modifiers = modifiers;
            Key = key;
        }

        public static readonly Shortcut ToggleDefault = new Shortcut(Native.MOD_CONTROL | Native.MOD_ALT, Keys.H);
        public static readonly Shortcut ClearDefault = new Shortcut(Native.MOD_CONTROL | Native.MOD_ALT, Keys.X);

        public string Display
        {
            get
            {
                List<string> parts = new List<string>();
                if ((Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
                if ((Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
                if ((Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
                if ((Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
                parts.Add(KeyName(Key));
                return string.Join("+", parts.ToArray());
            }
        }

        private static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9)
            {
                return ((int)(k - Keys.D0)).ToString();
            }
            switch (k)
            {
                case Keys.OemPeriod: return ".";
                case Keys.Oemcomma: return ",";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemQuestion: return "/";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.Oemtilde: return "`";
            }
            return k.ToString();
        }

        public string Serialize()
        {
            return Modifiers + ":" + (int)Key;
        }

        public static Shortcut Parse(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }
            string[] parts = s.Split(':');
            uint mods;
            int key;
            if (parts.Length != 2 || !uint.TryParse(parts[0], out mods) || !int.TryParse(parts[1], out key))
            {
                return null;
            }
            return new Shortcut(mods, (Keys)key);
        }
    }

    internal enum InteractKey { Alt, Control, Off }

    internal static class InkChoices
    {
        public static readonly string[] Ids = { "match", "red", "orange", "yellow", "green", "blue", "purple", "white", "black" };
        public static readonly string[] Titles = { "Match the pointer", "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "White", "Black" };

        public static Color? ColorOf(string id)
        {
            switch (id)
            {
                case "red": return PointerStyle.Red;
                case "orange": return Color.FromArgb(0xFF, 0x8A, 0x00);
                case "yellow": return Color.FromArgb(0xFF, 0xCC, 0x00);
                case "green": return Color.FromArgb(0x30, 0xC2, 0x5A);
                case "blue": return Color.FromArgb(0x0A, 0x84, 0xFF);
                case "purple": return Color.FromArgb(0x87, 0x54, 0xF2);
                case "white": return Color.FromArgb(0xFF, 0xFF, 0xFF);
                case "black": return Color.FromArgb(0x1D, 0x1D, 0x21);
            }
            return null;
        }

        public static Color Resolve(string id, PointerStyle style)
        {
            Color? c = ColorOf(id);
            return c.HasValue ? c.Value : style.Ink;
        }
    }

    internal static class TapSounds
    {
        public static readonly string[] Ids = { "thock", "click", "tock", "clack", "typewriter", "snap", "wood", "pop", "boop", "blip", "coin", "pew", "squeak", "quack", "chirp" };
        public static readonly string[] Titles = { "Thock", "Click", "Tock", "Clack", "Typewriter", "Snap", "Wood block", "Pop", "Boop", "Blip", "Coin", "Pew", "Squeak", "Quack", "Chirp" };
    }

    /// <summary>User settings, kept in HKCU\Software\TinyHand.</summary>
    internal static class Prefs
    {
        public static event EventHandler Changed;

        private const string KeyPath = @"Software\TinyHand";
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static object Get(string name)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                return k == null ? null : k.GetValue(name);
            }
        }

        private static void Set(string name, object value, bool notify)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (value is bool)
                {
                    k.SetValue(name, (bool)value ? 1 : 0, RegistryValueKind.DWord);
                }
                else if (value is int)
                {
                    k.SetValue(name, (int)value, RegistryValueKind.DWord);
                }
                else
                {
                    k.SetValue(name, value.ToString(), RegistryValueKind.String);
                }
            }
            if (notify)
            {
                RaiseChanged();
            }
        }

        public static void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null)
            {
                h(null, EventArgs.Empty);
            }
        }

        private static bool GetBool(string name, bool fallback)
        {
            object v = Get(name);
            return v is int ? (int)v != 0 : fallback;
        }

        private static string GetString(string name, string fallback)
        {
            string v = Get(name) as string;
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private static double GetDouble(string name, double fallback)
        {
            string v = Get(name) as string;
            double d;
            if (v != null && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
            {
                return d;
            }
            return fallback;
        }

        private static void SetDouble(string name, double value)
        {
            Set(name, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), true);
        }

        public static Shortcut ToggleShortcut
        {
            get { return Shortcut.Parse(GetString("shortcut.toggle", null)) ?? Shortcut.ToggleDefault; }
            set { Set("shortcut.toggle", value.Serialize(), true); }
        }

        public static Shortcut ClearShortcut
        {
            get { return Shortcut.Parse(GetString("shortcut.clear", null)) ?? Shortcut.ClearDefault; }
            set { Set("shortcut.clear", value.Serialize(), true); }
        }

        public const double PointerSizeMin = 0.08;
        public const double PointerSizeMax = 0.20;

        public static double PointerSize
        {
            get { return Math.Max(PointerSizeMin, Math.Min(PointerSizeMax, GetDouble("pointerSize", 0.13))); }
            set { SetDouble("pointerSize", value); }
        }

        public static bool ShowShadow
        {
            get { return GetBool("showShadow", true); }
            set { Set("showShadow", value, true); }
        }

        /// <summary>Seconds before a drawing fades. 0 keeps it until cleared.</summary>
        public static double FadeSeconds
        {
            get { return GetDouble("fadeSeconds", 3); }
            set { SetDouble("fadeSeconds", value); }
        }

        public static bool HandSound
        {
            get { return GetBool("sound.hands", true); }
            set { Set("sound.hands", value, true); }
        }

        public static bool LaserSound
        {
            get { return GetBool("sound.laser", false); }
            set { Set("sound.laser", value, true); }
        }

        public static string TapSound
        {
            get
            {
                string v = GetString("tapSound", "thock");
                return TapSounds.Ids.Contains(v) ? v : "thock";
            }
            set { Set("tapSound", value, true); }
        }

        public static InteractKey Interact
        {
            get
            {
                string v = GetString("interactKey", "alt");
                return v == "control" ? InteractKey.Control : v == "off" ? InteractKey.Off : InteractKey.Alt;
            }
            set { Set("interactKey", value == InteractKey.Control ? "control" : value == InteractKey.Off ? "off" : "alt", true); }
        }

        public static string InteractName
        {
            get { return Interact == InteractKey.Control ? "Ctrl" : Interact == InteractKey.Alt ? "Alt" : ""; }
        }

        public static bool InteractKeepsHand
        {
            get { return GetBool("interactKeepsHand", false); }
            set { Set("interactKeepsHand", value, true); }
        }

        public static bool DoubleShiftClickThrough
        {
            get { return GetBool("doubleShiftClickThrough", true); }
            set { Set("doubleShiftClickThrough", value, true); }
        }

        public static string InkChoice
        {
            get
            {
                string v = GetString("inkChoice", "match");
                return InkChoices.Ids.Contains(v) ? v : "match";
            }
            set { Set("inkChoice", value, true); }
        }

        public static bool ShowToolbar
        {
            get { return GetBool("showToolbar", true); }
            set { Set("showToolbar", value, true); }
        }

        public static bool ShowInkButton
        {
            get { return GetBool("showInkButton", true); }
            set { Set("showInkButton", value, true); }
        }

        public const int MaxPinned = 8;

        public static List<PointerStyle> Pinned
        {
            get
            {
                string raw = GetString("pinnedStyles", null);
                IEnumerable<string> ids = raw == null ? PointerStyle.DefaultPinned : raw.Split(',');
                List<PointerStyle> list = new List<PointerStyle>();
                foreach (string id in ids)
                {
                    PointerStyle s = PointerStyle.Find(id.Trim());
                    if (s != null && !list.Contains(s))
                    {
                        list.Add(s);
                    }
                }
                if (list.Count == 0)
                {
                    list.AddRange(PointerStyle.DefaultPinned.Select(PointerStyle.Find));
                }
                return list.Take(MaxPinned).ToList();
            }
            set { Set("pinnedStyles", string.Join(",", value.Select(s => s.Id).ToArray()), true); }
        }

        public static PointerStyle Style
        {
            get { return PointerStyle.Find(GetString("pointerStyle", "lavender")) ?? PointerStyle.Hands[0]; }
            set { Set("pointerStyle", value.Id, false); }
        }

        public static bool WelcomeShown
        {
            get { return GetBool("welcomeShown", false); }
            set { Set("welcomeShown", value, false); }
        }

        public static bool HintShown
        {
            get { return GetBool("firstRunHintShown", false); }
            set { Set("firstRunHintShown", value, false); }
        }

        public static bool LaunchAtLogin
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath))
                {
                    return k != null && k.GetValue("TinyHand") != null;
                }
            }
            set
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
                {
                    if (value)
                    {
                        k.SetValue("TinyHand", "\"" + Application.ExecutablePath + "\"");
                    }
                    else if (k.GetValue("TinyHand") != null)
                    {
                        k.DeleteValue("TinyHand");
                    }
                }
                RaiseChanged();
            }
        }

        /// <summary>Points an existing login entry at this copy of the exe, in case it moved.</summary>
        public static void RefreshLaunchAtLoginPath()
        {
            if (LaunchAtLogin)
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
                {
                    k.SetValue("TinyHand", "\"" + Application.ExecutablePath + "\"");
                }
            }
        }

        public static void ResetAll()
        {
            string[] names =
            {
                "shortcut.toggle", "shortcut.clear", "pointerSize", "showShadow", "fadeSeconds", "sound.hands",
                "sound.laser", "interactKey", "interactKeepsHand", "doubleShiftClickThrough", "pinnedStyles",
                "pointerStyle", "inkChoice", "showInkButton", "showToolbar", "tapSound",
            };
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                foreach (string n in names)
                {
                    if (k.GetValue(n) != null)
                    {
                        k.DeleteValue(n);
                    }
                }
            }
            RaiseChanged();
        }
    }
}
