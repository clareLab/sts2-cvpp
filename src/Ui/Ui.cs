using Godot;

namespace cvpp;

internal static class Ui
{
    internal const string Search = "res://images/atlases/relic_atlas.sprites/frozen_eye.tres";
    internal const string Step = "res://images/atlases/compressed.sprites/back_button_arrow.tres";
    internal const string Turn = "res://images/atlases/ui_atlas.sprites/settings_tiny_right_arrow.tres";
    internal const string Auto = "res://images/atlases/ui_atlas.sprites/map/icons/map_monster.tres";
    internal const string Route = "res://images/atlases/relic_atlas.sprites/history_course.tres";
    internal const string Settings = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_settings.tres";
    internal const string Heart = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres";
    internal const string Timer = "res://images/atlases/ui_atlas.sprites/top_bar/timer_icon.tres";
    internal const string Explored = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_map.tres";
    internal const string Card = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_deck.tres";
    internal const string Choice = "res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres";
    internal static readonly Color Muted = new("83918d");
    internal static readonly Color Gold = new("f2d68d");
    internal static readonly Color Loss = new("bd4746");
    internal static readonly Color Gain = new("507b46");
    private static Theme? _theme;
    private static Texture2D? _pause;
    private static Texture2D? _reset;
    private static Texture2D? _speed;
    internal static Theme Theme => _theme ??= CreateTheme();
    private static Texture2D Pause => _pause ??= Glyph("<path d='M8 5v14M16 5v14' stroke='#eee5cf' stroke-width='4' stroke-linecap='round'/>");
    internal static Texture2D Reset => _reset ??= Glyph("<path d='M4 10a8 8 0 1 1 1 7M4 4v6h6' fill='none' stroke='#eee5cf' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'/>");
    internal static Texture2D Speed => _speed ??= Glyph("<path d='m4 6 7 6-7 6m9-12 7 6-7 6' fill='none' stroke='#eee5cf' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'/>");

    private static Texture2D Glyph(string content)
    {
        using var image = new Image();
        image.LoadSvgFromString("<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24' viewBox='0 0 24 24'>" + content + "</svg>", 2);
        return ImageTexture.CreateFromImage(image);
    }

    internal static Texture2D Texture(string path)
    {
        var texture = GD.Load<Texture2D>(path);
        return texture is AtlasTexture atlas
            ? new AtlasTexture { Atlas = atlas.Atlas, Region = atlas.Region, FilterClip = true } : texture;
    }

    internal static TextureRect Image(string path, int size = 24) => Image(Texture(path), size);

    internal static TextureRect Image(Texture2D texture, int size = 24) => new()
    {
        Texture = texture,
        CustomMinimumSize = new Vector2(size, size),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    internal static Label Text(string text, int size = 20)
    {
        var label = new Label { Text = text, Theme = Theme, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    internal static void FitTree(Tree tree)
    {
        if (tree.GetNodeOrNull<Label>("CvppActionHeading") is { } heading)
        {
            var panel = tree.GetThemeStylebox("panel");
            float left = panel.ContentMarginLeft + tree.GetColumnWidth(0) + tree.GetColumnWidth(1);
            float inset = 28 + tree.GetThemeConstant("h_separation") * 2;
            float height = tree.GetThemeFont("title_button_font").GetHeight(tree.GetThemeFontSize("title_button_font_size"))
                + tree.GetThemeStylebox("title_button_normal").GetMinimumSize().Y;
            heading.Position = new Vector2(left + inset, panel.ContentMarginTop);
            heading.Size = new Vector2(Math.Max(1, tree.GetColumnWidth(2) - inset), height);
        }
        if (tree.GetRoot() is not { } root) return;
        string layout = root.GetInstanceId() + ":" + string.Join(':', Enumerable.Range(0, tree.Columns).Select(tree.GetColumnWidth));
        if (tree.GetMeta("cvpp_layout", "").AsString() == layout) return;
        tree.SetMeta("cvpp_layout", layout);
        var font = tree.GetThemeFont("font");
        foreach (var item in root.GetChildren())
        {
            const int column = 2;
            float width = Math.Max(1, tree.GetColumnWidth(column) - 52);
            string text = item.GetTooltipText(column);
            if (text.Contains(" → ") && font.GetStringSize(text, fontSize: 18).X > width) text = text.Replace(" → ", "\n→ ");
            item.SetText(column, text);
            int size = 18;
            while (size > 14 && font.GetMultilineStringSize(text, width: width, fontSize: size,
                brkFlags: TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound).Y > font.GetHeight(size) * 3 + 1) size--;
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            while (size > 1 && words.Any(word => font.GetStringSize(word, fontSize: size).X > width)) size--;
            item.SetAutowrapMode(column, TextServer.AutowrapMode.Word);
            item.SetTextOverrunBehavior(column, TextServer.OverrunBehavior.NoTrimming);
            item.SetCustomFontSize(column, size);
            item.CustomMinimumHeight = (int)Math.Ceiling(Math.Max(28, font.GetMultilineStringSize(text, width: width, fontSize: size,
                brkFlags: TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound).Y));
        }
    }

    internal static Button Icon(string path, string tooltip, string name, Action action, bool flip = false) => Icon(Texture(path), tooltip, name, action, flip);

    internal static Button Command(string path, string tooltip, string name, Action action, bool flip = false)
    {
        var button = Icon(path, tooltip, name, () => { if (SolverController.Busy) SolverController.Stop(); else action(); }, flip);
        button.ToggleMode = true;
        button.SetMeta("cvpp_tooltip", tooltip);
        var pause = Image(Pause);
        pause.Name = "Pause";
        pause.Visible = false;
        pause.CustomMinimumSize = Vector2.Zero;
        button.AddChild(pause);
        pause.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 8);
        return button;
    }

    internal static void Running(Button button, bool active, bool enabled)
    {
        button.SetPressedNoSignal(active);
        var pause = button.GetNode<TextureRect>("Pause");
        if (pause.Visible != active)
        {
            button.GetNode<TextureRect>("Icon").Visible = !active;
            pause.Visible = active;
            button.TooltipText = active ? "Pause (Esc)" : button.GetMeta("cvpp_tooltip").AsString();
        }
        pause.Modulate = enabled ? Colors.White : new Color(1, 1, 1, .3f);
        Enabled(button, enabled);
    }

    internal static Button Icon(Texture2D texture, string tooltip, string name, Action action, bool flip = false)
    {
        var button = new Button
        {
            Name = name,
            Theme = Theme,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(40, 40),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        var icon = Image(texture);
        icon.Name = "Icon";
        icon.CustomMinimumSize = Vector2.Zero;
        icon.FlipH = flip;
        button.AddChild(icon);
        icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 6);
        button.Pressed += () => { button.GetViewport().GuiGetFocusOwner()?.ReleaseFocus(); action(); };
        return button;
    }

    internal static void Enabled(Button button, bool enabled)
    {
        button.Disabled = !enabled;
        button.GetNode<TextureRect>("Icon").Modulate = enabled ? Colors.White : new Color(1, 1, 1, .3f);
    }

    internal static MarginContainer Padding(Control parent, int amount)
    {
        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, amount);
        parent.AddChild(margin);
        return margin;
    }

    private static StyleBoxFlat Surface(string background, string border, int padding = 2) => new()
    {
        BgColor = new Color(background),
        BorderColor = new Color(border),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6,
        CornerRadiusBottomRight = 6,
        CornerDetail = 8,
        ContentMarginLeft = padding,
        ContentMarginRight = padding,
        ContentMarginTop = padding,
        ContentMarginBottom = padding
    };

    private static Theme CreateTheme()
    {
        var theme = new Theme { DefaultFontSize = 20, DefaultFont = GD.Load<Font>("res://themes/kreon_regular_shared.tres") };
        var panel = Surface("1b2b35", "83918d");
        panel.ShadowColor = new Color("0b141c");
        panel.ShadowSize = 2;
        panel.ShadowOffset = new Vector2(0, 2);
        theme.SetStylebox("panel", "PanelContainer", panel);
        foreach (string type in new[] { "Button" })
        {
            theme.SetStylebox("normal", type, Surface("2e4351", "526c75", 6));
            theme.SetStylebox("hover", type, Surface("526575", "f2d68d", 6));
            theme.SetStylebox("pressed", type, Surface("15232d", "d1ac60", 6));
            theme.SetStylebox("hover_pressed", type, Surface("526575", "fff2cd", 6));
            theme.SetStylebox("disabled", type, Surface("23323a", "3c4c53", 6));
            theme.SetStylebox("focus", type, Surface("00000000", "f2d68d", 6));
        }
        foreach (string type in new[] { "Label", "Button", "Tree", "TooltipLabel", "LineEdit" })
        {
            theme.SetColor("font_color", type, new Color("eee5cf"));
            theme.SetColor("font_hover_color", type, new Color("fff2cd"));
            theme.SetColor("font_pressed_color", type, Gold);
            theme.SetColor("font_disabled_color", type, Muted);
        }
        theme.SetStylebox("normal", "LineEdit", Surface("14232b", "526c75", 6));
        theme.SetStylebox("read_only", "LineEdit", Surface("23323a", "3c4c53", 6));
        theme.SetStylebox("focus", "LineEdit", Surface("00000000", "f2d68d", 6));
        theme.SetStylebox("panel", "TooltipPanel", Surface("1b2b35", "83918d", 10));
        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = new Color("405662"), Thickness = 1, ContentMarginTop = 5, ContentMarginBottom = 5 });
        theme.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = new Color("405662"), Thickness = 1, Vertical = true });
        theme.SetConstant("separation", "HSeparator", 10);
        theme.SetConstant("separation", "VSeparator", 6);
        theme.SetConstant("v_separation", "Tree", 8);
        theme.SetConstant("h_separation", "Tree", 8);
        theme.SetConstant("inner_item_margin_left", "Tree", 0);
        theme.SetConstant("inner_item_margin_right", "Tree", 8);
        theme.SetFontSize("title_button_font_size", "Tree", 18);
        theme.SetStylebox("panel", "Tree", Surface("14232b", "526c75", 6));
        theme.SetStylebox("selected", "Tree", Surface("465c69", "d1ac60", 2));
        theme.SetStylebox("selected_focus", "Tree", Surface("465c69", "d1ac60", 2));
        var heading = new StyleBoxFlat
        {
            BgColor = new Color("2e4351"),
            BorderColor = new Color("526c75"),
            BorderWidthBottom = 1,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        foreach (string state in new[] { "normal", "hover", "pressed" }) theme.SetStylebox("title_button_" + state, "Tree", heading);
        theme.SetStylebox("background", "ProgressBar", new StyleBoxFlat { BgColor = new Color("31464e") });
        theme.SetStylebox("fill", "ProgressBar", new StyleBoxFlat { BgColor = new Color("d1ac60") });
        return theme;
    }
}
