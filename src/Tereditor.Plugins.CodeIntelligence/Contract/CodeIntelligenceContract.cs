using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.CodeIntelligence;

/// <summary>What a language-server call names: the workspace it runs for and the server it concerns.</summary>
/// <param name="Workspace">The absolute workspace path.</param>
/// <param name="Server">The server's catalog id; empty for a call about no one server.</param>
public sealed record LanguageServerParams(string Workspace, string Server = "");

/// <summary>One tool a server needs, as probed on the host.</summary>
/// <param name="Executable">The program's file name.</param>
/// <param name="Name">The name to show.</param>
/// <param name="State"><c>found</c>, <c>missing</c> or <c>rejected</c>.</param>
/// <param name="Path">Where it was found; empty when missing.</param>
/// <param name="Source"><c>override</c>, <c>home</c>, <c>path</c> or empty.</param>
/// <param name="InstallHint">One line on how to get it.</param>
/// <param name="Problem">Why an override was rejected; empty otherwise.</param>
/// <param name="Override">Whether the user set the path.</param>
/// <param name="Required">False when the current launch does not need the tool.</param>
public sealed record LanguageDependencyRow(string Executable, string Name, string State, string Path, string Source, string InstallHint,
    string Problem = "", bool Override = false, bool Required = true);

/// <summary>One server in the catalog, as the Code Intelligence page lists it.</summary>
/// <param name="Id">The catalog id.</param>
/// <param name="Name">The name to show.</param>
/// <param name="Version">The pinned version, or <c>toolchain</c> when the host SDK supplies it.</param>
/// <param name="Detail">The implementation line.</param>
/// <param name="HasTerms">Whether the user must accept terms before it runs.</param>
/// <param name="Dependencies">The tools it needs; null when the host did not probe them.</param>
public sealed record LanguageServerRow(string Id, string Name, string Version, string Detail, bool HasTerms, IReadOnlyList<LanguageDependencyRow>? Dependencies = null);

/// <summary>The catalog of the servers active plugins provide.</summary>
/// <param name="Servers">One row per server, in catalog order.</param>
/// <param name="Detail">Where the host keeps what it installs, in plain words.</param>
public sealed record LanguageCatalogState(IReadOnlyList<LanguageServerRow> Servers, string Detail = "");

/// <summary>How a server's launcher resolves.</summary>
/// <param name="State"><c>ready</c> or the reason it is not.</param>
/// <param name="Detail">A plain sentence about the state.</param>
/// <param name="Source"><c>managed</c>, <c>path</c>, <c>override</c> or <c>none</c>.</param>
/// <param name="Path">The executable; empty when none.</param>
/// <param name="Version">Its version; empty when unknown.</param>
/// <param name="Warning">A warning about an executable found on PATH; empty when none.</param>
/// <param name="Pinning">How the version is pinned.</param>
/// <param name="PathCapable">Whether the server may run an executable from PATH.</param>
public sealed record LanguageResolution(string State, string Detail, string Source, string Path, string Version, string Warning, string Pinning, bool PathCapable);

/// <summary>What an install call reports.</summary>
/// <param name="State"><c>ready</c> or <c>failed</c>.</param>
/// <param name="Detail">A plain sentence about the outcome.</param>
public sealed record LanguageProvisionResult(string State, string Detail = "");

/// <summary>The settings schema of one server.</summary>
/// <param name="State"><c>ready</c> or the reason there is none.</param>
/// <param name="Schema">The schema as JSON text; empty when none.</param>
public sealed record LanguageSchemaResult(string State, string Schema = "");

/// <summary>One running server.</summary>
/// <param name="Id">The catalog id.</param>
/// <param name="Workspace">The workspace it serves.</param>
/// <param name="MemoryBytes">Its last sampled memory.</param>
/// <param name="IdleSeconds">Seconds since a request last used it.</param>
public sealed record LanguageRunningRow(string Id, string Workspace, long MemoryBytes, long IdleSeconds);

/// <summary>What runs now and the memory budget.</summary>
/// <param name="Servers">The running servers.</param>
/// <param name="BudgetBytes">The memory budget in force.</param>
/// <param name="PhysicalBytes">The machine's memory.</param>
/// <param name="AutomaticBudget">Whether the budget is the automatic one.</param>
/// <param name="Pressure">Whether the system reports memory pressure.</param>
/// <param name="DefaultCaps">The default cap per kind, by kind id.</param>
public sealed record LanguageRunningState(IReadOnlyList<LanguageRunningRow> Servers, long BudgetBytes, long PhysicalBytes, bool AutomaticBudget, bool Pressure,
    IReadOnlyDictionary<string, int> DefaultCaps);

/// <summary>A terms call: read the terms, or record the user's acceptance.</summary>
/// <param name="Workspace">The absolute workspace path.</param>
/// <param name="Server">The server's catalog id.</param>
/// <param name="Accept">True to record acceptance of exactly the terms named below.</param>
/// <param name="ArtifactId">The installation the terms belong to.</param>
/// <param name="EulaSha256">The hash of the terms text.</param>
/// <param name="Answers">The user's answers by question id; complete to accept.</param>
public sealed record LanguageTermsParams(string Workspace, string Server, bool Accept = false, string ArtifactId = "", string EulaSha256 = "",
    IReadOnlyDictionary<string, string>? Answers = null);

/// <summary>A question asked next to the terms.</summary>
/// <param name="Id">The key of its answer.</param>
/// <param name="Prompt">What is asked.</param>
/// <param name="Values">The accepted answers.</param>
/// <param name="Labels">The label of each value.</param>
public sealed record LanguageTermsQuestionRow(string Id, string Prompt, IReadOnlyList<string> Values, IReadOnlyList<string> Labels);

/// <summary>The terms of one installation and what the user recorded.</summary>
/// <param name="State"><c>ready</c> or the reason there are none.</param>
/// <param name="Detail">A plain sentence about the state.</param>
/// <param name="Version">The installation's version.</param>
/// <param name="ArtifactId">The installation the terms belong to.</param>
/// <param name="EulaSha256">The hash of the terms text.</param>
/// <param name="Text">The terms.</param>
/// <param name="Accepted">Whether the user accepted them.</param>
/// <param name="AcceptedAtUtc">When, in round-trip format; empty when not.</param>
/// <param name="Questions">The questions to answer with the terms.</param>
/// <param name="Answers">The recorded answers, or the earlier ones to offer again.</param>
public sealed record LanguageTermsState(string State, string Detail, string Version, string ArtifactId, string EulaSha256, string Text, bool Accepted, string AcceptedAtUtc,
    IReadOnlyList<LanguageTermsQuestionRow> Questions, IReadOnlyDictionary<string, string> Answers);

/// <summary>A server's launcher choices, as the host saves them.</summary>
/// <param name="Server">The server's catalog id.</param>
/// <param name="AllowPath">Whether the user lets it run an unverified executable from PATH.</param>
/// <param name="Command">An explicit executable; empty for none.</param>
/// <param name="Arguments">Arguments that replace the defaults; null keeps the defaults.</param>
public sealed record LanguageServerOptionsParams(string Server, bool AllowPath = false, string Command = "", string[]? Arguments = null);

/// <summary>One value of a host setting this plugin owns, as JSON text or a plain string.</summary>
/// <param name="Value">The value to save.</param>
public sealed record CodeIntelligenceValue(string Value);

/// <summary>What the page reads of host state; pushed on <see cref="CodeIntelligenceContract.Settings"/>.</summary>
/// <param name="Enabled">Whether language services are on.</param>
/// <param name="LanguageServers">The launcher choices of every server, as the host saves them (JSON).</param>
/// <param name="LanguageTools">The host-wide tool paths (JSON).</param>
/// <param name="Limits">The limits (JSON).</param>
/// <param name="JavaKotlinProvider">The provider chosen for Java and Kotlin.</param>
public sealed record CodeIntelligenceSettingsState(bool Enabled, string LanguageServers, string LanguageTools, string Limits, string JavaKotlinProvider);

/// <summary>Asks for the current settings; the snapshot of <see cref="CodeIntelligenceContract.Settings"/>.</summary>
public sealed record CodeIntelligenceSettingsParams;

/// <summary>Pushed when the catalog changes because a plugin activated or deactivated.</summary>
/// <param name="Revision">Grows with every change.</param>
public sealed record CodeIntelligenceCatalogChange(long Revision);

/// <summary>The Code Intelligence plugin: the switch for every language feature the app shows, and the host side of its settings page.</summary>
public static class CodeIntelligenceContract
{
    public const string Id = "code-intelligence";

    /// <summary>The servers active plugins provide. Starts no process.</summary>
    public static readonly PluginMethod<LanguageServerParams, LanguageCatalogState> Catalog = new("catalog");

    /// <summary>How one server's launcher resolves, without starting it.</summary>
    public static readonly PluginMethod<LanguageServerParams, LanguageResolution> Resolution = new("resolution");

    /// <summary>Installs one server and its pinned runtimes without starting a session.</summary>
    public static readonly PluginMethod<LanguageServerParams, LanguageProvisionResult> Provision = new("provision");

    /// <summary>The settings schema of one server.</summary>
    public static readonly PluginMethod<LanguageServerParams, LanguageSchemaResult> ConfigurationSchema = new("configurationSchema");

    /// <summary>What runs now and the memory budget.</summary>
    public static readonly PluginMethod<LanguageServerParams, LanguageRunningState> Running = new("running");

    /// <summary>Reads or records the terms of one installation. Acceptance is the user's, never an agent's.</summary>
    public static readonly PluginMethod<LanguageTermsParams, LanguageTermsState> Terms = new("terms");

    /// <summary>Saves one server's launcher choices.</summary>
    public static readonly PluginMethod<LanguageServerOptionsParams, CodeIntelligenceSettingsState> SetServerOptions = new("setServerOptions");

    /// <summary>Saves the limits (JSON).</summary>
    public static readonly PluginMethod<CodeIntelligenceValue, CodeIntelligenceSettingsState> SetLimits = new("setLimits");

    /// <summary>Turns language services on or off (<c>true</c> or <c>false</c>).</summary>
    public static readonly PluginMethod<CodeIntelligenceValue, CodeIntelligenceSettingsState> SetEnabled = new("setEnabled");

    /// <summary>Saves the provider chosen for Java and Kotlin.</summary>
    public static readonly PluginMethod<CodeIntelligenceValue, CodeIntelligenceSettingsState> SetProvider = new("setProvider");

    /// <summary>The current settings; the snapshot of <see cref="Settings"/>.</summary>
    public static readonly PluginMethod<CodeIntelligenceSettingsParams, CodeIntelligenceSettingsState> ReadSettings = new("readSettings");

    /// <summary>Host state the page shows; one value for every client, pushed on every change.</summary>
    public static readonly PluginChannel<CodeIntelligenceSettingsState> Settings = PluginChannel<CodeIntelligenceSettingsState>.State("settings", ReadSettings);

    /// <summary>The catalog changed.</summary>
    public static readonly PluginChannel<CodeIntelligenceCatalogChange> CatalogChanged = PluginChannel<CodeIntelligenceCatalogChange>.Event("catalogChanged");

    public static readonly PluginContract Contract = PluginContract.Create(Id, 1,
        [Catalog, Resolution, Provision, ConfigurationSchema, Running, Terms, SetServerOptions, SetLimits, SetEnabled, SetProvider, ReadSettings],
        [Settings, CatalogChanged], CodeIntelligenceJson.Default);

    public static readonly PluginManifest Manifest = new(Id, "Code Intelligence", "code-s-slash-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.CodeIntelligence,
        Description = "The language features of the app: the Code Intelligence settings and tool window, and hover, go to definition, usages and diagnostics in the editor. The language server plugins and Air Context require it.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.Yes,
        Host = "Tereditor.Plugins.CodeIntelligence.Host.dll"
    };
}

[JsonSerializable(typeof(LanguageServerParams))]
[JsonSerializable(typeof(LanguageCatalogState))]
[JsonSerializable(typeof(LanguageResolution))]
[JsonSerializable(typeof(LanguageProvisionResult))]
[JsonSerializable(typeof(LanguageSchemaResult))]
[JsonSerializable(typeof(LanguageRunningState))]
[JsonSerializable(typeof(LanguageTermsParams))]
[JsonSerializable(typeof(LanguageTermsState))]
[JsonSerializable(typeof(LanguageServerOptionsParams))]
[JsonSerializable(typeof(CodeIntelligenceValue))]
[JsonSerializable(typeof(CodeIntelligenceSettingsState))]
[JsonSerializable(typeof(CodeIntelligenceSettingsParams))]
[JsonSerializable(typeof(CodeIntelligenceCatalogChange))]
internal sealed partial class CodeIntelligenceJson : JsonSerializerContext;