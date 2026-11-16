using System.Text.Json.Nodes;

using Tereditor.Plugins.Lsp.IntelliJ;

namespace Tereditor.Checks;

/// <summary>Maven discovery and the plugin-install pom guard, on fake directories only: no IntelliJ is downloaded or launched.</summary>
internal static class IntelliJMavenChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run(string root)
    {
        var workspace = Path.Combine(root, "maven-project");
        var tools = Path.Combine(root, "maven-tools");
        var home = Path.Combine(tools, "home");
        var onPath = Path.Combine(tools, "path");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        Directory.CreateDirectory(onPath);
        var mvn = OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn";
        var wrapper = OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw";
        Require(IntelliJMaven.Locate(workspace, onPath, null, null) is null, "Maven was found where none exists.");
        File.WriteAllText(Path.Combine(workspace, wrapper), "fixture");
        Require(IntelliJMaven.Locate(workspace, onPath, null, null) is { Source: "project wrapper" }, "The project wrapper was not used.");
        File.WriteAllText(Path.Combine(home, "bin", mvn), "fixture");
        Require(IntelliJMaven.Locate(workspace, onPath, home, null) is { Source: "MAVEN_HOME" } && IntelliJMaven.Locate(workspace, onPath, null, home) is { Source: "M2_HOME" }, "MAVEN_HOME or M2_HOME was ignored.");
        File.WriteAllText(Path.Combine(onPath, mvn), "fixture");
        Require(IntelliJMaven.Locate(workspace, onPath, home, null) is { Source: "PATH" }, "mvn on PATH did not come first.");
        File.Delete(Path.Combine(workspace, wrapper));
        File.Delete(Path.Combine(onPath, mvn));

        File.WriteAllText(Path.Combine(workspace, "pom.xml"), "<project/>");
        var result = new JsonObject { ["projects"] = new JsonArray(new JsonObject { ["type"] = "maven" }, new JsonObject { ["type"] = "gradle" }), ["buildTools"] = new JsonObject { ["file:///a"] = "maven", ["file:///b"] = "gradle" } };
        Require(IntelliJMaven.Wanted(result, "", workspace), "A Maven project was not recognised.");
        IntelliJMaven.Skip(result);
        var projects = (JsonArray)result["projects"]!;
        Require(projects.Count == 1 && projects[0]!["type"]!.ToString() == "gradle" && ((JsonObject)result["buildTools"]!).Count == 1, "Skipping Maven removed more than Maven.");
        Require(IntelliJMaven.NotFound.Contains("Maven was not found", StringComparison.Ordinal), "The status sentence changed.");

        var own = Path.Combine(workspace, "mavenPluginInstall-pom-mine.xml");
        File.WriteAllText(own, "user file");
        IntelliJMaven.Remember(workspace, ["pom.xml"]);
        var generated = Path.Combine(workspace, "mavenPluginInstall-pom123.xml");
        var other = Path.Combine(workspace, "pom-notes.xml");
        File.WriteAllText(generated, "<project/>");
        File.WriteAllText(other, "user file");
        Require(IntelliJMaven.Sweep(workspace, true) == 1 && !File.Exists(generated), "A generated plugin-install pom stayed in the workspace.");
        Require(File.Exists(own) && File.Exists(other) && File.Exists(Path.Combine(workspace, "pom.xml")), "The guard removed a file of the user's.");
        Console.WriteLine("PASS IntelliJ Maven discovery, skipped import and plugin-install pom guard");
    }
}