using System.Text.Json;
using System.Text.RegularExpressions;

namespace AL76.Core.Voice;

/// <summary>Who is speaking, as numbers (TitaNet through sherpa-onnx on Windows, the same model as the Mac).</summary>
public interface IVoiceEmbedder
{
    /// <summary>The voiceprint of 0.1–30 s of 16 kHz mono audio.</summary>
    Task<float[]> EmbedAsync(float[] samples, CancellationToken ct = default);
}

/// <summary>
/// The user's voiceprint (Mac: VoicePrint): a series of numbers for each recorded sentence, from which the audio cannot
/// be rebuilt. It lives only on this PC (voiceprint.json in the user's AL-76 folder).
/// </summary>
public sealed class VoicePrint(string file)
{
    private readonly object gate = new();
    private List<float[]> samples = Load(file);

    public bool IsEnrolled { get { lock (gate) return samples.Count > 0; } }
    public int Count { get { lock (gate) return samples.Count; } }

    private static List<float[]> Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<float[]>>(File.ReadAllText(file)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A sentence very different from the others of the same session is not the user's voice (someone spoke
    /// over it): left out. <paramref name="adding"/>: added to those already recorded (other distances and tones).</summary>
    public void Save(IReadOnlyList<float[]> embeddings, bool adding)
    {
        if (embeddings.Count == 0) return;
        var session = embeddings.Select(Normalized).ToList();
        var scores = Similarities(session);
        float median = scores.Order().ElementAt(scores.Count / 2);
        var kept = session.Where((_, i) => scores[i] >= median - 0.25f).ToList();
        lock (gate)
        {
            samples = adding ? [.. samples, .. kept] : kept;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(samples));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Delete()
    {
        lock (gate) samples = [];
        try { File.Delete(file); } catch (IOException) { }
    }

    /// <summary>Similarity with the three closest recorded sentences: a short word resembles some recording of the same
    /// word closely, less their average.</summary>
    public float Score(float[] embedding)
    {
        var e = Normalized(embedding);
        List<float[]> copy;
        lock (gate) copy = samples;
        var best = copy.Select(s => Dot(s, e)).OrderDescending().Take(3).ToList();
        return best.Count == 0 ? 0 : best.Average();
    }

    /// <summary>How much each recording resembles the others: how consistent the enrolment is.</summary>
    public List<float> SelfScores() { lock (gate) return Similarities(samples); }

    private static List<float> Similarities(List<float[]> list) =>
        list.Select((v, i) =>
        {
            var others = list.Where((_, j) => j != i).Select(o => Dot(o, v)).OrderDescending().Take(3).ToList();
            return others.Count == 0 ? 0 : others.Average();
        }).ToList();

    private static float Dot(float[] a, float[] b)
    {
        float s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }

    public static float[] Normalized(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm > 0 ? v.Select(x => x / norm).ToArray() : v;
    }
}

/// <summary>What a phrase is, for the voice check: the call (with or without a command after it), an answer to a
/// question of AL-76, or a dictated line.</summary>
public enum VoiceUse { Call, Answer, Dictation }

/// <summary>
/// "Is this the user's voice?" (Mac: Assistant.voiceAccepted, thresholds measured on voce.log, 12–17 November).
/// Modes: "off" never checks; "observe" (default) checks and logs but lets everything through; "on" refuses other
/// voices (the TV, a video, people in the room). A plain "no" always passes: it can only cancel.
/// </summary>
public sealed class VoiceCheck(VoicePrint print, IVoiceEmbedder embedder, Func<string> mode, Func<float> threshold)
{
    public const float NoisyFloorDb = -45, NoisyThreshold = 0.33f, NoisyLongThreshold = 0.40f, AnswerThreshold = 0.33f;
    public const float RelaxedThreshold = 0.36f, DoubtfulCall = 0.45f, FarCallDb = -22, LoneCallInTalk = 0.50f;
    public const int Rate = 16_000, ContinuousSpeech = 29 * Rate, LongestEmbedded = 30 * Rate, Window = 14_400;
    private (float score, DateTime at)? lastCall;

    public sealed record Verdict(bool Passed, float Score, float Threshold, string Note);

    public bool Active => mode() != "off" && print.IsEnrolled;

    /// <summary>The verdict, or null when there is nothing to check (off, not enrolled, the model missing).</summary>
    public async Task<Verdict?> CheckAsync(float[] samples, string heard, string? command, VoiceUse use, bool roomTalk, DateTime now, CancellationToken ct = default)
    {
        if (!Active) return null;
        bool answer = use == VoiceUse.Answer, strict = mode() == "on";
        double seconds = samples.Length / (double)Rate;
        if (samples.Length >= ContinuousSpeech)
            return new(!strict, 0, 1, $"{seconds:0.0} s di parlato senza pause: TV o altre persone");
        if (answer && IsNo(heard)) return new(true, 1, 0, "un no vale sempre");
        try
        {
            float whole = print.Score(await embedder.EmbedAsync(samples.Length > LongestEmbedded ? samples[..LongestEmbedded] : samples, ct));
            float score = whole;
            float noise = NoiseFloor(samples);
            // A long window where Whisper finds only the call is full of something else (TV, music).
            bool longAndEmpty = use == VoiceUse.Call && samples.Length >= 48_000 && (command ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 0;
            bool noisy = noise > NoisyFloorDb || longAndEmpty;
            float limit = samples.Length <= 40_000 && use == VoiceUse.Call ? NoisyThreshold : NoisyLongThreshold;
            float t = answer ? Math.Min(threshold(), AnswerThreshold) : noisy ? Math.Min(threshold(), limit) : threshold();
            string rule = "";
            // The conversation began with a call accepted only just: its answers do not get the low threshold.
            if (answer && lastCall is { } c && (now - c.at).TotalSeconds <= 180 && c.score < DoubtfulCall)
            {
                t = Math.Max(t, NoisyLongThreshold);
                rule = "la chiamata era passata di poco";
            }
            if (!noisy && !answer && command is not null)
            {
                if (command.Length == 0 && samples.Length <= 48_000) { t = Math.Min(t, 0.40f); rule = "chiamata da sola"; }
                else if (command.Length > 0 && (Commands.InstantCommands.Answer(command, "it", now) is not null || Commands.DirectCommand.Parse(command, now) is not null))
                { t = Math.Min(t, RelaxedThreshold); rule = "comando noto"; }
            }
            // A lone call while people talk, or from afar: Whisper copies its hint out of unclear talk.
            if (use == VoiceUse.Call && command is { Length: 0 } && (roomTalk || RoomFilter.Decibels(samples) < FarCallDb))
            {
                t = Math.Max(t, LoneCallInTalk);
                rule = roomTalk ? "chiamata da sola mentre si parla" : "chiamata da sola e lontana";
            }
            // With TV or music the phrase is mostly them: the window most like the user's voice counts.
            if (noisy && samples.Length > Window)
                for (int start = 0; start + Window <= samples.Length && score < t; start += Window / 3)
                    score = Math.Max(score, print.Score(await embedder.EmbedAsync(samples[start..(start + Window)], ct)));
            bool passed = score >= t;
            if (passed && use == VoiceUse.Call) lastCall = (score, now);
            string note = $"{score:0.000} {(passed ? "sì" : "no")} {mode()} [{seconds:0.0} s] intera {whole:0.000} rumore {noise:0} dB soglia {t:0.00}" + (rule.Length > 0 ? " · " + rule : "");
            return new(passed || !strict, score, t, note);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(true, 0, 0, "impronta non disponibile: " + e.Message);
        }
    }

    public static bool IsNo(string text) =>
        Regex.IsMatch(Commands.DirectCommand.Clean(text), @"^(?:no|no grazie|annulla|lascia stare|lascia perdere|niente|non farlo|no thanks|cancel|never mind|don't)$");

    /// <summary>Background noise: the typical level of the first 0.4 s, before the voice starts.</summary>
    public static float NoiseFloor(float[] samples)
    {
        var frames = new List<float>();
        for (int start = 0; start + 320 <= Math.Min(samples.Length, 6_400); start += 320)
        {
            float energy = 0;
            for (int i = start; i < start + 320; i++) energy += samples[i] * samples[i];
            frames.Add(10 * MathF.Log10(Math.Max(energy / 320, 1e-12f)));
        }
        if (frames.Count == 0) return -100;
        frames.Sort();
        return frames[frames.Count / 2];
    }
}

/// <summary>
/// Guided voice enrolment (Mac: VoiceEnrollment): one sentence at a time, kept only if it really contains the call (or
/// half the words of the command), so the TV or someone else do not end up in the voiceprint. The audio is not kept.
/// </summary>
public sealed class VoiceEnrollment
{
    public enum Kind { First, More, Commands }

    public static string[] FirstPrompts(string call, bool it) => it
        ? [call, call, call, call, $"{call} (più piano)", $"{call} (più piano)", $"{call} (più veloce)", $"{call} (più veloce)",
           $"{call} (allontanati un po' dal PC)", $"{call} (allontanati un po' dal PC)", $"{call} (come se fossi occupato)", $"{call} (come se fossi occupato)",
           $"{call}, che ore sono?", $"{call}, apri Word.", $"{call}, quali sono gli impegni di oggi?", $"{call}, apri il PDF di chimica.",
           $"{call}, ricordami di chiamare domani mattina.", $"{call}, che tempo fa oggi?"]
        : [call, call, call, call, $"{call} (more quietly)", $"{call} (more quietly)", $"{call} (faster)", $"{call} (faster)",
           $"{call} (step back from the PC)", $"{call} (step back from the PC)", $"{call} (as if you were busy)", $"{call} (as if you were busy)",
           $"{call}, what time is it?", $"{call}, open Word.", $"{call}, what's on today?", $"{call}, open the chemistry PDF.",
           $"{call}, remind me to call tomorrow morning.", $"{call}, what's the weather like today?"];

    public static string[] MorePrompts(string call, bool it) =>
        Enumerable.Repeat(it ? $"{call}\n(a ogni frase cambia distanza, direzione o tono)" : $"{call}\n(change distance, direction or tone each time)", 15).ToArray();

    public static string[] CommandPrompts(bool it) => it
        ? ["Apri Word.", "Chiudi Word.", "Che appuntamenti ho domani?", "Crea un evento domani alle quindici.", "Imposta un timer di venticinque minuti.",
           "Metti un po' di musica.", "Alza il volume.", "Che tempo fa oggi?", "Cerca il PDF di chimica.", "Inizio a studiare matematica.",
           "Quanto ho studiato questa settimana?", "Leggimi le mail nuove.", "Spiegami questo errore sullo schermo.", "Chiudi la scheda di YouTube.",
           "Sì, confermo.", "No, annulla.", "Un minuto.", "Che notizie ci sono?", "Dammi altre notizie.", "Come va lo studio?"]
        : ["Open Word.", "Close Word.", "What appointments do I have tomorrow?", "Create an event tomorrow at three.", "Set a timer for twenty-five minutes.",
           "Play some music.", "Turn the volume up.", "What's the weather like today?", "Find the chemistry PDF.", "I'm starting to study maths.",
           "How much did I study this week?", "Read me the new mail.", "Explain this error on the screen.", "Close the YouTube tab.",
           "Yes, confirm.", "No, cancel.", "One minute.", "What's in the news?", "Give me more news.", "How is studying going?"];

    private readonly WakePhrase call;
    private int refusals;
    public Kind Mode { get; }
    public string[] Prompts { get; }
    public int Step { get; private set; }
    public string Message { get; private set; }
    public List<float[]> Embeddings { get; } = [];
    public bool Italian { get; }
    public string Prompt => Step < Prompts.Length ? Prompts[Step] : "";
    public bool IsFinished => Step >= Prompts.Length;

    public VoiceEnrollment(Kind mode, WakePhrase call, string callName, bool it)
    {
        this.call = call;
        Mode = mode;
        Italian = it;
        Prompts = mode switch { Kind.More => MorePrompts(callName, it), Kind.Commands => CommandPrompts(it), _ => FirstPrompts(callName, it) };
        Message = it ? "Leggi la frase a voce normale, poi fai una breve pausa." : "Read the sentence in your normal voice, then pause briefly.";
    }

    /// <summary>The recorded sentence is the right one: the call, or at least half the words of the command.</summary>
    public bool Accepts(string heard)
    {
        if (Mode != Kind.Commands) return call.Command(heard) is not null;
        static HashSet<string> Words(string s) => Regex.Split(Web.NewsFeed.Fold(s), @"[^\p{L}]+").Where(w => w.Length > 0).ToHashSet();
        var expected = Words(Prompt);
        return expected.Count > 0 && expected.Intersect(Words(heard)).Count() / (double)expected.Count >= 0.5;
    }

    /// <summary>Whisper did not get the sentence. For commands, after three tries it moves on: one hard word must not lose the enrolment.</summary>
    public void Refused()
    {
        Message = Mode == Kind.Commands ? (Italian ? "Non ho capito la frase: rileggila." : "I did not get the sentence: read it again.")
                                        : (Italian ? "Non ho sentito la chiamata: ripeti la frase." : "I did not hear the call: say it again.");
        if (Mode != Kind.Commands) return;
        if (++refusals >= 3)
        {
            refusals = 0;
            Step++;
            Message = Italian ? "Questa la saltiamo: leggi la prossima." : "Let's skip this one: read the next.";
        }
    }

    public void Accept(float[] embedding)
    {
        refusals = 0;
        Embeddings.Add(embedding);
        Step++;
        Message = Italian ? "Bene." : "Good.";
    }
}
