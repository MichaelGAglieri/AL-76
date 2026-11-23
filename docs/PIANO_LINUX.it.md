# AL-76 per Linux — piano a fasi

*Scritto il 21 novembre 2026 sulla base della versione Mac 1.30.78 e della versione Windows 0.1.0 (pre-release `windows-v0.1.0`). Stato: **da approvare**. Nessuna riga di codice finché l'utente non approva.*

## Il principio

Vale lo stesso principio della versione Windows: **per tutti, non per una persona**. Ci sono le stesse cinque regole: niente di cablato su una persona, un nucleo con moduli che si accendono, va bene su un PC normale, installazione da persona normale, i dati restano sul PC. A queste se ne aggiunge una:

6. **Linux non è un sistema solo.** Ci sono varie distribuzioni, vari desktop (GNOME, KDE, Xfce…) e due sistemi grafici (X11 e Wayland). AL-76 guarda su cosa sta girando e usa la strada giusta. Quando una funzione non è possibile, lo dice con una frase chiara e non fa finta di riuscirci.

**Obiettivo:** un pacchetto `.deb` per Debian e Ubuntu, in due versioni, **amd64** e **arm64**. Deve avere le stesse funzioni della versione Windows, dove Linux lo permette.

**Sistemi di riferimento:**
- **Ubuntu 26.04 LTS:** GNOME 50, solo Wayland. Dalla 25.10 non c'è più la sessione Xorg, ma le app X11 girano ancora con Xwayland.
- **Ubuntu 24.04 LTS:** GNOME 46, Wayland oppure Xorg.
- **Debian 13:** GNOME.

Il secondo desktop è KDE Plasma (Kubuntu). Xfce, MATE e Cinnamon funzionano attraverso la strada X11, ma senza garanzie.

## La scelta dello stack

Il dato che decide è questo: il port Windows ha già separato il codice che non dipende da Windows.

| Parte di `windows/` | Righe | Dipende da Windows? |
|---|---|---|
| `src/AL76.Core` (logica, cervelli, MCP, strumenti, studio, account, Maker, telefono, sicurezza) | ≈15.400 | **No**: `net10.0`, provato anche sul Mac |
| `src/AL76.Speech` (Whisper.net, Kokoro, impronta vocale sherpa-onnx, modelli) | ≈240 | **No** |
| `web/hud` (sfera, satellite, reattore, robottino in three.js) | ≈2.700 | **No**: pagine web |
| `tests/AL76.Core.Tests` (oltre 460 test) | ≈4.800 | **No** |
| `src/AL76.Platform` (UI Automation, WASAPI, DPAPI, Hello, Bonjour…) | ≈2.250 | Sì |
| `src/AL76.App` (WPF: finestre, impostazioni, configurazione, host della voce) | ≈4.300 | Sì |

Il nucleo comunica con la piattaforma solo attraverso interfacce. Le principali sono `IPcActions`, `IAppDriver`, `IDocumentPlatform`, `IPageReader`, `ISecretStore`, `IVoice`, `ITranscriber`, `IVoiceEmbedder`, `IOwnerCheck`, `IStudioDriver`, `IPhoneScreen` e `IShell`. Per Linux bisogna scrivere solo queste parti, più le finestre: circa 6–7 mila righe nuove, mentre ne riusiamo circa 18 mila.

### Le opzioni

| | A. **.NET 10 + GTK3/WebKitGTK, tutte le finestre in HTML** | B. .NET 10 + Avalonia | C. Python + Qt 6 (PySide6) | D. Tauri (Rust) + nucleo .NET a parte |
|---|---|---|---|---|
| Nucleo | **Quello di Windows, così com'è** (riferimento al progetto) | Quello di Windows | Da riscrivere (15 mila righe) e da tenere allineato: una terza versione | Quello di Windows, ma in un processo separato collegato con JSON |
| Sfera e robottino | **Le pagine three.js di Windows senza modifiche** (WebKitGTK ha WebGL) | Avalonia non ha una WebView libera su Linux: o sfera e robottino riscritti in OpenGL (glTF, particelle…) o una WebKit a parte | QtWebEngine (Chromium, +150 MB) | WebView di sistema (è WebKitGTK anche qui) |
| Impostazioni e configurazione | Da rifare in HTML/JS: la logica resta in C# e la pagina disegna i campi che C# le manda | Quasi 1:1 con il WPF di `Pages.cs` | Da rifare in Qt | Da rifare in HTML |
| Finestre trasparenti, sopra le altre, posizionate | GTK3 lo fa da solo su X11 e Xwayland | Sì, su X11 e Xwayland | Sì, su X11; su Wayland ha gli stessi limiti | Sì, su X11; su Wayland ha gli stessi limiti |
| Peso del pacchetto | ≈90–110 MB (.NET autonomo e librerie native); WebKitGTK è già nel sistema | ≈110 MB | ≈250 MB | ≈100 MB, più Rust da imparare |
| Rischi | GTK3 è in manutenzione, ma `libwebkit2gtk-4.1` c'è in Ubuntu 26.04 e Debian 13 ed è la base di Tauri: resterà per anni | Due tecnologie grafiche nello stesso processo; sfera da riscrivere | Tre versioni da mantenere | Due processi e due linguaggi |

### Raccomandazione: **A**

- **Riuso:** le 15 mila righe del nucleo, le pagine three.js (identiche al Mac al 1–5%) e i test di Windows. Se una correzione entra nel nucleo, vale subito per Windows e per Linux.
- **Una tecnologia sola per tutte le finestre:** sfera, nucleo, fumetto, barra di scrittura, Impostazioni e configurazione guidata sono tutte pagine di WebKitGTK.
  - Le pagine di Windows usano `window.chrome.webview`. Un piccolo adattatore iniettato prima del caricamento lo traduce in `window.webkit.messageHandlers`, quindi non si cambia nemmeno una riga di `windows/web/hud`.
- **Niente librerie grafiche .NET:** GTK3 e WebKit si chiamano con circa 40 funzioni P/Invoke scritte da noi. Niente GtkSharp, che non si aggiorna da anni. Le API sono stabili.
- **Wayland:** è il punto più difficile su Linux. Le finestre di AL-76 girano con **Xwayland** (`GDK_BACKEND=x11`). È la strada usata da Tauri ed Electron, e funziona anche su GNOME 50: finestre trasparenti, sopra le altre, posizionate e con i clic che passano attraverso. Su Wayland "puro" un'app normale non può fare niente di questo.

### Regola per il codice condiviso
- `linux/` **usa** `windows/src/AL76.Core` e `windows/src/AL76.Speech` attraverso `ProjectReference` e copia `windows/web/hud` durante la compilazione. Questi file non si modificano mai da qui.
- Alcune parti del nucleo sono pensate per Windows:
  - l'elenco delle app vietate non contiene konsole, ptyxis, kitty, gnome-control-center e simili;
  - i tipi di file eseguibili non comprendono `.sh`, `.AppImage`, `.desktop`, `.deb` e i file con il permesso di esecuzione;
  - i comandi di installazione delle CLI sono righe di PowerShell.
- Come si gestiscono:
  1. dove possibile, la parte Linux è **più severa** del nucleo: il driver Linux rifiuta da solo le app e i file vietati su Linux;
  2. se serve davvero un punto di aggancio nel nucleo, lo chiedo alla sessione Windows attraverso la sessione principale, con una proposta precisa, e aspetto.
- `linux/MAPPA_WINDOWS_LINUX.md` tiene l'elenco di cosa è riusato, cosa è rifatto e cosa manca. È l'equivalente di `MAPPA_SWIFT_CSHARP.md`.
- `VoiceHost`, `PhoneHost` e `DevCommands` stanno nel progetto WPF, quindi non si possono riusare direttamente. Su Linux diventano una **copia adattata**, con un riferimento al file di origine. Se più avanti la sessione Windows li sposta in un progetto comune, la copia sparisce.

## Struttura del codice

```
linux/
  AL76.Linux.sln              riferimenti a windows/src/AL76.Core e AL76.Speech (sola lettura)
  Directory.Build.props
  src/AL76.Linux.Platform/    net10.0: audio PulseAudio/PipeWire, AT-SPI, X11/XTest, portali, D-Bus,
                              Secret Service, Avahi, Tesseract, polkit, GStreamer, percorsi XDG
  src/AL76.Linux.App/         guscio GTK3 + WebKitGTK (P/Invoke), finestre, area di notifica, istanza unica,
                              host della voce e del telefono
  web/app/                    pagine Linux: Impostazioni, configurazione, barra, fumetto, menu, adattatore webview
                              (le pagine della sfera si copiano da windows/web/hud durante la compilazione)
  gnome-extension/            piccola estensione di GNOME Shell (facoltativa): finestre delle altre app su Wayland
  packaging/                  control, postinst/postrm, .desktop, AppStream, icone, polkit, build-deb.sh
  scripts/                    build.sh (dal Mac), prova-orb.sh (prove automatiche in OrbStack)
  tests/AL76.Linux.Tests/     xUnit: girano sul Mac e in OrbStack (Linux vero, amd64 e arm64)
  README.md                   inglese, per il repository
  MANUALE.it.md               italiano, solo sul Mac (da aggiungere agli ESCLUSI)
  MAPPA_WINDOWS_LINUX.md
```

**Dati, secondo le regole XDG:**
- impostazioni in `~/.config/AL-76/`;
- dati e modelli in `~/.local/share/AL-76/` (modelli in `models/`);
- log in `~/.local/state/AL-76/`;
- socket dell'istanza unica in `$XDG_RUNTIME_DIR/al76.sock`.

Il modello del robottino si legge **solo** dalla cartella dati dell'utente (`~/.local/share/AL-76/companion/`) e non entra mai nel repository né nel pacchetto. Per trovare Documenti, Scrivania e le altre cartelle personali si usa `xdg-user-dir`: su un sistema in italiano si chiamano proprio «Documenti» e «Scrivania».

## Il quadro per desktop

Cosa si può fare su ciascun desktop. ✓ = funziona; ◐ = funziona con un permesso o un aiuto; ✗ = Linux non lo permette.

| Funzione | X11 (Xfce, MATE, 24.04 Xorg) | GNOME Wayland (Ubuntu 26.04, 24.04, Debian 13) | KDE Wayland |
|---|---|---|---|
| Sfera e nucleo trasparenti, sopra le altre finestre, con i clic che passano attraverso | ✓ | ✓ con Xwayland | ✓ con Xwayland |
| Tasto globale della barra di scrittura | ✓ `XGrabKey` | ◐ portale GlobalShortcuts (GNOME 48 e successivi; su 24.04 una scorciatoia personalizzata di GNOME che lancia `al76 --barra`) | ✓ portale |
| Elenco e apertura delle app | ✓ file `.desktop`, anche Flatpak e Snap | ✓ | ✓ |
| Chiudere un'app | ✓ come la X (EWMH) | ◐ con l'estensione; senza, un segnale gentile al processo | ◐ script di KWin |
| Portare un'app davanti | ✓ | ◐ con l'estensione (Wayland non lascia prendere il primo piano) | ◐ script di KWin |
| Leggere pulsanti e campi, premere, scrivere nei campi | ✓ AT-SPI | ✓ AT-SPI (funziona anche su Wayland) | ✓ AT-SPI |
| Tasti e clic su un punto | ✓ XTest | ◐ portale RemoteDesktop (un sì dell'utente la prima volta, poi ricordato) | ◐ portale |
| Foto della finestra e dello schermo | ✓ | ◐ portale Screenshot/ScreenCast; la posizione delle finestre arriva dall'estensione | ◐ portale |
| Schermo del PC sul telefono | ✓ | ◐ portale ScreenCast e PipeWire (GStreamer) | ◐ |
| Volume, musica, batteria, tema, spegnere lo schermo | ✓ | ✓ | ✓ |
| Icona nell'area di notifica | ◐ dipende dal pannello | ✓ su Ubuntu (estensione AppIndicator già installata); su Debian GNOME no: c'è sempre il menu del nucleo | ✓ |

**Estensione di GNOME Shell (proposta, fase 4):** è un piccolo file JavaScript distribuito nel `.deb`. Offre ad AL-76, solo su D-Bus di sessione, l'elenco delle finestre con titolo, app e posizione, e le azioni «porta davanti» e «chiudi». Si attiva una volta sola dalla configurazione guidata e serve un nuovo accesso. Senza estensione AL-76 funziona lo stesso, ma su GNOME Wayland non porta le app davanti e non le chiude come con la X.

## Inventario: Windows → Linux

| Windows (`AL76.Platform` / `AL76.App`) | Linux |
|---|---|
| WPF, finestre trasparenti, DWM | GTK3 con visuale RGBA, `keep_above`, `skip_taskbar`, forma dell'input per i clic che passano attraverso; tutto con Xwayland |
| WebView2 | WebKitGTK 4.1: schema `al76://` al posto degli host virtuali `hud.al76` e `companion.al76`; adattatore per `chrome.webview` |
| NotifyIcon | StatusNotifierItem con `libayatana-appindicator3` (GTK3) |
| `RegisterHotKey` | `XGrabKey` / portale GlobalShortcuts / scorciatoia di GNOME |
| WASAPI (NAudio) e AEC di Windows | PulseAudio (`libpulse-simple`, che funziona anche su PipeWire); cancellazione dell'eco con `module-echo-cancel` di PipeWire (WebRTC), se c'è; altrimenti cattura normale (il «basta» funziona comunque, grazie a `BargeIn`) |
| Voci di Windows (livello Base) | **Piper** tramite sherpa-onnx, già usato per l'impronta vocale (voci italiane e inglesi, veloce anche su CPU deboli); da verificare la licenza di espeak-ng incluso; ripiego su speech-dispatcher. Kokoro resta la voce di Medio e Alto, con im_nicola e af_heart come su Windows |
| Whisper.net, KokoroSharp, sherpa-onnx | **Gli stessi pacchetti**: hanno le librerie native linux-x64 e linux-arm64 (da confermare in fase 0); su Linux Whisper può usare anche Vulkan |
| UI Automation, SendInput, PrintWindow | AT-SPI2 su D-Bus (`Tmds.DBus`, MIT): albero, azioni, testo modificabile senza rubare la tastiera; XTest e XGetImage su X11; portali RemoteDesktop, Screenshot e ScreenCast su Wayland; estensione GNOME o script KWin per le finestre |
| `shell:AppsFolder` | File `.desktop` (sistema, utente, Flatpak, Snap) con i nomi tradotti (`Name[it]`); avvio con `gio launch` |
| Core Audio, SMTC | `wpctl`/`pactl`; MPRIS su D-Bus |
| `GetSystemPowerStatus` | UPower su D-Bus |
| Tema scuro, spegnere lo schermo | GNOME `gsettings color-scheme`; KDE `plasma-apply-colorscheme`; X11 `xset dpms`; GNOME con D-Bus di Mutter |
| Cestino | Specifica freedesktop del cestino (`gio trash`) |
| DPAPI e Credential Manager | Secret Service su D-Bus (GNOME Keyring o KWallet); senza portachiavi AL-76 chiede di attivarlo e non salva in chiaro |
| Windows Hello (dopo il sì) | **polkit**: un'azione `org.al76.confirm` installata dal pacchetto; il sistema chiede la password o l'impronta (fprintd) all'utente nella sua finestra, e AL-76 non vede nulla |
| Fotocamera | GStreamer (`v4l2src` / portale Camera), una foto |
| OCR di Windows | Tesseract (`tesseract-ocr`, `-ita`, `-eng`), con le parole e i riquadri in TSV, che servono per Bambu Studio |
| `PrintToPdfAsync` di WebView2 | Stampa su PDF di WebKitGTK, in una vista nascosta |
| Bonjour di Windows | Avahi su D-Bus (`_jarvis._tcp`, come Mac e Windows) |
| Avvio con Windows (registro) | `~/.config/autostart/al76.desktop`, creato dall'app quando l'utente lo accende |
| Collegamenti nel menu Start | `.desktop` di sistema (installato dal pacchetto) |
| Inno Setup / MSIX / winget | `.deb` amd64 e arm64; più avanti, forse, un repository APT firmato |

## Livelli di PC

Gli stessi di Windows (`TierPolicy` del nucleo). Su Linux la misura legge `/proc/meminfo`, i processori e la scheda video da `/sys/class/drm` o Vulkan. Cosa cambia:
- livello **Base**: voce Piper al posto della voce di Windows;
- livello **Alto** con NVIDIA: Whisper con CUDA o Vulkan.

La voce Qwen3 del Mac resta fuori, come su Windows, salvo un modulo futuro per NVIDIA.

## Le fasi

Ogni fase finisce con test, una prova nella VM e un commit su main con i soli file di `linux/` e di questo piano. Le stime sono in giornate di lavoro.

### Fase 0 — Fondamenta (1–2 giorni)
- **VM Ubuntu 26.04 Desktop arm64** sul Mac (vedi «Come si prova»). **OrbStack** con Ubuntu 24.04 e 26.04, in arm64 e in amd64 (con Rosetta), per le prove automatiche: compilazione, test, `apt install` del pacchetto.
- Soluzione `linux/` che compila il nucleo e la voce di Windows per `linux-x64` e `linux-arm64`, con pubblicazione autonoma dal Mac (`dotnet publish -r linux-arm64 --self-contained`).
- Verifica che le librerie native di Whisper.net, ONNX Runtime, KokoroSharp e sherpa-onnx ci siano per tutte e due le architetture.
- Il guscio GTK3/WebKitGTK: una finestra trasparente con la **sfera di Windows**, caricata con l'adattatore `chrome.webview`.
- Misura del PC, livello, log, percorsi XDG, istanza unica.
- **Un primo `.deb` già in fase 0**, così il pacchetto viene provato a ogni fase e non solo alla fine.
- **Finito quando:** nella VM `sudo apt install ./al76_0.0.1_arm64.deb` installa l'app, AL-76 compare tra le app, si apre la sfera, e la finestra «Stato» indica livello, desktop (GNOME/KDE/altro) e sessione (Wayland/X11). In OrbStack i test del nucleo di Windows passano anche su Linux.

### Fase 1 — Voce (2–3 giorni)
- Microfono e uscita con PulseAudio/PipeWire; cancellazione dell'eco con PipeWire, quando si può attivare; l'indicazione della modalità usata, come su Windows.
- Chiamata «Ehi Al» a scelta, Whisper base e small-q8_0 con il contesto adattato e la via veloce (le stesse misure di Windows); modelli solo inglesi per l'inglese.
- Kokoro in streaming a frasi; Piper per il livello Base; «basta» con `BargeIn`; filtro del sottofondo e impronta vocale TitaNet (sherpa-onnx).
- Modalità test con i WAV (`prove/`).
- **Finito quando:** «Ehi Al, che ore sono?» funziona nella VM in italiano e in inglese, con tempi misurati come su Windows (obiettivo: sotto 1 s fino alla risposta istantanea).

### Fase 2 — Cervelli e azioni di base (2 giorni)
- Il nucleo c'è già. Su Linux restano da fare:
  - **installazione e accesso** di ogni cervello in un terminale che l'utente vede (`x-terminal-emulator`, ptyxis, gnome-terminal o konsole), con i comandi ufficiali per Linux (da ricontrollare il giorno in cui si scrive il codice):
    - Claude: `curl -fsSL https://claude.ai/install.sh | bash`
    - ChatGPT: l'installer ufficiale di Codex per Linux
    - Gemini: `curl -fsSL https://antigravity.google/cli/install.sh | bash` (installa `agy` in `~/.local/bin`; c'è anche lo snap `antigravity-cli`)
    - Ollama: `curl -fsSL https://ollama.com/install.sh | sh`, che chiede la password di amministratore nel terminale. La scrive l'utente, mai AL-76.
  - `LinuxPcActions`: app, volume, musica, URL, batteria, cartelle, tema scuro, spegnere lo schermo, apertura dei file. Chi apre un eseguibile (con il permesso di esecuzione, `.sh`, `.AppImage`, `.desktop`, `.deb`) deve dire sì.
- Server MCP e regole uguali al Mac e a Windows (niente terminale né modifiche ai file per i cervelli).
- **Finito quando:** il banco dei cervelli (8 richieste) dà gli stessi risultati di Windows con Claude nella VM; «apri il calcolatore», «alza il volume» e «metti pausa» funzionano su GNOME.

### Fase 3 — Interfaccia e configurazione guidata (3–4 giorni)
- Sfera, nucleo (satellite, reattore, robottino), finestrella del nucleo con le stesse dimensioni e regole di Windows, fumetto del robottino.
- **Barra di scrittura** (Ctrl+Alt+T è già il terminale di Ubuntu, quindi il tasto predefinito sarà un altro, da scegliere con le anteprime), agganciata sotto il nucleo come l'opzione A.
- Area di notifica, menu del nucleo.
- Impostazioni e configurazione guidata in HTML: gli stessi passi e le stesse pagine di Windows, con la logica in C# (`SetupFlow` del nucleo). Il passo dei permessi spiega microfono, portachiavi, avvio automatico e, su Wayland, i permessi dei portali.
- Ogni aspetto nuovo si sceglie prima tra anteprime, come per Windows.
- **Finito quando:** i tre profili di prova di Windows completano la configurazione nella VM; la sfera e il robottino girano a 20 fps o più con il 3D della VM acceso; il nucleo resta sopra le finestre su GNOME 50.

### Fase 4 — Controllo del PC (3–4 giorni; la parte più grossa)
- `IAppDriver` con AT-SPI:
  - lettura dell'albero, pressione, scrittura con `EditableText` (non ruba la tastiera), scorrimento;
  - AT-SPI si attiva nelle app con `org.a11y.Status.IsEnabled`, come fa un lettore di schermo, così parlano anche Firefox, Chromium ed Electron.
- Tasti e clic: XTest su X11; portale RemoteDesktop su Wayland (un sì la prima volta, poi un token ricordato).
- Foto: X11 diretto; portali su Wayland.
- **Estensione GNOME** per finestre, primo piano e chiusura; script KWin per KDE.
- App vietate per Linux, che si aggiungono a quelle del nucleo: terminali (gnome-terminal, ptyxis, konsole, kitty, alacritty, xterm, tilix), Impostazioni (gnome-control-center, systemsettings), Password e chiavi (seahorse), KeePassXC, gestori di pacchetti (Software, Synaptic), GParted, finestre di polkit e `sudo`.
- File e PDF dal nucleo; indice degli appunti con l'OCR di Tesseract; «guarda il mio schermo».
- **Finito quando:** nella VM, su GNOME Wayland, si ripetono le prove di Windows del 18/11: Calcolatrice, Editor di testo, File, scrittura con accenti ed €, ricerca dei file, PDF, indice degli appunti con una pagina scansionata, richieste scritte a Claude.

### Fase 5 — Organizzazione e account (1 giorno)
- Il nucleo ha già calendario e promemoria locali, ambiti, briefing, OAuth con PKCE, Microsoft Graph e Google.
- Su Linux restano:
  - i token nel **Secret Service**;
  - il browser con `xdg-open`;
  - le notifiche di sistema (`org.freedesktop.Notifications`).
- **Finito quando:** calendario e promemoria locali funzionano nella VM, i token sono nel portachiavi e un accesso finto passa i test. L'accesso vero a Microsoft e a Google resta da fare, come su Windows.

### Fase 6 — Studio, lavoro e documenti (1 giorno)
- Il nucleo ha già sessioni, piano di studio, flashcard, orario ICS/PDF/foto, riunioni, portale universitario come modulo, dettatura, brainstorming, schemi, notizie e progetti.
- Su Linux:
  - `IDocumentPlatform` con la stampa PDF di WebKitGTK e il cestino;
  - il progetto di VS Code letto da `~/.config/Code` invece di `%APPDATA%`.
- **Finito quando:** `create_pdf`, `webpage_to_pdf`, la dettatura e le sessioni con «pausa» funzionano nella VM.

### Fase 7 — Moduli: Maker, Telefono, TV (2–3 giorni)
- **Maker:**
  - Arduino con `arduino-cli`. Per usare la porta seriale l'utente deve stare nel gruppo `dialout`: la configurazione guidata mostra il comando, e la password la scrive l'utente.
  - Stampante Bambu via MQTT (dal nucleo) e Bambu Studio per Linux (AppImage o Flatpak) con l'OCR di Tesseract, con le stesse regole prudenti di Windows.
  - **ROS 2** nativo: su Linux è a casa sua (Jazzy su 24.04), con rosbridge locale o sulla rete.
  - Robot Console con l'API pubblica. Il canale privato del Mac non esiste nemmeno qui.
- **Telefono:**
  - lo stesso protocollo (porta 47831, TLS con chiave dal codice di abbinamento, BouncyCastle nel nucleo);
  - annuncio Avahi `_jarvis._tcp`;
  - schermo del PC sul telefono: XGetImage su X11, ScreenCast su Wayland;
  - se `ufw` è attivo, AL-76 mostra la regola da aggiungere solo per la rete di casa.
- **TV Samsung** e laboratorio dal nucleo; conferma dell'utente con polkit; fotocamera con GStreamer.
- **Finito quando:** i test dei moduli passano. Nella VM l'iPhone si abbina e manda una richiesta (serve l'iPhone dell'utente sulla stessa rete); gli strumenti senza hardware rispondono «non configurato».

### Fase 8 — Pacchetto `.deb` e documenti (2 giorni)
- **Contenuto:**
  - `/opt/al76/`: l'app .NET autonoma;
  - `/usr/bin/al76`;
  - `/usr/share/applications/al76.desktop`;
  - icone in `/usr/share/icons/hicolor/`;
  - AppStream (`/usr/share/metainfo/`), così l'app compare in GNOME Software quando si apre il `.deb`;
  - azione polkit;
  - estensione GNOME in `/usr/share/gnome-shell/extensions/`.
- **Dipendenze** (`Depends`, con le alternative per i nomi `t64` di 24.04 e successivi): `libgtk-3-0t64 | libgtk-3-0`, `libwebkit2gtk-4.1-0`, `libayatana-appindicator3-1`, `libpulse0`, `libx11-6`, `libxtst6`, `libssl3t64 | libssl3`, `libicu` (alternative per versione: da leggere con `apt-cache` in 24.04, 26.04 e Debian 13), `xdg-utils`.
- **Consigliati** (`Recommends`): `tesseract-ocr`, `tesseract-ocr-ita`, `tesseract-ocr-eng`, `avahi-daemon`, `xdg-desktop-portal`, `gstreamer1.0-pipewire`, `gstreamer1.0-plugins-good`, `wireplumber`, `gnome-keyring`.
- **`postinst` corto:** i trigger di dpkg aggiornano già il menu e le icone. Niente servizi di sistema e niente utenti creati. **L'avvio automatico è per utente** e lo decide l'utente nella configurazione. Si attiva solo il gruppo `dialout` se il modulo Maker lo chiede, e sempre con il comando mostrato all'utente.
- **`postrm`:** con `purge` non si cancellano i dati nella cartella dell'utente. Il manuale spiega come toglierli.
- Il pacchetto si costruisce sul Mac (`dpkg-deb` di Homebrew, oppure in OrbStack) in amd64 e arm64. Si controlla con `lintian` in OrbStack.
- Scaricamento dei modelli al primo avvio, «Esporta diagnostica» come su Windows, README in inglese, `MANUALE.it.md`, controllo delle parole private.
- Pubblicazione su GitHub (pre-release `linux-v0.1.0` con i due `.deb`) **solo con il sì dell'utente** e con `local/pubblica_su_github.sh`.
- **Finito quando:**
  - il `.deb` si installa da zero sulla VM 26.04 arm64 e la configurazione guidata arriva fino a «Pronto»;
  - in OrbStack i pacchetti amd64 e arm64 si installano su 24.04, 26.04 e Debian 13 e l'app parte senza schermo, con Xvfb;
  - se l'utente vuole, una prova vera amd64 su un PC con Ubuntu in «live USB», senza installare nulla sul disco.

### Allineamento finale
Un confronto voce per voce con la sezione «Allineamento con il Mac» di Windows, in `MAPPA_WINDOWS_LINUX.md`. Resta escluso solo ciò che è solo Apple (Comandi Rapidi, AirPods, Portachiavi iCloud) o solo del Mac (canale privato di Robot Console).

**Totale stimato:** circa 18–22 giornate di lavoro. La versione minima utile (fasi 0–2 con la sola sfera) richiede circa 5 giorni, perché il nucleo c'è già.

## Come si prova

1. **VM grafica (prove vere: voce, sfera, controllo del PC):** Ubuntu 26.04 LTS Desktop **arm64** sul Mac M4, con 8 GB, 4 core e un disco da 40 GB.
   - **VMware Fusion** (raccomandato, perché è già usato per Windows):
     - Linux arm64 ha `open-vm-tools`, quindi ci sono la cartella condivisa (`vmhgfs-fuse`) e l'adattamento della risoluzione;
     - il **3D accelerato** va provato: per Windows ARM il 3D era spento, e su Linux con mesa potrebbe funzionare. Si misura con `glmark2` e con la sfera;
     - su Wayland copia e incolla con il Mac possono non funzionare.
   - Alternativa: **UTM**, già installato e gratuito, con GPU `virtio-gpu-gl` (3D attraverso virgl) e cartella condivisa VirtFS.
   - La VM può stare sull'SSD esterno accanto a quella di Windows oppure sul disco interno: sul Mac ci sono 183 GB liberi.
   - Il microfono del Mac arriva nella VM. Prove brevi e VM spenta quando non serve (MacBook Air senza ventola).
2. **OrbStack (prove automatiche, senza finestra):** è già installato. Macchine Ubuntu 24.04 e 26.04 e Debian 13, in arm64 e amd64 con Rosetta. Servono per i test del nucleo e di Linux, l'installazione del `.deb` con `apt` (le dipendenze sono giuste?), `lintian` e un avvio con Xvfb che fotografa la sfera. Uno script, `linux/scripts/prova-orb.sh`, fa tutto con un comando.
3. **amd64 su hardware vero (facoltativo, alla fine):** Ubuntu da chiavetta USB in modalità prova sul PC Windows dell'utente o su quello dell'amico, senza toccare il disco.

## Rischi e punti aperti

- **Wayland:** primo piano, chiusura e posizione delle finestre delle altre app dipendono dall'estensione GNOME o dagli script KWin. I portali chiedono un sì all'utente la prima volta. Su desktop Wayland diversi da GNOME e KDE (Sway, Hyprland) si prova senza garanzie.
- **Xwayland e schermi HiDPI con scala frazionaria:** le finestre di AL-76 possono apparire un po' sfocate. Si valuta in fase 3; c'è l'opzione Wayland nativo per le finestre senza «sopra le altre».
- **WebGL nella VM:** senza 3D la sfera va lenta, come su Windows (2,3 fps). Su un PC vero c'è almeno una grafica integrata.
- **Nomi dei pacchetti** diversi tra 24.04, 26.04 e Debian 13 (transizione `t64`, versione di `libicu`): si verificano in OrbStack.
- **Licenze:** Piper e espeak-ng (GPL) da controllare prima di distribuirli, oppure si scaricano a parte al primo avvio; il robottino Sketchfab e la voce clonata mai nel pacchetto.
- **Codice condiviso con `windows/`:** un cambiamento della sessione Windows al nucleo può rompere la compilazione Linux. I test Linux in OrbStack se ne accorgono. Le correzioni si chiedono alla sessione Windows e non si fanno da qui.
- **ESCLUSI per GitHub:** prima di pubblicare, `docs/PIANO_LINUX.it.md` e `linux/MANUALE.it.md` vanno aggiunti all'elenco degli esclusi di `local/pubblica_su_github.sh`, che è un file locale fuori da `linux/`: lo modifico solo con il sì dell'utente.
- **Firma:** il `.deb` non è firmato; un repository APT firmato con aggiornamenti automatici si può aggiungere dopo la 0.1.0.

## Decisioni da prendere con l'utente

1. **Stack:** A (.NET 10, nucleo di Windows, GTK3/WebKitGTK con tutte le finestre in HTML), raccomandato, oppure un'altra opzione.
2. **Desktop:** GNOME (Ubuntu e Debian) come riferimento e KDE come secondo, raccomandato; oppure solo GNOME.
3. **VM:** VMware Fusion o UTM, sull'SSD esterno o sul disco interno. Serve il permesso per scaricare l'immagine di Ubuntu 26.04 Desktop arm64 da ubuntu.com (circa 3–4 GB).
4. **Estensione GNOME** facoltativa per il controllo delle finestre su Wayland: sì o no.
