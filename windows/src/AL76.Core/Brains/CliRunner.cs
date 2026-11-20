using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AL76.Core.Brains;

/// <summary>
/// Runs a brain's CLI that prints one JSON object per line and hands every line over. The arguments go
/// through ArgumentList, so Windows quoting is right for any text; stdin is empty (without it the CLIs wait
/// for input that never comes); stderr is kept, because there the CLIs explain a missing login.
/// </summary>
public static class CliRunner
{
    public sealed class Run
    {
        public StringBuilder Errors { get; } = new();
        public int? ExitCode { get; internal set; }
    }

    public static async IAsyncEnumerable<JsonObject> JsonLinesAsync(string exe, IEnumerable<string> args, string workDir,
        IReadOnlyDictionary<string, string>? env, Run run, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        // Claude Code refuses to start inside another Claude Code session (the bench may run in one).
        psi.Environment.Remove("CLAUDECODE");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} non partito");
        process.StandardInput.Close();
        var lines = Channel.CreateUnbounded<JsonObject>();
        var errors = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int n;
            while ((n = await process.StandardError.ReadAsync(buffer)) > 0)
                lock (run.Errors) if (run.Errors.Length < 20_000) run.Errors.Append(buffer, 0, n);
        });
        var reader = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (line.Length == 0 || line[0] != '{') continue;
                    if (ParseLenient(line) is JsonObject json) await lines.Writer.WriteAsync(json);
                }
            }
            finally { lines.Writer.TryComplete(); }
        });

        using var kill = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        await foreach (var json in lines.Reader.ReadAllAsync(CancellationToken.None))
        {
            ct.ThrowIfCancellationRequested();
            yield return json;
        }
        await reader;
        await errors;
        await process.WaitForExitAsync(CancellationToken.None);
        run.ExitCode = process.ExitCode;
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// One JSON line, also when a key repeats: Codex writes some items with "id" twice, which JsonNode refuses
    /// (17 November: the whole answer was lost). The last value wins, as in most JSON readers.
    /// </summary>
    public static JsonNode? ParseLenient(string line)
    {
        // Converted eagerly: JsonNode.Parse finds a repeated key only later, when a property is first read.
        try
        {
            using var doc = JsonDocument.Parse(line);
            return Convert(doc.RootElement);
        }
        catch (JsonException) { return null; }

        static JsonNode? Convert(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => Obj(e),
            JsonValueKind.Array => new JsonArray(e.EnumerateArray().Select(Convert).ToArray()),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => JsonValue.Create(e.Clone()),
        };
        static JsonObject Obj(JsonElement e)
        {
            var o = new JsonObject();
            foreach (var p in e.EnumerateObject()) o[p.Name] = Convert(p.Value);
            return o;
        }
    }

    /// <summary>Runs a short command (a status check) and returns its output, or null when it cannot start or times out.</summary>
    public static async Task<string?> OutputAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("CLAUDECODE");
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            process.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = process.StandardError.ReadToEndAsync(cts.Token);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } return null; }
            return await output + await error;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>The last line of an error message, without terminal colours: what the user can read.</summary>
    public static string LastLine(string text)
    {
        string clean = System.Text.RegularExpressions.Regex.Replace(text, @"\u001B\[[0-9;]*m", "");
        var line = clean.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "nessuna risposta";
        return line.Length > 200 ? line[..200] : line;
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Finds a CLI: first the usual install folders (the app does not inherit a terminal's PATH), then PATH.
    /// On Windows only real .exe files count: npm's .cmd shims would pass the instructions through cmd.exe,
    /// which breaks on quotes and new lines.
    /// </summary>
    public static string? Find(string name, IEnumerable<string>? extra = null)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>(extra ?? []);
        if (IsWindows)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.AddRange([
                Path.Combine(home, ".local", "bin", name + ".exe"),
                Path.Combine(local, "Programs", name, name + ".exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", name + ".exe"),
                Path.Combine(roaming, "npm", name + ".exe"),
                // Antigravity's installer (sources disagree on the folder).
                Path.Combine(local, name, "bin", name + ".exe"),
                Path.Combine(local, "Antigravity", name + ".exe"),
                Path.Combine(local, "Programs", "Antigravity", name + ".exe"),
            ]);
        }
        else
        {
            candidates.AddRange(["/opt/homebrew/bin/" + name, "/usr/local/bin/" + name, Path.Combine(home, ".local", "bin", name),
                                 Path.Combine(home, ".npm-global", "bin", name)]);
        }
        // The PATH of this process, then the saved one: an installer that ran after AL-76 started changed only the saved one.
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (IsWindows)
            path += Path.PathSeparator + Environment.ExpandEnvironmentVariables(
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) + Path.PathSeparator
                + Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Distinct())
            candidates.Add(Path.Combine(dir, IsWindows ? name + ".exe" : name));
        return candidates.FirstOrDefault(File.Exists);
    }
}
