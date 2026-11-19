using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

using static Tereditor.Plugins.Api.Host.LanguageSettingField;

namespace Tereditor.Plugins.Lsp.Kotlin;

/// <summary>The Kotlin (Kotlin LS) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class KotlinPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-kotlin";

    public static readonly PluginManifest Manifest = new(Id, "Kotlin (Kotlin LS)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Jvm,
        // Runs a language server as a process of the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Provides the Kotlin (Kotlin LS) language server to code intelligence.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.Yes,
        DependsOn = [new("code-intelligence", 1)],
        Assets = "assets",
        Host = "Tereditor.Plugins.Lsp.Kotlin.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "kotlin",
        Name = "Kotlin (Kotlin LS)",
        Icon = LanguageServerIcon.Embedded(typeof(KotlinPlugin).Assembly, "Icon.svg"),
        Version = "271.2085.0",
        Executable = "kotlin-lsp",
        Arguments = ["--stdio"],
        Extensions = [".kt", ".kts"],
        Aliases = ["kt"],
        Implementation = "Kotlin Language Server · JetBrains",
        Source = "JetBrains · download.jetbrains.com",
        Website = "https://github.com/Kotlin/kotlin-lsp#readme",
        Repository = "https://github.com/Kotlin/kotlin-lsp",
        Pinning = LanguageServerPinning.Archive,
        Settings = [Init("Project JDK home", "defaultSdk", pathKind: "folder", help: "JDK for analysis and Maven/Gradle import. Blank follows the workspace .sdkmanrc or SDKMAN current; the bundled server runtime is separate.")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "pom.xml" or "settings.gradle" or "settings.gradle.kts" or "build.gradle" or "build.gradle.kts" or "workspace.json";
        },
        ProjectScope = _ => "JetBrains Kotlin imports Gradle/Maven JVM projects; dependencies require successful build import. Other project types depend on server support.",
        InstalledExecutable = "intellij-server",
        ConfigureInitialization = JvmInitialization.Configure,
        Artifacts = [
        .. new[]
        {
            ("macos-x86_64", "", ".sit", "zip", "68457ab1825331e3eac85191b7c32737cbd8993867f5a9c50dfe012c5509e8f7"),
            ("macos-aarch64", "-aarch64", ".sit", "zip", "2d22388f58be6f4dfa7a8f18c1e231871164dcacc16e407559029d7232816b4a"),
            ("linux-x86_64", "", ".tar.gz", "tar.gz", "e398e799027f41749ea33faa57470307d13d40f910961858c7e30adbd778ae08"),
            ("linux-aarch64", "-aarch64", ".tar.gz", "tar.gz", "877aee612a17dab48144466282a6414ec58ab428a10b97c7e13edd9b6b3dc96e"),
            ("windows-x86_64", "", ".win.zip", "zip", "da210a7d84a06f855b8b7b04c5bd54936d270e79f3ea13a9c6ddc2f1046b2a98"),
            ("windows-aarch64", "-aarch64", ".win.zip", "zip", "7cce2ebb1a28e2500e2093015f9f862f39c99c2a626073272604c57bb57ec23b")
        }.Select(item => new LanguageServerArtifact(item.Item1,
            $"https://download.jetbrains.com/language-server/kotlin-server/271.2085.0/kotlin-server-271.2085.0{item.Item2}{item.Item3}", item.Item5, item.Item4,
            $"https://download.jetbrains.com/language-server/kotlin-server/271.2085.0/kotlin-server-271.2085.0{item.Item2}{item.Item3}.sha256", JetBrains))
        ],
        ProjectKind = manifest => Path.GetFileName(manifest) switch
        {
            "pom.xml" => "Maven project",
            var name when name.StartsWith("settings.gradle", StringComparison.Ordinal) => "Gradle build",
            var name when name.StartsWith("build.gradle", StringComparison.Ordinal) => "Gradle project",
            ".project" => "Eclipse project",
            _ => null
        }
    };

    private static readonly string[] JetBrains = ["download.jetbrains.com", "download-cdn.jetbrains.com"];
}