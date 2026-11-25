# AL-76 for Linux (in progress)

AL-76 for Debian and Ubuntu, packaged as a `.deb` for **amd64** and **arm64**. It is the same assistant as the Windows
version: the logic, the brains, the tools and the 3D sphere come from `windows/` and are shared; only what touches the
system is written again for Linux.

**Status:** phase 0 of 8 (foundations). The package installs with `apt`, opens the sphere and a status window that
shows the PC level and the session. Voice, brains and the rest arrive phase by phase. Not ready for everyday use yet.

## What it runs on

- Ubuntu 26.04 LTS and 24.04 LTS, Debian 13; GNOME first, KDE Plasma second. Other desktops through X11.
- On Wayland sessions AL-76's own windows (the sphere and the nucleus) use **Xwayland**: it is the only way for a normal
  app to float a window above the others and place it. Everything else works on Wayland directly.

## Build it

On a Mac or a Linux PC with the .NET 10 SDK:

```bash
linux/scripts/build.sh all
```

This publishes a self-contained app to `linux/out/linux-<arch>/` and makes `linux/out/al76_<version>_<arch>.deb`
(the `.deb` step needs `dpkg-deb`: on a Mac the script uses an OrbStack Linux machine).

Install it:

```bash
sudo apt install ./al76_0.0.1_amd64.deb
```

## How it is put together

- `src/Shared/` compiles the Windows core and voice projects from their own files (they are never edited from here).
- `src/AL76.Linux.Platform/` holds what is Linux-specific: XDG paths, hardware, desktop session, later audio,
  accessibility (AT-SPI), portals and D-Bus.
- `src/AL76.Linux.App/` is the app: GTK3 and WebKitGTK called directly, every window an HTML page. The Windows HUD
  pages run unchanged: a small bridge gives them the same `window.chrome.webview` they use with WebView2.
- `tests/` runs the Windows core tests and the Linux tests, on the Mac or inside Linux (`scripts/prova-orb.sh`);
  `scripts/prova-vm.sh` installs the package in a desktop VM over SSH and starts it in the graphical session.
- `MAPPA_WINDOWS_LINUX.md` lists what is reused and what is rebuilt.

The ROBOTTINO 3D model is never part of the package: the app reads it from `~/.local/share/AL-76/companion/` if the
user puts it there.
