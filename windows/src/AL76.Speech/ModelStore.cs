namespace AL76.Speech;

/// <summary>
/// Whisper models in %LOCALAPPDATA%\AL-76\models (any folder on the Mac), downloaded at first need from
/// the official whisper.cpp repository on Hugging Face. A partial download is kept as .part and resumed.
/// </summary>
public sealed class ModelStore(string folder)
{
    public const string Source = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public string Folder { get; } = folder;

    /// <summary>Kokoro 82M v1.0 in ONNX (Apache-2.0), the same files the Mac uses.</summary>
    public const string KokoroSource = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";
    /// <summary>"kokoro-v1.0.onnx" (310 MB) or "kokoro-v1.0.int8.onnx" (88 MB).</summary>
    public const string KokoroModel = "kokoro-v1.0.onnx";

    public string PathFor(string model) => Path.Combine(Folder, FileFor(model));

    public bool Has(string model) => File.Exists(PathFor(model));

    private static string FileFor(string model) => model.StartsWith("kokoro") || model == SherpaEmbedder.ModelFile ? model : $"ggml-{model}.bin";
    private static string UrlFor(string model) => model == SherpaEmbedder.ModelFile ? SherpaEmbedder.ModelUrl
        : model.StartsWith("kokoro") ? KokoroSource + model : Source + $"ggml-{model}.bin";

    /// <summary>The model file (Whisper name like "small-q8_0", or a Kokoro file name), downloading it if missing.
    /// <paramref name="progress"/> gets 0..1.</summary>
    public async Task<string> EnsureAsync(string model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string path = PathFor(model);
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(Folder);
        string part = path + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, UrlFor(model));
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        bool resumed = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) have = 0;
        long total = have + (response.Content.Headers.ContentLength ?? 0);

        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create))
        {
            var buffer = new byte[1 << 16];
            int read;
            long done = have;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        File.Move(part, path, overwrite: true);
        return path;
    }
}
