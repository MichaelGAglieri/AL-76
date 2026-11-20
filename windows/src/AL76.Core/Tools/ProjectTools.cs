using AL76.Core.Projects;
using AL76.Core.Safety;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>The user's projects and the code open in VS Code (Mac: ProjectInsights, CodeHelper, cloneRepository).</summary>
public static class ProjectTools
{
    public static IEnumerable<ToolDef> Create(ProjectDesk projects, CodeHelper code, IPcActions pc)
    {
        yield return new ToolDef
        {
            Name = "list_projects",
            Description = "List the user's projects (folders in Projects, source\\repos, Documents\\GitHub or the places set in Settings).",
            Run = (a, c, _) => Task.FromResult(ToolOutcome.Ok(projects.List(c.Italian))),
        };
        yield return new ToolDef
        {
            Name = "project_info",
            Description = "Details of one project: README, git branch and recent commits, recently changed files.",
            Properties = new() { ["name"] = Prop("string", "Project name, e.g. MioRobot, Tesi.") },
            Required = ["name"],
            Run = async (a, c, ct) => ToolOutcome.Ok(await projects.InfoAsync(a.Str("name"), c.Italian, ct)),
        };
        yield return new ToolDef
        {
            Name = "recent_project_activity",
            Description = "Which projects were worked on recently.",
            Properties = new() { ["days"] = Prop("integer", "Days to look back, default 7.") },
            Run = (a, c, ct) => Task.Run(() => ToolOutcome.Ok(projects.RecentActivity(Math.Clamp(a.Int("days") ?? 7, 1, 90), c.Italian, DateTime.Now)), ct),
        };
        yield return new ToolDef
        {
            Name = "clone_repository",
            Description = "Download a public Git repository (GitHub, GitLab, Codeberg, Hugging Face) into a new folder (\"scarica la repo\", \"clona il progetto\"). "
                          + "Nothing is installed or run. The address must be the real one, from web_search or a page the user opened, never guessed.",
            Properties = new()
            {
                ["url"] = Prop("string", "https://github.com/owner/name"),
                ["folder"] = Prop("string", "Parent folder; default the first project place (Projects in the user folder)."),
                ["name"] = Prop("string", "Folder name; default the repository name."),
            },
            Required = ["url"],
            Run = async (a, c, ct) =>
            {
                string folder = a.Str("folder").Length > 0 ? FileTools.Expand(a.Str("folder")) : "";
                string text = await projects.CloneAsync(a.Str("url"), folder, a.Str("name"), c.Italian, ct);
                int at = text.IndexOf(" in ", StringComparison.Ordinal);
                if ((text.StartsWith("Scaricato ") || text.StartsWith("Downloaded ")) && at > 0)
                {
                    string target = text[(at + 4)..].Split(". ")[0].TrimEnd('.');
                    if (Directory.Exists(target)) await pc.OpenPathAsync(target, true, ct);
                }
                return ToolOutcome.Ok(text);
            },
        };
        yield return new ToolDef
        {
            Name = "read_code",
            Description = "Read the code file open in VS Code (or the given path), with line numbers. Use it instead of look_at_screen for \"guarda il codice\", "
                          + "\"spiegami questo codice\", \"cosa non va?\".",
            Properties = new()
            {
                ["path"] = Prop("string", "File path; default the file open in VS Code."),
                ["from_line"] = Prop("integer", "First line."),
                ["to_line"] = Prop("integer", "Last line."),
            },
            Run = async (a, c, ct) => ToolOutcome.Ok(await code.ReadAsync(a.Str("path"), a.Int("from_line"), a.Int("to_line"), c.Italian, ct)),
        };
        yield return new ToolDef
        {
            Name = "edit_code",
            Description = "Change a code file when the user asked for it (add comments, reformat, fix). Call read_code first and pass the WHOLE new file. "
                          + "mode comments: only comments added or changed, every code line identical with its indentation (checked). mode rewrite: code may change; "
                          + "for Python the syntax is checked. A backup is kept.",
            Properties = new()
            {
                ["content"] = Prop("string", "The whole new file."),
                ["mode"] = Prop("string", "What may change.", "comments", "rewrite"),
                ["path"] = Prop("string", "File path; default the file open in VS Code."),
            },
            Required = ["content", "mode"],
            // A copy is kept, so it can be undone: the "yes" only at the Prudente level, as for any overwrite.
            Risk = Risk.Overwrite,
            Summary = (a, c) => c.Italian ? "Modificare il file di codice" : "Change the code file",
            Run = async (a, c, ct) => ToolOutcome.Ok(await code.EditAsync(a.Str("path"), a["content"]?.ToString() ?? "",
                                                                          a.Str("mode") == "rewrite" ? "rewrite" : "comments", c.Italian, ct)),
        };
        yield return new ToolDef
        {
            Name = "restore_code",
            Description = "Undo the last edit_code on a file (\"rimetti com'era\", \"annulla la modifica al codice\").",
            Properties = new() { ["path"] = Prop("string", "File path; default the file open in VS Code.") },
            Run = async (a, c, ct) => ToolOutcome.Ok(await code.RestoreAsync(a.Str("path"), c.Italian, ct)),
        };
    }
}
