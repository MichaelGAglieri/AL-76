using AL76.Core.Maker;
using AL76.Core.Projects;

namespace AL76.Core.Tests;

public class ProjectTests
{
    private sealed class FakeShell : IShell
    {
        public List<string> Calls { get; } = [];
        public Func<string[], (int, string, string)> Answer { get; set; } = _ => (0, "", "");
        public Task<(int code, string output, string error)> RunAsync(string exe, IEnumerable<string> args, TimeSpan limit, CancellationToken ct)
        {
            var a = args.ToArray();
            Calls.Add(exe + " " + string.Join(" ", a));
            return Task.FromResult(Answer(a));
        }
    }

    private static string Home(string name)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".al76-test-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task FindsProjectsAndCollections()
    {
        string root = Home("Projects");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "MioRobot"));
            Directory.CreateDirectory(Path.Combine(root, "Lab_Projects", "Rover"));
            Directory.CreateDirectory(Path.Combine(root, ".nascosta"));
            File.WriteAllText(Path.Combine(root, "MioRobot", "README.md"), "# Mio robot\nUn braccio.");
            Directory.CreateDirectory(Path.Combine(root, "MioRobot", "node_modules"));
            File.WriteAllText(Path.Combine(root, "MioRobot", "node_modules", "x.js"), "x");
            File.WriteAllText(Path.Combine(root, "MioRobot", "main.py"), "print(1)");
            var desk = new ProjectDesk(() => [root], new FakeShell(), _ => true);
            Assert.Equal(["MioRobot", "Rover"], desk.Folders().Select(Path.GetFileName));
            Assert.EndsWith("Rover", desk.Find("rover"));
            Assert.EndsWith("MioRobot", desk.Find("mio robot"));
            string info = await desk.InfoAsync("miorobot", true, CancellationToken.None);
            Assert.Contains("Un braccio.", info);
            Assert.Contains("Non è un repository git", info);
            Assert.Contains("main.py", info);
            Assert.DoesNotContain("x.js", info);
            Assert.Contains("MioRobot: 2 file", desk.RecentActivity(7, true, DateTime.Now));
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
    }

    [Fact]
    public async Task CloneOnlyPublicHostsInsideHome()
    {
        string root = Home("Projects");
        try
        {
            var shell = new FakeShell();
            var desk = new ProjectDesk(() => [root], shell, _ => true);
            Assert.Contains("Scarico solo", await desk.CloneAsync("https://evil.example/a/b", "", "", true, CancellationToken.None));
            Assert.Contains("Scarico solo", await desk.CloneAsync("https://github.com/solo", "", "", true, CancellationToken.None));
            Assert.Contains("cartella utente", await desk.CloneAsync("https://github.com/a/b", OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc", "", true, CancellationToken.None));
            shell.Answer = a => a.Contains("ls-remote") ? (2, "", "not found") : (0, "git version 2", "");
            Assert.Contains("non esiste o non è pubblico", await desk.CloneAsync("github.com/a/b.git", "", "", true, CancellationToken.None));
            shell.Answer = _ => (0, "", "");
            string ok = await desk.CloneAsync("https://github.com/owner/repo", "", "", true, CancellationToken.None);
            Assert.StartsWith("Scaricato https://github.com/owner/repo in " + Path.Combine(root, "repo"), ok);
            var clone = shell.Calls.Last();
            Assert.Contains("--no-recurse-submodules", clone);
            Assert.Contains("core.hooksPath=NUL", clone);
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
    }

    [Fact]
    public async Task RestoreTakesTheCopyOfThatFileOnlyWithItsBytes()
    {
        string dir = Home("code");
        string data = Path.Combine(Path.GetDirectoryName(dir)!, "data");
        try
        {
            string a = Path.Combine(dir, "A", "main.py"), b = Path.Combine(dir, "B", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(a)!);
            Directory.CreateDirectory(Path.GetDirectoryName(b)!);
            byte[] bom = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("x = 1\n")];
            File.WriteAllBytes(a, bom);
            File.WriteAllText(b, "y = 1\n");
            var helper = new CodeHelper(Path.Combine(data, "backup"), () => null, _ => true, new FakeShell());
            await helper.EditAsync(a, "x = 2\n", "rewrite", true, CancellationToken.None);
            await helper.EditAsync(a, "x = 3\n", "rewrite", true, CancellationToken.None);   // same second: both copies kept
            await helper.EditAsync(b, "y = 2\n", "rewrite", true, CancellationToken.None);
            Assert.Contains("com'era", await helper.RestoreAsync(a, true, CancellationToken.None));
            Assert.Contains("com'era", await helper.RestoreAsync(a, true, CancellationToken.None));
            Assert.Equal(bom, File.ReadAllBytes(a));   // the original, BOM included, not B's code
            Assert.Equal("y = 2\n", File.ReadAllText(b));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public async Task CommentsModeKeepsCodeAndRestores()
    {
        string dir = Home("code");
        string data = Path.Combine(Path.GetDirectoryName(dir)!, "data");
        try
        {
            string file = Path.Combine(dir, "motore.cpp");
            File.WriteAllText(file, "int main() {\r\n    return 0; // fine\r\n}\r\n");
            var helper = new CodeHelper(Path.Combine(data, "backup"), () => null, _ => true, new FakeShell());
            string read = await helper.ReadAsync(file, null, null, true, CancellationToken.None);
            Assert.Contains("   2      return 0; // fine", read);
            string refused = await helper.EditAsync(file, "int main() {\n  return 0;\n}\n", "comments", true, CancellationToken.None);
            Assert.StartsWith("Refused", refused);
            string saved = await helper.EditAsync(file, "// Punto di ingresso\nint main() {\n    return 0; // esce\n}\n", "comments", true, CancellationToken.None);
            Assert.StartsWith("Salvato motore.cpp (solo commenti, +1 righe)", saved);
            Assert.Equal("// Punto di ingresso\r\nint main() {\r\n    return 0; // esce\r\n}\r\n", File.ReadAllText(file));
            Assert.Contains("com'era", await helper.RestoreAsync(file, true, CancellationToken.None));
            Assert.Equal("int main() {\r\n    return 0; // fine\r\n}\r\n", File.ReadAllText(file));
            Assert.Contains("Non ho copie", await helper.RestoreAsync(file, true, CancellationToken.None));
            Assert.Contains("non è un file di codice", await helper.ReadAsync(Path.Combine(dir, "foto.jpg"), null, null, true, CancellationToken.None));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void CodeLinesIgnoreCommentsInsideStrings()
    {
        Assert.Equal(["x = \"# non è un commento\""], CodeHelper.CodeLines("x = \"# non è un commento\"  # questo sì\n\n# solo commento", ".py"));
        Assert.Equal(["    a++;"], CodeHelper.CodeLines("    a++; // uno\n// due", ".c"));
    }

    [Fact]
    public void VsCodeFoldersFromStorage()
    {
        string dir = Home("vscode");
        try
        {
            string folder = Path.Combine(dir, "Progetto");
            Directory.CreateDirectory(folder);
            string storage = Path.Combine(dir, "storage.json");
            var json = new System.Text.Json.Nodes.JsonObject
            {
                ["windowsState"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["lastActiveWindow"] = new System.Text.Json.Nodes.JsonObject { ["folder"] = new Uri(folder).AbsoluteUri },
                    ["openedWindows"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["folder"] = "file:///nessuna/cartella" }),
                },
            };
            File.WriteAllText(storage, json.ToJsonString());
            Assert.Equal([folder], CodeHelper.VsCodeFolders(storage));
            Assert.Empty(CodeHelper.VsCodeFolders(Path.Combine(dir, "manca.json")));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }
}
