namespace Tereditor.Plugins.Api;

/// <summary>
/// The categories builtin plugins use, and the order Settings › Plugins lists them in: one per language (all the plugins of
/// an ecosystem together, such as a language server and the plugin that activates its environments), then the tool groups.
/// </summary>
public static class PluginCategories
{
    /// <summary>C and C++: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Cpp = "C & C++";

    /// <summary>C#: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string CSharp = "C#";

    /// <summary>CSS, SCSS and Less: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Css = "CSS";

    /// <summary>Dart and Flutter: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Dart = "Dart";

    /// <summary>Go: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Go = "Go";

    /// <summary>JavaScript and TypeScript: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string JavaScript = "JavaScript & TypeScript";

    /// <summary>Java, Kotlin and Scala: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Jvm = "JVM";

    /// <summary>PHP: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Php = "PHP";

    /// <summary>PowerShell: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string PowerShell = "PowerShell";

    /// <summary>Python: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Python = "Python";

    /// <summary>R: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string R = "R";

    /// <summary>Ruby: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Ruby = "Ruby";

    /// <summary>Rust: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Rust = "Rust";

    /// <summary>Shell scripts: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Shell = "Shell";

    /// <summary>Swift: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Swift = "Swift";

    /// <summary>Environment managers that shape every shell, such as mise and direnv.</summary>
    public const string Environment = "Environment";

    /// <summary>AI coding agents and the context they use.</summary>
    public const string Agents = "Agents";

    /// <summary>Model providers: the accounts, gateways and keys that supply models to the agents.</summary>
    public const string ModelProviders = "Model providers";

    /// <summary>Version control views.</summary>
    public const string VersionControl = "Version Control";

    /// <summary>Specifications, diagrams and document viewers.</summary>
    public const string Documents = "Specs & Documents";

    /// <summary>Connections to other services and machines.</summary>
    public const string Integrations = "Integrations";

    /// <summary>Where a plugin with no category is listed, after every other group.</summary>
    public const string Other = "Other";

    /// <summary>The builtin categories in listing order; any other category follows alphabetically, then <see cref="Other"/>.</summary>
    public static IReadOnlyList<string> Order { get; } =
        [Cpp, CSharp, Css, Dart, Go, JavaScript, Jvm, Php, PowerShell, Python, R, Ruby, Rust, Shell, Swift, Environment, Agents, ModelProviders, VersionControl, Documents, Integrations];

    /// <summary>The icon name (a core icon of the plugin UI kit, or <c>file-type:&lt;name&gt;</c>, a language's single-colour Material Icon Theme logo) shown before a category's chip. The letter languages (C &amp; C++, C#, CSS, Go, JavaScript &amp; TypeScript, PHP, R) have none: their chips already spell the letters. See <see cref="IconOf"/>.</summary>
    public static IReadOnlyDictionary<string, string> Icons { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Dart] = "file-type:dart",
        [Jvm] = "file-type:java",
        [PowerShell] = "file-type:powershell",
        [Python] = "file-type:python",
        [Ruby] = "file-type:ruby",
        [Rust] = "file-type:rust",
        [Shell] = "file-type:console",
        [Swift] = "file-type:swift",
        [Environment] = "settings",
        [Agents] = "robot",
        [ModelProviders] = "cloud",
        [VersionControl] = "gitBranch",
        [Documents] = "fileText",
        [Integrations] = "plug",
        [Other] = "puzzle"
    };

    /// <summary>The icon name for <paramref name="category"/>, from <see cref="Icons"/>. A builtin category without one gets an empty name (no icon); any other name gets the puzzle glyph.</summary>
    public static string IconOf(string category) => Icons.TryGetValue(category, out var icon) ? icon : Order.Contains(category) ? "" : "puzzle";
}

/// <summary>The support levels a manifest's <see cref="PluginManifest.Support"/> names; Settings › Plugins badges them.</summary>
public static class PluginSupportLevels
{
    /// <summary>Basic support: the core flows work, deeper integration is still to come.</summary>
    public const string Early = "early";
}