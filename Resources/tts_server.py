"""Local speech synthesis service for JARVIS: Qwen3 in Italian, Kokoro in English.

Started by the app with the venv interpreter in
~/Library/Application Support/JARVIS/tts/.venv. Listens on 127.0.0.1, on a port
chosen by the system, and writes it to stdout ("PORT 12345") as soon as it is ready.

POST /synth  {"text": ..., "voice": "bm_lewis", "lang": "en-gb", "speed": 1.0}
  "voice" can also be a blend: "im_nicola:0.5+bm_fable:0.5"
  "voice": "qwen3" uses Qwen3 with the voice of voce_jarvis.wav, "qwen3:robottino" the young voice of
  voce_robottino.wav (the ROBOTTINO companion, 16 November); if it fails it falls back to
  Supertonic ("supertonic:M4:0.5+M3:0.5") and then to "fallback", a Kokoro voice.
  "keep": true saves the audio to disk: used for acknowledgments, fixed sentences to say at once.
  "stream": true (Qwen3 only) sends the audio in pieces while it is generated: the first words
  arrive after about a third of a second instead of after the whole sentence. The body has no
  length and ends when the connection closes.
  "onlySaved": true returns only an already saved audio, without generating anything.
  A streamed sentence heard for the second time is saved as it was heard (no new generation);
  from then on it comes from disk at once, with no GPU work (13 November).
  -> PCM float32 little-endian audio, mono, 24000 Hz

POST /embed  body: PCM float32 little-endian audio, mono, 16000 Hz
  -> voiceprint (float32 little-endian), to recognise who is speaking.
  The audio is not saved.

Quits by itself when the app that started it exits.
"""

import hashlib
import json
import os
import queue
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import numpy as np
from kokoro_onnx import Kokoro

HERE = os.path.expanduser("~/Library/Application Support/JARVIS/tts")
kokoro = Kokoro(os.path.join(HERE, "kokoro-v1.0.onnx"), os.path.join(HERE, "voices-v1.0.bin"))
lock = threading.Lock()
TOKEN = os.environ.get("JARVIS_TTS_TOKEN", "")


# Voiceprint (optional): if something is missing JARVIS's voice works anyway.
try:
    import sherpa_onnx

    speaker = sherpa_onnx.SpeakerEmbeddingExtractor(sherpa_onnx.SpeakerEmbeddingExtractorConfig(
        model=os.path.join(HERE, "nemo_en_titanet_small.onnx"), num_threads=2))
except Exception:  # noqa: BLE001
    speaker = None


def embed(body):
    if speaker is None:
        raise RuntimeError("modello dell'impronta vocale non disponibile")
    samples = np.frombuffer(body, dtype="<f4")
    if not 1600 <= len(samples) <= 16000 * 30:
        raise ValueError("durata non valida")
    with lock:
        stream = speaker.create_stream()
        stream.accept_waveform(16000, samples)
        stream.input_finished()
        return np.asarray(speaker.compute(stream), dtype="<f4").tobytes()


# The models are already here: never online.
os.environ["HF_HUB_OFFLINE"] = "1"

# Qwen3 Italian voice (optional). It works entirely in its own thread: MLX wants a single
# thread, and the speaker voice check must not wait for a sentence to finish.
# The small Qwen3 (0.6B, since 1.30.19) does about 40% less GPU work than the 1.7B one, with the
# same cloned voice; the 1.7B one stays as a fallback if the small one is missing.
QWEN_DIR = next((os.path.join(HERE, d) for d in ("qwen3-0.6b", "qwen3")
                 if os.path.exists(os.path.join(HERE, d, "config.json"))), os.path.join(HERE, "qwen3"))
QWEN_STREAM_INTERVAL = 0.6  # seconds of audio per piece
QWEN_VOICE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "voce_jarvis.wav")
QWEN_VOICE_TEXT = ("Subito, signore. Ho completato il controllo: tutti i sistemi sono operativi "
                   "e possiamo procedere con il volo di prova quando desidera.")
# Sampling (12 November): with the library's default, temperature 0.9, every sentence came out with a
# different intonation. A lower temperature and the same seed for each sentence keep the tone steady;
# the user chose this setting ("Q2") from samples.
QWEN_SAMPLING = {"temperature": 0.6, "top_p": 0.9}
QWEN_SEED = 7
QWEN_CLIPS = os.environ.get("JARVIS_TTS_CLIPS") or os.path.join(HERE, "conferme")
# Sentences said often: kept once seen twice, short ones only (long ones never repeat word for word).
REPEAT_MAX_CHARS = 160
REPEAT_SEEN = os.path.join(QWEN_CLIPS, "viste.json")   # only hashes, never the text
REPEAT_SEEN_MAX = 3000
CLIPS_MAX_BYTES = 200 * 1024 * 1024
repeat_lock = threading.Lock()
SUPERTONIC_VOICE = "M4:0.5+M3:0.5"
qwen_jobs = queue.Queue()
qwen_ready = threading.Event()
# Other cloned voices: name -> (reference audio, what it says). The ROBOTTINO speaks with a young, calm
# voice the user chose on 16 November ("E2"), designed with Qwen3 VoiceDesign and cloned like JARVIS's.
QWEN_VOICES = {
    "qwen3": (QWEN_VOICE, QWEN_VOICE_TEXT),
    "qwen3:robottino": (os.path.join(os.path.dirname(os.path.abspath(__file__)), "voce_robottino.wav"),
                        "Ciao, sono qui con te. Ho controllato tutto: i sistemi funzionano bene, "
                        "e possiamo cominciare quando vuoi."),
}
qwen_broken = not (os.path.exists(os.path.join(QWEN_DIR, "config.json")) and os.path.exists(QWEN_VOICE))


def qwen_voice(voice):
    """The cloned voice to use: an unknown or missing one falls back to JARVIS's."""
    if voice in QWEN_VOICES and os.path.exists(QWEN_VOICES[voice][0]):
        return voice
    return "qwen3"


def qwen_worker():
    global qwen_broken
    try:
        import mlx.core as mx
        import soundfile
        from mlx_audio.tts.utils import load

        model = load(QWEN_DIR)
        references = {}

        def reference(voice):
            if voice not in references:
                references[voice] = mx.array(soundfile.read(QWEN_VOICES[voice][0], dtype="float32")[0])
            return references[voice], QWEN_VOICES[voice][1]

        def generate(text, speed, voice="qwen3"):
            audio, said = reference(voice)
            mx.random.seed(QWEN_SEED)
            parts = model.generate(text=text, ref_audio=audio, ref_text=said,
                                   lang_code="italian", speed=speed, verbose=False, **QWEN_SAMPLING)
            samples = np.concatenate([np.asarray(part.audio).squeeze() for part in parts])
            mx.clear_cache()
            return samples

        def generate_pieces(text, speed, job, voice):
            first = True
            audio, said = reference(voice)
            mx.random.seed(QWEN_SEED)
            for part in model.generate(text=text, ref_audio=audio, ref_text=said,
                                       lang_code="italian", speed=speed, verbose=False, stream=True,
                                       streaming_interval=QWEN_STREAM_INTERVAL, **QWEN_SAMPLING):
                if job.get("cancel"):
                    break
                samples = np.asarray(part.audio).squeeze()
                if first:
                    samples = trim_leading_silence(samples, 24000)
                    first = False
                if len(samples):
                    job["pieces"].put(np.asarray(samples, dtype="<f4").tobytes())
            mx.clear_cache()

        generate("Pronto.", 1.0)
    except Exception:  # noqa: BLE001
        qwen_broken = True
        qwen_ready.set()
        return
    qwen_ready.set()
    while True:
        text, speed, job = qwen_jobs.get()
        voice = job.get("voice", "qwen3")
        try:
            if "pieces" in job:
                generate_pieces(text, speed, job, voice)
            else:
                job["audio"] = np.asarray(trim_silence(generate(text, speed, voice), 24000), dtype="<f4").tobytes()
        except Exception as error:  # noqa: BLE001
            job["error"] = error
        if "pieces" in job:
            job["pieces"].put(None)
        job["done"].set()


def saved_clip_path(text, speed, voice="qwen3"):
    model = "" if os.path.basename(QWEN_DIR) == "qwen3" else os.path.basename(QWEN_DIR) + "|"
    # The sampling is part of the key: clips saved with another intonation are not reused.
    sampling = f"t{QWEN_SAMPLING['temperature']}p{QWEN_SAMPLING['top_p']}s{QWEN_SEED}|"
    # JARVIS's voice keeps its old key (its saved clips stay valid); the others add their name.
    name = "" if voice == "qwen3" else voice + "|"
    key = f"{name}{os.path.getsize(QWEN_VOICES[voice][0])}|{model}{sampling}{speed}|{text}"
    return os.path.join(QWEN_CLIPS, hashlib.sha256(key.encode()).hexdigest() + ".pcm")


def saved_clip(text, speed, voice="qwen3"):
    with open(saved_clip_path(text, speed, qwen_voice(voice)), "rb") as file:
        return file.read()


def synth_qwen(text, speed, keep, voice="qwen3"):
    voice = qwen_voice(voice)
    clip = None
    if keep:
        clip = saved_clip_path(text, speed, voice)
        if os.path.exists(clip):
            return saved_clip(text, speed, voice)
    spoken = italian_speech(text)
    if not spoken or not qwen_ready.wait(60) or qwen_broken:
        raise RuntimeError("Qwen3 non disponibile")
    job = {"done": threading.Event(), "voice": voice}
    qwen_jobs.put((spoken, speed, job))
    if not job["done"].wait(80):
        raise RuntimeError("Qwen3 non ha risposto")
    if "error" in job:
        raise job["error"]
    if clip:
        os.makedirs(QWEN_CLIPS, exist_ok=True)
        with open(clip, "wb") as file:
            file.write(job["audio"])
    return job["audio"]


def stream_qwen(text, speed, voice="qwen3"):
    """Starts a Qwen3 sentence in pieces; returns the job, or None if Qwen3 cannot do it."""
    spoken = italian_speech(text)
    if not spoken or not qwen_ready.wait(60) or qwen_broken:
        return None
    job = {"done": threading.Event(), "pieces": queue.Queue(), "voice": qwen_voice(voice)}
    qwen_jobs.put((spoken, speed, job))
    return job


def saved_if_any(text, speed, voice="qwen3"):
    """The saved audio of this sentence, or None. Its date is refreshed: the least used go first."""
    try:
        voice = qwen_voice(voice)
        clip = saved_clip_path(text, speed, voice)
        if not os.path.exists(clip):
            return None
        os.utime(clip)
        return saved_clip(text, speed, voice)
    except OSError:
        return None


def remember_repeat(text, speed, audio, voice="qwen3"):
    """A whole streamed sentence: the second time it is heard, its audio is saved."""
    if len(text) > REPEAT_MAX_CHARS or not audio:
        return
    with repeat_lock:
        try:
            clip = saved_clip_path(text, speed, qwen_voice(voice))
            key = os.path.basename(clip)[:-4]
            os.makedirs(QWEN_CLIPS, exist_ok=True)
            try:
                with open(REPEAT_SEEN) as file:
                    seen = json.load(file)
            except (OSError, ValueError):
                seen = []
            if key in seen:
                with open(clip + ".tmp", "wb") as file:
                    file.write(audio)
                os.replace(clip + ".tmp", clip)
                seen.remove(key)
                prune_clips()
            else:
                seen = (seen + [key])[-REPEAT_SEEN_MAX:]
            with open(REPEAT_SEEN + ".tmp", "w") as file:
                json.dump(seen, file)
            os.replace(REPEAT_SEEN + ".tmp", REPEAT_SEEN)
        except OSError:
            pass


def prune_clips():
    """Over the limit, the clips not used for the longest time go (acknowledgments are made again at start)."""
    clips = [os.path.join(QWEN_CLIPS, name) for name in os.listdir(QWEN_CLIPS) if name.endswith(".pcm")]
    sizes = {path: os.path.getsize(path) for path in clips}
    total = sum(sizes.values())
    for path in sorted(clips, key=os.path.getmtime):
        if total <= CLIPS_MAX_BYTES:
            break
        os.remove(path)
        total -= sizes[path]


# Qwen3 fallback: it loads only when needed, so it takes no memory.
supertonic = None
supertonic_failed = False
supertonic_styles = {}


def load_supertonic():
    global supertonic, supertonic_failed
    if supertonic is None and not supertonic_failed:
        try:
            from supertonic import TTS

            supertonic = TTS(model_dir=os.path.join(HERE, "supertonic"), auto_download=False)
        except Exception:  # noqa: BLE001
            supertonic_failed = True
    return supertonic


UNITS = {"%": "per cento", "€": "euro", "$": "dollari", "£": "sterline", "°C": "gradi", "°": "gradi",
         "kg": "chili", "km": "chilometri", "cm": "centimetri", "mm": "millimetri", "GB": "gigabyte",
         "MB": "megabyte", "TB": "terabyte", "kHz": "chilohertz", "Hz": "hertz", "mAh": "milliampere ora",
         "V": "volt", "W": "watt"}
ONES = ["zero", "uno", "due", "tre", "quattro", "cinque", "sei", "sette", "otto", "nove", "dieci", "undici",
        "dodici", "tredici", "quattordici", "quindici", "sedici", "diciassette", "diciotto", "diciannove"]
TENS = ["", "", "venti", "trenta", "quaranta", "cinquanta", "sessanta", "settanta", "ottanta", "novanta"]


def number_words(n):
    if n < 20:
        return ONES[n]
    if n < 100:
        tens, unit = divmod(n, 10)
        word = TENS[tens]
        if unit in (1, 8):
            word = word[:-1]
        return word + ("tré" if unit == 3 else ONES[unit] if unit else "")
    if n < 1000:
        hundreds, rest = divmod(n, 100)
        word = ("" if hundreds == 1 else ONES[hundreds]) + "cento"
        if rest // 10 == 8:
            word = word[:-1]
        return word + (number_words(rest) if rest else "")
    if n < 1000000:
        thousands, rest = divmod(n, 1000)
        word = "mille" if thousands == 1 else number_words(thousands) + "mila"
        return word + (" " + number_words(rest) if rest else "")
    if n < 1000000000:
        millions, rest = divmod(n, 1000000)
        word = "un milione" if millions == 1 else number_words(millions) + " milioni"
        return word + (" " + number_words(rest) if rest else "")
    return " ".join(ONES[int(c)] for c in str(n))


def digits_words(digits):
    return " ".join(ONES[int(c)] for c in digits) if digits.startswith("0") and len(digits) > 1 else number_words(int(digits))


def italian_speech(text):
    """Numbers and symbols as words: Supertonic alone misreads them ("149,99 €" became "59,99")."""
    t = re.sub(r"\b(\d{1,2}):(\d{2})\b",
               lambda m: number_words(int(m[1])) + (" e " + number_words(int(m[2])) if m[2] != "00" else ""), text)
    t = re.sub(r"\b(\d{1,3}(?:\.\d{3})+)\b", lambda m: m[1].replace(".", ""), t)
    t = re.sub(r"([€$£])\s?(\d+(?:,\d+)?)", r"\2 \1", t)
    units = "|".join(sorted(map(re.escape, UNITS), key=len, reverse=True))
    t = re.sub(r"(\d)\s?(" + units + r")(?![A-Za-zÀ-ÿ])", lambda m: m[1] + " " + UNITS[m[2]], t)
    t = re.sub(r"\b(\d+)[,.](\d+)\b",
               lambda m: digits_words(m[1]) + " virgola "
               + (number_words(int(m[2])) if len(m[2]) == 2 and m[2][0] != "0" else " ".join(ONES[int(c)] for c in m[2])), t)
    t = re.sub(r"\d+", lambda m: digits_words(m[0]), t)
    t = t.replace("C++", "C più più").replace(" & ", " e ").replace("_", " ")
    t = re.sub(r"[^\w\s.,;:!?'’\"«»()\-–—…]", " ", t)
    return re.sub(r"\s+", " ", t).strip()


def supertonic_style(voice):
    """A voice ("M4") or a blend ("M4:0.5+M3:0.5")."""
    from supertonic.core import Style

    if voice not in supertonic_styles:
        ttl = dp = 0
        for part in voice.split("+"):
            name, _, weight = part.partition(":")
            style = supertonic.get_voice_style(voice_name=name.strip())
            ttl = ttl + float(weight or 1) * style.ttl
            dp = dp + float(weight or 1) * style.dp
        supertonic_styles[voice] = Style(ttl, dp)
    return supertonic_styles[voice]


def trim_silence(samples, rate):
    samples = np.asarray(samples, dtype=np.float32).squeeze()
    audible = np.abs(samples) > 0.005
    if audible.any():
        where = np.flatnonzero(audible)
        samples = samples[max(0, where[0] - int(0.02 * rate)):where[-1] + int(0.12 * rate)]
    return samples


def trim_leading_silence(samples, rate):
    samples = np.asarray(samples, dtype=np.float32).squeeze()
    audible = np.flatnonzero(np.abs(samples) > 0.005)
    return samples[max(0, audible[0] - int(0.02 * rate)):] if len(audible) else samples[:0]


def to_app_rate(samples, rate, target=24000):
    """From 44.1 kHz to the 24 kHz the app plays, with a filter that avoids hiss."""
    samples = trim_silence(samples, rate)
    n = np.arange(101) - 50
    cutoff = 0.95 * target / rate
    fir = np.sinc(cutoff * n) * cutoff * np.hamming(101)
    filtered = np.convolve(samples, fir, mode="same")
    count = int(len(filtered) * target / rate)
    return np.interp(np.arange(count) * rate / target, np.arange(len(filtered)), filtered)


def synth_supertonic(text, voice, speed):
    spoken = italian_speech(text)
    if not spoken:
        raise ValueError("testo vuoto")
    with lock:
        samples, _ = supertonic.synthesize(spoken, voice_style=supertonic_style(voice), lang="it", speed=speed)
    return np.asarray(to_app_rate(samples, supertonic.sample_rate), dtype="<f4").tobytes()


def voice_style(voice):
    """A voice ("bm_lewis") or a blend ("im_nicola:0.5+bm_fable:0.5")."""
    if "+" not in voice and ":" not in voice:
        return voice
    style = 0
    for part in voice.split("+"):
        name, _, weight = part.partition(":")
        style = style + float(weight or 1) * kokoro.get_voice_style(name.strip())
    return style


def synth(text, voice, lang, speed, fallback=None, keep=False):
    if voice in QWEN_VOICES:
        try:
            return synth_qwen(text, speed, keep, voice)
        except Exception:  # noqa: BLE001
            voice, speed = "supertonic:" + SUPERTONIC_VOICE, 1.2
    if voice.startswith("supertonic:"):
        try:
            if load_supertonic() is None:
                raise RuntimeError("Supertonic non disponibile")
            return synth_supertonic(text, voice.removeprefix("supertonic:"), speed)
        except Exception:  # noqa: BLE001
            if not fallback:
                raise
            voice = fallback
    with lock:
        samples, _ = kokoro.create(text, voice=voice_style(voice), speed=speed, lang=lang)
    return np.asarray(samples, dtype="<f4").tobytes()


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        # JARVIS only: right token, no requests from browser web pages.
        host = self.headers.get("Host", "")
        if (self.headers.get("Authorization") != f"Bearer {TOKEN}" or self.headers.get("Origin")
                or not host.startswith(("127.0.0.1:", "localhost:"))):
            self.send_error(403)
            return
        if self.path not in ("/synth", "/embed"):
            self.send_error(404)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length)
            if self.path == "/embed":
                audio = embed(body)
            else:
                req = json.loads(body)
                if req.get("stream") and req.get("voice") in QWEN_VOICES and self.send_pieces(req):
                    return
                audio = saved_clip(req["text"], float(req.get("speed", 1.0)), req.get("voice", "qwen3")) if req.get("onlySaved") else synth(req["text"], req.get("voice", "bm_lewis"), req.get("lang", "en-gb"), float(req.get("speed", 1.0)), req.get("fallback"), bool(req.get("keep")))
        except Exception as error:  # noqa: BLE001 - the error must be returned to the app
            body = str(error).encode()
            self.send_response(500)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(audio)))
        self.end_headers()
        self.wfile.write(audio)

    def send_pieces(self, req):
        """The sentence in pieces as Qwen3 makes them. False if no piece came: the normal path takes over."""
        text, speed, voice = req["text"], float(req.get("speed", 1.0)), req.get("voice", "qwen3")
        saved = saved_if_any(text, speed, voice)
        if saved:
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(saved)))
            self.end_headers()
            try:
                self.wfile.write(saved)
            except OSError:
                pass
            return True
        job = stream_qwen(text, speed, voice)
        if job is None:
            return False
        try:
            first = job["pieces"].get(timeout=80)
        except queue.Empty:
            job["cancel"] = True
            return False
        if first is None:
            return False
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.end_headers()
        piece = first
        heard = []
        try:
            while piece is not None:
                self.wfile.write(piece)
                self.wfile.flush()
                heard.append(piece)
                piece = job["pieces"].get(timeout=80)
        except (OSError, queue.Empty):
            # JARVIS stopped listening ("basta"): the rest of the sentence is not generated.
            job["cancel"] = True
            return True
        # Only a sentence heard whole and without errors may be kept.
        if job["done"].wait(5) and "error" not in job and not job.get("cancel"):
            remember_repeat(text, speed, b"".join(heard), voice)
        return True

    def log_message(self, *args):
        pass


def watch_parent(parent_pid):
    while True:
        time.sleep(2)
        if os.getppid() != parent_pid:
            os._exit(0)


if __name__ == "__main__":
    threading.Thread(target=watch_parent, args=(os.getppid(),), daemon=True).start()
    # A first dry synthesis: loads everything into memory, so the first real sentence is fast.
    synth("Ready.", "bm_lewis", "en-gb", 1.0)
    if qwen_broken:
        qwen_ready.set()
    else:
        threading.Thread(target=qwen_worker, daemon=True).start()
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    print(f"PORT {server.server_address[1]}", flush=True)
    server.serve_forever()
