namespace AL76.Core.Voice;

/// <summary>A rectangle on screen, y downwards (Windows), in the units of the windows (DIPs).</summary>
public readonly record struct Area(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2;
    public double CenterY => Top + Height / 2;
}

/// <summary>
/// What the sphere shows, shared by the sphere and the nucleus (Mac: Assistant.state, deepIdle).
/// Listening falls back to waiting after the follow-up window; a minute of waiting is "deep idle".
/// </summary>
public sealed class HudStatus
{
    private DateTime idleSince = DateTime.Now;
    private DateTime listeningUntil = DateTime.MaxValue;

    public HudState State { get; private set; } = HudState.Booting;
    /// <summary>Why, for errors only.</summary>
    public string Detail { get; private set; } = "";

    public void Set(HudState next, string why, TimeSpan? listenFor, DateTime now)
    {
        if (next == HudState.Idle && State != HudState.Idle) idleSince = now;
        State = next;
        Detail = next == HudState.Error ? why : "";
        listeningUntil = next == HudState.Listening && listenFor is { } t ? now + t : DateTime.MaxValue;
    }

    /// <summary>A tool started: thinking becomes acting, as on the Mac.</summary>
    public void ToolUsed()
    {
        if (State == HudState.Thinking) State = HudState.Acting;
    }

    /// <summary>The state now (listening may have run out) and whether a minute of waiting has passed.</summary>
    public (HudState State, bool DeepIdle) Read(DateTime now)
    {
        if (State == HudState.Listening && now > listeningUntil) { State = HudState.Idle; idleSince = now; }
        return (State, State == HudState.Idle && now - idleSince > TimeSpan.FromSeconds(60));
    }
}

/// <summary>
/// The NUCLEUS window (Mac ReactorPanel): sizes, places and the gesture that sends the robot to the edge.
/// Styles: "reattore", "satellite", "robottino" (the robot only with its model; without it, the satellite).
/// </summary>
public static class NucleusLayout
{
    public const double ReactorSize = 170, SatelliteWidth = 221, SatelliteHeight = 130;
    /// <summary>The sphere's centre in the HUD window (Mac HUDView: 440 wide, centre 220 from the top).</summary>
    public const double HudWidth = 440, HudHeight = 472, HudCenterFromTop = 220;

    public static string Effective(string style, bool robotModel) => style switch
    {
        "robottino" => robotModel ? "robottino" : "satellite",
        "satellite" => "satellite",
        _ => "reattore",
    };

    /// <summary>Window size: the satellite (and the robot without model) in a low window, the robot in a tall one
    /// (the small one by default, a fifth larger on request), the reactor in a square.</summary>
    public static (double Width, double Height) Size(string effective, bool robotLarge) => effective switch
    {
        "robottino" => robotLarge ? (200, 330) : (160, 264),
        "satellite" => (SatelliteWidth, SatelliteHeight),
        _ => (ReactorSize, ReactorSize),
    };

    /// <summary>The satellite and the robot float above the windows; the reactor lives on the desktop, behind them.</summary>
    public static bool Floats(string effective) => effective is "satellite" or "robottino";

    /// <summary>Inside the area, as near as possible to where it was asked.</summary>
    public static (double X, double Y) Clamp(double x, double y, double width, double height, Area area) =>
        (Math.Min(Math.Max(x, area.Left), area.Right - width), Math.Min(Math.Max(y, area.Top), area.Bottom - height));

    /// <summary>Satellite and robot start every launch in the top right corner (Mac: 6 points from the edges).</summary>
    public static (double X, double Y) TopRight(double width, double height, Area area) =>
        (area.Right - width - 6, area.Top + 6);

    public static (double X, double Y) Center(double width, double height, Area area) =>
        (area.CenterX - width / 2, area.CenterY - height / 2);

    /// <summary>Where the HUD goes so that its sphere opens exactly around the nucleus, inside the screen.</summary>
    public static (double X, double Y) HudAround(double centerX, double centerY, Area area) =>
        Clamp(centerX - HudWidth / 2, centerY - HudCenterFromTop, HudWidth, HudHeight, area);

    /// <summary>The robot dropped or thrown towards the bottom right goes back to the edge and walks; pulled
    /// anywhere else it stays where it is left, floating. Velocity in DIPs a second, y downwards.</summary>
    public static bool DropWalks(Area window, Area screen, double vx, double vy)
    {
        bool low = window.CenterY > screen.Top + screen.Height * 0.7;
        bool right = window.CenterX > screen.Left + screen.Width * 0.55;
        bool thrown = vy > 700 && vx > -200;
        return (low && right) || thrown;
    }
}

/// <summary>
/// The ROBOTTINO on the bottom edge (Mac RobotNucleusView.tick): it flies down to the edge, lives in the bottom
/// right corner and now and then, only while AL-76 waits, strolls along the edge and comes back. Called 30 times a
/// second; it moves the window and tells the robot whether it stands, walks, and how it leans.
/// </summary>
public sealed class RobotWalker
{
    private double? target;              // end of the stroll (window x); null: home
    private double strollUntil, nextStroll = 20;
    private bool wasWalking;
    private double lastX = double.NaN;
    private readonly Random random;

    public RobotWalker(Random? random = null) => this.random = random ?? new Random();

    /// <summary>-1 walking left, 1 right, 0 standing.</summary>
    public int Walk { get; private set; }
    /// <summary>Standing on the edge with the engines off.</summary>
    public bool Grounded { get; private set; }
    /// <summary>Sideways lean while the window moves (flying).</summary>
    public double Lean { get; private set; }

    /// <param name="t">Seconds since start.</param>
    /// <param name="dt">Seconds since the last call.</param>
    /// <param name="walks">The user wants it on the edge.</param>
    /// <param name="dragging">The user is moving it.</param>
    /// <param name="idle">AL-76 waits (not listening, thinking or speaking).</param>
    /// <param name="deepIdle">A minute of waiting: strolls are rarer.</param>
    /// <param name="feet">The feet, as a fraction of the window's height from the bottom.</param>
    /// <param name="stride">How fast the window must move while walking, DIPs a second (the robot's stride).</param>
    /// <returns>Where the window goes (top left).</returns>
    public (double X, double Y) Tick(double t, double dt, bool walks, bool dragging, bool idle, bool deepIdle,
                                     Area screen, double x, double y, double width, double height, double feet, double stride)
    {
        if (walks && !dragging)
        {
            double ground = screen.Bottom + feet * height - 1 - height;
            double home = screen.Right - width * 0.78;
            if (Math.Abs(y - ground) > 1.5)
            {
                // Flying down to the edge, engines on.
                Grounded = false;
                Walk = 0;
                y += (ground - y) * Math.Min(1, dt * 3);
                if (Math.Abs(y - ground) < 1.5) y = ground;
                strollUntil = 0;
                nextStroll = t + 4;
            }
            else
            {
                Grounded = true;
                y = ground;
                if (idle)
                {
                    // A stroll every minute or so (every few minutes in deep idle), ending with a look around.
                    if (target is null && t > nextStroll)
                    {
                        double reach = screen.Width * (0.18 + random.NextDouble() * 0.27);
                        target = Math.Max(screen.Left + 10, home - reach);
                        strollUntil = 0;
                    }
                }
                else target = null;   // busy: it stops where it is and acts the state
                double goal = target ?? home;
                double distance = goal - x;
                if (!idle || t < strollUntil) Walk = 0;
                else if (Math.Abs(distance) < 2)
                {
                    Walk = 0;
                    if (target is not null)
                    {
                        strollUntil = t + 2.5 + random.NextDouble() * 1.5;
                        target = null;
                    }
                    else if (nextStroll < t)
                        nextStroll = t + (deepIdle ? 150 + random.NextDouble() * 150 : 50 + random.NextDouble() * 40);
                }
                else
                {
                    Walk = distance > 0 ? 1 : -1;
                    x += Math.Min(Math.Abs(distance), stride * dt) * Walk;
                }
            }
            wasWalking = true;
        }
        else if (wasWalking && !walks)
        {
            // Walking turned off: engines on, and up a little from the edge.
            wasWalking = false;
            Grounded = false;
            Walk = 0;
            target = null;
            y -= height * 0.3;
        }
        double dx = double.IsNaN(lastX) ? 0 : x - lastX;
        lastX = x;
        Lean = Grounded ? 0 : Math.Clamp(dx * 0.04, -0.4, 0.4);
        return (x, y);
    }
}
