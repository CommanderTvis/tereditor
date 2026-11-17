namespace Tereditor.Plugins.Acp.Host;

/// <summary>How a registry npx agent is started with Bun instead of npx: the same pinned package, the runner changes.</summary>
internal static class AcpRunner
{
    /// <summary>
    /// <c>npx -y name@1.2.3 args</c> becomes <c>bunx name@1.2.3 args</c> (run as <c>bun x</c>, which has no prompt to answer and so no <c>-y</c>).
    /// A leading <c>-y</c> or <c>--yes</c> is dropped; every other argument, the package spec included, is kept as it was.
    /// </summary>
    internal static (string Command, IReadOnlyList<string> Args) Bun(string bun, IReadOnlyList<string> npxArgs) => AcpEntryText.Bun(bun, npxArgs);
}