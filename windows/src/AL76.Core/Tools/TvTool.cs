using AL76.Core.Maker;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>tv (Mac: the same tool): the Samsung TV over the local network, only with TvHost set.</summary>
public static class TvTool
{
    public static ToolDef Create(TvControl tv) => new()
    {
        Name = "tv",
        Description = "Control the Samsung TV in the house over the local network. Report only what the result says.",
        Properties = new()
        {
            ["action"] = Prop("string", "What to do.", ["status", "on", "off", "app", .. TvControl.Keys.Keys]),
            ["value"] = Prop("string", "For app: Netflix, YouTube, Prime or Disney. For volume or channel keys: how many presses, 1-20."),
        },
        Required = ["action"],
        Available = () => tv.Configured,
        Run = async (a, c, ct) => ToolOutcome.Ok(await tv.RunAsync(a.Str("action") is { Length: > 0 } x ? x : "status", a.Str("value") is { Length: > 0 } v ? v : a.Int("value")?.ToString(), c.Italian, ct)),
    };
}
