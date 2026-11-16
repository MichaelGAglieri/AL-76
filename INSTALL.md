# Installing AL-76

This guide takes you from a clean Mac to AL-76 running on the Mac, and its companion app on your iPhone and iPad.
Everything personal (your Apple account, your Team ID, your keys, your passwords) is yours to enter: nothing of
the kind is in this repository, and nothing you enter is sent to it.

- [What you need](#what-you-need)
- [1. Prepare the Mac](#1-prepare-the-mac)
- [2. Your Apple account in Xcode](#2-your-apple-account-in-xcode)
- [3. Your local settings: `local.env`](#3-your-local-settings-localenv)
- [4. Install AL-76 on the Mac](#4-install-al-76-on-the-mac)
- [5. First launch and permissions](#5-first-launch-and-permissions)
- [6. Your personal settings](#6-your-personal-settings)
- [7. Install the app on iPhone and iPad](#7-install-the-app-on-iphone-and-ipad)
- [8. Pair the phone with the Mac](#8-pair-the-phone-with-the-mac)
- [9. The 7-day expiry](#9-the-7-day-expiry)
- [Updating](#updating)
- [Troubleshooting](#troubleshooting)

## What you need

| | Required | Notes |
|---|---|---|
| Mac with Apple Silicon (M1 or later) | yes | macOS 15 or later. 16 GB of RAM is the practical minimum for the local model, 24 GB is comfortable |
| Free disk space | yes | about 20 GB: Xcode, the local model (about 7 GB), the voices (about 3 GB), Whisper (about 0.8 GB) |
| Xcode | yes | free on the Mac App Store; it brings the Swift toolchain and builds the iPhone app |
| Homebrew | yes | [brew.sh](https://brew.sh); the setup uses it for Ollama and `uv` |
| An Apple account | yes | your own Apple ID, signed in to Xcode. A free account is enough; see the [7-day expiry](#9-the-7-day-expiry) |
| A Claude subscription and Claude Code | optional | for the "Claude" brain; without it AL-76 uses only the local model |
| iPhone or iPad | optional | iOS / iPadOS 18 or later, and a USB cable for installing |
| Tailscale | optional | to use the phone app away from home, on both devices |
| Picovoice AccessKey | optional | only for Porcupine instead of Whisper; Porcupine still knows only the word "Jarvis", not the call "Ehi Al" |

Internet is needed during installation (downloads of the model, voices and Whisper); afterwards the core runs
locally.

## 1. Prepare the Mac

1. Install **Xcode** from the Mac App Store and open it once: accept the licence and let it install its
   components. For the iPhone and iPad app, also add the **iOS platform** (Xcode › Settings › Components).
2. Install **Homebrew** by following the command on [brew.sh](https://brew.sh).
3. Download this repository: on its GitHub page use **Code › Download ZIP**, or copy the address shown under
   **Code** and run:

   ```bash
   git clone <repository address>
   cd AL-76
   ```

## 2. Your Apple account in Xcode

The Mac app is signed with an **Apple Development** certificate, so that macOS remembers its permissions
(microphone, screen, accessibility) between builds. The iPhone and iPad app needs the same account to be
installed at all.

1. Open Xcode › Settings › **Accounts**, press **+** and sign in with **your own Apple ID**.
2. Select your team (with a free account it is called "Personal Team") and press **Manage Certificates…** ›
   **+** › **Apple Development**.
3. Find your **Team ID**, a 10-character code. Either open developer.apple.com › Account › Membership, or ask
   the certificate you have just created:

   ```bash
   security find-certificate -c "Apple Development" -p | openssl x509 -noout -subject
   ```

   The Team ID is the value after `OU=`.

If you do not want to use an Apple account for the Mac app, `JARVIS_ADHOC_SIGN=1 ./scripts/build.sh --install`
signs it "ad hoc"; macOS will then ask for every permission again after each build. The iPhone app always
needs the account.

## 3. Your local settings: `local.env`

```bash
cp local.env.example local.env
open -e local.env
```

Fill in your own values. `local.env` stays on your Mac: it is listed in `.gitignore`.

| Variable | What to put |
|---|---|
| `JARVIS_BUNDLE_ID` | an identifier of your own for the Mac app, e.g. `com.yourname.jarvis`. macOS ties the permissions to it, so do not change it after the first launch |
| `JARVIS_IPHONE_BUNDLE_ID` | an identifier for the phone app, e.g. `com.yourname.jarvis.iphone`. It must be unique to you: with a free account Apple refuses identifiers already used by someone else |
| `JARVIS_TEAM` | your Team ID from step 2 |

## 4. Install AL-76 on the Mac

```bash
./scripts/setup.sh
```

The script can be run again at any time: it skips what is already there. It:

1. installs **Ollama** and downloads the local model (Gemma 4, about 7 GB);
2. installs the **neural voices** in a Python environment managed by `uv` (Qwen3 for Italian, Kokoro for
   English, Supertonic as a fallback, plus the small model that recognises your voice), about 3 GB;
3. checks **Claude Code**, if you want it (see below);
4. builds the app, signs it with your certificate and copies it to `/Applications`, then opens it.

Optional: for notes search by meaning, not only by words, also download the embedding model:

```bash
ollama pull bge-m3
```

For the Claude brain (optional), install Claude Code and sign in with **your own** subscription:

```bash
brew install --cask claude-code
claude auth login --claudeai
```

## 5. First launch and permissions

On the first launch AL-76 downloads **Whisper** by itself (about 800 MB) and spends about a minute
preparing it for the Neural Engine. Then macOS asks for permissions, each once:

| Permission | When | What for |
|---|---|---|
| Microphone | first launch | hearing "Ehi Al" and your commands |
| Accessibility | first app control | clicking and typing in other apps, and the phone's control of the Mac |
| Screen Recording | first "look at my screen" or phone screen view | reading the screen, sending it to the phone |
| Calendar, Reminders, Contacts, Mail | first use | the matching commands |
| Desktop, Documents, Downloads | first file command | finding and opening files |
| Full Disk Access (optional) | set it yourself | fast mail search; without it AL-76 asks Mail, which is slower |

They are all in System Settings › Privacy & Security, where you can also withdraw them.

## 6. Your personal settings

Open AL-76's settings (⚙︎ on the HUD) and enter **your own** details. Nothing in the code is about you: the
defaults are neutral examples.

- **Your name and how AL-76 addresses you**, a line about you, and the words Whisper should spell right
  (in `~/Library/Application Support/JARVIS/config.json`: `userName`, `userFullName`, `userAbout`,
  `vocabulary`).
- **Notes folders** to search.
- **iPhone app link**: off until you turn it on (step 8).
- **Università** (optional): the name of your university, the server address of its student app and, if that
  server asks for it, the app's User-Agent; then **your** username and password. They are kept in the macOS
  Keychain and sent only to that server. The addresses AL-76 reads are those of one student-app API: another
  university's server may need others.
- **Bambu printer** (optional): IP address, serial number and access code shown on the printer.
- **Picovoice AccessKey** (optional).

Passwords and codes go to the Keychain or to `config.json`, which only your user can read; they never go into
the repository.

## 7. Install the app on iPhone and iPad

The phone app is a remote: listening, the models and the tools stay on the Mac.

**Once per device:**

1. Connect the iPhone or iPad to the Mac with a cable, unlock it and tap **Trust** on "Trust This Computer?".
2. Turn on **Developer Mode**: Settings › Privacy & Security › Developer Mode (at the bottom), then restart
   the device and confirm. If the item is missing, connect the device to Xcode once (Window › Devices and
   Simulators) and it appears.

**Install:**

- Double-click **`Installa su iPhone e iPad.command`** in the project folder (or run it in Terminal). It reads
  your Team ID and identifiers from `local.env`, builds the app, registers each connected device with your
  account and installs it on every iPhone and iPad connected and unlocked. The first build takes a few
  minutes.
- Or open `iPhone/JARVISPhone.xcodeproj` in Xcode, choose your team in Signing & Capabilities, select the device
  and press **Run**.

**With a free account, the first time:** iOS does not open an app from an unknown developer. Go to Settings ›
General › **VPN & Device Management**, tap your Apple ID under "Developer App" and then **Trust**. This is needed
once per device.

## 8. Pair the phone with the Mac

1. On the Mac: settings › **iPhone**: turn on **Collegamento con l'app iPhone**, press **Salva**, then
   **Mostra il codice di abbinamento**. The code is 20 characters, created on your Mac; it never travels over
   the network.
2. On the phone: open AL-76, tap **Abbina al Mac** and frame the QR code (or type the code).
3. Away from home: install **Tailscale** on the Mac and the phone and sign both in to your own Tailscale account.

Only one device is served at a time; the other shows "Usa qui" to take over.

## 9. The 7-day expiry

With a **free** Apple account, Apple signs the phone app for **7 days**. After that the app no longer opens
until you install it again. This is a limit of the free account, not of AL-76.

- The app warns you two days before ("L'app scade tra 2 giorni: reinstallala dal Mac").
- To renew, connect the device and run `Installa su iPhone e iPad.command` again. It sets aside an expiring
  signature and asks for a new one valid 7 more days. Pairing and settings stay.
- A free account can also have only a few apps of its own on a device at the same time (three, at the time of
  writing).
- With a paid **Apple Developer Program** membership the signature lasts a year.

The Mac app is not affected: it runs on the Mac it was built on, with no expiry.

## Updating

```bash
git pull
./scripts/build.sh --install                  # the Mac app
./"Installa su iPhone e iPad.command"         # the phone app, with the devices connected
```

`build.sh --install` closes AL-76, replaces it and opens it again; your settings and data in
`~/Library/Application Support/JARVIS` stay.

## Troubleshooting

| Problem | What to do |
|---|---|
| `Certificato Apple Development non trovato` | step 2: sign in to Xcode and create the Apple Development certificate |
| The installer does not see the phone | cable connected, device unlocked, "Trust" accepted; try another cable or port |
| `Attiva la Modalità sviluppatore` | step 7, Developer Mode, then restart the device |
| Build fails mentioning the account or the profile | Xcode › Settings › Accounts: check you are signed in; with a free account use an identifier of your own in `JARVIS_IPHONE_BUNDLE_ID` |
| "Untrusted Developer" on the phone | Settings › General › VPN & Device Management › Trust |
| The app stopped opening after a week | the 7-day expiry: install again |
| The phone does not find the Mac | the link is on in the Mac's settings; same Wi-Fi, or Tailscale on both |
| Apple voice instead of the neural one | `./scripts/setup_tts.sh` |
| Claude does not answer | `claude auth status`, then `claude auth login --claudeai` |

More in the [README](README.md#troubleshooting) and in the full manual, [`Manual_AL-76.pdf`](Manual_AL-76.pdf).
