using System.Net;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using Tereditor.Checks.E2E;
using Tereditor.Host.Abstractions;
using Tereditor.Host.Remote;
using Tereditor.Plugins.UI.Kit;
using Tereditor.UI;
using Tereditor.UI.Docking;
using Tereditor.UI.Panels;
using Tereditor.UI.Rendering;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

internal static partial class FileIconsChecks
{
    internal static void UiChecks(string root)
    {
        SharedSvgLifetime();
        DeferredCatalogClick(root);
        foreach (var remote in new[] { false, true })
        {
            var mode = remote ? "remote" : "local";
            var workspace = Path.Combine(root, "file-icons-ui", mode);
            Directory.CreateDirectory(workspace);
            var source = Environment.GetEnvironmentVariable("TEREDITOR_TEST_GIT_SOURCE");
            if (source is not null)
            {
                IsolatedGit.Run(workspace, "init", "-b", "main");
                IsolatedGit.Run(workspace, "fetch", "--no-tags", "--depth=1", source, "HEAD");
                IsolatedGit.Run(workspace, "checkout", "-B", "main", "FETCH_HEAD");
            }
            File.WriteAllText(Path.Combine(workspace, "README.md"), "# Icon fixture\n");
            File.WriteAllText(Path.Combine(workspace, "SPEC.md"), "# Icon specification\n");
            File.WriteAllBytes(Path.Combine(workspace, "logo.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
            File.WriteAllText(Path.Combine(workspace, "sample.pdf"), "%PDF-1.7\n");
            File.WriteAllText(Path.Combine(workspace, "unknown.qqqq"), "unknown\n");
            File.WriteAllText(Path.Combine(workspace, "server.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.Combine(workspace, "src"));
            var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "icons", workspace + "-state") : null;
            try
            {
                if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
                using var app = server is null ? new E2eWorkspace(workspace)
                    : new E2eWorkspace(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "icons", workspace, workspace + "-profile", workspace);
                Until(() => app.Window.WorkspaceMounted);
                app.ShowTool("files");
                Require(app.Window.Layout.State.Groups.Where(group => group.Tools.Any(tab => tab.Id == "files")).All(group => app.Window.Layout.Selected(group.Id)?.Id == "files"),
                    "Clicking Files selects its rail tab before checking icons.");
                Scenario(app, source is not null);
                Console.WriteLine($"PASS file-icons {mode} UI: typed tree/tab/change icons, theme raster, resize retention and disable/re-enable");
            }
            finally
            {
                if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            }
        }
        UnavailableAssets(root);
    }

    private static void DeferredCatalogClick(string root)
    {
        TaskCompletionSource? gitReady = null;
        var directory = IsolatedGit.Repository(Path.Combine(root, "file-icons-deferred-click"));
        using var app = new E2eWorkspace(directory, openFiles: false, prepare: host => gitReady = host.HoldGit());
        Until(() => app.Window.WorkspaceMounted);
        var button = app.Find<Button>("Tab_files");
        app.Window.UpdateLayout();
        button.BringIntoView();
        Settle(550);
        var point = button.TranslatePoint(new Point(Math.Min(24, button.Bounds.Width / 2), button.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Left);
        gitReady!.SetResult();
        Until(() => app.Window.Layout.Tools.Any(tool => tool.Id == "plugin:branch-graph:graph"));
        app.Window.MouseUp(point, MouseButton.Left);
        Until(() => app.Window.Layout.State.Groups.Where(group => group.Tools.Any(tab => tab.Id == "files")).All(group => app.Window.Layout.Selected(group.Id)?.Id == "files"));
        Until(() => ReferenceEquals(button, app.Find<Button>("Tab_files")) || app.Find<Button>("Tab_files").IsFocused);
        Console.WriteLine("PASS file-icons deferred catalog: Git discovery during a tab press preserves its click");
    }

    private static Control Asset(Control scope, string name) => scope.GetLogicalDescendants().OfType<Control>()
        .Single(control => control.Name == "FileTypeIcon" && Equals(control.Tag, $"file-type:{name}"));

    private static void Loaded(Control scope, string name) => Until(() => scope.GetLogicalDescendants().OfType<Control>()
        .Any(control => control.Name == "FileTypeIcon" && Equals(control.Tag, $"file-type:{name}") &&
            control.GetLogicalDescendants().OfType<SvgAsset>().Any(svg => svg.Source is not null && svg.IsEffectivelyVisible)));

    // Settings sits over the workbench in its overlay with a scrim; the icon is read from the workbench alone.
    private static byte[] Raster(E2eWorkspace app, Control icon)
    {
        var layers = app.Window.GetLogicalDescendants().OfType<Control>()
            .Where(control => control.IsVisible && (control is Tereditor.UI.OverlayHost || control is Border border && ReferenceEquals(border.Background, Ui.Overlay))).ToArray();
        foreach (var layer in layers) layer.IsVisible = false;
        try { return Raster(app.Window, icon); }
        finally { foreach (var layer in layers) layer.IsVisible = true; }
    }

    private static byte[] Raster(Window window, Control icon)
    {
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame()!;
        var at = icon.TranslatePoint(default, window)!.Value * window.RenderScaling;
        var width = (int)Math.Ceiling(icon.Bounds.Width * window.RenderScaling);
        var height = (int)Math.Ceiling(icon.Bounds.Height * window.RenderScaling);
        var pixels = new byte[width * height * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect((int)at.X, (int)at.Y, width, height), pinned.AddrOfPinnedObject(), pixels.Length, width * 4); }
        finally { pinned.Free(); }
        return pixels;
    }

    private static void SharedSvgLifetime()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><path fill='currentColor' d='M0 0H16V16H0Z'/></svg>");
        var first = new SvgAsset(bytes, 16, Brushes.White);
        var second = new SvgAsset(bytes, 16, Brushes.White);
        var tinted = new SvgAsset(bytes, 16, Brushes.Red);
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { first, second, tinted } };
        var window = new Window { Width = 80, Height = 40, Background = Brushes.Black, Content = panel };
        window.Show();
        try
        {
            Settle(50);
            var shared = first.Source;
            Require(shared is not null && ReferenceEquals(shared, second.Source) && !ReferenceEquals(shared, tinted.Source),
                "Mounted icons share parsed SVGs only when both asset bytes and tint match.");
            var before = Raster(window, second);
            Require(before.Where((_, index) => index % 4 < 3).All(channel => channel == 255), "The shared white SVG renders its filled square.");
            Require(!Raster(window, tinted).SequenceEqual(before), "A different tint renders independently.");
            panel.Children.Remove(first); Settle(50);
            Require(first.Source is null && ReferenceEquals(second.Source, shared) && Raster(window, second).SequenceEqual(before),
                "Recycling one icon releases its reference without disposing a still-visible sibling's SVG.");
            panel.Children.Remove(second);
            Require(second.Source is null, "The last detached icon releases its image.");
            panel.Children.Add(first); Settle(50);
            Require(first.Source is not null && !ReferenceEquals(first.Source, shared) && Raster(window, first).SequenceEqual(before),
                "A later attachment parses a fresh source after the last user released the old one.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS SVG icon sharing: matching assets/tints, rendered sibling lifetime and release after last detach");
    }

    private static void Scenario(E2eWorkspace app, bool git)
    {
        TwoTone(app);
        AgentIcons(app);
        foreach (var (file, typeIcon) in new[] { ("README.md", "readme"), ("SPEC.md", "markdown"), ("logo.png", "image"), ("sample.pdf", "pdf"), ("unknown.qqqq", "file") })
            Loaded(app.FileRow(file), typeIcon);
        var row = app.FileRow("README.md");
        row.BringIntoView();
        Settle(100);
        var icon = Asset(row, "readme");
        var image = icon.GetLogicalDescendants().OfType<SvgAsset>().Single();
        var before = Raster(app, icon);
        var original = Ui.Theme;
        var colors = original.Colors.ToDictionary();
        colors["text"] = Color.Parse("#ff3366");
        var source = image.Source;
        Ui.Apply(original with { Id = original.Id + "-file-icons", Colors = colors });
        Until(() => !ReferenceEquals(image.Source, source));
        Settle(50);
        Require(!Raster(app, icon).SequenceEqual(before), "The SVG follows the theme's text color in the rendered frame.");
        Require(ReferenceEquals(Asset(app.FileRow("README.md"), "readme"), icon), "A theme change retains the row's icon control.");
        Ui.Apply(original);

        app.Open("README.md", keep: true);
        Loaded(app.Tab("README.md"), "readme");
        var tabIcon = Asset(app.Tab("README.md"), "readme");
        var splitter = app.Find<ResizeHandle>("leftSeparator");
        var point = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point);
        app.Window.MouseDown(point, MouseButton.Left);
        for (var index = 1; index <= 6; index++)
        {
            app.Window.MouseMove(point + new Point(index * 5, 0));
            app.Window.UpdateLayout();
            Require(ReferenceEquals(Asset(app.Tab("README.md"), "readme"), tabIcon), "Resizing a column retains its tab icon instead of remounting it each frame.");
        }
        app.Window.MouseUp(point + new Point(30, 0), MouseButton.Left);

        if (git)
        {
            var refresh = app.Window.RefreshAsync();
            Until(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            app.ShowTool("changes");
            Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file-type:python")));
            Loaded(app.Window, "python");
            app.Click(app.Find<ToggleButton>("ChangesTree"));
            Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree"));
            Loaded(app.Find<TreeView>("ChangesTree"), "python");
        }
        else Console.WriteLine("SKIP file-icons change rows: set TEREDITOR_TEST_GIT_SOURCE to an existing clone.");

        app.ShowTool("files");
        app.Window.ShowSettings("Appearance");
        Until(() => app.Window.OpenSettingsViews.Any());
        var settings = app.Window.OpenSettingsViews.Single();
        // Every host settings change rebuilds the Appearance page, so the cards are looked up afresh for each pick.
        OptionCards Styles() => settings.GetLogicalDescendants().OfType<OptionCards>().Single(control => control.Name == "FileTypeIconChoices");
        void Pick(string name) => app.Click(Styles().GetLogicalChildren().OfType<Button>().Single(card => card.Name == "FileTypeIconChoices_" + name));
        Until(() => Styles().IsEnabled);
        Require(app.Workbench.State.Current.Settings is { FileTypeIconsEnabled: true, FileTypeIconStyle: "" }, "Core file-type icons are on and plain by default.");
        Pick("none");
        Until(() => !app.Workbench.State.Current.Settings.FileTypeIconsEnabled);
        Until(() => !app.FileRow("README.md").GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "FileTypeIcon"));
        Require(app.FileRow("README.md").GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file") && control.IsEffectivelyVisible),
            "Disabling File Icons restores core's plain file glyph.");
        Require(Styles().Options.Count == 5, "The cards offer Hidden beside the four icon styles.");
        Hidden(app, Pick);
        Pick("plain");
        Until(() => app.Workbench.State.Current.Settings.FileTypeIconsEnabled);
        Control Icon(string file, string name)
        {
            var row = app.FileRow(file);
            row.BringIntoView();
            Settle(50);
            return Asset(row, name);
        }
        Loaded(app.FileRow("server.py"), "python");
        var plain = Raster(app, Icon("server.py", "python"));
        Pick("colored");
        Until(() => app.Workbench.State.Current.Settings.FileTypeIconStyle == "colored");
        Loaded(app.FileRow("server.py"), "colored/python");
        var coloredBefore = Raster(app, Icon("server.py", "colored/python"));
        Require(!coloredBefore.SequenceEqual(plain), "The colored style renders differently from plain in the Files row.");
        var theme = Ui.Theme;
        var palette = theme.Colors.ToDictionary();
        palette["info"] = Color.Parse("#ff00ff");
        Ui.Apply(theme with { Id = theme.Id + "-file-colors", Colors = palette });
        Until(() => !Raster(app, Icon("server.py", "colored/python")).SequenceEqual(coloredBefore));
        Ui.Apply(theme);
        Pick("material");
        Until(() => app.Workbench.State.Current.Settings.FileTypeIconStyle == "material");
        Loaded(app.FileRow("server.py"), "material/python");
        Loaded(app.FileRow("src"), "material/folder");
        var material = Raster(app, Icon("server.py", "material/python"));
        Require(!material.SequenceEqual(plain) && !material.SequenceEqual(coloredBefore), "The Material style renders its own colors in the Files row.");
        Pick("plain");
        Until(() => app.Workbench.State.Current.Settings.FileTypeIconStyle.Length == 0);
        Loaded(app.FileRow("server.py"), "python");
        // No settings row draws its hairline around hidden content: the fixed theme list is gone under Match system, so no empty band sits between two rules.
        void NoEmptyRows(string when)
        {
            var empty = settings.GetLogicalDescendants().OfType<Border>().Where(row => row.Classes.Contains("settings-row") && row.IsVisible && row.Child is { } child && (!child.IsVisible || child.Bounds.Height <= 0)).ToList();
            Require(empty.Count == 0, $"The Appearance page ({when}) has {empty.Count} visible rows with nothing in them, which draw two hairlines with an empty band between.");
            // A one-line row centers its label between the rules: the gap above equals the gap below, apart from the 1 px rule.
            foreach (var row in settings.GetLogicalDescendants().OfType<Border>().Where(row => row.Classes.Contains("settings-row") && row.IsVisible && row.Bounds.Height > 0))
                foreach (var label in row.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.Classes.Contains("settings-label") && text.IsEffectivelyVisible && text.GetLogicalParent() is StackPanel { Children.Count: 1 }))
                {
                    var top = label.TranslatePoint(default, row)!.Value.Y;
                    var bottom = row.Bounds.Height - 1 - (top + label.Bounds.Height);
                    Require(Math.Abs(top - bottom) <= 1.5, $"The one-line row '{label.Text}' ({when}) sits {top:0} px under its top rule and {bottom:0} px above its bottom rule.");
                }
        }
        void PickMode(string mode) => app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(card => card.Name == "ThemeMode_" + mode));
        PickMode("fixed");
        Until(() => app.Workbench.State.Current.Settings.ThemeMode == "fixed");
        Settle(50);
        NoEmptyRows("fixed theme");
        PickMode("system");
        Until(() => app.Workbench.State.Current.Settings.ThemeMode == "system");
        Settle(50);
        NoEmptyRows("match system");
        settings.Close();
        Loaded(app.FileRow("README.md"), "readme");
        Loaded(app.Tab("README.md"), "readme");
    }

    // Hidden: no icon element in the tree or the tab, and the text starts where the icon was rather than after a blank slot.
    private static void Hidden(E2eWorkspace app, Action<string> pick)
    {
        pick("hidden");
        Until(() => app.Workbench.State.Current.Settings is { FileTypeIconsEnabled: false, FileTypeIconStyle: "hidden" });
        bool Bare(Control scope) => !scope.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "FileTypeIcon" || control.Tag is "file" or "folder" or "fileText");
        foreach (var path in new[] { "README.md", "server.py", "src" })
        {
            Until(() => app.FileRow(path) is DrawnTreeLabel { Child: null } row && Bare(row));
            var label = (DrawnTreeLabel)app.FileRow(path);
            Require(label.TextOffset == 24, $"A Hidden row's name starts where its icon sat, with no blank slot: {path} at {label.TextOffset}.");
        }
        Require(((DrawnTreeLabel)app.FileRow("src")).Entry.CanExpand, "A Hidden folder keeps its expander chevron.");
        bool TabLeading() => app.Tab("README.md").GetLogicalDescendants().OfType<MarkedRow>().Single()
            .Children.Any(child => child.GetValue(MarkedRow.RoleProperty) == RowRole.Leading);
        Until(() => Bare(app.Tab("README.md")) && !TabLeading());
        app.Window.UpdateLayout();
        var title = app.Tab("README.md").GetLogicalDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("dock-tab-title"));
        Require(title.Bounds.X == 0, $"A Hidden tab's title starts where its icon was, at {title.Bounds.X}.");
        pick("none");
        Until(() => app.Workbench.State.Current.Settings is { FileTypeIconsEnabled: false, FileTypeIconStyle: "" });
        Until(() => app.FileRow("README.md").GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file") && control.IsEffectivelyVisible));
        Require(((DrawnTreeLabel)app.FileRow("README.md")).TextOffset > 24, "None after Hidden brings back the generic glyph and its slot.");
        Until(TabLeading);
        pick("hidden");
        Until(() => app.FileRow("README.md") is DrawnTreeLabel { Child: null });
    }

    // The Agent icons choice: Claude's mark turns brand orange, Pi's three brand colors, omp's its gradient drawing, the ACP mark white on a dark theme and a registry agent's own icon keeps its fills,
    // in an open window; other agents' marks stay plain, and turning it off restores plain (a registry icon's fills then follow the text color).
    private static bool Has(byte[] pixels, Color color)
    {
        for (var at = 0; at + 3 < pixels.Length; at += 4)
            if (pixels[at + 3] == 255 && (pixels[at] == color.B && pixels[at + 1] == color.G && pixels[at + 2] == color.R || pixels[at] == color.R && pixels[at + 1] == color.G && pixels[at + 2] == color.B)) return true;
        return false;
    }

    // omp's gradient has both ends: opaque pixels near its pink start (red over green, blue over green) and near its cyan end (green and blue over red).
    private static bool Gradient(byte[] pixels)
    {
        bool pink = false, cyan = false;
        for (var at = 0; at + 3 < pixels.Length; at += 4)
        {
            if (pixels[at + 3] != 255) continue;
            // The frame is BGRA or RGBA by platform, so red and blue are judged as a pair; the black background and white match neither end.
            int low = Math.Min(pixels[at], pixels[at + 2]), green = pixels[at + 1], high = Math.Max(pixels[at], pixels[at + 2]);
            pink |= green < 130 && low > 150 && high > 180;
            cyan |= green > 150 && low < 90 && high > 180;
        }
        return pink && cyan;
    }

    private static void AgentIcons(E2eWorkspace app)
    {
        var claude = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><circle cx="8" cy="8" r="7" fill="currentColor"/></svg>"""u8.ToArray();
        var mark = new SvgAsset(claude, 32, Brushes.White) { Agent = "claude" };
        var other = new SvgAsset(claude, 32, Brushes.White) { Agent = "codex" };
        var pi = """<svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 560 560"><path fill="currentColor" d="M420 280H280V140H0V0H420V280Z"/><path fill="currentColor" d="M560 560H420V280H560V560Z"/><path fill="currentColor" d="M140 560H0V140H140V280H280V420H140V560Z"/></svg>"""u8.ToArray();
        var registry = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><rect width="8" height="16" fill="#FF0000"/><rect x="8" width="8" height="16" style="fill:#00FF00"/><rect y="6" width="16" height="4" fill="currentColor"/></svg>"""u8.ToArray();
        var piMark = new SvgAsset(pi, 32, Brushes.White) { Agent = "pi" };
        var acpMark = new SvgAsset(claude, 32, new SolidColorBrush(Color.Parse("#808080"))) { Agent = "acp" };
        var registryMark = new SvgAsset(registry, 32, Brushes.White) { Agent = "acp-registry" };
        var omp = """<svg fill="currentColor" viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg"><path d="M10 14h44v9H43v33h-9V23h-9v22h-9V23H10z"/></svg>"""u8.ToArray();
        var ompMark = new SvgAsset(omp, 32, Brushes.White) { Agent = "omp" };
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { mark, other, piMark, acpMark, registryMark, ompMark } };
        var window = new Window { Width = 224, Height = 48, Background = Brushes.Black, Content = panel };
        window.Show();
        try
        {
            Require(!Ui.ColoredAgentIcons && !app.Workbench.State.Current.Settings.AgentIconsColored, "Agent icons are plain by default.");
            Require(Ui.AgentColor("claude") is { } orange && orange == Color.Parse("#D97757") && Ui.AgentColor("codex") is null && Ui.AgentColor("omp") is null && Ui.AgentColor("pi") is null,
                "Only Claude has one brand color; Codex, Pi and omp have no single one to apply.");
            Require(Ui.AgentDrawing("omp") is { } drawing && Ui.AgentDrawing("pi") is null && Ui.AgentDrawing("codex") is null &&
                new[] { "#f84fcc", "#9362f4", "#00dbe4" }.All(drawing.Contains), "Only omp has a colored drawing of its own, in the three documented colors.");
            Settle(100);
            var plain = Raster(window, mark);
            var plainOther = Raster(window, other);
            var palette = Ui.AgentPalette("pi")!;
            var colors = new[] { Color.Parse("#E39586"), Color.Parse("#E9C069"), Color.Parse("#6198BB") };
            Require(palette.SequenceEqual(colors), "Pi's brand colors are the documented constants: salmon #E39586, yellow #E9C069, blue #6198BB.");
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var ompAsset = Path.Combine(directory.FullName, "src", "Tereditor.Plugins.Omp", "assets", "omp-color.svg");
                if (!File.Exists(ompAsset)) continue;
                Require(System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(ompAsset), "<!--.*?-->", "").Trim() == Ui.AgentDrawing("omp"), "omp-color.svg is exactly the drawing the app bundles.");
                var shape = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(Path.GetDirectoryName(ompAsset)!, "omp.svg")), " d=\"([^\"]+)\"").Groups[1].Value;
                Require(shape.Length > 0 && Ui.AgentDrawing("omp")!.Contains($" d=\"{shape}\"", StringComparison.Ordinal), "The colored mark has the plain mark's shape.");
                break;
            }
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var asset = Path.Combine(directory.FullName, "src", "Tereditor.Plugins.Pi", "assets", "pi-color.svg");
                if (!File.Exists(asset)) continue;
                var fills = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(asset), "<path fill=\"(#[0-9A-Fa-f]{6})\"").Select(match => Color.Parse(match.Groups[1].Value)).ToArray();
                Require(fills.SequenceEqual(colors), "pi-color.svg paints exactly the documented constants.");
                break;
            }
            var plainPi = Raster(window, piMark);
            var plainRegistry = Raster(window, registryMark);
            var plainOmp = Raster(window, ompMark);
            Require(Has(plainOmp, Colors.White) && !Gradient(plainOmp), "Plain, omp's mark is the text color.");
            Require(colors.All(color => !Has(plainPi, color)) && Has(plainPi, Colors.White), "Plain, Pi's mark is the text color.");
            Require(!Has(plainRegistry, Colors.Red) && !Has(plainRegistry, Color.Parse("#00FF00")) && Has(plainRegistry, Colors.White), "Plain, a registry icon's fills follow the text color.");
            var acpPlain = Raster(window, acpMark);
            Require(Has(acpPlain, Color.Parse("#808080")), "Plain, the ACP mark follows the text color.");
            Require(plain.SequenceEqual(plainOther), "Plain, an agent's mark is the text color, the same for Claude and the others.");
            app.Window.ShowSettings("Appearance");
            Until(() => app.Window.OpenSettingsViews.Any());
            var settings = app.Window.OpenSettingsViews.Single();
            OptionCards Choices() => settings.GetLogicalDescendants().OfType<OptionCards>().Single(control => control.Name == "AgentIconChoices");
            void PickIcons(string id) => app.Click(Choices().GetLogicalChildren().OfType<Button>().Single(card => card.Name == "AgentIconChoices_" + id));
            Until(() => Choices().IsEnabled);
            Require(Choices().Options.Count == 2 && Choices().SelectedId == "plain", "The cards offer Plain and Colored, Plain first and selected.");
            PickIcons("colored");
            Until(() => app.Workbench.State.Current.Settings.AgentIconsColored && Ui.ColoredAgentIcons);
            Until(() => !Raster(window, mark).SequenceEqual(plain));
            Require(Raster(window, other).SequenceEqual(plainOther), "Colored leaves an agent without a brand color plain.");
            Until(() => colors.All(color => Has(Raster(window, piMark), color)));
            Until(() => Gradient(Raster(window, ompMark)));
            Require(!Has(Raster(window, ompMark), Colors.White), "Colored, none of omp's mark keeps the text color.");
            Require(Has(Raster(window, registryMark), Colors.Red) && Has(Raster(window, registryMark), Color.Parse("#00FF00")) && Has(Raster(window, registryMark), Colors.White), "Colored keeps a registry icon's own fills and still tints its currentColor.");
            Require(Ui.Theme.IsLight ? Has(Raster(window, acpMark), Color.Parse("#808080")) : Has(Raster(window, acpMark), Colors.White) && !Has(Raster(window, acpMark), Color.Parse("#808080")),
                "Colored, the ACP mark is white on a dark theme and keeps the text color on a light one.");
            Until(() => Choices().SelectedId == "colored");
            PickIcons("plain");
            Until(() => !app.Workbench.State.Current.Settings.AgentIconsColored && !Ui.ColoredAgentIcons);
            Until(() => Raster(window, mark).SequenceEqual(plain));
            Until(() => colors.All(color => !Has(Raster(window, piMark), color)) && !Has(Raster(window, registryMark), Colors.Red));
            Until(() => Raster(window, ompMark).SequenceEqual(plainOmp));
            Require(Has(Raster(window, acpMark), Color.Parse("#808080")), "Turning Agent icons off returns the ACP mark to the text color.");
            settings.Close();
        }
        finally { window.Close(); }
    }

    private static void TwoTone(E2eWorkspace app)
    {
        var icon = WorkbenchWindow.CoreFileIcon("python", Brushes.White, 96);
        var window = new Window { Width = 96, Height = 96, Background = Brushes.Black, Content = icon };
        window.Show();
        try
        {
            Loaded(window, "python");
            Settle(100);
            using var frame = window.CaptureRenderedFrame()!;
            var pixels = new byte[frame.PixelSize.Width * frame.PixelSize.Height * 4];
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { frame.CopyPixels(new PixelRect(frame.PixelSize), pinned.AddrOfPinnedObject(), pixels.Length, frame.PixelSize.Width * 4); }
            finally { pinned.Free(); }
            int Channel(int x, int y) => pixels[((int)(y * window.RenderScaling) * frame.PixelSize.Width + (int)(x * window.RenderScaling)) * 4];
            Require(Channel(40, 24) >= 250 && Channel(48, 64) is >= 110 and <= 120,
                "The Python icon renders its primary at full color and its secondary at 45% opacity, as in the fork.");
        }
        finally { window.Close(); }
    }

    private static void UnavailableAssets(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "file-icons-fallback"));
        Until(() => app.Window.WorkspaceMounted);
        foreach (var malformed in new[] { false, true })
        {
            var icon = malformed ? new AssetIcon("malformed", "file", null, 14) : WorkbenchWindow.CoreFileIcon("missing-icon");
            if (malformed) ((AssetIcon)icon).Show(System.Text.Encoding.UTF8.GetBytes("not an SVG"), Brushes.White, 14);
            var window = new Window { Width = 80, Height = 80, Content = icon };
            window.Show();
            try
            {
                Settle(100);
                Require(icon.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "file") && control.IsEffectivelyVisible),
                    "Missing and malformed SVG assets draw the plain file glyph rather than an empty slot.");
            }
            finally { window.Close(); }
        }
        Console.WriteLine("PASS core file-icons asset fallback: missing and malformed SVGs retain the file glyph");
    }
}