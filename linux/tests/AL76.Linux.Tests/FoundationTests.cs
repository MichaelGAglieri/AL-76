using System.Text.Json;
using AL76.Core.Hardware;
using AL76.Core.Localization;
using AL76.Linux.App;
using AL76.Linux.Platform;

namespace AL76.Linux.Tests;

public class FoundationTests
{
    [Fact]
    public void XdgVariablesWinOnlyWhenAbsolute()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("/tmp/conf", XdgPaths.Base("XDG_CONFIG_HOME", ".config", _ => "/tmp/conf"));
        Assert.Equal(Path.Combine(home, ".config"), XdgPaths.Base("XDG_CONFIG_HOME", ".config", _ => null));
        // The spec: a relative value is ignored.
        Assert.Equal(Path.Combine(home, ".config"), XdgPaths.Base("XDG_CONFIG_HOME", ".config", _ => "relative/dir"));
    }

    [Fact]
    public void ItalianUserFoldersComeFromUserDirs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string file = Path.GetTempFileName();
        File.WriteAllText(file, "# written by xdg-user-dirs-update\nXDG_DESKTOP_DIR=\"$HOME/Scrivania\"\nXDG_DOCUMENTS_DIR=\"$HOME/Documenti\"\n");
        try
        {
            Assert.Equal(Path.Combine(home, "Scrivania"), XdgPaths.UserDir("DESKTOP", file));
            Assert.Equal(Path.Combine(home, "Documenti"), XdgPaths.UserDir("DOCUMENTS", file));
            Assert.Equal(Path.Combine(home, "Downloads"), XdgPaths.UserDir("DOWNLOAD", file));   // missing: English default
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void MemoryFromProcMeminfo()
    {
        const string meminfo = "MemTotal:       16316412 kB\nMemFree:         1234567 kB\nMemAvailable:    8158206 kB\n";
        Assert.Equal(15.56, LinuxHardware.RamGB(meminfo), 2);
        Assert.Equal(50, LinuxHardware.FreePercent(meminfo));
        Assert.Null(LinuxHardware.FreePercent("nothing here"));
    }

    [Fact]
    public void InstalledMemoryFromMemoryBlocks()
    {
        string memory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Null(LinuxHardware.InstalledGB(memory));   // no blocks (a container): MemTotal is used instead
            File.WriteAllText(Path.Combine(memory, "block_size_bytes"), "8000000\n");   // 128 MB, hexadecimal
            for (int i = 0; i < 66; i++)
            {
                Directory.CreateDirectory(Path.Combine(memory, $"memory{i}"));
                File.WriteAllText(Path.Combine(memory, $"memory{i}", "state"), i < 64 ? "online\n" : "offline\n");
            }
            Assert.Equal(8.0, LinuxHardware.InstalledGB(memory));
            // An 8 GB PC is not below the requirements because of what the kernel keeps for itself.
            Assert.False(LinuxTier.Classify(new HardwareInfo(LinuxHardware.InstalledGB(memory)!.Value, 4, "Arm64", [])).BelowRequirements);
        }
        finally { Directory.Delete(memory, true); }
    }

    [Fact]
    public void GraphicsCardsByPciVendor()
    {
        Assert.Equal("NVIDIA (nvidia)", LinuxHardware.GpuName("0x10de", "nvidia"));
        Assert.Equal("Intel (i915)", LinuxHardware.GpuName("0x8086\n", "i915"));
        Assert.Equal("virtio-gpu", LinuxHardware.GpuName("0x1af4", "virtio-pci"));
        Assert.True(new GpuInfo(LinuxHardware.GpuName("0x10de", "nvidia"), 8).IsNvidia);
    }

    [Fact]
    public void GraphicsCardsFromAFakeSysfs()
    {
        string drm = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(drm, "card0", "device"));
            File.WriteAllText(Path.Combine(drm, "card0", "device", "vendor"), "0x1002\n");
            File.WriteAllText(Path.Combine(drm, "card0", "device", "mem_info_vram_total"), (8L << 30).ToString());
            Directory.CreateDirectory(Path.Combine(drm, "card0-DP-1"));   // a connector, not a card
            var gpus = LinuxHardware.Gpus(drm);
            var amd = Assert.Single(gpus);
            Assert.StartsWith("AMD", amd.Name);
            Assert.Equal(8, amd.VramGB);
        }
        finally { Directory.Delete(drm, true); }
    }

    [Theory]
    [InlineData("ubuntu:GNOME", DesktopKind.Gnome)]
    [InlineData("GNOME", DesktopKind.Gnome)]
    [InlineData("KDE", DesktopKind.Kde)]
    [InlineData("XFCE", DesktopKind.Xfce)]
    [InlineData("sway", DesktopKind.Other)]
    [InlineData("", DesktopKind.Other)]
    public void DesktopFromXdgCurrentDesktop(string value, DesktopKind kind) => Assert.Equal(kind, DesktopSession.Kind(value));

    [Fact]
    public void WaylandWithXwaylandAndPlainX11()
    {
        var env = new Dictionary<string, string> { ["XDG_SESSION_TYPE"] = "wayland", ["WAYLAND_DISPLAY"] = "wayland-0", ["DISPLAY"] = ":0", ["XDG_CURRENT_DESKTOP"] = "ubuntu:GNOME" };
        var gnome = DesktopSession.Detect(k => env.GetValueOrDefault(k), "/nonexistent");
        Assert.Equal(DisplayServer.Wayland, gnome.Server);
        Assert.True(gnome.HasXwayland);
        Assert.Equal(DesktopKind.Gnome, gnome.Desktop);
        Assert.Equal("Linux", gnome.Distribution);

        var x11 = DesktopSession.Detect(k => k == "DISPLAY" ? ":1" : null, "/nonexistent");
        Assert.Equal(DisplayServer.X11, x11.Server);
        Assert.False(x11.HasXwayland);
        Assert.Equal(DisplayServer.None, DesktopSession.Detect(_ => null, "/nonexistent").Server);
    }

    [Fact]
    public void BaseLevelGetsPiperInsteadOfWindowsVoices()
    {
        var weak = LinuxTier.Classify(new HardwareInfo(8, 4, "Arm64", []));
        Assert.Equal(Tier.Base, weak.Tier);
        Assert.Equal("piper", weak.VoiceEngine);
        var medium = LinuxTier.Classify(new HardwareInfo(16, 8, "X64", []));
        Assert.Equal("kokoro", medium.VoiceEngine);
    }

    [Fact]
    public void SchemeServesOnlyItsOwnFolders()
    {
        const string app = "/opt/al76", companion = "/home/u/.local/share/AL-76/companion";
        Assert.Equal("/opt/al76/web/hud/hud.html", WebLayer.Resolve("al76://hud/hud.html", app, companion));
        Assert.Equal("/opt/al76/web/hud/vendor/three.module.min.js", WebLayer.Resolve("al76://hud/vendor/three.module.min.js?v=1", app, companion));
        Assert.Equal(companion + "/scene.gltf", WebLayer.Resolve("al76://companion/scene.gltf", app, companion));
        Assert.Null(WebLayer.Resolve("al76://hud/../../../etc/passwd", app, companion));
        Assert.Null(WebLayer.Resolve("al76://hud/%2e%2e/%2e%2e/etc/passwd", app, companion));
        Assert.Null(WebLayer.Resolve("al76://other/x.html", app, companion));
        Assert.Null(WebLayer.Resolve("https://example.com/x.html", app, companion));
        Assert.Equal("text/javascript", WebLayer.Mime("sphere.js"));
        Assert.Equal("model/gltf+json", WebLayer.Mime("scene.gltf"));
    }

    [Fact]
    public void LinuxTextsExistInBothLanguages()
    {
        var it = new LinuxStrings(new Strings("it"));
        var en = new LinuxStrings(new Strings("en"));
        foreach (var key in new[] { "linux.session", "linux.session.wayland", "linux.session.x11", "linux.desktop.unknown", "linux.phase", "linux.webgl" })
        {
            Assert.NotEqual(key, it[key]);
            Assert.NotEqual(key, en[key]);
        }
        Assert.NotEqual(it["linux.phase"], en["linux.phase"]);
        // The core's texts come through too.
        Assert.Equal(new Strings("it")["hw.title"], it["hw.title"]);
    }

    [Fact]
    public void BridgeSpeaksTheWebView2Api()
    {
        Assert.Contains("window.chrome.webview", WebLayer.Bridge);
        Assert.Contains("postMessage: (m) => handler && handler.postMessage(JSON.stringify(m))", WebLayer.Bridge);
        Assert.Contains("__deliver", WebLayer.Bridge);
        // A message from C# must be valid JS once serialized.
        string json = JsonSerializer.Serialize(new { state = "idle", label = "«Ehi Al» </script>" });
        Assert.DoesNotContain("</script>", json);
    }
}
