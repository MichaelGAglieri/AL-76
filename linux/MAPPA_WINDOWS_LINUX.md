# Map Windows → Linux

What the Linux version reuses from `windows/` (compiled from there, never edited from here) and what it rebuilds.
Status: `—` not started, `◐` partial, `✓` done. Phases as in the Linux plan.

## Reused as is

| Windows | Linux | How | Status |
|---|---|---|---|
| `src/AL76.Core` (logic, brains, MCP, tools, study, accounts, Maker, phone, safety) | `src/Shared/AL76.Core` | sources linked, own obj folder, same packages (checked by `scripts/build.sh`) | ✓ builds for linux-x64/arm64; 547 of 548 Windows core tests pass on Ubuntu 26.04 arm64 (see "Core requests") |
| `src/AL76.Speech` (Whisper.net, Kokoro, sherpa-onnx, models) | `src/Shared/AL76.Speech` | sources linked | ◐ builds; native libraries present for linux-arm64 and linux-x64 (whisper, onnxruntime, sherpa-onnx); used in phase 1 |
| `tests/AL76.Core.Tests` | `tests/AL76.Core.Tests` | sources linked | ✓ |
| `web/hud` (sphere, satellite, reactor, robot, nucleus) | copied next to the app at build (`web/hud`) | unchanged; `window.chrome.webview` provided by `WebLayer.Bridge` | ◐ sphere ✓ (phase 0); nucleus in phase 3 |

## Rebuilt for Linux

| Windows | Linux | Phase | Status |
|---|---|---|---|
| `Platform/AppPaths.cs` | `Platform/XdgPaths.cs` (XDG config/data/state/cache, `user-dirs.dirs`) | 0 | ✓ |
| `Platform/WindowsHardware.cs` | `Platform/LinuxHardware.cs` (installed memory from `/sys/devices/system/memory`, else `/proc/meminfo`; `/sys/class/drm`, `nvidia-smi`) | 0 | ✓ |
| — | `Platform/DesktopSession.cs` (X11 / Wayland / Xwayland, GNOME / KDE / Xfce, distribution) | 0 | ✓ |
| `Core/Hardware/TierPolicy` (Windows voice on Base) | `Platform/LinuxTier.cs` (Piper on Base) | 0 | ✓ |
| WPF + WebView2 (`App/WebLayer.cs`, `HudWindow.cs`) | GTK3 + WebKitGTK 4.1 by P/Invoke (`App/Native/Gtk.cs`, `GtkLoop.cs`, `WebLayer.cs`, `WebWindow.cs`, `HudWindow.cs`), `al76://` scheme | 0 | ◐ sphere window (Xwayland via `gdk_set_allowed_backends`, `WEBKIT_DISABLE_DMABUF_RENDERER` via libc `setenv`), drag, Esc; checked on GNOME 50 Wayland in the VM; menu and nucleus in phase 3 |
| `App/MainWindow` (status) | `App/StatusWindow.cs` + `web/app/stato.html` | 0 | ✓ |
| Named mutex (single instance) | `App/SingleInstance.cs` (Unix socket in `$XDG_RUNTIME_DIR`) | 0 | ✓ |
| Inno Setup / MSIX | `packaging/build-deb.sh` (`.deb` amd64 and arm64), `al76.desktop`, hicolor icons | 0–8 | ◐ installs with apt on Ubuntu 26.04 arm64 (OrbStack and the GNOME VM), Ubuntu 24.04 amd64, Debian 13 arm64 |
| `Platform/Microphone.cs`, `AudioOut.cs`, `WindowsVoice.cs`, `App/VoiceHost.cs` | PulseAudio/PipeWire, Piper, voice host | 1 | — |
| `Platform/WindowsPcActions.cs` | `.desktop` apps, wpctl, MPRIS, UPower, gsettings | 2 | — |
| `App/NucleusWindow.cs`, `BubbleWindow.cs`, `TypeBar.cs`, `SettingsWindow.cs`, `SetupWindow.cs`, `Pages.cs` | HTML pages in WebKitGTK | 3 | — |
| `Platform/WindowsAppDriver*.cs`, `WindowsPageReader.cs` | AT-SPI, XTest, portals, GNOME extension, Tesseract | 4 | — |
| `Platform/WindowsSecrets.cs` | Secret Service | 5 | — |
| `App/WindowsDocuments.cs` | WebKitGTK print to PDF, freedesktop trash | 6 | — |
| `Platform/WindowsBonjour.cs`, `WindowsPhoneScreen.cs`, `WindowsHelloAndCamera.cs`, `App/PhoneHost.cs` | Avahi, X11/ScreenCast, polkit, GStreamer | 7 | — |

## Core requests

Small changes the shared core needs for Linux. They are asked of the Windows work, never made from `linux/`.

1. **PDF fonts on Linux** (`Core/Tools/DocumentTools.cs`, `SystemFonts`): only Windows and macOS fonts are looked for, so on Linux the PDFs lose accents ("Giovedì" → "Giovedi"; test `PortedToolsTests.PdfKeepsAccentsHeadingsAndBullets` fails on Linux). Proposal: after the macOS pair, try `/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf` + `DejaVuSans-Bold.ttf`, `/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf` + `-Bold.ttf`, `/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf` + `-Bold.ttf`.
