using System.Runtime.InteropServices;
using AL76.Core.Phone;
using AL76.Core.Tools;
using AL76.Core.Voice;

namespace AL76.Platform;

public sealed partial class WindowsAppDriver
{
    /// <summary>The whole screen as the phone sees it (AL-76 included, as on the Mac) and where it lies in pixels.</summary>
    internal static async Task<(byte[] jpeg, Area area)?> PhonePictureAsync(CancellationToken ct)
    {
        var raw = await Run(() => ScreenPixels(hideOwn: false), ct);
        if (raw is null) return null;
        var picture = await Picture(raw.Value, "screen", 0);
        return picture is null ? null : (picture.Jpeg, raw.Value.Area);
    }

    /// <summary>The phone's gesture at a point of the screen, in physical pixels.</summary>
    internal static Task PhonePointerAsync(string kind, int x, int y, int amount, CancellationToken ct) => Run(() =>
    {
        SetCursorPos(x, y);
        Thread.Sleep(15);
        switch (kind)
        {
            case "click": Send([Mouse(MOUSEEVENTF_LEFTDOWN, 0), Mouse(MOUSEEVENTF_LEFTUP, 0)]); break;
            case "double":
                Send([Mouse(MOUSEEVENTF_LEFTDOWN, 0), Mouse(MOUSEEVENTF_LEFTUP, 0)]);
                Thread.Sleep(40);
                Send([Mouse(MOUSEEVENTF_LEFTDOWN, 0), Mouse(MOUSEEVENTF_LEFTUP, 0)]);
                break;
            case "right": Send([Mouse(MOUSEEVENTF_RIGHTDOWN, 0), Mouse(MOUSEEVENTF_RIGHTUP, 0)]); break;
            case "down": Send([Mouse(MOUSEEVENTF_LEFTDOWN, 0)]); break;
            case "up": Send([Mouse(MOUSEEVENTF_LEFTUP, 0)]); break;
            // "move" and "drag": the cursor already moved (with the button held for drag).
            case "scroll": Wheel(-amount * 40); break;   // the phone's points: positive scrolls down
        }
        return true;
    }, ct);

    internal static Task PhoneTypeAsync(string text, CancellationToken ct) => Run(() =>
    {
        foreach (char ch in text)
        {
            if (ch == '\n') Keys(new KeyCombo("enter", false, false, false, false));
            else if (ch != '\r') Unicode(ch);
            Thread.Sleep(8);
        }
        return true;
    }, ct);

    internal static Task PhoneKeyAsync(string name, CancellationToken ct) => Run(() =>
    {
        Keys(new KeyCombo(name switch { "return" => "enter", "delete" => "backspace", _ => name }, false, false, false, false));
        return true;
    }, ct);

    private const uint MOUSEEVENTF_RIGHTDOWN = 8, MOUSEEVENTF_RIGHTUP = 0x10;
}

/// <summary>
/// The PC screen and its input for the phone's screen view. The user moves the mouse and types seeing the screen,
/// so these do not go through the rules meant for the model (as on the Mac); they work only while the view is open.
/// </summary>
public sealed class WindowsPhoneScreen : IPhoneScreen
{
    private Area area;

    public async Task<byte[]?> CaptureAsync(CancellationToken ct)
    {
        var p = await WindowsAppDriver.PhonePictureAsync(ct);
        if (p is null) return null;
        area = p.Value.area;
        return p.Value.jpeg;
    }

    public Task PointerAsync(string kind, double x, double y, int amount, CancellationToken ct)
    {
        if (area.Width <= 0) return Task.CompletedTask;
        int px = (int)Math.Round(area.Left + x * (area.Width - 1)), py = (int)Math.Round(area.Top + y * (area.Height - 1));
        return WindowsAppDriver.PhonePointerAsync(kind, px, py, amount, ct);
    }

    public Task TypeAsync(string text, CancellationToken ct) => WindowsAppDriver.PhoneTypeAsync(text, ct);

    public Task KeyAsync(string name, CancellationToken ct) => WindowsAppDriver.PhoneKeyAsync(name, ct);
}
