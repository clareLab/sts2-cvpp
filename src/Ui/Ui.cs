using Godot;

namespace cvpp;

internal static class Ui
{
    internal const string Search = "res://images/atlases/relic_atlas.sprites/frozen_eye.tres";
    internal const string Step = "res://images/atlases/compressed.sprites/back_button_arrow.tres";
    internal const string Turn = "res://images/atlases/ui_atlas.sprites/settings_tiny_right_arrow.tres";
    internal const string Auto = "res://images/atlases/ui_atlas.sprites/map/icons/map_monster.tres";
    internal const string Close = "res://images/atlases/compressed.sprites/back_button_x.tres";
    internal const string Route = "res://images/atlases/relic_atlas.sprites/history_course.tres";
    internal const string Settings = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_settings.tres";
    internal const string Heart = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres";
    internal const string Timer = "res://images/atlases/ui_atlas.sprites/top_bar/timer_icon.tres";
    internal const string Card = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_deck.tres";
    internal const string Choice = "res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres";
    internal static readonly Color Muted = new("83918d");
    internal static readonly Color Gold = new("f2d68d");
    private static Theme? _theme;
    private static readonly Dictionary<string, Texture2D> Textures = new();
    internal static Theme Theme => _theme ??= CreateTheme();

    internal static Texture2D Texture(string path)
    {
        if (Textures.TryGetValue(path, out var cached)) return cached;
        var texture = GD.Load<Texture2D>(path);
        return Textures[path] = texture is AtlasTexture atlas
            ? new AtlasTexture { Atlas = atlas.Atlas, Region = atlas.Region, FilterClip = true } : texture;
    }

    internal static TextureRect Image(string path, int size = 24) => new()
    {
        Texture = Texture(path),
        CustomMinimumSize = new Vector2(size, size),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    internal static Label Text(string text, int size = 20)
    {
        var label = new Label { Text = text, Theme = Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    internal static Button Icon(string path, string tooltip, string name, Action action, bool flip = false)
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
        var icon = Image(path);
        icon.Name = "Icon";
        icon.CustomMinimumSize = Vector2.Zero;
        icon.FlipH = flip;
        button.AddChild(icon);
        icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 6);
        button.Pressed += action;
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
        foreach (string type in new[] { "Label", "Button", "Tree", "TooltipLabel" })
        {
            theme.SetColor("font_color", type, new Color("eee5cf"));
            theme.SetColor("font_hover_color", type, new Color("fff2cd"));
            theme.SetColor("font_pressed_color", type, Gold);
            theme.SetColor("font_disabled_color", type, Muted);
        }
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
