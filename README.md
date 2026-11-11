# TinyHand for Windows 👉

A tiny hand on a stick that points at things on your screen. Like a teacher's pointer, but it
lives in your notification area and never pokes anyone's eye out.

This is a **Windows port of [TinyHand](https://tinyhand.app)**, the Mac app by
**[Kamran Ahmed](https://github.com/nilbuild)**. The idea, the 36 lovingly made hands and the
tap sounds all come from his [original project](https://github.com/nilbuild/tinyhand). This
version rebuilds the app for Windows. If you're on a Mac, go get the real thing at
[tinyhand.app](https://tinyhand.app).

## Get it

1. Download `TinyHand.exe` from the [latest release](../../releases/latest).
2. Run it. That's it. No installer, nothing else to set up.

A little hand appears in your notification area (down by the clock). Windows SmartScreen may
raise an eyebrow the first time, because the exe isn't code-signed. Click **More info → Run
anyway**.

Made for Windows 10 and 11 (tested on Windows 11).

## Point at things

| Do this | And this happens |
|---|---|
| Click the tray hand, or press **Ctrl+Alt+H** | The pointer pops on (or off) |
| Right-click the tray hand | Menu and Settings |
| Click | *Tap!* The hand taps, and nothing underneath gets clicked |
| Hold and drag | Draw on the screen |
| Hold **Ctrl** while drawing | Your line gets an arrowhead |
| Hold **Alt** and click | Click for real (pick a different key in Settings) |
| Double-tap **Shift** | Every click goes through until you double-tap again |
| **Ctrl+Alt+X** | Wipe the drawing |
| **Esc** | Wipe everything and put the pointer away |

While the pointer is on, a small toolbar pops up when you bring the hand near the bottom of the
screen. Swap hands (or grab the laser dot), pick an ink colour, mute the tap, open Settings, or
call it a day.

The taskbar, the Start menu and open menus still get real clicks, and your normal cursor comes
back over them, so you're never stuck.

## Make it yours

Right-click the tray hand and choose **Settings**:

- **General:** change the shortcuts, choose how clicking for real works, and open TinyHand when
  you sign in.
- **Pointers:** choose which hands live in the toolbar. Press **+** to add one, drag to
  reorder, or drag one out to remove it.
- **Appearance:** pointer size and shadow.
- **Drawing:** ink colour, and how long lines stick around.
- **Sound:** 15 tap sounds, from a polite *thock* to a *quack*.

Settings follows your Windows light or dark mode.

## Automation

Launchers and scripts can boss TinyHand around with `tinyhand://` links:

```bat
start tinyhand://toggle
```

Or call the exe directly: `TinyHand.exe tinyhand://toggle`.

| Link | What it does |
|---|---|
| `tinyhand://on`, `tinyhand://off`, `tinyhand://toggle` | Turn the pointer on, off, or flip it |
| `tinyhand://clear` | Wipe the drawing |
| `tinyhand://style/<id>` | Switch to a hand (or `laser`) and turn the pointer on |
| `tinyhand://sound/on`, `/off`, `/toggle` | Tap sound for the current pointer |
| `tinyhand://sound/play` | Play the tap sound once |
| `tinyhand://settings` | Open Settings |
| `tinyhand://quit` | Quit TinyHand |

If TinyHand isn't running, a link starts it first and then does the thing.

While it runs, TinyHand writes its current state to `%APPDATA%\TinyHand\state.json`: whether the
pointer is on, which style is picked, whether sound is on, its `pid`, and every style with its
`id`, name and type (`hand` or `laser`). The file goes away when TinyHand quits.

## Build it yourself

No Visual Studio, no SDK. The C# compiler that already ships with Windows does the job:

```bat
powershell -ExecutionPolicy Bypass -File build.ps1
```

That compiles `src\*.cs` and packs everything in `assets\` into `dist\TinyHand.exe`.

Handy checks that draw to an image without taking over your mouse:

- `TinyHand.exe --render-test out.png`: the hand on its stick, and the laser dot.
- `TinyHand.exe --settings-test out.png`: every Settings page (set `TH_THEME=light` for the light
  theme).
- `TinyHand.exe --ink-test out.png`: draws, wipes and draws again, and exits with 1 if the wiped
  line sneaks back.

## How it works

| Piece | On Windows | On the Mac original |
|---|---|---|
| Turning clicks into taps | Low-level mouse hook (`WH_MOUSE_LL`) on its own thread | `CGEventTap` |
| Hand, ink and toolbar | Per-pixel-alpha layered windows drawn with GDI+ | Core Animation layers |
| Tap sound | One `waveOut` stream kept open while in use, mixing taps into silence | `AVAudioEngine` |
| Hiding the real cursor | Swaps the system cursors for a blank one, then restores yours | `CGDisplayHideCursor` |
| Shortcuts | `RegisterHotKey` | Carbon hot keys |
| Settings storage | `HKCU\Software\TinyHand` | `UserDefaults` |

Your real cursor always comes back: on quit, after a crash, and the next time TinyHand starts.

## What's different from the Mac version

- **Alt** is the "click for real" key (Option on the Mac), and **Ctrl** adds arrowheads
  (Command on the Mac).
- You can't make custom hands from your own pictures yet.
- No auto-updater. Grab new versions from the [releases](../../releases) page.
- Windows doesn't let normal apps intercept clicks on windows running as administrator, so on
  those the hand can't stop clicks from going through.
- Logs live in `%LOCALAPPDATA%\TinyHand\tinyhand.log`, if you ever need to peek.

## Credits and licence

TinyHand was created by **[Kamran Ahmed](https://github.com/nilbuild)**
([tinyhand.app](https://tinyhand.app), [source](https://github.com/nilbuild/tinyhand)). The hand
art, tap sounds and app design come from his project. This Windows port is by
[hotdogday](https://github.com/hotdogday).

MIT licensed, the same as the original: use it, change it, share it, even sell it. Just keep the
copyright notice and [LICENSE](LICENSE) with it. It comes with no warranty.
