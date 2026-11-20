using System.Text.Json.Nodes;
using AL76.Core.Config;
using AL76.Core.Tools;

namespace AL76.Core.Tests;

public class OwnerCheckTests
{
    private sealed class FakeHello(bool? answer) : IOwnerCheck
    {
        public int Asked;
        public Task<bool?> VerifyAsync(string reason, CancellationToken ct) { Asked++; return Task.FromResult(answer); }
    }

    private static (Assistant a, string file) Make(IOwnerCheck hello)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"al76-hello-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "script.bat");
        File.WriteAllText(file, "echo");
        var a = new Assistant(new AppConfig(), () => { }, new RecordingPcActions(), [], dir, dir, files: new FileSearch(() => [dir])) { Owner = hello };
        return (a, file);
    }

    [Theory]
    [InlineData(false, "Niente Windows Hello", 0)]
    [InlineData(true, "Fatto", 1)]
    [InlineData(null, "Fatto", 1)]
    public async Task ProgramsNeedHelloAfterTheYes(bool? hello, string start, int opened)
    {
        var check = new FakeHello(hello);
        var (a, file) = Make(check);
        var pc = (RecordingPcActions)typeof(Assistant).GetField("pc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(a)!;
        await a.Tools.CallAsync("open_path", new JsonObject { ["path"] = file }, new ToolContext("apri lo script", "it", "voce"));
        Assert.True(a.Tools.HasPending);
        Assert.True(a.Tools.Pending!.Secure);
        var answer = await a.HandleAsync("sì", "it");
        Assert.StartsWith(start, answer.Text);
        Assert.Equal(1, check.Asked);
        Assert.Equal(opened, pc.Calls.Count(c => c.StartsWith("open ")));
    }

    [Fact]
    public async Task CameraHiddenWithoutOne()
    {
        var (a, _) = Make(new FakeHello(true));
        Assert.DoesNotContain(a.Tools.Visible, t => t.Name == "look_at_camera");
        a.Camera = _ => Task.FromResult<(byte[], string)?>(([1, 2, 3], "Webcam"));
        Assert.Contains(a.Tools.Visible, t => t.Name == "look_at_camera");
        var reply = await a.Tools.CallFullAsync("look_at_camera", new JsonObject { ["question"] = "che componente è?" }, new ToolContext("", "it", "voce"));
        Assert.Equal([1, 2, 3], reply.Image);
        Assert.Contains("che componente è?", reply.Text);
        var phone = await a.Tools.CallFullAsync("look_at_camera", [], new ToolContext("", "it", "iphone"));
        Assert.Null(phone.Image);
    }
}
