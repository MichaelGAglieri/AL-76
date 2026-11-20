using System.Text.Json.Nodes;
using AL76.Core.Accounts;
using AL76.Core.Config;
using AL76.Core.Maker;
using AL76.Core.Safety;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>The "maker" module's pieces, built once by the assistant.</summary>
public sealed class MakerDesk(Arduino arduino, BambuPrinter printer, RosBridge ros, RobotConsole robot, Func<string>? labProject = null)
{
    public string LabProject => labProject?.Invoke().Trim() ?? "";
    public Arduino Arduino => arduino;
    public BambuPrinter Printer => printer;
    public RosBridge Ros => ros;
    public RobotConsole Robot => robot;
    /// <summary>Filament and prints through Bambu Studio's buttons; null where AL-76 cannot see other apps.</summary>
    public BambuStudio? Studio { get; set; }
    /// <summary>App control switched on in the settings: Bambu Studio is pressed only then (Mac: appControlEnabled).</summary>
    public Func<bool> AppControlOn { get; set; } = () => true;

    public static MakerDesk Create(AppConfig config, ISecretStore secrets, string documents, Action? save = null)
    {
        IReadOnlyList<string> Folders() => config.MakerFolders.Count > 0 ? config.MakerFolders
            : [Path.Combine(documents, "Arduino"), documents, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects")];
        return new MakerDesk(
            new Arduino(() => config.ArduinoCli, Folders, new ProcessShell()),
            // The router gave the printer another address: it is found again and kept in the settings.
            new BambuPrinter(() => config.PrinterHost, () => config.PrinterSerial, () => secrets.Read("bambu.code"),
                             moved: ip => { config.PrinterHost = ip; save?.Invoke(); }),
            new RosBridge(() => config.RosBridgeUrl),
            new RobotConsole(() => config.RobotConsoleUrl, () => secrets.Read("robotconsole.token"), new HttpClient { Timeout = TimeSpan.FromSeconds(25) }),
            () => config.LabProject);
    }
}

/// <summary>
/// Phase 7, "maker" module: Arduino, the 3D printer, ROS 2 and Robot Console (Mac: the robotics tools). Visible only
/// with the module on. The robot and the boards act at once (the user's choice, 08/11/2026): on the real robot
/// Robot Console still asks its operator to approve every execution.
/// </summary>
public static class MakerTools
{
    private static string L(ToolContext c, string it, string en) => c.Italian ? it : en;

    public static IEnumerable<ToolDef> Create(MakerDesk desk, Func<bool> on)
    {
        IEnumerable<ToolDef> All()
        {
            yield return new ToolDef
            {
                Name = "arduino_boards",
                Description = "List the Arduino boards connected over USB, with port and model.",
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Arduino.ListAsync(c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "arduino_compile",
                Description = "Compile an Arduino sketch by project or .ino name (it is looked for in the Maker folders).",
                Properties = new() { ["sketch"] = Prop("string", "Project or sketch name."), ["fqbn"] = Prop("string", "Board FQBN, e.g. arduino:avr:uno; default the connected board's.") },
                Required = ["sketch"],
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Arduino.CompileAsync(a.Str("sketch"), a.Str("fqbn") is { Length: > 0 } f ? f : null, c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "arduino_upload",
                Description = "Compile and upload a sketch to the connected board, then verify it.",
                Properties = new()
                {
                    ["sketch"] = Prop("string", "Project or sketch name."), ["port"] = Prop("string", "COM port, e.g. COM4; default the first board."),
                    ["fqbn"] = Prop("string", "Board FQBN; default the board's."),
                },
                Required = ["sketch"],
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Arduino.UploadAsync(a.Str("sketch"), a.Str("port") is { Length: > 0 } p ? p : null,
                    a.Str("fqbn") is { Length: > 0 } f ? f : null, c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "arduino_serial",
                Description = "Read what a board prints on the serial port for a few seconds (opening the port usually resets the board).",
                Properties = new()
                {
                    ["port"] = Prop("string", "COM port; default the first board."), ["baud"] = Prop("integer", "Baud rate, default 9600."),
                    ["seconds"] = Prop("number", "How long to listen, 1-30, default 5."),
                },
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Arduino.ReadSerialAsync(a.Str("port") is { Length: > 0 } p ? p : null, a.Int("baud") ?? 9600,
                    double.TryParse(a.Str("seconds"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 5, c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "printer_status",
                Description = "How the 3D printer (Bambu Lab, LAN mode) is doing: state, percentage, time left, layers, temperatures. Read only: the printer cannot be controlled from here.",
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Printer.SpokenStatusAsync(c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "bambu_open",
                Description = "Open Bambu Studio, optionally with a 3D model or project (3MF/STL/STEP/OBJ) found by name in the user's folders.",
                Properties = new() { ["model"] = Prop("string", "Model or project name, e.g. gripper.") },
                Available = () => desk.Studio is not null,
                Run = (a, c, ct) => Task.FromResult(ToolOutcome.Ok(desk.Studio!.Open(a.Str("model") is { Length: > 0 } m ? m : null))),
            };
            yield return new ToolDef
            {
                Name = "printer_filament",
                Description = "Load or unload the filament of the user's Bambu printer (external spool), using Bambu Studio on screen. Heats the nozzle. Refused while printing.",
                Properties = new() { ["action"] = Prop("string", "load or unload", "load", "unload") },
                Required = ["action"],
                Available = () => desk.Studio is not null,
                Run = async (a, c, ct) =>
                {
                    if (!desk.AppControlOn()) return ToolOutcome.Ok("Il controllo delle app è spento nelle impostazioni: non posso usare Bambu Studio.");
                    return a.Str("action").ToLowerInvariant() is var act && act is "load" or "unload"
                        ? ToolOutcome.Ok(await desk.Studio!.FilamentAsync(act == "load", ct))
                        : ToolOutcome.Ok("Azione non valida: load o unload.");
                },
            };
            yield return new ToolDef
            {
                Name = "printer_print",
                Description = "Slice a model in Bambu Studio and get it ready to print on the user's printer; the user confirms before it starts. Without model, uses the project already open in Bambu Studio. Prints the selected plate only.",
                Properties = new() { ["model"] = Prop("string", "Optional model or project name, e.g. gripper.") },
                Available = () => desk.Studio is not null,
                Run = async (a, c, ct) => !desk.AppControlOn()
                    ? ToolOutcome.Ok("Il controllo delle app è spento nelle impostazioni: non posso usare Bambu Studio.")
                    : await desk.Studio!.PreparePrintAsync(a.Str("model") is { Length: > 0 } m ? m : null, ct),
            };
            yield return new ToolDef
            {
                Name = "ros_topics",
                Description = "List the active ROS 2 topics with their types (through rosbridge).",
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Ros.TopicsAsync(c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "ros_echo",
                Description = "Read the next message of a ROS 2 topic (e.g. /joint_states).",
                Properties = new() { ["topic"] = Prop("string", "Topic name."), ["seconds"] = Prop("number", "How long to wait, default 5.") },
                Required = ["topic"],
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Ros.EchoAsync(a.Str("topic"),
                    double.TryParse(a.Str("seconds"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 5, c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "ros_publish",
                Description = "Publish one message on a ROS 2 topic, only when the user asks (it can move a robot).",
                Properties = new()
                {
                    ["topic"] = Prop("string", "Topic name."), ["type"] = Prop("string", "Message type, e.g. std_msgs/msg/String or geometry_msgs/msg/Twist."),
                    ["message"] = Prop("string", "The message as JSON, e.g. {\"data\": \"ciao\"}."),
                },
                Required = ["topic", "type", "message"],
                Run = async (a, c, ct) =>
                {
                    JsonObject? m;
                    try { m = JsonNode.Parse(a.Str("message")) as JsonObject; } catch (System.Text.Json.JsonException) { m = null; }
                    return m is null ? ToolOutcome.Error(L(c, "Il messaggio deve essere un oggetto JSON.", "The message must be a JSON object."))
                        : ToolOutcome.Ok(await desk.Ros.PublishAsync(a.Str("topic"), a.Str("type"), m, c.Italian, ct));
                },
            };
            yield return new ToolDef
            {
                Name = "ros_service",
                Description = "Call a ROS 2 service with its arguments (JSON) and return the answer.",
                Properties = new() { ["service"] = Prop("string", "Service name."), ["args"] = Prop("string", "Arguments as JSON object, optional.") },
                Required = ["service"],
                Run = async (a, c, ct) =>
                {
                    JsonObject? args = null;
                    if (a.Str("args") is { Length: > 0 } text)
                        try { args = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { return ToolOutcome.Error(L(c, "Argomenti non validi.", "Invalid arguments.")); }
                    return ToolOutcome.Ok(await desk.Ros.ServiceAsync(a.Str("service"), args, c.Italian, ct));
                },
            };
            yield return new ToolDef
            {
                Name = "ros_graph",
                Description = "List the running ROS 2 nodes and topics.",
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Ros.GraphAsync(c.Italian, ct)),
            };
            yield return new ToolDef
            {
                Name = "lab_start",
                Description = "Start the ROS 2 laboratory simulation (Webots and the ROS node windows) with pixi.",
                Run = (a, c, ct) => Task.FromResult(ToolOutcome.Ok(LabLauncher.Start(desk.LabProject, c.Italian))),
            };
            yield return new ToolDef
            {
                Name = "lab_command",
                Description = "Send an Italian command to the ROS 2 laboratory and wait for the outcome. Valid commands: \"stato\", \"sposta campione a in postazione 1\", \"sposta campione b in postazione 2\", \"riporta campione a\", \"riporta campione b\", \"stop\".",
                Properties = new() { ["text"] = Prop("string", "The command, in Italian.") },
                Required = ["text"],
                Run = async (a, c, ct) => ToolOutcome.Ok(Security.External("laboratorio ROS", await desk.Ros.LabAsync(a.Str("text") is { Length: > 0 } t ? t : "stato", 30, c.Italian, ct))),
            };
            yield return new ToolDef
            {
                Name = "lab_status",
                Description = "Latest state, outcome and reply of the ROS 2 laboratory.",
                Run = async (a, c, ct) => ToolOutcome.Ok(Security.External("laboratorio ROS", await desk.Ros.LabStatusAsync(c.Italian, ct))),
            };
            yield return new ToolDef
            {
                Name = "robot_status",
                Description = "Robot Console status: what the API may do, pending approval, running program, saved targets.",
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Robot.StatusAsync(ct)),
            };
            yield return new ToolDef
            {
                Name = "robot_plan",
                Description = "Prepare a robot program in Robot Console (planning only, the robot does not move): to a saved target, pick and place between two targets, or home.",
                Properties = new()
                {
                    ["task"] = Prop("string", "What to do.", "target", "pick_place", "home"), ["target"] = Prop("string", "Target name for task target."),
                    ["from"] = Prop("string", "Pick target for pick_place."), ["to"] = Prop("string", "Place target for pick_place."),
                    ["speed"] = Prop("integer", "Speed percent 1-100, default 50."),
                },
                Required = ["task"],
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Robot.PlanAsync(a.Str("task"), a.Str("target"), a.Str("from"), a.Str("to"), a.Int("speed") ?? 50, ct)),
            };
            yield return new ToolDef
            {
                Name = "robot_execute",
                Description = "Run the program prepared with robot_plan. On the real robot the operator approves it in Robot Console.",
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Robot.ExecuteAsync(ct)),
            };
            yield return new ToolDef
            {
                Name = "robot_stop",
                Description = "STOP the robot at once (Robot Console).",
                Run = async (a, c, ct) => ToolOutcome.Ok(await desk.Robot.StopAsync(ct)),
            };
        }
        foreach (var t in All())
        {
            var tool = t;
            yield return new ToolDef
            {
                Name = tool.Name, Description = tool.Description, Properties = tool.Properties, Required = tool.Required,
                Risk = tool.Risk, Summary = tool.Summary, Run = tool.Run,
                Available = tool.Available is { } also ? () => on() && also() : on,
            };
        }
    }
}
