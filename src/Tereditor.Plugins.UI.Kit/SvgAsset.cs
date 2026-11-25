using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>
/// Draws SVG bytes, such as a plugin's <c>asset:</c> icon, at a square size while attached. An unreadable document
/// draws nothing. A glyph drawn in <c>currentColor</c> takes the given brush's colour and follows theme changes, the way
/// a monochrome icon follows its text colour.
/// </summary>
public sealed partial class SvgAsset : Image
{
    private readonly byte[] svg;
    private readonly ISolidColorBrush? color;
    private static readonly Dictionary<(byte[] Bytes, Color? Tint, string? Variant), SharedSource> Sources = [];
    private (byte[] Bytes, Color? Tint, string? Variant)? sourceKey;

    /// <summary>
    /// The agent this mark belongs to: <c>claude</c>, <c>pi</c> and <c>acp</c> have brand colours (see <see cref="Ui.AgentColor"/> and <see cref="Ui.AgentPalette"/>),
    /// and <c>acp-registry</c> marks a registry agent's own icon, which keeps its hard-coded colours while <see cref="Ui.ColoredAgentIcons"/> is on and is drawn
    /// in the tint alone while it is off. While <see cref="Ui.ColoredAgentIcons"/> is on and the agent has a brand colour, it replaces the tint, and the mark redraws when the choice changes.
    /// </summary>
    public string? Agent { get; init; }

    private Color[]? Palette => Agent is not null && Ui.ColoredAgentIcons ? Ui.AgentPalette(Agent) : null;

    private string? Variant => Palette is not null ? "palette" : Agent == "acp-registry" && !Ui.ColoredAgentIcons ? "mono" : null;

    private Color? Tint => Agent is not null && Ui.ColoredAgentIcons && Ui.AgentColor(Agent) is { } brand ? brand : color?.Color;

    private sealed class SharedSource(SvgSource? source)
    {
        internal SvgSource? Source { get; } = source;
        internal SvgImage? Image { get; } = source is null ? null : new SvgImage { Source = source };
        internal int Users { get; set; }
    }

    public SvgAsset(byte[] svg, double size) : this(svg, size, null) { }

    public SvgAsset(byte[] svg, double size = 16, IBrush? color = null)
    {
        this.svg = svg;
        this.color = color as ISolidColorBrush;
        Width = size;
        Height = size;
        Stretch = Stretch.Uniform;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (color is not null) Ui.ThemeChanged += Load;
        if (Agent is not null) Ui.AgentIconsChanged += Load;
        Load();
    }

    private void Load()
    {
        var key = (svg, Tint, Variant);
        if (sourceKey == key) return;
        Release();
        if (!Sources.TryGetValue(key, out var shared))
            Sources.Add(key, shared = new SharedSource(Parse()));
        shared.Users++;
        sourceKey = key;
        Source = shared.Image;
    }

    private SvgSource? Parse()
    {
        try
        {
            var bytes = svg;
            var text = Encoding.UTF8.GetString(svg);
            if (Variant == "mono") text = HardColor().Replace(text, match => match.Groups["v"].Value.Trim() is "none" or "currentColor" or "inherit" or "transparent" || match.Groups["v"].Value.Contains("url(", StringComparison.Ordinal) ? match.Value : match.Groups["p"].Value + "currentColor");
            if (Palette is { } palette)
            {
                var next = 0;
                text = CurrentColor().Replace(text, _ => ToHex(palette[Math.Min(next++, palette.Length - 1)]));
            }
            if (Tint is { } tint) text = text.Replace("currentColor", ToHex(tint), StringComparison.Ordinal);
            if (Variant is not null || Tint is not null) bytes = Encoding.UTF8.GetBytes(text);
            using var stream = new MemoryStream(bytes);
            return SvgSource.LoadFromStream(stream);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string ToHex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    [System.Text.RegularExpressions.GeneratedRegex("currentColor")]
    private static partial System.Text.RegularExpressions.Regex CurrentColor();

    // A fill, stroke or stop-color, as an attribute or a style declaration.
    [System.Text.RegularExpressions.GeneratedRegex("""(?<p>\b(?:fill|stroke|stop-color)\s*[=:]\s*["']?)(?<v>[^"';}]+)""")]
    private static partial System.Text.RegularExpressions.Regex HardColor();

    private void Release()
    {
        Source = null;
        if (sourceKey is not { } key) return;
        sourceKey = null;
        var shared = Sources[key];
        if (--shared.Users == 0)
        {
            Sources.Remove(key);
            shared.Source?.Dispose();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Ui.ThemeChanged -= Load;
        Ui.AgentIconsChanged -= Load;
        Release();
    }
}