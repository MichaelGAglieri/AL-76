# Changelog

Every version was built, installed and tested on the Mac (and on the iPhone and iPad app when it was
involved) before moving to the next one. All dates are in 2026.

## 1.30.77 — 19 November

- **ROBOTTINO locked in the corner** (user request): it stays still in the bottom right corner, no more strolls, and dragging does not move it (a click is still a pat, a double click the HUD). If it is elsewhere it flies there on its jets, without walking. Three ways to turn it on and off: its right-click menu ("Blocca nell'angolo in basso a destra", with a tick), by voice without the model ("Al, stai fermo", "resta nell'angolo", "smetti di camminare" / "Al, puoi camminare", "sblocca il robottino", which also sets it strolling again) and Settings › Appearance › Robottino. The phrases do not overlap the robot arm's emergency stop ("fermati", "ferma il robot"). Tested in the app: from x 941 it flew to 1345 (the corner) and stayed there for two minutes; unlocked, it went back to strolling.

## 1.30.76 — 19 November

Less energy while AL-76 waits for "Ehi Al" (energy check: in the week of 12-19 November AL-76 used about 29.5 Wh, more than half on the Neural Engine, that is Whisper listening).

- **Voice filter before Whisper.** In three days of logs 1983 of 2042 wake checks were not "Ehi Al", and more than half came out of Whisper as "Grazie.", empty or "[Musica]": there was no voice at all. Now macOS's built-in sound classifier (SoundAnalysis, nothing to download) looks at each phrase first: silence, hum, keys, music and system sounds (voice 0.01-0.05) skip Whisper; below 0.08 a phrase is not a voice. It costs about 45 ms of CPU instead of 0.9 s of Neural Engine. If the classifier fails, everything goes to Whisper as before; it does not filter while the voiceprint is being enrolled. "Basta" and the robot's "stop" do not go through it.
- **One Whisper pass less.** Short phrases always got a second transcription without the "Ehi Al" hint. It is now skipped when the first pass already understood a clear phrase of three or more words that is not the call.
- diagnostica.log writes "non è voce" with the score for every skipped phrase, and "voce 0.xx" in the "wake OK" and "wake NO" lines, to tune the threshold with real use.

## 1.30.75 — 18 November

- **Thinking robot**: the blue gear above the head and the blue dots leading to it are gone (user request). The rest of the animation is unchanged: head and eyes drifting slowly side to side, the tapping hand.

## 1.30.74 — 17 November

A **Permissions** page in Settings (user request: see everything AL-76 can do and decide what it may do, like professional apps).

- **macOS permissions** with their state refreshed every second: microphone, accessibility, calendar, reminders, contacts, screen recording, camera (also new in the first-run setup), full disk access, automation. Switching one on asks macOS or opens the right System Settings page; switching it off opens System Settings, the only place it can be taken back.
- **AL-76's own abilities**: 17 switches in 6 groups, each with a one-line explanation. A switched-off ability refuses its tools for every brain, voice, the writing bar and the iPhone, and the refusal goes to the security log. The briefing skips mail or calendar when they are off, Claude loses web search and file reading, ChatGPT web search. Stopping the robot, printer status and timers are never blocked.
- Switching on an ability that needs a macOS permission not yet given (e.g. «look at the screen») asks for it at once; if still missing, a warning with «Allow» stays under it.

## 1.30.73 — 17 November

Listening, from a day of logs (the user: «sometimes it does not hear me», and two activations by itself).

- **False «Ehi Al» while people talk.** Out of unclear talk Whisper writes its own hint, «Ehi Al.», and the voiceprint let it through at 0.41 and 0.45 (15:41 and 16:35; the TV on the 16th at 0.46-0.49). A lone «Ehi Al» now needs 0.50 when a phrase without the call was heard in the last 30 s, or when it is quieter than -22 dB. The user's own calls in a quiet room score 0.55-0.72.
- **«Al.» without «Ehi».** Four calls were lost because Whisper wrote only «Al.» or «Al. Apri il pdf…». «Al» followed by a punctuation mark at the start of a phrase now counts as the call («Al secondo passo» does not). The audio kept before a phrase grows from 0.5 to 0.8 s, so a soft «Ehi» is not cut.
- **The TV filter skipped loud voices.** Its bar could reach -10 dB, and the user's calls arrive at -4…-19 dB. Now a phrase louder than -20 dB always goes to Whisper. The log says how loud the loudest skipped phrase was.
- **Empty command after the beep.** Five times Whisper understood nothing of what followed «Ehi Al», and AL-76 went back to waiting in silence. Now it tries again with a looser check, and if it is the user's voice it asks «Non ho capito. Puoi ripetere?» once.
- **Crash at 15:15.** Two microphone rebuilds 8 s apart while a Bluetooth device switched to 8 kHz: on the same engine macOS aborted the app. A second rebuild within 15 s now uses a new engine.
- Privacy mode on and off is written in diagnostica.log too: six minutes without a line (15:41-15:47) looked like a deaf AL-76, and it was privacy mode.

## 1.30.72 — 17 November

- **Right-click menu of the ROBOTTINO, SATELLITE and HUD.** «Mostra barra di scrittura» / «Nascondi barra di scrittura» opens and closes the typing bar without ⌃⌥T. «Mostra dialogo» opens and closes the Frasi panel with the conversation.
- **Speech bubble (ROBOTTINO).** «Mostra nuvoletta» says the last answer again, spoken ones included. «Nascondi nuvoletta» closes it, and that answer does not come back. «Nuvoletta anche per le risposte a voce» (a tick, remembered) puts spoken answers in the bubble too, not only typed ones.

## 1.30.71 — 17 November

- **AL-76 «no longer heard».** Whisper writes «Grazie.» over sound it cannot make out. After three in 45 s AL-76 skipped, for a minute and without Whisper, every phrase not louder than the loudest of them. At 08:36 the three were at -6, -18 and -12 dB: voices, not steady noise. With the bar at -6 dB (so up to -2) nine calls in a row were thrown away. Now the three must be within 10 dB of each other, as a fan or rain is, and a phrase louder than -20 dB always goes to Whisper.
- Checked live before the fix: once the filter was off, «Ehi Al, che ore sono» was understood and answered.

## 1.30.70 — 17 November

Fixes from the logs of 16 and 17 November: AL-76 answered the TV on its own, and one morning its voice was slow.

- **Continuous talk is not a command.** After «Ehi Al» a capture stops at 30 s. Three times (20:00 and 21:59 on 16 November, 01:02 on 17 November with Prime Video on) the TV or people in the room filled all 30 s. With the look-back half second the capture went over the 30 s the voiceprint service accepts, it answered «durata non valida», and on an error AL-76 let the phrase through: Claude answered a film line and then kept talking with the film. Now a command or answer of 29 s or more is ignored as continuous talk, and the voiceprint never gets more than 30 s.
- **Answers after a doubtful call.** Whisper, given the hint « Ehi Al.», sometimes writes just «Ehi Al.» over unclear sound. At 01:02 one of these passed the voiceprint by little (0.419 with threshold 0.40), and then a line from the film passed as an answer (0.385 with the answer threshold 0.33). When the call scored below 0.45 in the last three minutes, answers now need 0.40.
- **«Basta» skips the morning briefing.** On 17 November «Ehi Al… basta» on the first call of the morning stopped nothing, and the briefing, waiting for AL-76 to be idle, started at once. A «basta» now cancels today's briefing; it can still be asked for.
- **The local model waits for the voice.** Just after the Mac woke, Wi-Fi took 65 s to reconnect. Thinking it was offline, AL-76 loaded the local model as a fallback (64 s) while it spoke, and the first piece of voice took 8.2 s instead of about 0.4. With a cloud brain the fallback now waits until AL-76 has finished speaking and, after a wake, up to 90 s for the network; it loads only if still offline.
- Installed and started; not yet tested live with the TV or on a wake from sleep.

## 1.30.69 — 16 November

- **Gemini goes through Antigravity CLI.** With the Gemini CLI the Google sign-in worked, but every request was refused: «This client is no longer supported for Gemini Code Assist for individuals». Since 18 June 2026 Google no longer serves it to personal accounts, free or AI Pro, and points to Antigravity CLI (`agy`) instead. AL-76 now uses it in headless mode (`agy -p --output-format stream-json`), with the conversation resumed for 30 minutes (`--conversation`). It works in its own folder, Application Support/JARVIS/antigravity, with AL-76's MCP server in `.agents/mcp_config.json` and the instructions in `AGENTS.md`.
- **Safety.** In testing, on its own, Antigravity ran "safe" commands (`ls ~`) and wrote files in its working folder. Rules can only go in its global settings, so the user chose to add to `~/.gemini/antigravity-cli/settings.json` the allow rule `mcp(jarvis/*)` and the deny rules `command(*)`, `write_file(*)`, `read_url(*)` and `execute_url(*)`. Test repeated: command and file blocked («Matches user-configured deny rule»), AL-76's tools allowed. Without these rules AL-76 does not use Gemini and answers with the local core. In Settings › Brain the button «Prepara Antigravity per AL-76» adds them and leaves the rest of the file as it is.
- **Tools.** Antigravity calls MCP tools through one generic tool (`call_mcp_tool`): AL-76 reads the real name inside it, for the audit log, the «thought» panel and the rule that stays quiet during actions. App control works with it too. The audit log says «gemini».
- **Tested live.** From the writing bar, Gemini 3.8 Flash read the calendar and saved a memory with AL-76's tools (32.7 s the first time). On the next question it remembered the conversation (13.9 s). The test memory was removed afterwards. It is slower than Claude and ChatGPT.

## 1.30.68 — 16 November

- **ChatGPT and Gemini as brains, besides Claude.** Anyone with a ChatGPT subscription or a Google account uses them like Claude, with no paid API, through their CLIs: OpenAI's Codex (`codex exec`) and Google's Gemini CLI (`gemini -p`). They get the same instructions and the same AL-76 tools, from the same local MCP server. The server's token travels in an environment variable and is never written to disk or put on the command line.
  - **Same safety as Claude:** no terminal and no file edits. Codex runs with the shell off and a read-only sandbox, and approves automatically only AL-76's tools. Gemini has only reading and web search among its own tools, and AL-76's server as trusted. The two CLIs' personal settings are ignored. App control, before only for Claude, works with all three cloud brains, never with the local model. The audit log says which brain acted.
  - **Conversation memory:** as with Claude, the session goes on for 30 minutes (`codex exec resume`, `gemini --resume`).
  - **You choose** in Settings › Brain and in the first-run setup: for each one you see whether it is installed and signed in and, if not, what to type in Terminal. Also from AL-76's menu, from the four HUD buttons (LOCALE, CLAUDE, GPT, GEMINI) and by voice: «usa ChatGPT», «passa a Gemini», «torna al nucleo locale». Whisper's spellings "chat GPT", "ciat gpt" and "Gemelli" count; "Gemma" stays the local model.
  - **If the chosen brain is not signed in or does not answer,** AL-76 says so («Gemini non è collegato, signore») and answers with the local core, as it already did with Claude.
  - **Tested on the Mac:** ChatGPT, from the writing bar, searched the web and read the calendar with AL-76's tools (8.2 s), and on the next question remembered what it had been told. Gemini is installed but not yet signed in with Google: without a sign-in AL-76 warns and switches to local in 1.4 s.

## 1.30.67 — 16 November

- **First-run setup.** Whoever downloads AL-76 finds a HUD-style window that guides them in eight steps, instead of settings scattered between the app and INSTALL.md:
  - **Welcome:** who AL-76 is, where the name comes from, what stays on the Mac.
  - **About you:** first name, full name, how to be addressed (no title, signore, signora or your own), one line about yourself and your interests. They go into the model's instructions; the name also goes into the words Whisper must spell right.
  - **Study:** subjects with exam date and weekly hours (they go into the study plan), notes folders chosen in the Finder, university.
  - **Brain:** whether Ollama and the local model are there, whether Claude Code is installed, and which one to use. ChatGPT and Gemini are already listed as "coming".
  - **Permissions:** microphone, Accessibility, calendar, reminders, contacts and screen recording, one per row with its purpose and a button. The tick appears as soon as the permission is given; after a "no" the button opens the right System Settings pane.
  - **Voice:** progress of the Whisper preparation, a test of «Ehi Al» with a «Ti ho sentito!» confirmation, and voiceprint enrolment.
  - **The rest:** look (reactor, satellite, robottino), iPhone link, morning briefing, greeting, launch at login.
  - **Ready:** a summary and phrases to try. At the end AL-76 introduces itself out loud.

  Each step is saved on the way forward, so closing halfway loses nothing. «Salta, configuro dopo» closes at once. On first launch AL-76 waits for the microphone to be granted from the setup, instead of showing the macOS request straight away with no explanation, and does not greet until the setup is finished. Anyone who already had a config.json does not see it by itself; it reopens from the menu with «Configurazione guidata…».
- **Settings in the same style.** Dark HUD background and cyan accent, with no grey title bar. On the left, a column of eleven pages (Profile, Activation, Voice, Brain, Study, App control, Initiative, Look, iPhone, Printer, System), each with a title and a line on what it holds. At the bottom, Save and, when there are changes, «Modifiche non salvate» and Cancel. New: the Profile page, with title, line about you, interests and words for Whisper, which before could only be changed in config.json.

## 1.30.66 — 16 November

- **WhatsApp sends messages asked from the writing bar again.** The bar (⌃⌥T) takes the keyboard without making AL-76 the active app: while it is the key window, the keys AL-76 posts land there even with WhatsApp in front. So "Buonasera" and Return reached the bar instead of the chat, while by voice it worked (in the morning, by voice, the message had gone). Now, before typing, pressing keys or clicking in another app, AL-76 makes its own windows give up the keyboard, and types only if the right app really has it. The bar stays open with the answer. Tested with a panel like the bar and TextEdit: before, the text went into the panel; now it goes into the document.
- **No more small hole next to the bubble's tail.** Bubble and tail were one path: with the tail pointing the other way the two directions cancelled out and left a small transparent triangle near the corner. Now they are filled separately in one layer with the same transparency, with no holes or seams, at every tail position (each one checked).

## 1.30.65 — 16 November

- **The ROBOTTINO's speech bubble always stays on screen, and its tail follows the mouth.** The bubble's side used to be chosen once per sentence, so when Al then moved it went past the edge. Now it changes side only when it no longer fits, and its position is always corrected to stay inside the screen. The tail leaves the side of the bubble and reaches the mouth's height wherever the robot is: from the lower corner when the bubble is above the mouth (the usual case), from the upper corner or the side when the screen pushes it lower.
- **No line cut in half.** The bubble is a whole number of lines tall (up to half the screen, at most 260 points), and a long answer scrolls by whole lines.
- **It no longer comes back.** After fading out, on the next frame the same answer reopened it and typed it again from the start, so it seemed never to go away. Now an answer already read does not return unless new text arrives, and then it goes on from where it had got to. It stays up long enough to read it (about 18 characters a second), between 3 and 20 seconds.
- **Docs:** the READMEs have a "From JARVIS to AL-76" section, with why the name changed and the versions (JARVIS up to 1.30.61, tag `jarvis-1.30.61`; AL-76 from 1.30.64), and one on the ROBOTTINO, with the link to the model on Sketchfab and the credit its CC-BY 4.0 licence requires.

## 1.30.64 — 16 November

- **JARVIS becomes AL-76.** The project is now the user's own, and they no longer wanted Iron Man's name. The new name comes from Isaac Asimov's story *Robot AL-76 Goes Astray* (1942): a robot built for mining on the Moon gets lost on Earth and, in a shack, builds itself a disintegrator out of the scrap it finds. Randolph Payne, the man who keeps it, simply calls it "Al".
- **It is called with "Ehi Al".** The check (WakeVerifier) looks for the call at the start of a phrase, after a punctuation mark or after words like "ok" and "senti". It also takes the ways Whisper writes it: "Hey Al", "Eyal", "Eial", "Ehi, altri", and "E al" (only at the very start, since "e al mare" inside a sentence is not a call). Whisper is given " Ehi Al." as a hint: in a test with five Italian voices of the Mac this took the call from 1–2 phrases out of 5 to 4 out of 5. None of the 1,820 phrases heard in the room until then would have been a false wake. First live test: "Ehi Al, che ore sono?", written by the large Whisper as "Eyal, che ore sono?", answered instantly. "Jarvis" no longer wakes anything; ⌃⌥Space works as before.
- **The new name wherever it is seen or heard:** the app (/Applications/AL-76.app), menus, HUD, satellite, ROBOTTINO, typing bar, panels, settings, permission texts, spoken sentences, the model's instructions (it introduces itself as AL-76, "Al", from Asimov's story), the ~/Desktop/Schemi AL-76 folder, the program name in Robot Console. The voice reads "AL-76" as "Al 76". Only the sentences that held the name were rewritten: every other sentence stayed as it was.
- **iPhone and iPad app:** Home Screen name, texts, the "Parla con AL-76" shortcut, and listening for "Ehi Al" with Apple's recognition, with the same rules as the Mac.
- **Internal names stay "jarvis",** because changing them would lose permissions and data: bundle id, data folder, Keychain, iPhone pairing, the jarvis:// scheme, the MCP server, the name the TV knows the app by. The same goes for Porcupine, which only knows "Jarvis": "Ehi Al" would need a keyword trained on Picovoice. Same signature and bundle id, so macOS keeps the permissions already granted; the login item is registered once more for the new path.
- **Voiceprint to record again:** the old one was built on "Jarvis" and was moved to the Trash at the user's request. Guided enrolment now asks for "Ehi Al" and keeps only the phrases that hold it.

## 1.30.63 — 16 November

- **A third look for the NUCLEUS: the ROBOTTINO.** Besides reactor and satellite, a small 3D robot that floats on jet flames from its backpack and its feet (fire or ion colour). Its face is drawn live on its screen, and it acts out JARVIS's states:
  - waiting: looks around, waves now and then;
  - listening: leans in with hands together, eyes a little larger, a soft smile;
  - thinking: a hologram gear drifts above its head, bubbles lead to it, and head and eyes follow it while it taps its fingers;
  - speaking: the mouth follows the real voice;
  - error: X eyes, smoke and sputtering engines.

  Mouse and window:
  - only the model catches the mouse: around it clicks go through to the windows below;
  - a click is a pat (it beams and waves), a double click opens the HUD, a drag moves it.

  Optional, in Settings › SATELLITE:
  - it lands on the bottom edge of the display and lives in the bottom right corner. While JARVIS waits it takes a stroll along the edge now and then (about once a minute, every few minutes in deep idle), looks around and walks back, engines off, at a speed taken from its stride so the feet do not slide. It never sleeps;
  - the larger size.

  A gesture turns the edge walk on and off: pull it away from the edge and it flies free; drop or throw it towards the bottom right and it lands and walks again.

  It has its own voice: young, calm and friendly, chosen by the user from samples. It was designed with Qwen3 VoiceDesign from a description and is cloned at runtime by the same Qwen3 0.6B as JARVIS's voice (`Resources/voce_robottino.wav`, a synthetic voice), so it keeps the same fluency, pronunciation and streaming. The voice server takes `"voice": "qwen3:robottino"`; saved sentences are kept apart for each voice, and JARVIS's own are still valid. With the reactor or the satellite JARVIS speaks with his usual voice.

  Answers to typed questions (⌃⌥T) come out of its mouth in a comic speech bubble beside its head. The arms can never pass through the head or the body: a limit checked by a test over every state.

  The 3D model is not part of JARVIS: it is a glTF file the user puts in `~/Library/Application Support/JARVIS/Companion` (the rig follows the parts of "Mini Robot" by Onur on Sketchfab, CC-BY 4.0). Without it, the satellite is shown.

## 1.30.62 — 16 November

- **JARVIS draws diagrams.** On 16 November "crea uno schema del pendolo semplice" got "non ho uno strumento per generare immagini". New tool `draw_diagram`: Claude draws the diagram as SVG (forces, components, angles, axes, legend) and writes the steps from the drawing to the formulas; JARVIS puts them on one page, saves it in ~/Desktop/Schemi JARVIS and opens it in the browser. The page fetches nothing from the internet: scripts, embedded HTML, event handlers, outside links and images are removed from the SVG, and the page forbids anything else. First diagram: the simple pendulum (T, mg, mg sinθ, mg cosθ, why −mg sinθ along the tangent and T − mg cosθ = mv²/L along the thread, small oscillations).

## 1.30.61 — 16 November

- **Controlling the Mac from iPhone and iPad like a trackpad.** With control on, the screen stays still and one sliding finger only moves the cursor, with no button pressed: no more text selected by mistake or screen moving. To drag (select text, move windows) press and hold for a moment, feel a short tap, then slide; holding without moving is a right click. A tap clicks, two taps double click, two fingers scroll the Mac and pinch to zoom.
- **Moving the view.** A new hand button next to the control button makes one finger move the zoomed view instead of the cursor; three fingers also move the view. Tested on the simulator against the real screen: the cursor follows the finger with no click and the zoomed view stays put; dragging, right click and three fingers were not tried there.
- **Safety.** A gesture the Mac does not know is ignored; it used to become a click.
- **Installation guide.** [`INSTALL.md`](INSTALL.md): what you need, your Apple account and Team ID in Xcode, `local.env`, the setup, permissions, your own settings, installing on iPhone and iPad (Developer Mode, trusting the developer), pairing, and the 7-day expiry of the free account.

## 1.30.60 — 16 November

- **The sphere has the satellite's menu.** With the sphere open, a right click (two-finger tap on the trackpad) opens the same menu as the SATELLITE: Parla, Scrivi (the bar opens under the sphere), Nuova conversazione, Riduci a satellite, Esci da JARVIS. A double click still reduces it to the satellite.
- **Blue icon.** Instead of the near-black tile, a vivid blue, brighter in the middle, with the current neural sphere (no chart screens) rendered by the HUD's own engine on a transparent background.
- **University settings for any university.** Settings › Università takes the university's name, the server address of its student app and, if needed, the app's User-Agent; they live in config.json, so no university is named in the code. Username and password stay in the Keychain (one item per university). The tool is now called `university`.

## 1.30.59 — 16 November

- **The university booklet.** The university's student-app server has no booklet, and the student portal that shows it asks for the authenticator code at every access, so JARVIS cannot read it by itself. It was read once from the portal with the user signed in and saved on the Mac only (`libretto.json` in JARVIS's folder, never on GitHub): every course of the plan with CFU, year and, once passed, grade and date. From then on the user says the new grades: "ho passato fisica uno con 28", "ho preso 30 e lode a chimica", "segna fisica 2 24", "togli il voto di …". Averages are worked out as on the portal (arithmetic and CFU-weighted, "Idoneo" counts for credits only, degree base out of 110).
- **Questions answered without the model:** "che esami ho dato", "che esami mi mancano (del secondo anno)", "qual è la mia media", "quanti crediti ho", "con che voto mi laureo". An exam name can be partial ("analisi", "controlli", "fisica uno"); when it fits two courses ("fisica", "meccanica") JARVIS asks which. The `university` tool gives Claude the whole booklet and can record a grade. Checked on 5172 phrases from the logs: none taken by mistake.
- **Liquid neural sphere with more axons.** While waiting the sphere undulates like a liquid: two waves compress and widen the surface, the outline ripples and the sphere stretches like a drop along a turning axis, so every side of the edge moves. While speaking the same undulation, stronger, adds to the pulse and the voice's wave. After a minute of waiting it slows down with the rotation. Each neuron is also joined to its 4th and 5th nearest: 51 more axons, with their travelling pulses, dimmer the longer they are.

## 1.30.58 — 16 November

- **Lessons and agenda live from the university.** JARVIS signs in to the server of the university's student app with the username and password the user types in Settings › Università, where the university's name and server address are also set (kept in config.json, never in the code); username and password stay in the macOS Keychain, with the access pass. Read only: only an allow-list of reading addresses can be called; enrolling, signing records and accepting results are not possible. "Che lezioni ho oggi / domani / giovedì / questa settimana", "dove ho lezione domani" are answered without the model in about a second; the `university` tool also gives Claude enrolled lessons, attendance, results to accept and messages.
- **A lesson that disappears or moves is announced.** Every two hours (7–22) the next two weeks are compared with the last look: "Non è più in agenda la lezione di … : potrebbe essere saltata", "La lezione di … è cambiata: ora … aula …". Tested on the real agenda with one lesson removed and one moved.

## 1.30.56 — 16 November

- **News are not repeated.** On 16 November JARVIS gave nearly the same news all day: news already given were left out only after "altre notizie", and the list was forgotten at every restart. Now every news given is remembered for 4 days (also after a restart) and is never given again, nor is the same story told by another site; each round has at most one item per story, and when nothing new is left in the usual window JARVIS looks further back instead of repeating. Tested with three news requests in a row: every round new, no story twice.

## 1.30.55 — 15 November

- **A bar to type to JARVIS.** ⌃⌥T (or "Scrivi" in the satellite's menu and in the menu bar) opens a small capsule right under the SATELLITE; it follows the satellite, and with the sphere's window open it sits under the sphere and can be dragged by its keyboard icon (double click puts it back). Return sends, Esc closes, and it leaves by itself after 45 s without use. A typed request follows the same path as a spoken one (instant commands, local model, Claude, confirmations: a "sì" can be typed), is logged with source "tastiera", and is answered in writing under the bar without voice; the speaker icon turns the voice on too. After a typed request the microphone does not reopen. Tested on the Mac with real keystrokes: an instant command, a question answered by Claude, Esc, the shortcut, satellite and sphere. The iPhone and iPad app already had its "Scrivi a JARVIS" field: unchanged.
- **The satellite's right-click menu works again.** Every row came out grey and could not be clicked: macOS asked the satellite's view whether each row was valid and got a no. Checked with a real right click: all five rows are active.

## 1.30.54 — 15 November

- **Alerts are said in privacy mode too.** On 15 November four printer alerts (paused twice, "a problem", finished) were never said because privacy mode was on. Now the microphone stays off but alerts (printer, battery, calendar, reminders, timers) are spoken; questions that need an answer (the forgotten study session) and the morning briefing still wait, and after an alert JARVIS does not listen.
- **Privacy mode really closes the microphone.** The voice and the microphone share one audio engine, and speaking started it again: after "Modalità privacy…" the microphone stayed open (macOS's indicator on) although nothing was listening. Once JARVIS is silent the engine is stopped. Checked with Core Audio: microphone off after the privacy phrase, while a timer was said and after it.
- **Neural sphere without data screens and with a fuller edge.** The eight small chart screens are gone. The outer shell has 900 more nodes, linked to their neighbours in the same style, and three more glowing ribbons: the outline is denser at the top, bottom and sides. Neurons, axons, core and existing ribbons are unchanged.

## 1.30.53 — 15 November

- **Films and TV no longer go through Whisper line by line.** Every phrase heard in the room was checked for "Jarvis": with a series playing, up to 600 an hour, about a second of work each. When several phrases in a row are not for JARVIS it measures how loud that talk reaches the microphone and sends to Whisper only phrases at least 4 dB louder than its loud lines; the level follows the film, so turning it up raises the bar. If the talk is so loud that a voice could not stand out (its loud lines above -14 dB) nothing is skipped, as before; a minute of quiet ends it. Replayed on the 3057 phrases heard on 15 November: 74% skipped, the bar never above -10 dB (the user's voice close to the Mac is about -9…+4 dB). Tested live: after four film lines a quiet "Jarvis" was skipped and a loud one went through. The "wake OK" log line now shows the phrase's peak and the bar.

## 1.30.52 — 15 November

- **Hears again right after opening the lid.** Closing the lid (or sleep, or a microphone reconnection) gives seconds of digital silence, which dragged the background-noise estimate to -120 dB: for the next half minute the room itself counted as voice, so phrases never ended (8-second blocks, two "Jarvis, accendi la tv" merged into one) and the first "Jarvis" after opening seemed lost. Silent frames are now skipped, and from an impossible level the first real one is taken at once.
- **A command said twice counts once.** "Jarvis, accendi la tv, Jarvis, accendi la tv" (repeated because the first seemed lost) went to Claude and took 12 seconds; now it is the instant command.
- **The small Whisper's phrase is used when the large one has no "Jarvis".** For "Jarvis, che ore sono" the large one gave nothing and its looser retry "Ciao, ciao.", which was taken as the command. "Kere sona", "sicure sono" and similar now mean the time.
- **An unanswered confirmation lapses after 90 seconds.** While a confirmation is open JARVIS says no alerts: on 15 November "apro makerworld.com?" stayed open from 20:21 to 21:59, so the printer's pause alerts were never said and the study summary came an hour late.
- Tried and left off: listening with the Mac's microphone while AirPods are the input (macOS refuses a different input device on the shared engine, -10851).

## 1.30.51 — 15 November

- **A longer technical log, written with less work.** `diagnostica.log` keeps 3000 lines instead of 400 (two hours with the TV on were not enough to see what had happened before a crash). Each line is now added at the end of the file; the file is reread and shortened only when it has grown a fifth beyond its limit, instead of at every line.
- **Microphone: working buffers reused.** The two buffers used to convert each block of audio (about 47 a second) are kept instead of being created each time.
- **No compiler warnings left** (they were 18): a new one will now be noticed at once.
- Measured, not changed: at rest with a film playing in the room JARVIS uses about 10% of one core, almost all of it Whisper checking each phrase for "Jarvis". Cutting it would mean hearing less, so it stays.

## 1.30.50 — 15 November

- **A dead microphone is reconnected.** The "no sound" check never reconnected anything: the audio engine still said "running" and the reconnection stopped there, so a microphone gone silent stayed so until JARVIS was reopened. Now it is forced (three close attempts, then one every ten minutes; nothing with the lid closed, where silence is normal). Tested by cutting the microphone on purpose: back after 7 seconds.
- **Programs run by JARVIS no longer hang.** `Shell.run` read the output first and the errors after: a program with many errors waited until the timeout, and one whose child kept the pipe open blocked a thread for ever. Both are read together, a program that ignores the timeout is closed for good, and the answer comes back one second after it ends. Tested on five cases (300 KB of errors, 300 KB of output, child holding the pipe, program ignoring the close, normal).
- **Smaller fixes from the code review.** The piece counter of the streamed voice is behind a lock; "search" in Music without a text answers "Manca cosa cercare" instead of searching nothing; the printer's network search closes each attempt safely across threads.
- Echo cancellation is off again in the settings: after a sleep its audio channel stayed blocked in both directions (microphone at -66 dB, greeting not heard).

## 1.30.49 — 14 November

- **Echo cancellation only while JARVIS speaks.** With the filter always on (since 13 November) JARVIS heard the user worse: the voiceprint score fell from a median of 0.65 to 0.54 and Whisper misheard more phrases ("Chireson", "our YouTube"), also after new recordings. The filter is still what lets "Jarvis, basta" through over JARVIS's own voice, so it now works only then (and for half a second after); the rest of the time the microphone arrives as it is, as before 13 November. No restart of the microphone is needed to switch, and each mode keeps its own background level.

## 1.30.48 — 14 November

- **JARVIS no longer closes by itself after hours.** It crashed four times between 11 and 14 November, always after hours with the Mac in and out of sleep, so on return it was gone and macOS showed it closing. The crash was inside Swift's own check of "am I on the main thread?" (`MainActor.assumeIsolated`), called every second by the satellite's timer and every minute by the iPhone bridge's. Those eight callbacks now use `onMainThread`, which checks the thread with `Thread.isMainThread` and reads nothing from the Swift runtime.

## 1.30.47 — 13 November

- **Access to Library, without the system's folders.** Asked by the user after JARVIS could not find Minecraft's mods folder. One rule (`HomeAccess`) now decides for finding, opening, moving, downloading, creating PDFs and editing code: the whole home is open, and in ~/Library the app data is too — Application Support (Minecraft, Bambu Studio, VS Code, Arduino…), iCloud Drive, Google Drive / Dropbox / OneDrive, Arduino15, Fonts. Still closed: macOS's own folders (Keychains, Mail, Messages, Preferences, Containers, com.apple…, iPhone backups), browser profiles and password managers, the data of JARVIS and of the AI apps (access keys), the logins and caches of browsers built into apps (Minecraft, Steam, GeForce NOW: "Login Data", "Cookies", "webcache2"), and hidden folders like .ssh. Checked on 31 real paths, all right. "Apri la cartella mods" also looks in Application Support.
- **Better file search.** Up to three words are searched, not only the longest, and the folder counts with the name: "mods minecraft" now finds …/minecraft/mods first. Equally similar, the name with the exact word wins ("controlify" before Qt's "control-icon"). Installed Python packages (venv, site-packages) are left out. Tested live: mods folder, Controlify, no login files, the chemistry PDFs as before.

## 1.30.46 — 13 November

From the log of a Minecraft session (DualSense, Fabric, Controlify):
- **Long requests are no longer lost.** Three times today the quick Whisper heard "Jarvis, …" perfectly and the large one returned nothing (every piece dropped as uncertain, with echo cancellation on): the phrase was discarded and had to be repeated. Now the large Whisper tries once more, less strict, and if it still finds nothing the quick one's phrase is used.
- **Two confirmations in one answer.** Claude opened two sites at once (Fabric and Controlify), each needing a "sì": only the last one waited, so "sì" opened Controlify and Fabric was lost. Actions to confirm in the same answer now wait together, and one "sì" does them all.
- **No more blinking satellite.** While Minecraft went to full screen the satellite appeared and disappeared six times in six seconds; it now changes only after three equal readings.

## 1.30.45 — 13 November

- **Closed lid, first real test.** `sistema.log` showed the Mac going to sleep the moment the lid closed (15:08:59) and waking when it opened (15:10:11), with no stall of JARVIS: the 5 November problem was the Mac sleeping. After waking JARVIS heard every "Jarvis, che ore sono?", but Whisper wrote it as "Già vissi che ore sono" and "Jarvis. Sikres." (with echo cancellation on, four times out of four today).
- **"Già vissi che ore sono" now wakes JARVIS.** A known soundalike of "Jarvis" at the start ("già visto", "già vissi", "già di visto"…) followed by a command JARVIS knows counts as a call, with the usual voice check; if the large Whisper then loses "Jarvis", the command heard first is used. Of all the phrases in the logs only that one would wake it; "già visto, bellissimo film" does not. "Chireson" is also read as "che ore sono".
- **"Vabbè, un'ora" after a session is closed.** A short answer with no time to "Se ha smesso prima, a che ora?" now leaves the session as it is, instead of going to Claude (in English, 30 seconds). Questions answered without the model no longer log "cervello: Claude".

## 1.30.44 — 13 November

- **The closed-lid question, with evidence.** On 5 November, with the lid closed, JARVIS seemed to stop "after a beep or an answer"; that night the Mac had gone to sleep, and the log could not tell a sleeping Mac from a stuck JARVIS. A new log, `sistema.log` (2000 lines, kept for days), records sleep and wake, screens off and on, lid closed and open, mains or battery. A sentinel on the main thread writes down any stall of 5 seconds or more while the Mac is awake, and when it restarts; for each stall (at most three a day) macOS's `sample` saves what JARVIS was doing in `blocchi/`. Sleep does not count, because the clock used stops while the Mac sleeps. Tested with a 15-second stall caused on purpose (developer mode): the stall, its end after 15.8 s and the exact line of code were all recorded.

## 1.30.43 — 13 November

- **Fewer requests to the model.** All 571 phrases in `voce.log` were run through the parsers: 147 of 456 commands were already handled without the model, now 177. New without the model: "chiudi tutte le applicazioni (tranne il Finder)" (also "tutte le hop"), "minimizza Netflix" (the app's windows to the Dock) and "minimizza la finestra del Finder / Chimica Organica", "apri/chiudi per memoria" (how Whisper hears Promemoria), "apri Prime Video" (the word "video" sent it to the model), "alza il volume all'80%", "considera completato il promemoria X", "quante mail ho non lette?" (the count, then the usual summary). "Metti un timer" and "imposta un promemoria" without details now ask "Per quanto tempo?" and "Cosa le ricordo, e quando?" instead of going to the model. Tested live (muted): timer, mail count, reminder cancelled; closing all apps and completing a real reminder were not run.
- **"Il timer è scaduto."** A timer without a name said "il timer timer è scaduto"; with a name it now says "il timer per la pasta è scaduto".

## 1.30.42 — 13 November

- **Forgotten study sessions.** An hour into a study session (from the start, the end of the last break or the last "sì"), JARVIS asks "La sessione di Fisica 1 è aperta da un'ora: sta ancora studiando?" and listens for the answer, without the model. "Sì" → it asks again an hour later. "No, ho smesso alle 17", "ho finito mezz'ora fa" → the session closes at that time; a plain "no" closes it now and asks "Se ha smesso prima, a che ora?" (a time before the start is refused as a mishearing). No answer in 10 minutes: if the Mac is in use (headphones, perhaps) it asks again in half an hour; otherwise the session closes at the last sign of you (a key, a click, a word to JARVIS), and when you are back JARVIS says so and asks whether you studied longer. In quiet hours there is no question: a session idle for 20 minutes closes in silence the same way. Not while dictating, and 20 minutes apart from the two-hour break alert. Tested live (muted) with a test session, then removed.
- **No more "Buongiorno, ."** With no honorific set, about one fixed sentence in six replaced "signore" with nothing ("Subito, ."): now it is always removed.

## 1.30.41 — 13 November

- **Sentences said often are ready on disk.** Until now only the short acknowledgments ("Subito", "Ecco") were saved; every other sentence was generated again by Qwen3 each time, with the GPU at over 90%. Now a sentence of up to 160 characters heard whole for the second time is saved exactly as it was heard (no extra generation), and from the third time it starts at once from disk. Measured with a second voice server: first piece 2.28 s and 0.40 s the first two times, 0.00 s the third; GPU from 94–100% to 28%, the HUD's idle level. Same voice, same intonation (the key includes speed, sampling and seed). At most 200 MB, the least used go first; only hashes of the sentences are kept, never the text.

## 1.30.40 — 13 November

- **"Jarvis, riordina il brainstorming".** JARVIS reads the last brainstorming (or "di ieri", a date) and writes a tidy version next to it, "… riordinato.pdf": themes with their points, ideas to develop, things to do, open questions, and at most three suggestions of its own, marked as such. Only your ideas, obvious mishearings corrected; the original PDF is never changed. It then says in a sentence how many themes there are and what to do first.
- **"Jarvis, continua il brainstorming (di ieri)"**, also "aggiungi al brainstorming": the new phrases are added to the same PDF, under a "Ripreso …" heading; the version before goes to the Trash. Model-free, like starting a dictation. Each brainstorming now keeps its phrases in JARVIS's folder, so it can be continued and tidied; older PDFs are read from their text.

## 1.30.39 — 13 November

- **Dictation for brainstorming.** "Jarvis, da questo momento scrivi tutto quello che dico" (or "prendi appunti", "inizia la dettatura"): every phrase in your voice is written down without "Jarvis", until "Jarvis, basta scrivere". The text becomes a PDF in Documents/Brainstorming, with a new paragraph after a pause of over a minute. "Jarvis, cancella l'ultima frase" removes the last one. The draft is saved after every phrase, so a restart loses nothing.
- **Satellite and full screen.** The satellite stays above the windows but disappears when a window covers the whole display (series, films, full-screen apps), and comes back afterwards.
- **New icon.** The neural sphere of the HUD itself, captured from the open window, in place of the reactor.
- **Dictation from iPhone and iPad.** While dictating, the phone app sends every phrase it hears without "Jarvis" and without the chime, shows "✎ Dettatura attiva" and the text written; "Jarvis, basta scrivere" ends it from the phone too, and the answer comes out of the phone. The phone's microphone is not checked against the voiceprint (it is in your hand).
- **Same icon on iPhone and iPad**, as a full square (iOS rounds the corners).
- **Tidier neural sphere.** The ten long axons that crossed the whole sphere are gone: each neuron is joined only to its three nearest. Curved axons bend sideways and more gently. Neurons, dendrites, shell and data screens are where they were.

## 1.30.38 — 12 November

- Recognition of the owner's voice: of 549 phrases in `voce.log`, 50 had been rejected and about half were the
  owner's, nearly all short and just under the threshold (0.36–0.45); TV and video voices never went above 0.33.
  Four cases now pass with less, only in a quiet room: the command right after a "Jarvis" clearly his, "Jarvis"
  alone (the command after the beep is checked again), a whole known command ("Jarvis, esegui sequenza",
  "Jarvis, apri Safari") and "no", which can only cancel and always counts. On the recorded rejections it
  recovers 15 and lets no TV voice through. Someone in the room with a similar voice passes more easily with a
  whole command: the voiceprint is not a security barrier.
- Whisper's hint also carries the names in use, which change by themselves: subjects of the study plan and of
  the sessions, project folders and proper names from today's news ("Praxis-1" had become "Proxis", "Proxys" and
  "Proxima").

## 1.30.37 — 12 November

- More commands without the model: "ricordami di comprare i servo domani alle 9", "ricordami tra 20 minuti di…",
  "fissa un appuntamento dentista giovedì alle 15" (the date is read with fixed rules: today, tomorrow, the day
  after, weekdays, "il 20 novembre", "alle 9 e mezza", "stasera", "tra mezz'ora"; other forms go to the model),
  "apri la cartella Appunti Corso", "com'è la temperatura del PC?", "crea una cartella sulla scrivania col
  titolo…".
- Morning briefing without the model when Claude is not there (no internet or not logged in): date,
  appointments, reminders, important mail and what to study, in one second instead of the 45 or so Gemma takes to
  load. With Claude the briefing stays his, with the news.
- "Chiudi la finestra del Finder" and "chiudi tutte le finestre del Finder": JARVIS could not, now it closes them
  directly (the Finder stays open).
- Folders named loosely: "My Project" is the existing `My_Projects`. This applies to downloading a
  repository, saving a PDF, creating a folder and moving a file: before, JARVIS made a new one.

## 1.30.36 — 12 November

- Less model: JARVIS answers by itself, without Claude or Gemma and mostly without internet, for study sessions
  (start, stop, break, resume, "how long did I study", "how is studying going"), "any important mail?", today's
  and tomorrow's appointments, reminders, weather, battery, Mac temperature, the date, sums ("quanto fa 150 diviso
  4"), "create a folder…", "metti <song>", display off and dark mode. Only the whole sentence counts: everything
  else goes to the model as before. At the end of a session it asks what to write in the report and saves the
  answer without the model; only the flashcards go through it. Of the 372 recorded requests it recognises 26
  more, with no false match.
- "Scarica la repo": downloads a public repository (GitHub, GitLab, Codeberg, Hugging Face) into a new folder,
  installing and running nothing; the address is checked first.
- News: also recognises badly misheard names ("Proxima 1" for "Praxis-1"); JARVIS no longer guesses web
  addresses or reads them aloud.
- "1 ora" and "1 minuto" instead of "1 ore" and "1 minuti".

## 1.30.35 — 12 November

- Italian voice with a steadier tone: Qwen3 generated every sentence with a different intonation (temperature
  0.9). It now uses temperature 0.6 and the same seed for each sentence, chosen from samples. Same voice and same
  GPU use; the saved short acknowledgments are generated again with the new intonation.

## 1.30.34 — 12 November

- Going deeper into a news item: after the news, "tell me more about…" recognises which one is meant even if the
  name was misheard ("Proxis 1" for "Praxis-1"), reads the article and searches the web for other sources with
  the right name, instead of looking among the projects or online with the wrong name.
- The news given during the day is kept in `notizie_date.json`: JARVIS remembers it after a restart too.

## 1.30.33 — 12 November

- "Add to the report" updates the same PDF instead of making a new one with "(2)": only for documents JARVIS
  created, and the earlier version goes to the Trash.
- "The news you told me": JARVIS remembers all the news given during the day and uses it for the report, instead
  of searching for new items.

## 1.30.32 — 12 November

- Important mail: "any new mail?" and the morning briefing no longer read the latest arrivals but last week's
  unread mail by importance. First people (in Contacts, or already written to) and your university or company,
  then notices about accounts, payments and orders; advertising and lists are only counted. On first use: 46
  advertising emails out of 64 no longer read aloud. "Le mail di Rossi sono importanti" and "ignora le mail di
  Booking" teach JARVIS.
- News: "what's in the news?" and the morning briefing give three or four items on robotics, AI, devices and tech
  from today and yesterday, naming the source, from Sky TG24, ANSA, la Repubblica, Il Sole 24 Ore, DDay, HDblog,
  Bloomberg, Wired, The Verge, Ars Technica, MIT Technology Review, TechCrunch, IEEE Spectrum, The Robot Report
  and Sky News. A topic can be asked ("news about Apple?").
- "Continua" or "more news": further items, never those already said, also reading BBC, The Guardian, New York
  Times, Financial Times, CNBC, 9to5Mac, MacRumors, Engadget, GSMArena, Android Authority, Tom's Hardware, CNET,
  Robohub and MIT News. Established newsrooms only; a story carried by several of them ranks higher and is marked
  as confirmed; one affair takes two places at most.
- Reports and folders: "make a PDF report of this news" writes the document directly (title, date, sections,
  bullets, several pages) instead of going through TextEdit, where the file had been lost in the save panel. News
  reports go to `Desktop/Report notizie`. "Create a folder…" and "put it in…" create the folder and move the file
  without the Finder.

## 1.30.31 — 12 November

- PDFs made directly, without clicking in PdfCraft: "combine these PDFs" makes a new PDF in the order given (images
  too), in a couple of seconds instead of this morning's 17 minutes and 151 steps. Also: extracting pages ("pages
  1-3 and 5"), page count and size, renaming or moving a file and saving a public web page as PDF. It never
  overwrites: a taken name gets "(2)". Pages behind his login (Outlook, the university portal) still go through
  Safari, File › Export as PDF.

## 1.30.30 — 12 November

- "Jarvis" mentioned in a sentence no longer wakes it: "è il Jarvis", "che Jarvis", "a Jarvis", "non è Jarvis", or
  "Jarvis." as a sentence of its own inside a conversation ("Iron Man ce l'avete presente? Jarvis. È un
  sistema…"). Of 294 real phrases in `voce.log` it drops the 14 where JARVIS was talked about to others, and no
  command; "Basta Jarvis", "Ciao Jarvis" and "Jarvis, …" work as before.

## 1.30.29 — 12 November

- Code help: "look at the code I'm writing" reads the file open in VS Code (name from the window, folder from
  VS Code's state) with line numbers, instead of taking a screenshot; JARVIS explains it by voice without
  reading it line by line.
- "Comment it", "reformat it", "fix it": JARVIS rewrites the file. In comments mode every code line must stay
  identical, indentation included, or the change is refused; for Python the syntax is checked before saving. A
  copy goes to `backup_codice` before every change, and "put it back" restores it. With unsaved changes in
  VS Code it asks to save first.

## 1.30.28 — 12 November

- Form of address less often: about one answer in six, never twice in a row. With `"honorific": ""` in
  `config.json` JARVIS uses no form of address at all, in Italian or English ("signore" and "sir" are removed).
- Mac temperature: "a che temperatura è il PC?" reads the chip, disk and battery sensors (no password), macOS's
  thermal state and the three processes using the most CPU.
- "Metti Rick e Morty su Netflix": opens the title's search in the Netflix, Prime Video or YouTube app, then starts
  the right result.
- Notes search: when nothing is found it retries with other spellings ("giacobiana" → "jacobiana", "jacobian";
  k/c, ph/f, y/i). The Jacobian matrix of 11 November is now found (73 passages).
- Noise heard as "Grazie.": after three in a row, for one minute phrases no louder than that noise skip Whisper,
  and those "Grazie." are no longer transcribed a second time joined to the next phrase. Less GPU work and heat;
  voice near the Mac is checked as before.

## 1.30.27 — 12 November

- Study plan: exam dates and weekly hours per subject are said by voice ("l'esame di Fisica 1 è il 20 gennaio,
  voglio studiarla 6 ore a settimana") and kept in `piano_studio.json`. "How is studying going?" and "what do I
  study today?" compare this week's hours with the goals, say how many days are left to each exam and which
  subject is best to study today, and for how long. "Fisica" counts for "Fisica 1", "Chimica" for
  "Chimica Organica".
- The morning briefing also says what to study today and the next exam.
- Evening summary at 21:00: today's hours, this week's hours against the goals, exams within two weeks and what to
  study tomorrow. Only on days with study or a near exam; it waits until JARVIS is free, at the latest until 23:00.
  The hour is in Settings (0 turns it off).
- After the report of a study session JARVIS asks whether to make five flashcards on the topic; after a yes it
  takes them from the notes.

## 1.30.26 — 12 November

- Mail that answers: unread mail and searches are read straight from Mail's index on disk, in under a second and
  even with Mail closed. With 15,908 messages in the inbox, the Mail script took 4 to 23 seconds for the latest
  ten and 57 for a search, so the morning briefing said Mail had not answered in time.
- Needs Full Disk Access for JARVIS (System Settings › Privacy & Security). Without it JARVIS uses the old script
  and writes why in `diagnostica.log`.

## 1.30.25 — 11 November

- More detailed SATELLITE, same style: gold propellant tanks, a richer engine with vernier nozzles, truss yokes,
  radiator fins and edge lights on the solar wings, a second small dish, a helical antenna, a crown of lights around
  the eye and marks on the rings.
- The two rings now sit in front of the wings, around the eye, and tilt less: they no longer pass through the wing
  supports, the dish or the antennas.

## 1.30.24 — 11 November

- Neural sphere, brain look: a large neuron with long branching dendrites at the centre instead of the round light;
  the 44 neurons are spread evenly through the whole sphere, joined by a mix of straight and curved axons; each
  neuron has more branches, with a dot at every fork. The dashes turning around the outline are gone and the small
  data screens go from 34 to 8.
- The sphere breathes slowly, and while JARVIS speaks its surface waves follow the beat of the voice.

## 1.30.23 — 11 November

- Denser neural sphere: 44 neurons instead of 16, each joined to its three nearest instead of two, and 1500 points in
  the background mesh instead of 1150. The same sphere is in the iPhone and iPad app.

## 1.30.22 — 11 November

- No more state label under the neural sphere ("in attesa", "elaborazione", "risposta"): the sphere shows the state
  by itself. Privacy mode and errors are still written, because they need attention.

## 1.30.21 — 11 November

- The window of the neural sphere wraps it closely: 440×472 points instead of 600×600, so much less invisible space
  around the sphere takes clicks meant for the windows behind it.
- A double click on the sphere goes back to the SATELLITE.

## 1.30.20 — 11 November

- iPhone and iPad app: the same neural sphere as on the Mac. The app had stopped building after the sphere
  redesign, because the sphere drew itself with macOS-only code; it now draws with Metal on iOS and iPadOS too.
- Faster voice on the phone: the new Italian voice reaches the iPhone and iPad in pieces of 0.3 s while it is being
  generated, so the first words start after about a third of a second, as on the Mac.

## 1.30.19 — 11 November

- Lighter, quicker Italian voice: JARVIS now uses the small Qwen3 voice model (Qwen3-TTS 12Hz 0.6B Base, 8-bit) with
  the same reference voice, and plays each sentence in pieces while the rest is still being generated. In a test of
  three sentences the first word came after 0.35 s instead of 4.1 s, and the GPU worked about 40% less per second
  of speech. "Basta" also stops the generation of the rest of the sentence. The 1.7B model stays as a fallback.

## 1.30.18 — 11 November

- The SATELLITE now floats above every window, on every desktop and over full-screen apps. The other NUCLEUS styles
  still stay on the desktop, behind the apps.
- No more buttons around the neural orb. Hiding it (Esc), the SATELLITE and the settings are in the JARVIS icon's menu.

## 1.30.17 — 11 November

- The panels (Frasi, Registro, Attività, Moduli, Protocollo, Pensiero) are no longer attached beside JARVIS. They open
  from the "Schede" menu, in the menu bar on the left while JARVIS is in front and in the JARVIS icon's menu on the
  right. Each panel is its own window that can be dragged anywhere and resized, with both the neural orb and the
  SATELLITE. Each window remembers where it was left, and the open ones reopen at the next launch.
- Around the orb only the hide, settings and SATELLITE buttons remain.

## 1.30.16 — 11 November

- Long sentences are no longer cut off: a short command still ends after 0.8 s of silence, a longer sentence waits
  1.3–1.7 s, and the limit for one sentence rises from 20 to 30 seconds.
- Breaks in study and work sessions: "pausa" or "facciamo una pausa" no longer closes the session. JARVIS pauses it,
  starts a 10-minute timer, then says "la pausa è finita, ricominciamo? Avvio la sessione?"; a "sì" resumes it.
  Breaks are not counted as study time; at the end JARVIS says the effective time, the total time and how many
  breaks were taken.
- Session reports: when a session ends JARVIS asks what to write in the report and saves it; at the next session
  with the same name it reminds you where you had got to.
- AirPods stem button: while JARVIS uses the headphones' microphone, a press works like ⌃⌥Space (speak without
  "Jarvis", or interrupt) instead of starting the music or showing "impossibile connettere JARVIS al microfono".

## 1.30.15 — 11 November

- Crash with AirPods 4 fixed: when the headset's microphone opened, macOS switched it to 24 kHz while JARVIS still
  used the 48 kHz format, and installing the microphone tap with the old format made macOS close the app. JARVIS now
  compares the two formats, rebuilds the audio engine once and, if they still differ, retries later without
  crashing.
- Headsets that keep changing audio configuration no longer cause a rebuild every two seconds (88 in six minutes):
  repeated rebuilds wait longer, and a change that leaves the microphone working is ignored.
- After an audio change the microphone is reattached on the same engine instead of a new one: closing and
  reopening it switched the AirPods' profile again each time, so the yellow microphone dot kept blinking. JARVIS keeps
  using the headphones' microphone.

## 1.30.14 — 10 November

- The Satellite window wraps it closely: 221 × 130 points instead of 221 × 221 (the wings reach the sides, above and
  below there was only empty space), so it can sit much closer to the edges of the display. The satellite keeps the
  same size on screen; the dark backdrop becomes an ellipse.
- At every launch the satellite starts in the top right corner of its display; it can still be dragged anywhere
  while JARVIS runs. The reactor keeps its remembered position.

## 1.30.13 — 10 November

- The Satellite look is 30% larger (221 points instead of 170); the reactor keeps its size. Switching style in
  Settings resizes the window around the same centre. GPU of the JARVIS process alone, desktop uncovered: 0.54%
  while waiting (reactor 1.21%), 0.8% in a thinking-and-speaking test (1.8% in an earlier test at the old size: the
  numbers vary with how long it speaks).

## 1.30.12 — 10 November

- New "Satellite" look for the desktop window (Settings → SATELLITE → Aspetto: "Reattore" or "Satellite"; the
  reactor stays the default). A faithful SceneKit port of the three.js preview the user approved, built in code with
  no external models: same sizes and parts, physically based chrome and titanium lit by a soft studio environment,
  hexagonal hull panels, wrinkled gold foil, solar cells, lens eye with 8-blade aperture and glow, LED mouth,
  eyebrows, two gyroscopic rings, folding solar wings, dish, star trackers, antenna, strobe and RCS puffs.
  Waiting it drifts slowly with wings half open; listening it turns to the viewer, opens wings and iris and signal
  rings come in; thinking the rings spin and tilt, the dish scans and particles orbit; speaking mouth, ring lights
  and outgoing waves follow the real voice level.
- Light on the fanless Air: 15 fps without antialiasing while waiting (10 after a minute), 30 fps with 2x
  antialiasing while active, stopped when covered or hidden; parts that never move on their own are merged into a
  few draw calls. GPU of the JARVIS process alone (IOKit, voice server excluded), desktop uncovered, waiting:
  reactor 1.21%, satellite 0.40–0.68%; satellite while thinking and speaking 1.8%.
- The desktop window is now called SATELLITE in menus, tooltips and Settings (the local AI model is still the
  "nucleo locale").

## 1.30.11 — 10 November

- Instant robot STOP: from a movement command ("esegui sequenza", "torna a home", joint moves) until Robot Console
  reports the arm still, every short phrase is checked for "stop", "basta", "fermati", "ferma", "alt" and similar,
  without saying "Jarvis" and without the voiceprint. The STOP goes to Robot Console before anything else: in tests
  with audio files it left about 0.45 s after the word ended (plus the 0.3 s pause that marks the end of a phrase).
  Phrases are cut shorter only while the arm moves; "non fermarti" or a long phrase are not a STOP.

## 1.30.10 — 10 November

- Direct channel to Robot Console: "abilita robot" (enable the robot), "esegui sequenza" (run the sequence), joint
  moves and closing the app go through a private local socket instead of clicks.
- Fixes after the first real tests, in Robot Console's Python side: HOME is set only after the 3 second USB quiet
  window (before it, the heartbeat had not started and the firmware watchdog tripped); "HALTED"/"ERROR WATCHDOG"
  lines left over from the previous session are ignored before the board identifies itself; enabling restores the
  last limits applied to the real robot.
- Whisper's spellings "Abilità robot", "E segui sequenzo", "segui sequenza" are understood.
- If the board stays silent after the USB opens (it did not even answer hello), enabling reopens the port once with
  a board reset, as pressing "Connetti Mega" again would.
- "Torna a home" (go back HOME) uses the direct channel, like the "Vai a HOME…" button, instead of the old
  token-based API.

## 1.30.9 — 9 November

- New **Thought** panel (PENSIERO) in the HUD, available from the terminal icon. It shows the request route, commands, queries, files consulted, domains found, and timestamped outcomes.
- The panel follows the latest line, keeps up to 300 events in memory, and clears on “New conversation”.

## 1.30.8 — 9 November

- The HUD sphere becomes a neural network: the spiral is replaced by 16 neurons with dendrites, joined by soft
  glowing axons. Each neuron fires in turn: its body flashes and a pulse runs along its axons, more often while
  JARVIS listens, thinks or speaks.
- The striped blocks are small data screens with live bars, waveforms or scrolling lines; they turn with the sphere
  (none circles the outline any more).
- Denser shell, brighter at the edges, so the sphere reads as a solid that turns. While waiting it turns about three
  times faster, at 20 fps instead of 12; after a minute it slows down to 6 fps as before.

## 1.30.7 — 9 November

- One NUCLEUS on every display. A click opens the HUD around that NUCLEUS; if the HUD is open on another display,
  it first shrinks into the NUCLEUS there. Each display remembers its own position, and a NUCLEUS can be dragged
  only within its display: one pushed onto another display got stuck at the edge.
- The NUCLEUS sits at the back of the normal level instead of level −1: there, on the main display, the full-screen
  Übersicht window stayed in front of it and took its clicks. If an app window ends up underneath it, the NUCLEUS
  goes back behind it, or hides while that window covers it completely (an app enlarged to fill the screen).

## 1.30.6 — 9 November

- New holographic sphere in the open HUD, inspired by JARVIS in *Age of Ultron*: drawn with Metal, a plexus of
  nodes, glowing ribbons spiralling into a white core, striped data blocks and a broken rim. It reacts to listening,
  thinking and the voice; every change of state eases in, so pulses never jump or reverse abruptly. The NUCLEUS on
  the desktop keeps the original reactor.
- Smoothness: each frame takes a fresh drawable from the Metal layer. A live test (`tests/reactor_sphere/live.sh`,
  `GPU_LOAD=1` adds a load like speech synthesis) measures 30 fps in every active state and 12 while waiting.

## 1.30.5 — 9 November

- Running a Robot Console sequence by voice failed: the app exposes only what is on screen and JARVIS could not
  scroll, so it pressed unrelated buttons and switched tabs. New `app_scroll` tool: scrolls the page, or the list that
  contains an element, without keys or clicks. The model is told to scroll instead of searching elsewhere.
- In protocols `{resto}` also works in the request to the model (`chiedi`), not only in tool arguments.

## 1.30.4 — 9 November

- Actions without commentary: during a Robot Console sequence JARVIS said 22 sentences in a minute, describing steps
  already finished. Now, while it acts (apps, clicks, robot, printer, music), the sentences said between one step and
  the next are dropped before speech synthesis: it gives the acknowledgment and at the end only "Done", the result,
  the error or a question. Questions and conversation are answered as before.
- Protocols with `"sintetico": true`: sequences of actions that acknowledge at once and speak only at the end.
- A result with numbers ("15, sir.") is no longer mistaken for a repeated acknowledgment and left unsaid.

## 1.30.3 — 9 November

From the logs of the last day:
- Printer switched off: the network search (254 addresses) ran every 4 minutes all night, even in privacy mode.
  Now it comes back less and less often (4, 8, 16, then every 30 minutes), never in quiet hours or privacy mode,
  and starts at once when you ask about the printer.
- "Mi sa che Jarvis ti ha dato ragione" ("I think Jarvis proved you right"), said to another person, woke it up.
  With "Jarvis" in the middle of the sentence the model reads the whole sentence and stays silent if it is not
  addressed to it. Real requests are answered as before.
- Morning briefing: Mail takes about 10 seconds. The briefing now waits for it at most 5 seconds; when it starts
  by itself, the data is gathered while JARVIS answers the first request.
- "Jarvis Apriclode" went to the model (7.7 seconds): instant commands recognise how Whisper writes "Claude"
  (Clode, Cloud, Claud, Clod).

## 1.30.2 — 8 November

- Repository in English: README, changelog, code comments and manual. The app itself still speaks Italian
  (and English when you speak English).
- MIT license.

## 1.30.1 — 8 November

- Ready for GitHub: only neutral example values in the code. Name, profile, vocabulary, notes folders and
  lab project live in the settings (`config.json`).
- Bundle identifier and Apple Team in `local.env`, outside Git (example in `local.env.example`), read by
  `scripts/build.sh` and `Installa su iPhone e iPad.command`.
- Configurable path for the ROS 2 lab (`labProject`).
- README and manual updated with the 1.29.2 confirmation rules.

## 1.30.0 — 8 November

- Screen view on iPhone and iPad: dragging with one finger moves windows, two fingers scroll.

## 1.29.2 — 8 November

- Confirmations only for three things: purchases, passwords and sending email. Robots, printing and
  everything else run at once, and still go to the security log.
- A "yes" counts only as a short answer (six words at most): a sentence said to someone else no longer confirms.

## 1.29.1 — 8 November

- The Bambu printer is found again on the network when its address changes.
- Looking at apps also sees their small confirmation windows; one "yes" per action.

## 1.29.0 — 8 November

- Mac screen on the phone as full-resolution H.264 video, Display P3 colours, shown by the system video
  player: smoother, sharper and lighter on the CPU.

## 1.28.3 — 8 November

- Screen on the phone as a continuous stream of 12 images per second, 1280 points wide.

## 1.28.2 — 8 November

- "Alt" recognised only as a whole word; an empty answer during a quiz is asked again; quizzes taken from
  your notes.

## 1.28.1 — 7 November

- TV: the remote control accepts the TV's certificate.

## 1.28.0 — 7 November

- Music from the Apple Music catalog, Samsung TV control on the local network, Protocols (one phrase,
  several actions) and flashcards. New manual chapter on Protocols and review.

## 1.27.0 — 7 November

- iPad app; the Mac follows one device at a time. Simpler final message in the installer.

## 1.26.2 — 7 November

- The microphone reconnects by itself when the Mac's audio device changes.

## 1.26.1 — 7 November

- iPhone: the screen view rotates properly, keyboard included.

## 1.26.0 — 7 November

- iPhone: manual control of the Mac, printing from away, reactor in the centre.

## 1.25.0 — 7 November

- iPhone: hands-free "Jarvis", Siri, self-service installation; clicks in small windows.

## 1.24.0 — 7 November

- iPhone app away from home (Tailscale), with Face ID and the Mac screen.

## 1.23.1 — 7 November

- iPhone app tested on a real phone: microphone, icon and ROS limits.

## 1.23.0 — 7 November

- Notes search also understands meaning (local meaning vectors).

## 1.22.0 — 7 November

- iPhone app: talk to JARVIS from the phone, on the local network, over an encrypted link.

## 1.21.1 — 7 November

- Notes search tolerates misheard words and quotes the page.

## 1.21.0 — 6 November

- Search in your notes (PDF, Word, PowerPoint): answers quoting document and page.

## 1.20.1 — 6 November

- Printer: tells apart the printer's requests while loading and unloading filament.

## 1.20.0 — 6 November

- Loads and unloads filament and prepares prints from Bambu Studio.

## 1.19.0 — 6 November

- Reads the Bambu printer's state and warns about problems.

## 1.18.2 — 6 November

- More instant commands; the frontmost app goes into Claude's context.

## 1.18.1 — 6 November

- Instant commands answer while the app is opening.

## 1.18.0 — 6 November

- Italian voice with Qwen3 on MLX, imitating `Resources/voce_jarvis.wav` (a synthetic voice).

## 1.17.0 — 6 November

- HUD panels can open all together around the reactor.

## 1.16.1 — 6 November

- A pause after "Jarvis" no longer loses the command.

## 1.16.0 — 6 November

- Italian voice with Supertonic.

## 1.15.0 — 6 November

- Confirmations only for dangerous actions, also for apps, ROS and Arduino.

## 1.14.2 — 6 November

- Apps open instantly even with a misheard verb ("abre", "aprei").

## 1.14.1 — 6 November

- Instant commands understand verbs misheard by Whisper; the spoken acknowledgment no longer goes missing.

## 1.14.0 — 6 November

- JARVIS acknowledges at once ("Right away, sir") and then works.

## 1.13.1 — 6 November

- Buttons in a circle around the reactor, with soft corners.

## 1.13.0 — 6 November

- The JARVIS window becomes the reactor alone, with drop-down panels.

## 1.12.1 — 6 November

- Answers to JARVIS's questions are no longer discarded by the voice check.

## 1.12.0 — 6 November

- After a question JARVIS keeps listening for the answer.

## 1.11.2 — 6 November

- "Turn the volume down a bit" and "volume to the max".

## 1.11.1 — 6 November

- Apps can be called by their Italian names.

## 1.11.0 — 6 November

- Instant commands: apps, volume and timers without going through the model.

## 1.10.0 — 5 November

- Recognises the Mac's apps even when their name is misheard.

## 1.9.0 — 5 November

- Owner voice recognition also on the command after the beep, more robust with TV and music in the
  background; new enrolment with real commands. Greeting at launch depends on the time of day. Gemma loads
  only when you call Jarvis without internet.

## 1.8.0 — 4 November

- Touch ID after the "yes" for delicate actions; three confirmation levels (Touch ID only for scripts,
  Shortcuts and memories).
- Deleting events and reminders, closing browser tabs by name, minimising without confirmation.
- More sentences can be added to the voiceprint; after each answer JARVIS goes back to waiting.
- New manual.

## 1.7.0 — 4 November

- Owner voice recognition on "Jarvis": enrolment and measurement.

## 1.6.0 — 4 November

- App control with the Accessibility permission.

## 1.5.0 — 3 November

- Interrupting by voice, earlier end of sentence detection, Gemma not loaded when Claude answers; wake word
  and interruption with the large Whisper (turbo).

## 1.4.0 — 3 November

- Initiative: JARVIS speaks first (appointments, reminders, battery, morning briefing).

## 1.0 – 1.3 — 2 and 3 November

- 1.0: local voice assistant for macOS (wake word, Whisper, Gemma with Ollama, neural voice, HUD).
- Local model out of RAM when unused; robust start-up with Whisper models from disk.
- Robotics: ROS 2, Robot Console, Arduino and Bambu Studio.
- Vision: screen and camera, also the iPhone camera through Continuity Camera.
- Security: confirmations, defence against instructions hidden in content, audit log, privacy mode.
- More reliable wake word, timing diagnostics, typo-tolerant file search.
- HUD showing who is answering; choice of Claude model (Haiku, Sonnet, Opus); Claude online and Gemma offline.
- Personal Whisper vocabulary, learned with Claude Opus.
