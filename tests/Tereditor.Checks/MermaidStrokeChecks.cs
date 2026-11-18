using System.Xml.Linq;

using Tereditor.Plugins.UI.Kit.Visualization;

namespace Tereditor.Checks;

/// <summary>A class or style stroke belongs to the node shape; labels keep only their colour.</summary>
internal static class MermaidStrokeChecks
{
    private const string Options = """{"svg":{"pipeline":"resvg-safe","root_background_color":"transparent"},"host_theme":{"appearance":"dark","roles":{"text":"#e5e7eb"},"output":{"pipeline":"resvg-safe","css_override_policy":"strip-existing-important"}}}""";

    private const string Source = """
        flowchart TD
          A[Start] --> B{Pick}
          B -->|go| C[Run]
          subgraph S[Group]
            D[Inner]
          end
          subgraph T[Other]
            E[Last]
          end
          C --> D
          D --> E
          classDef run fill:#0b1220,stroke:#60a5fa,stroke-width:4px,color:#fff
          class C,S run
          style A stroke:#f59e0b,stroke-width:4px,color:#0f0
          style T stroke:#ff0000,stroke-width:3px,color:#00f
        """;

    internal static void Run()
    {
        PinsInjectedStyle();
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("PASS Mermaid label stroke: injected style pinned (native renderer is macOS only here)");
            return;
        }
        var result = MermaidRenderer.Render(Source, Options);
        Require(result.Svg is not null, "Merman renders the stroked flowchart: " + result.Error);
        var document = XDocument.Parse(result.Svg!);
        var texts = document.Descendants().Where(element => element.Name.LocalName == "text").ToList();
        Require(texts.Count >= 7, "Every node, cluster and edge label is a text element.");
        foreach (var text in texts)
        {
            var style = Declarations((string?)text.Attribute("style"));
            Require(style.GetValueOrDefault("stroke") == "none" && style.GetValueOrDefault("stroke-width") == "0",
                "A label text carries no stroke: " + text.Value);
            Require(style.GetValueOrDefault("fill") is not ("#0b1220" or "#60a5fa"), "A label text never takes the class fill or stroke: " + text.Value);
        }
        Require(Fill(texts, "Run") is "rgb(255, 255, 255)" or "#fff" or "#ffffff", "classDef color reaches the node label: " + Fill(texts, "Run"));
        Require(Fill(texts, "Start") is "rgb(0, 255, 0)" or "#0f0" or "#00ff00", "style color reaches the node label: " + Fill(texts, "Start"));
        Require(Fill(texts, "go") is "#e5e7eb", "An edge label keeps the theme ink: " + Fill(texts, "go"));
        var shapes = document.Descendants().Where(element => element.Name.LocalName is "rect" or "polygon" or "path").ToList();
        bool Stroked(string stroke, string width) => shapes.Any(shape => Declarations((string?)shape.Attribute("style")) is var style
            && style.GetValueOrDefault("stroke") == stroke && style.GetValueOrDefault("stroke-width") == width);
        Require(Stroked("#60a5fa", "4px"), "classDef stroke and stroke-width reach the node shape.");
        Require(Stroked("#f59e0b", "4px"), "style stroke and stroke-width reach the node shape.");
        Require(Stroked("#ff0000", "3px"), "style stroke and stroke-width reach the subgraph shape.");
        Console.WriteLine("PASS Mermaid label stroke: class and style strokes stay on shapes, color reaches nodes and edge labels");
    }

    private static void PinsInjectedStyle()
    {
        const string plain = "<svg><text fill=\"#fff\" class=\"merman-foreignobject-fallback-text run\">a</text><text class=\"other\">b</text></svg>";
        var pinned = MermaidRenderer.WithoutLabelStroke(plain);
        Require(pinned.Contains("style=\"fill:#fff;stroke:none;stroke-width:0\"", StringComparison.Ordinal), "A fallback text without a style gains one: " + pinned);
        Require(pinned.EndsWith("<text class=\"other\">b</text></svg>", StringComparison.Ordinal), "Other texts stay untouched.");
        var styled = MermaidRenderer.WithoutLabelStroke("<text fill=\"red\" class=\"merman-foreignobject-fallback-text\" style=\"font-size:16px\">a</text>");
        Require(styled.Contains("style=\"fill:red;stroke:none;stroke-width:0;font-size:16px\"", StringComparison.Ordinal), "An existing style keeps its declarations after the pin: " + styled);
    }

    private static string? Fill(List<XElement> texts, string value)
        => (string?)texts.First(text => text.Value == value).Attribute("fill");

    private static Dictionary<string, string> Declarations(string? style)
        => (style ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':', 2)).Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0].Trim()).ToDictionary(group => group.Key, group => group.First()[1].Trim());

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}