// Development tool: passes WAV files through the voice loop and measures speed.
//   AL76.Bench <models folder> <wake model> <command model> <language> <wav>...   one configuration
//   AL76.Bench                                                                 speed matrix (for the VM):
//       models from %LOCALAPPDATA%\AL-76\models (downloaded if missing), WAVs from ../prove,
//       results printed and written to bench.txt next to the exe.
using System.Diagnostics;
using System.Text;
using AL76.Core.Voice;
using AL76.Speech;

const string EchoIt = "Oggi è lunedì sedici novembre duemilaventisei. Fuori ci sono dodici gradi e il cielo è sereno, domani potrebbe piovere nel pomeriggio.";
const string EchoEn = "Today is Monday, November sixteenth. It is twelve degrees outside and the sky is clear, it may rain tomorrow afternoon.";

if (args.Length >= 1 && args[0] == "web")
{
    // AL76.Bench web: the free web services the direct commands use (weather, search), live.
    Console.WriteLine(await AL76.Core.Web.WebActions.WeatherAsync("Bologna", true));
    var found = await AL76.Core.Web.WebActions.SearchAsync("Alessandro Manzoni Promessi sposi");
    Console.WriteLine($"{found.Count} risultati: " + string.Join(" | ", found.Take(3).Select(r => $"{r.Title} [{r.Url}]")));
    return;
}

if (args.Length >= 1 && args[0] == "brains")
{
    // AL76.Bench brains [claude chatgpt gemini local:<ollama model>]: the same requests to every brain (phase 2).
    await BrainBench.RunAsync(args[1..], Console.WriteLine);
    return;
}

if (args.Length >= 3 && args[0] == "voices")
{
    // AL76.Bench voices <kokoro .onnx> <output folder> [voice...]: one sample per Italian and English voice, with speed.
    using var k = new KokoroSpeaker(args[1]);
    Directory.CreateDirectory(args[2]);
    var wanted = args.Length > 3 ? args[3..] : KokoroSpeaker.Voices("it").Concat(KokoroSpeaker.Voices("en")).ToArray();
    await k.SynthesizeAsync("Ciao.", wanted[0]);   // warm-up
    foreach (var v in wanted)
    {
        bool it = v.StartsWith('i');
        string text = it ? "Ciao, sono AL-76. Oggi è lunedì sedici novembre, fuori ci sono dodici gradi. Vuoi che ti ricordi la lezione di domani alle nove?"
                         : "Hi, I'm AL-76. Today is Monday, November sixteenth, and it's twelve degrees outside. Shall I remind you about tomorrow's class at nine?";
        var clock = Stopwatch.StartNew();
        var samples = await k.SynthesizeAsync(text, v);
        double secs = samples.Length / (double)KokoroSpeaker.SampleRate;
        string file = Path.Combine(args[2], $"kokoro-{v.Replace(':', '_').Replace('+', '-')}.wav");
        Wav.Write(file, samples, KokoroSpeaker.SampleRate);
        Console.WriteLine($"{v} | {secs:0.0} s di audio in {clock.Elapsed.TotalSeconds:0.00} s ({clock.Elapsed.TotalSeconds / secs:0.00}× tempo reale) | {Path.GetFileName(file)}");
    }
    return;
}

if (args.Length >= 2 && args[0] == "kokoro")
{
    // AL76.Bench kokoro <models folder>: only the Kokoro report (fp32 vs int8), samples next to the models.
    await KokoroReport(new ModelStore(args[1]), AppContext.BaseDirectory, args[1], Console.WriteLine);
    return;
}

if (args.Length >= 5 && args[0] == "barge")
{
    // AL76.Bench barge <models> <model> <lang> <wav>...: "basta" over AL-76's own voice, as BargeIn sees it.
    var store = new ModelStore(args[1]);
    using var t = new WhisperTranscriber(await store.EnsureAsync(args[2]), args[3]);
    foreach (var f in args[4..]) Console.WriteLine(await Barge(t, f, args[3]));
    return;
}

if (args.Length >= 5)
{
    // AL76_FIT=0: full 30 s window. AL76_VAD=0: whole file as one phrase (to tell a VAD cut from a Whisper miss).
    var store = new ModelStore(args[0]);
    string lang = args[3];
    var tuning = new WhisperTuning(FitContext: Environment.GetEnvironmentVariable("AL76_FIT") != "0");
    using var wake = new WhisperTranscriber(await store.EnsureAsync(args[1]), lang, tuning);
    using var command = args[2] == args[1] ? wake : new WhisperTranscriber(await store.EnsureAsync(args[2]), lang, tuning);
    var loop = new VoiceLoop(wake, command, null, new WakePhrase(new AL76.Core.Config.AppConfig().EffectiveWakePhrase(lang)), lang);
    var input = Environment.GetEnvironmentVariable("AL76_VAD") == "0"
        ? args[4..].Select(f => (f, Wav.ReadMono16k(f)))
        : Phrases(args[4..]);
    foreach (var (file, p) in input)
    {
        var r = await loop.HandleAsync(p);
        Console.WriteLine($"{Path.GetFileName(file)} | {p.Length / 16000.0:0.0} s | «{r.Heard}» | {r.Note} | {r.Reply} | {r.Elapsed.TotalSeconds:0.00} s");
    }
    return;
}

string models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AL-76", "models");
string here = AppContext.BaseDirectory;
string wavs = new[] { Path.Combine(here, "prove"), Path.Combine(here, "..", "prove") }.First(Directory.Exists);
var files = Directory.GetFiles(wavs, "f*.wav").OrderBy(f => f).ToArray();
var phrases = Phrases(files).ToList();
var output = new StringBuilder();
void Say(string line) { Console.WriteLine(line); output.AppendLine(line); File.WriteAllText(Path.Combine(here, "bench.txt"), output.ToString()); }

Say($"{DateTime.Now:yyyy-MM-dd HH:mm} · {Environment.ProcessorCount} core · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} · {phrases.Count} frasi");
var storeAll = new ModelStore(models);
int cores = Environment.ProcessorCount;
var configs = new (string name, string wake, string command, WhisperTuning tuning, bool fast)[]
{
    ("A attuale (30 s)", "base", "small", new(0, false), false),
    ("B veloce, 30 s", "base", "small", new(0, false), true),
    ("C contesto min 0", "base", "small", new(0, true, 0), true),
    ("D contesto min 384", "base", "small", new(0, true, 384), true),
    ("E contesto min 512", "base", "small", new(0, true, 512), true),
    ("F contesto min 768", "base", "small", new(0, true, 768), true),
    ("G E + tutti i core", "base", "small", new(cores, true, 512), true),
    ("H small-q8_0 min 512", "base", "small-q8_0", new(0, true, 512), true),
    ("I small-q5_1 min 512", "base", "small-q5_1", new(0, true, 512), true),
};
foreach (var c in configs)
{
    using var wake = new WhisperTranscriber(await storeAll.EnsureAsync(c.wake), "it", c.tuning);
    using var command = c.command == c.wake ? wake : new WhisperTranscriber(await storeAll.EnsureAsync(c.command), "it", c.tuning);
    var loop = new VoiceLoop(wake, command, null, new WakePhrase("Ehi Al"), "it") { FastInstant = c.fast };
    await loop.HandleAsync(phrases[0].samples);   // warm-up, not counted
    int right = 0;
    var times = new List<double>();
    var lines = new List<string>();
    foreach (var (file, p) in phrases)
    {
        var r = await loop.HandleAsync(p);
        bool ok = Expected(Path.GetFileNameWithoutExtension(file), r);
        if (ok) right++;
        if (r.Called) times.Add(r.Elapsed.TotalSeconds);
        lines.Add($"   {(ok ? "✓" : "✗")} {Path.GetFileName(file)} «{r.Heard}» {r.Note} {r.Elapsed.TotalSeconds:0.00} s");
    }
    Say($"{c.name,-20} | {right}/{phrases.Count} giuste | media con chiamata {times.DefaultIfEmpty(0).Average():0.00} s | massimo {times.DefaultIfEmpty(0).Max():0.00} s");
    lines.ForEach(Say);
}
foreach (var lang in new[] { "it", "en" })
{
    var bargeFiles = Directory.GetFiles(wavs, "*.wav").Where(f => Path.GetFileName(f) is var n
        && (n.StartsWith("basta") || n.StartsWith("stop") || n.StartsWith("soloeco")) && n.Contains($"-{lang}")).OrderBy(f => f).ToList();
    if (bargeFiles.Count == 0) continue;
    using var t = new WhisperTranscriber(await storeAll.EnsureAsync(lang == "en" ? "base.en" : "base"), lang);
    Say($"basta mentre parla ({lang}):");
    foreach (var f in bargeFiles) Say("   " + await Barge(t, f, lang));
}
await KokoroReport(storeAll, here, wavs, Say);
Say("fine");

// Kokoro, full and int8 model: time to the first sentence (streaming) and to the whole answer, and whether
// Whisper still understands it (words heard / words said).
static async Task KokoroReport(ModelStore store, string here, string outDir, Action<string> say)
{
    string voices = Directory.Exists(Path.Combine(here, "voices")) ? Path.Combine(here, "voices") : null!;
    using var judgeIt = new WhisperTranscriber(await store.EnsureAsync("small-q8_0"), "it", new WhisperTuning(FitContext: false));
    using var judgeEn = new WhisperTranscriber(await store.EnsureAsync("small.en-q8_0"), "en", new WhisperTuning(FitContext: false));
    const string It = "Sono le ventidue e quindici. Domani alle nove hai lezione di analisi, in aula tre. Vuoi che ti metta un promemoria?";
    const string En = "It's ten fifteen PM. Tomorrow at nine you have your calculus class, in room three. Shall I set a reminder?";
    foreach (var model in new[] { "kokoro-v1.0.onnx", "kokoro-v1.0.int8.onnx" })
    {
        try
        {
            using var k = new KokoroSpeaker(await store.EnsureAsync(model), voices);
            await k.SynthesizeAsync("Pronto.", "im_nicola");
            say($"voce Kokoro {model} (CPU):");
            foreach (var (v, text, judge) in new[] { ("im_nicola", It, judgeIt), ("af_heart", En, judgeEn) })
            {
                var clock = Stopwatch.StartNew();
                var firstPiece = Sentences.Split(text)[0];
                await k.SynthesizeAsync(firstPiece, v);
                double first = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var samples = await k.SynthesizeAsync(text, v);
                double whole = clock.Elapsed.TotalSeconds, secs = samples.Length / (double)KokoroSpeaker.SampleRate;
                string tag = model.Contains("int8") ? "int8" : "fp32";
                Wav.Write(Path.Combine(outDir, $"voce-campione-kokoro-{tag}-{v}.wav"), samples, KokoroSpeaker.SampleRate);
                string heard = await judge.TranscribeAsync(Wav.Resample(samples, KokoroSpeaker.SampleRate, 16000), "");
                say($"   {v}: prima frase in {first:0.00} s · tutto ({secs:0.0} s di audio) in {whole:0.00} s ({whole / secs:0.00}× tempo reale) · capito {WordMatch(text, heard):0}% «{heard}»");
            }
        }
        catch (Exception e) { say($"voce Kokoro {model}: errore {e.Message}"); }
    }
}

// Share of the said words that Whisper heard (numbers written as digits count as heard).
static double WordMatch(string said, string heard)
{
    var h = TextTools.NormalizePhrase(heard.Replace('\'', ' ')).Split(' ').ToHashSet();
    var s = TextTools.NormalizePhrase(said.Replace('\'', ' ')).Split(' ');
    string[] numbers = ["ventidue", "quindici", "nove", "tre", "ten", "fifteen", "nine", "three", "pm"];
    return 100.0 * s.Count(w => h.Contains(w) || numbers.Contains(w)) / s.Length;
}

// Slides a 1 s window every 0.4 s over the file, like BargeIn on the live microphone.
static async Task<string> Barge(WhisperTranscriber t, string file, string lang)
{
    var audio = Wav.ReadMono16k(file);
    string own = lang == "en" ? EchoEn : EchoIt;
    string name = Path.GetFileNameWithoutExtension(file);
    double? onset = int.TryParse(name.Split('-').Last(), out int ms) ? ms / 1000.0 : null;
    var times = new List<double>();
    double win = double.TryParse(Environment.GetEnvironmentVariable("AL76_WIN"), System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1.0;
    double step = double.TryParse(Environment.GetEnvironmentVariable("AL76_STEP"), System.Globalization.CultureInfo.InvariantCulture, out var st) ? st : 0.4;
    for (double now = step; now <= audio.Length / 16000.0 + 0.6; now += step)
    {
        int end = Math.Min(audio.Length, (int)(now * 16000));
        int start = Math.Max(0, end - (int)(win * 16000));
        var window = audio[start..end];
        if (BargeIn.Rms(window) < 0.01f) continue;
        var clock = Stopwatch.StartNew();
        string heard = await t.TranscribeAsync(window, "");
        times.Add(clock.Elapsed.TotalSeconds);
        if (Environment.GetEnvironmentVariable("AL76_VERBOSE") == "1") Console.WriteLine($"      {now:0.0} s «{heard}»");
        if (StopWords.IsStop(heard, own))
            return $"{name}: FERMATO a {now:0.0} s{(onset is double o ? $" ({now - o:0.0} s dopo la parola)" : " — FALSO STOP")} «{heard}» · finestra {times.Average():0.00} s";
    }
    return $"{name}: non fermato{(onset is null ? " (giusto)" : " — MANCATO")} · finestra {times.DefaultIfEmpty(0).Average():0.00} s";
}

static bool Expected(string name, PhraseResult r) => name switch
{
    "f1" or "f6" => r.Reply?.StartsWith("Sono le") == true || r.Reply?.StartsWith("È") == true,
    "f2" => r.Reply?.StartsWith("Oggi è") == true,
    "f3" => !r.Called,
    "f4" => r.Reply == "Dimmi.",
    "f5" => r.Command?.Contains("musica", StringComparison.OrdinalIgnoreCase) == true,
    _ => r.Called,
};

static IEnumerable<(string file, float[] samples)> Phrases(IEnumerable<string> wavFiles)
{
    foreach (var file in wavFiles)
    {
        var vad = new VoiceActivityDetector();
        var found = vad.Feed(Wav.ReadMono16k(file));
        if (vad.Flush() is float[] rest) found.Add(rest);
        foreach (var p in found) yield return (file, p);
    }
}
