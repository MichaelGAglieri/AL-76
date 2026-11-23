using System.IO.Compression;
using AL76.Core.Setup;

namespace AL76.Core.Tests;

public class CompanionModelTests
{
    private static string Zip(string folder, string name, params string[] entries)
    {
        string path = Path.Combine(folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries)
            using (var w = new StreamWriter(zip.CreateEntry(e).Open())) w.Write("x");
        return path;
    }

    [Fact]
    public void FindsAndInstallsTheModelAndNothingElse()
    {
        string dir = Directory.CreateTempSubdirectory("al76-companion").FullName;
        try
        {
            Zip(dir, "altro.zip", "foto.png");
            string zip = Zip(dir, "mini_robot.zip", "scene.gltf", "scene.bin", "textures/body.png", "license.txt", "virus.exe", "../fuori.png");
            Assert.Equal(zip, CompanionModel.FindZip(dir));
            string target = Path.Combine(dir, "Companion");
            Assert.Null(CompanionModel.Install(zip, target));
            Assert.True(File.Exists(Path.Combine(target, "scene.gltf")));
            Assert.True(File.Exists(Path.Combine(target, "textures", "body.png")));
            Assert.True(File.Exists(Path.Combine(target, "CREDITI.txt")));
            Assert.False(File.Exists(Path.Combine(target, "virus.exe")));
            Assert.False(File.Exists(Path.Combine(dir, "fuori.png")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AModelInsideAFolderOfTheZip()
    {
        string dir = Directory.CreateTempSubdirectory("al76-companion").FullName;
        try
        {
            string zip = Zip(dir, "m.zip", "mini_robot/scene.gltf", "mini_robot/scene.bin");
            Assert.Null(CompanionModel.Install(zip, Path.Combine(dir, "C")));
            Assert.True(File.Exists(Path.Combine(dir, "C", "scene.bin")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AZipWithoutTheModelIsRefused()
    {
        string dir = Directory.CreateTempSubdirectory("al76-companion").FullName;
        try
        {
            string zip = Zip(dir, "m.zip", "model.fbx");
            Assert.Null(CompanionModel.FindZip(dir));
            Assert.NotNull(CompanionModel.Install(zip, Path.Combine(dir, "C")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
