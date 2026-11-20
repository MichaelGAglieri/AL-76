# Map Swift (Mac) → C# (Windows)

Every Swift file of the Mac app and where it lives on Windows. Status: `—` not started, `◐` partial, `✓` done.
The Windows version is generic (for everyone): "generic" says what stops being personal.

| Swift (Sources/JARVIS) | C# (windows/) | Phase | Status | Generic |
|---|---|---|---|---|
| Core/Config.swift | Core/Config/AppConfig.cs | 0 | ◐ | name, wake phrase, profile, areas, modules, language |
| Core/Diagnostics.swift | Core/Diagnostics/Log.cs | 0 | ◐ | |
| Core/MemoryGuard.swift, SystemWatch.swift | Platform (RAM, sleep) | 0–4 | — | |
| (new) | Core/Hardware/TierPolicy.cs, Platform/WindowsHardware.cs | 0 | ✓ | Base / Medio / Alto levels |
| (new) | Core/Localization, Core/locales/*.json | 0 | ◐ | Italian + English |
| (new) | Core/Modules/Modules.cs | 0 | ◐ | Maker, Phone, university portal off by default |
| App.swift | App/App.xaml.cs | 0 | ◐ | tray icon, single instance; runs in the Windows 11 ARM VM (2026-11-16) |
| Audio/AudioInput.swift, VoicePipeline.swift, BackgroundTalk.swift | Platform/Microphone.cs, Core/Voice/VoiceActivityDetector.cs, VoiceLoop.cs, App/VoiceHost.cs | 1 | ◐ | adaptive end-of-phrase silence; "basta" while speaking (BargeIn); Windows echo cancellation via WASAPI communications mode, falling back to plain capture |
| AI/Transcriber.swift, WakeVerifier | Speech/WhisperTranscriber.cs, ModelStore.cs, Core/Voice/WakePhrase.cs, Hallucinations.cs | 1 | ◐ | wake phrase from config; needs the VC++ runtime on Windows |
| Audio/SpeechEngine.swift, KokoroTTS.swift | Platform/WindowsVoice.cs, AudioOut.cs; Speech/KokoroSpeaker.cs (KokoroSharp + MisakiSharp, no Python); App/KokoroVoice.cs | 1 | ◐ | Windows voice on Base, Kokoro on Medio/Alto; voice chosen from samples; "basta" via Core/Voice/BargeIn.cs; sentence streaming (Core/Voice/Sentences.cs, IVoice.StreamAsync); int8 model rejected (slower on CPU) |
| Core/VoicePrint.swift | Core + ONNX | 1 | — | optional |
| Audio/Porcupine.swift | not needed | — | — | |
| Core/Assistant.swift | Core/Assistant.cs | 2 | ◐ | router: pending yes/no, time/date, "ricordati che", instant actions through the tools, then the brain; follow-up answers without the call (VoiceLoop) |
| Core/InstantCommand.swift, DirectCommand.swift | Core/Commands/InstantCommands.cs, InstantAction.cs, DirectCommand.cs, When.cs | 1–2 | ◐ | time, date, apps, volume, music, timers, areas; direct: ask timer/reminder, reminders with date rules, weather, battery, math, folders, dark mode, screen off (it + en); mail, calendar, sessions, booklet come with phases 5–6 |
| Core/ConfirmationPolicy.swift, Security.swift, Acknowledgment.swift | Core/Safety/ConfirmationPolicy.cs, Security.cs, Core/Commands/Acknowledgment.cs | 2 | ✓ | Essential / Careful levels by risk flags; Windows executable types; acknowledgment with the chosen form of address (none by default) |
| AI/Persona.swift | Core/Brains/Persona.cs | 2 | ✓ | generic: assistant name, user, profile, "about me", areas, honorific (none by default) |
| AI/Tools.swift | Core/Tools (ToolRegistry, CoreTools, IPcActions) + Platform/WindowsPcActions.cs | 2–7 | ◐ | memory, areas, timers, apps (shell:AppsFolder), volume, music (SMTC), web pages; the rest in later phases |
| AI/MCPServer.swift | Core/Mcp/McpServer.cs | 2 | ✓ | own minimal server on a TCP socket (HttpListener needs admin on 127.0.0.1); token, Origin/Host checks |
| AI/ClaudeBridge.swift, CloudBrains.swift, Ollama.swift | Core/Brains (Claude, Codex, Gemini, Ollama), Core/Voice/SentenceSplitter.cs | 2 | ◐ | same limits as the Mac; sentence streaming, "brief" actions, fallback to the local model, recap on a new session; Core/Brains/BrainCatalog.cs: official Windows installers and sign-in in a visible PowerShell window; Core/Commands/BrainSwitch.cs: "usa ChatGPT", "chiedi a Claude…", "usa Opus" |
| HUD/Onboarding.swift, SettingsView.swift | Core/Setup/SetupFlow.cs, App/SetupWindow.cs, App/SettingsWindow.cs, App/Pages.cs, App/Theme.xaml | 3 | ◐ | setup for anyone: language → you (profile) → study/work → areas → brain (install and sign-in) → permissions (microphone, notifications, start with Windows) → voice and call → modules → ready; settings in pages saved at once; checked in the VM on 17 November |
| HUD/Reactor.swift, HUDView.swift (sphere, HUD window) | web/hud/sphere.js, web/hud/hud.html, App/HudWindow.cs, Core/Voice/HudState.cs | 3 | ◐ | same seed and geometry as the Mac (69,163 filaments), full sphere on every PC (user's choice); transparent 440×472 window, drag, right-click menu, Esc; states and levels from the voice loop; checked in the VM on 17 November (2.3 fps there: no GPU, software WebGL) |
| HUD/Satellite.swift, CompactReactor.swift, Reactor.swift (classic) | web/hud/satellite.js, web/hud/reactor.js | 3 | ◐ | same parts, materials, textures and motion as the Mac; mean colours within 3% (satellite) and 2% (reactor) of frames rendered by the Mac's own code |
| HUD/CompactReactor.swift (ReactorPanel), App.swift (showCompact, openHUD) | App/NucleusWindow.cs, App/WebLayer.cs, web/hud/nucleus.html, Core/Voice/Nucleus.cs | 3 | ◐ | sizes per style, satellite and robot float (hidden over full-screen apps), reactor behind the windows, click opens the sphere around it, drag within the screen, clicks pass around the nucleus, robot walks on the bottom edge (RobotWalker), "Aspetto" settings page; one nucleus only (Mac: one per display), no zoom animation |
| HUD/Robot/RobotKit.swift, RobotGLTF.swift, RobotNucleus.swift | web/hud/robot.js | 3 | ◐ | same rig, face, flames and effects; within 1% of the Mac in all eight test states; the model is loaded only from the user's data folder (%APPDATA%\AL-76\Companion), never shipped |
| RobotKit.swift (Bubble, BubbleView), RobotNucleus.swift (bubble, menu) | Core/Voice/SpeechBubble.cs, App/BubbleWindow.cs, App/NucleusWindow.cs | 3 | ◐ | same typing speed, reading time, fade, side choice and tail; spoken answers in the bubble from the robot's menu (VoiceLoop.ReplyGrowing); typed answers when the writing bar exists; drawn natively in WPF, not yet seen in the VM |
| HUD/*.swift (bubble, panels) | App + web/hud | 3 | — | three.js in WebView2, chosen from previews first |
| Actions/AppControl.swift, MacActions.swift, AppIndex.swift, HomeAccess.swift | Core/Tools/AppControl.cs + AppTools.cs (rules), Platform/WindowsAppDriver.cs (UI Automation) | 4 | ◐ | blocked apps and pages for Windows; not yet tried on a real PC |
| Actions/VisionActions.swift, Core/ScreenShare.swift | Platform/Capture | 4, 7 | — | |
| Actions/PDFTools.swift, Booklet.swift, Personal/NotesIndex.swift, Dictation.swift | Core/Tools/FileTools.cs (search, open, rename, PDF with PdfPig), Core/Notes/NotesIndex.cs (SQLite FTS5 + bge-m3), Platform/WindowsPageReader.cs (Windows OCR); booklet and dictation still to do | 4 | ◐ | notes folders chosen in the settings |
| Actions/MailActions.swift, MailStore.swift, CalendarActions.swift | Platform/Accounts (Microsoft, Google) | 5 | — | areas |
| Core/Initiative.swift | Core/Initiative | 5 | — | follows area and profile |
| Personal/StudyPlan.swift, Flashcards.swift, ActivityTracker.swift, ProjectInsights.swift | Core/Study, Core/Work | 6 | — | any course, any job |
| Actions/UniversityClient.swift | Portals/UniversityPortal | 6 | — | one portal module among many |
| Actions/NewsFeed.swift, WebActions.swift, TVControl.swift | Core/Web/WebActions.cs (+ news later) | 2–6 | ◐ | search (ads dropped), page reading, weather (named city kept) done; news sources chosen, TV later |
| Robotics/*.swift | Modules/Maker | 7 | — | |
| Core/PhoneBridge.swift, PhoneLink.swift | Modules/Phone | 7 | — | |
| Core/OwnerCheck.swift | Platform (Windows Hello) | 4 | — | |
| Core/HotKey.swift, HUD/TypeBar.swift | App | 3 | — | key chosen |
