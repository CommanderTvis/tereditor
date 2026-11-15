namespace Tereditor.Plugins.Acp;

/// <summary>Asks where an agent's sign-in comes from. Only the agent's id travels: the host answers from its own entry.</summary>
public sealed record AcpSignInParams(string AgentId);

/// <summary>
/// One place a sign-in can come from. <see cref="Kind"/> is <c>env</c>, <c>stored</c>, <c>gh</c>, <c>provider</c> or <c>agent</c>.
/// <see cref="CheckedFirst"/> marks the first source the agent checks that is known to be there. The text never holds a secret.
/// </summary>
public sealed record AcpSignInLine(string Kind, string Text)
{
    public bool CheckedFirst { get; init; }

    /// <summary>The line as the pane shows it.</summary>
    public string Display() => CheckedFirst ? Text + " (checked first)" : Text;
}

/// <summary>A sign-in the agent offered in its answer to <c>initialize</c>. <see cref="CommandLine"/> is set when it is a terminal login: the exact line a terminal would run.</summary>
public sealed record AcpSignInMethod(string Id, string Name, string? Description)
{
    public string? Label { get; init; }
    public string? CommandLine { get; init; }
}

/// <summary>What the host found for one agent: the sources in the order the agent checks them, why, and what the agent itself offered.</summary>
public sealed record AcpSignInInfo(string AgentId, IReadOnlyList<AcpSignInLine> Lines, string Why, IReadOnlyList<AcpSignInMethod> Methods)
{
    /// <summary>The lines under the heading "Signs in with", ready to show.</summary>
    public IReadOnlyList<string> Display() => [.. Lines.Select(line => line.Display())];

    /// <summary>The first terminal login the agent offered, if any.</summary>
    public AcpSignInMethod? Terminal => Methods.FirstOrDefault(method => method.CommandLine is { Length: > 0 });
}
