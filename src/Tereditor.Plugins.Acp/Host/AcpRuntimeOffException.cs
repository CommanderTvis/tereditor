namespace Tereditor.Plugins.Acp.Host;

/// <summary>An agent cannot start because its runtime is missing and Runtimes is off. The chat offers to turn it on.</summary>
internal sealed class AcpRuntimeOffException(string kind, string runtime, string message) : InvalidOperationException(message)
{
    /// <summary>The runtime as the Runtimes page names it.</summary>
    internal string Kind { get; } = kind;

    /// <summary>The runtime's name for the user.</summary>
    internal string Runtime { get; } = runtime;
}