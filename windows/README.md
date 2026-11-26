# AL-76 for Windows

AL-76 is a voice assistant for study, work and the PC. This folder is the Windows version: C# on .NET 10 with
WPF and WebView2, built for everyone (any school, job or city), with optional modules. The Mac version lives in
the rest of the repository; the two share ideas, not code.

> **Status: preview (0.1.4).** Everything below is written and covered by automated tests, and the app has
> been run in a Windows 11 virtual machine and by a first tester on a real PC (voice, apps, files). It has not
> yet been tried with a real camera, Windows Hello, Samsung TV, Bambu Lab printer or robot: those parts may need fixes. Reports are welcome
> as GitHub issues (the tray menu's **Export diagnostics** makes a zip without personal data to attach).

## Download and install

1. Open the [Releases page](../../../releases) and download
   **`AL-76-Setup-<version>-x64.exe`** from the latest `windows-v…` release. It is for ordinary Windows PCs
   (Intel or AMD, 64-bit). Arm64 PCs (Snapdragon) can run it too, through Windows' x64 emulation.
2. Run it. The installer is not signed yet, so Windows SmartScreen may say *"Windows protected your PC"*:
   choose **More info › Run anyway**. Check first that the file comes from this repository's Releases page.
3. It installs for your user only, in `%LOCALAPPDATA%\Programs\AL-76`: no administrator rights are needed.
   You can tick "Create a desktop shortcut"; the Start menu entry is always created.
4. At the end AL-76 starts with a guided setup: language, your name, what you use it for (study, work, both),
   the microphone test and the call, the brain (Claude Code, Codex, Gemini CLI or a local model with Ollama) and
   the optional modules. The Whisper and voice models are downloaded at that point (a few hundred MB, once).
5. If Windows asks whether AL-76 may use the network, choose **Private networks**: it is needed only by the
   phone app on the home network.

**Settings** open from the AL-76 icon by the clock (right click › Settings…), from the right-click menu of the
sphere or the robot, by starting AL-76 again from the Start menu while it runs, or by typing *impostazioni* in the
writing bar (Ctrl+Alt+T). The **Plancia** (dialogue, day, status, thought) opens from the same menus or with
*apri la plancia*. **Without AI** (Settings › Brain, the menus, or *disattiva l'IA* / *riattiva l'IA*) turns every
model off: AL-76 then runs only the commands it knows.

The **robot** look needs its 3D model, which is not part of AL-76: "Mini Robot" by Onur on Sketchfab (CC-BY 4.0,
a free Sketchfab account is needed). In Settings › Look choose Robottino, press **Open Sketchfab**, download the
model as glTF, then press **Install the downloaded zip**: AL-76 finds it in the Downloads folder.

If the Microsoft Visual C++ Redistributable is missing, AL-76 says so at the first start and shows Microsoft's
official download link. **Uninstall** from Windows Settings › Apps › Installed apps › AL-76. Your settings,
memory and downloaded models stay in `%APPDATA%\AL-76` and `%LOCALAPPDATA%\AL-76`: delete those folders too
to remove everything.

### Updating

Download the newer installer and run it: it replaces the program and keeps your settings and memory.

## What it does

| Area | What you can ask |
|---|---|
| Voice | Say the call ("Hey Al" / "Ehi Al"), then the request; the call alone beeps and listens. Whisper (whisper.cpp) listens, Windows or Kokoro voices answer. "Stop" / "basta" interrupts. Ctrl+Alt+T opens a writing bar under the nucleus. |
| Brains | Claude Code, Codex (ChatGPT) or Gemini CLIs with your own subscription, or a local model through Ollama. Instant commands (time, date, timers, agenda) need no model. |
| PC control | Open and close apps, volume, media, dark mode, files and PDFs (search, merge, extract, rename), read and use other apps' windows (UI Automation), look at the screen. Password managers, banks, Windows Settings and terminals are off limits. |
| Notes | An index of your notes (PDF, Word, PowerPoint, text, scanned pages through Windows OCR) answers questions about them. |
| Organizer | Calendar, reminders and to-dos per area (study, work, personal…), on this PC or on a Microsoft or Google account; mail and contacts of the chosen account; morning briefing; alerts 15 minutes before appointments. |
| Study and work | Time tracking with breaks and reports, study plan with exams and daily suggestion, grades and weighted average, flashcards with spaced repetition, class timetable (dictated, from a PDF/photo or an ICS link), meeting notes, evening summary. |
| Writing and documents | Dictation for brainstorming ("write down everything I say") into a PDF, tidied copy on request; PDFs from text or from a web page; diagrams as a page in the browser; projects and code (read, comment, edit with backup). |
| News and home | Headlines from RSS feeds you choose, ranked by your topics, never repeated; a Samsung TV on the home network (on, off, keys, apps). |
| Safety | Optional voiceprint (only your voice can call AL-76), background talk from a TV or film skipped before Whisper, Windows Hello after the spoken "yes" for programs and for forgetting memories. |
| Modules | **Maker**: Arduino (arduino-cli), Bambu Lab printer (status in LAN mode; filament load/unload and prints through Bambu Studio's buttons, read by OCR, the print only after a "yes"), ROS 2 through rosbridge (topics, the lab's commands and state), Robot Console API ("go home", and "stop" without the call while the arm moves). **Phone**: the iPhone/iPad app talks to the PC as it does with the Mac, finds it on the home network and can show and control the PC screen. **University portal**: read-only lessons and career from a student-app server set in the settings, or transcript, averages and exam calls from Esse3 (Cineca), the system of most Italian universities ("Test the connection" in Settings). |

## Requirements

- Windows 10 2004 or later, Windows 11 recommended; x64 or Arm64.
- Microsoft Visual C++ Redistributable (the Store and winget packages bring it; the classic installer can run it).
- WebView2 runtime (part of Windows 11).
- For the brains: at least one of Claude Code, Codex, Gemini CLI signed in, or Ollama. The setup shows how.
- Optional: Arduino IDE 2, a Bambu Lab printer in LAN mode, ROS 2 with rosbridge (WSL2 or another computer).

The first start measures the PC (RAM, cores, GPU) and picks a tier (Base, Medium, High) that decides the Whisper
models and the local model; models are downloaded at first need into `%LOCALAPPDATA%\AL-76\models`.

## Privacy

- Settings, memory, logs: `%APPDATA%\AL-76`. Account tokens and passwords are encrypted with Windows DPAPI in
  `%APPDATA%\AL-76\segreti` and are sent only to their own service.
- Account sign-in happens in the browser (OAuth with PKCE): AL-76 never sees the password.
- Sending e-mail and purchases always ask for a "yes"; the "Careful" level asks for messages and deletions too.
- The phone link listens only on the home network and Tailscale, encrypted with a key derived from the pairing
  code, which never travels over the network.
- **Export diagnostics** (tray menu) writes a zip to the desktop without personal data: quoted phrases, e-mails,
  addresses, user folders and names are removed, and the action log is not included.

## Build it yourself

All the source of the Windows version is in this folder, under the repository's [MIT licence](../LICENSE): you can
change it and make your own installer. Everything below happens on a Windows 10/11 PC.

### 1. What you need

```powershell
winget install Microsoft.DotNet.SDK.10      # .NET 10 SDK (build and tests)
winget install JRSoftware.InnoSetup         # Inno Setup 6 (the installer)
winget install Git.Git                      # to download the code (or use "Download ZIP" on GitHub)
```

Visual Studio 2022/2026 or VS Code with the C# extension is handy for editing (`windows\AL76.sln`), but not required.

### 2. Get the code

Copy the address from the green **Code** button at the top of this repository on GitHub, then:

```powershell
git clone <the address you copied>
cd AL-76_ex-JARVIS
```

Without Git: **Code › Download ZIP**, extract it, and open PowerShell in the extracted folder.

### 3. Build the app and the installer

```powershell
.\windows\scripts\build.ps1
```

It runs the tests, builds a self-contained app (no .NET needed on the PC that installs it) into
`windows\out\win-x64` and the installer `windows\out\AL-76-Setup-0.1.4-x64.exe`. If PowerShell refuses to run
scripts, start it once with `powershell -ExecutionPolicy Bypass -File .\windows\scripts\build.ps1`.

| Option | What it does |
|---|---|
| `-Arch arm64` | for Arm64 PCs (Snapdragon) instead of x64 |
| `-SkipTests` | skips the tests (faster while you try changes) |
| `-NoInstaller` | only the app in `windows\out\<rid>`: start `AL-76.exe` from there |
| `-AppName "My AL"` `-Publisher "Your name"` `-AppId <GUID>` | your own installer, see below |

To try a change without installing: `dotnet run --project windows\src\AL76.App`, or `-NoInstaller` and start
`windows\out\win-x64\AL-76.exe`.

### 4. Your own installer

With the defaults the installer is the official AL-76: installing it replaces AL-76 on that PC. For your own
version give it another name and id, so it installs next to the official one and updates only itself:

```powershell
$id = [guid]::NewGuid()          # once: write it down and reuse it for every later version
.\windows\scripts\build.ps1 -AppName "AL-76 Lab" -Publisher "Your name" -AppId $id
```

The result is `windows\out\AL-76-Lab-Setup-0.1.4-x64.exe`, installed in `%LOCALAPPDATA%\Programs\AL-76 Lab` with
its own Start menu entry and uninstall entry. The version number is in `windows\Directory.Build.props` and
`windows\packaging\AL-76.iss` (`AppVersion`). Both versions keep their settings in the same `%APPDATA%\AL-76`
folder; the installer is not signed, so Windows SmartScreen warns as for the official one (sign it with your own
certificate if you distribute it).

### 5. Where to change what

| To change | Look in |
|---|---|
| Voice commands answered without a model | `src/AL76.Core/Commands` (`DirectCommand.cs`, `InstantCommands.cs`) |
| The tools the brains can use | `src/AL76.Core/Tools` (one `ToolDef` per tool; `ToolRegistry.cs` registers them) |
| Settings pages and texts | `src/AL76.App/Pages.cs`, `src/AL76.Core/locales/it.json` and `en.json` |
| What needs a "yes" | `src/AL76.Core/Safety/ConfirmationPolicy.cs` |
| Modules (Maker, phone, university portal) | `src/AL76.Core/Modules`, `src/AL76.Core/Maker`, `src/AL76.Core/Portals` |
| The sphere and the nucleus | `web/hud` (three.js), `src/AL76.App/NucleusWindow.cs` |

Add a test in `tests/AL76.Core.Tests` for what you change: `dotnet test windows\tests\AL76.Core.Tests`.

### Other ways to package

- **On the Mac** (how the official builds are made): `windows/scripts/build.sh x64` builds the app with the .NET
  SDK for macOS (`all` for x64 and Arm64); the installer step still needs Windows.
- **Microsoft Store / MSIX:** `windows/packaging/make_msix.ps1 -Rid win-x64` (needs the Windows SDK). The manifest
  declares the Visual C++ runtime, the firewall rule for the phone link (private networks only) and the start-up
  task. `-TestCertificate` signs it with a local certificate to try it.
- **winget:** drafts in `windows/packaging/winget`; fill in publisher, address and SHA256 of the release.
- Icons and logos: `python3 -I windows/packaging/make_icons.py`.
- A `dev.flag` file next to the exe enables the development tools; `build.ps1` and the installer leave it out.

## Layout

| Folder | Content |
|---|---|
| `src/AL76.Core` | Everything without Windows APIs: assistant, tools, brains, voice loop, organizer, study, modules, phone protocol. Built and tested on the Mac too. |
| `src/AL76.Platform` | Windows APIs: hardware, microphone, audio, UI Automation, DPAPI, OCR, start-up. |
| `src/AL76.Speech` | Whisper models and transcription. |
| `src/AL76.App` | The WPF app: tray, HUD sphere (three.js in WebView2), nucleus, settings, setup, phone host. |
| `tests/AL76.Core.Tests` | xUnit tests of the core (`dotnet test windows/tests/AL76.Core.Tests`). |
| `packaging` | MSIX manifest, scripts, Inno Setup, winget drafts, logos. |
| `scripts` | `build.ps1` (Windows: tests, app, installer), `build.sh` (the same on the Mac, without the installer). |
| `web/hud` | The sphere and the nucleus drawn in WebView2 (three.js). |

## Licences of the parts

whisper.cpp / Whisper.net (MIT), KokoroSharp and Kokoro 82M (Apache-2.0), sherpa-onnx (Apache-2.0) with
NVIDIA NeMo TitaNet small (CC-BY-4.0, downloaded at the first voice enrolment), ONNX Runtime (MIT), PdfPig (Apache-2.0),
Microsoft.Data.Sqlite (MIT), BouncyCastle (MIT), QRCoder (MIT), three.js (MIT), WebView2 (Microsoft).
No cloned voice, no third-party 3D model and no personal wake-word files are part of the distribution.
