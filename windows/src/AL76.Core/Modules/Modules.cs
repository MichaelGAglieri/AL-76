namespace AL76.Core.Modules;

/// <summary>
/// An optional part of AL-76 (robotics, phone, a university portal...). Off by default: when it is off the
/// brains do not even see its tools, so the instructions stay short.
/// </summary>
public interface IModule
{
    string Id { get; }
    /// <summary>Key of the name in locales/*.json.</summary>
    string NameKey { get; }
    string DescriptionKey { get; }
}

public sealed record ModuleInfo(string Id, string NameKey, string DescriptionKey) : IModule;

public static class ModuleRegistry
{
    /// <summary>The modules planned so far; they get tools and settings in later phases.</summary>
    public static readonly IReadOnlyList<IModule> All =
    [
        new ModuleInfo("maker", "module.maker", "module.maker.desc"),
        new ModuleInfo("phone", "module.phone", "module.phone.desc"),
        new ModuleInfo("university", "module.university", "module.university.desc"),
    ];

    public static bool IsEnabled(IModule module, IEnumerable<string> enabled) =>
        enabled.Contains(module.Id, StringComparer.OrdinalIgnoreCase);
}
