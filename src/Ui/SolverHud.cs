using Godot;
using MegaCrit.Sts2.Core.Combat;

namespace cvpp;

internal static class SolverHud
{
    private static CanvasLayer? _layer;
    private static PanelContainer _panel = null!;
    private static ColorRect _shield = null!;
    private static VBoxContainer _details = null!;
    private static VBoxContainer _routes = null!;
    private static Label _status = null!;
    private static Label _score = null!;
    private static Label _count = null!;
    private static Button _solve = null!;
    private static Button _step = null!;
    private static Button _turn = null!;
    private static Button _auto = null!;
    private static Button _stop = null!;
    private static Button _fold = null!;
    private static OptionButton _budget = null!;
    private static ProgressBar _progress = null!;
    private static bool _dragging;
    private static Vector2 _offset;
    private static CombatPlan? _shown;
    private static int _shownStep = -1;
    private static readonly Color Gold = new("f2d68d");
    private static Theme _theme = null!;
    private static readonly Dictionary<string, Texture2D> Icons = new();

    internal static void Install()
    {
        if (_layer != null) return;
        var tree = (SceneTree)Engine.GetMainLoop();
        _theme = Theme();
        _layer = new CanvasLayer { Layer = 210, Name = "Cvpp" };
        tree.Root.AddChild(_layer);
        _shield = new ColorRect { Color = new Color(0, 0, 0, .12f), Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _layer.AddChild(_shield);
        _shield.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _panel = new PanelContainer { Name = "CvppPanel", Theme = _theme, CustomMinimumSize = new Vector2(348, 0) };
        _layer.AddChild(_panel);
        _panel.Position = new Vector2(Math.Max(16, tree.Root.GetVisibleRect().Size.X - 376), 112);
        var column = new VBoxContainer();
        Padding(_panel, 10).AddChild(column);
        column.AddThemeConstantOverride("separation", 8);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 4);
        column.AddChild(bar);
        var handle = Label("CV++", 20, Gold);
        handle.CustomMinimumSize = new Vector2(62, 36);
        handle.VerticalAlignment = VerticalAlignment.Center;
        handle.MouseFilter = Control.MouseFilterEnum.Stop;
        handle.MouseDefaultCursorShape = Control.CursorShape.Drag;
        handle.TooltipText = "Combat Solver ++ · drag to move";
        handle.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouse)
            {
                _dragging = mouse.Pressed;
                _offset = handle.GetGlobalMousePosition() - _panel.Position;
            }
            if (input is InputEventMouseMotion && _dragging) _panel.Position = handle.GetGlobalMousePosition() - _offset;
        };
        bar.AddChild(handle);
        _solve = Button("search", "Search for a winning route with the most HP", () => SolverController.Solve(), "CvppSolve");
        _step = Button("step", "Play one card and its choices", () => SolverController.Play(ExecutionRange.Step), "CvppStep");
        _turn = Button("turn", "Play this turn", () => SolverController.Play(ExecutionRange.Turn), "CvppTurn");
        _auto = Button("auto", "Take over this combat", () => SolverController.Play(ExecutionRange.Combat), "CvppAuto");
        _stop = Button("stop", "Stop · Esc", SolverController.Stop, "CvppStop");
        _fold = Button("up", "Collapse · F10", Toggle, "CvppFold");
        foreach (var button in new[] { _solve, _step, _turn, _auto, _stop, _fold }) bar.AddChild(button);
        _details = new VBoxContainer();
        _details.AddThemeConstantOverride("separation", 10);
        column.AddChild(_details);
        _progress = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 3), MaxValue = 1 };
        _details.AddChild(_progress);
        var statusRow = new HBoxContainer();
        _details.AddChild(statusRow);
        _status = Label("Ready", 17);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.AutowrapMode = TextServer.AutowrapMode.Word;
        _status.CustomMinimumSize = new Vector2(180, 0);
        statusRow.AddChild(_status);
        _count = Label("", 15, new Color("9aacb0"));
        statusRow.AddChild(_count);
        var resultRow = new HBoxContainer();
        resultRow.AddThemeConstantOverride("separation", 8);
        _details.AddChild(resultRow);
        resultRow.AddChild(new TextureRect { Texture = Icon("heart"), CustomMinimumSize = new Vector2(22, 22), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = new Color("8fc8ac") });
        _score = Label("—", 22, new Color("b4ddc3"));
        _score.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        resultRow.AddChild(_score);
        _budget = new OptionButton { Name = "CvppBudget", FocusMode = Control.FocusModeEnum.None, TooltipText = "Search time · longer searches may find better routes" };
        foreach (int seconds in new[] { 5, 15, 30, 60 }) _budget.AddItem(seconds + " s", seconds);
        _budget.Selected = 1;
        _budget.ItemSelected += index => SolverController.Seconds = _budget.GetItemId((int)index);
        resultRow.AddChild(_budget);
        _details.AddChild(new HSeparator());
        var scroll = new ScrollContainer { Name = "CvppRoute", CustomMinimumSize = new Vector2(0, 216), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _details.AddChild(scroll);
        _routes = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _routes.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(_routes);
        _routes.AddChild(Label("Solve to preview a route", 16, new Color("9aacb0")));
        var footer = Label("HP first  ·  No potions", 14, new Color("9aacb0"));
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        _details.AddChild(footer);
    }

    internal static void Toggle()
    {
        if (_layer == null) return;
        _details.Visible = !_details.Visible;
        _fold.Icon = Icon(_details.Visible ? "up" : "down");
        _panel.ResetSize();
    }

    internal static void Tick()
    {
        if (_layer == null) return;
        bool busy = SolverController.Busy;
        var plan = SolverController.Plan;
        _layer.Visible = CombatManager.Instance.IsInProgress || busy || plan != null;
        _shield.Visible = SolverController.Executing;
        _solve.Disabled = busy || !SolverController.Ready;
        _step.Disabled = _turn.Disabled = busy || plan == null || SolverController.Step >= plan.Steps.Length || !SolverController.Ready;
        _auto.Disabled = busy || !SolverController.Ready;
        _stop.Disabled = !busy;
        _budget.Disabled = busy;
        _budget.Selected = _budget.GetItemIndex(SolverController.Seconds);
        _status.Text = SolverController.Error ?? SolverController.Status;
        _status.AddThemeColorOverride("font_color", SolverController.Error == null ? new Color("eee5cf") : new Color("e8a39b"));
        _status.TooltipText = SolverController.Error ?? "";
        _score.TooltipText = "Highest final HP found among complete winning routes. Optimality is not guaranteed.";
        _count.Text = busy ? $"{SolverController.Elapsed:F1}s" : "";
        _score.Text = busy && !SolverController.Executing && SolverController.Progress?.BestHp is { } hp ? $"{hp} HP"
            : plan != null ? $"{plan.FinalHp} HP  ·  {plan.Turns} turns" : "—";
        _progress.Value = busy ? Math.Min(.98, SolverController.Elapsed / SolverController.Seconds) : plan == null ? 0 : 1;
        if (_shown != plan || _shownStep != SolverController.Step) Populate();
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) _dragging = false;
        Vector2 view = ((SceneTree)Engine.GetMainLoop()).Root.GetVisibleRect().Size;
        _panel.Position = _panel.Position.Clamp(new Vector2(8, 8), new Vector2(Math.Max(8, view.X - _panel.Size.X - 8), Math.Max(8, view.Y - _panel.Size.Y - 8)));
    }

    private static void Populate()
    {
        foreach (var child in _routes.GetChildren()) { _routes.RemoveChild(child); child.QueueFree(); }
        _shown = SolverController.Plan;
        _shownStep = SolverController.Step;
        if (_shown == null)
        {
            _routes.AddChild(Label("Solve to preview a route", 16, new Color("9aacb0")));
            return;
        }
        int turn = -1;
        for (int index = _shownStep; index < _shown.Steps.Length; index++)
        {
            var step = _shown.Steps[index];
            if (step.Turn != turn)
            {
                turn = step.Turn;
                var heading = Label($"TURN {turn}", 13, new Color("9aacb0"));
                heading.CustomMinimumSize = new Vector2(0, 24);
                _routes.AddChild(heading);
            }
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 9);
            row.AddChild(new TextureRect
            {
                Texture = Icon(step.Kind == "turn" ? "turn" : step.Kind == "choice" ? "choice" : "card"),
                CustomMinimumSize = new Vector2(19, 24),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = index == _shownStep ? Gold : new Color("9aacb0")
            });
            var text = Label(step.Label, 17, index == _shownStep ? Gold : new Color("eee5cf"));
            text.AutowrapMode = TextServer.AutowrapMode.Word;
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            text.CustomMinimumSize = new Vector2(230, 0);
            row.AddChild(text);
            _routes.AddChild(row);
        }
        if (_shownStep == _shown.Steps.Length) _routes.AddChild(Label("Combat complete", 17, Gold));
    }

    internal static void Disable() { if (_layer != null) _layer.Visible = false; }

    private static Label Label(string text, int size, Color? color = null)
    {
        var label = new Label { Text = text, Theme = _theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        if (color.HasValue) label.AddThemeColorOverride("font_color", color.Value);
        return label;
    }

    private static Button Button(string icon, string tooltip, Action action, string name)
    {
        var button = new Button
        {
            Name = name,
            Icon = Icon(icon),
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(36, 36),
            FocusMode = Control.FocusModeEnum.None
        };
        button.Pressed += action;
        return button;
    }

    private static Texture2D Icon(string name)
    {
        if (Icons.TryGetValue(name, out var texture)) return texture;
        string path = name switch
        {
            "search" => "M16 16l5 5M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0",
            "step" => "M5 4l12 8-12 8zM20 4v16",
            "turn" => "M3 5l8 7-8 7zM11 5l8 7-8 7zM22 5v14",
            "auto" => "M14 2L4 14h7l-1 8 10-12h-7z",
            "stop" => "M5 5h14v14H5z",
            "up" => "M5 15l7-7 7 7",
            "down" => "M5 9l7 7 7-7",
            "heart" => "M12 21L3 12C-3 4 7-2 12 6c5-8 15-2 9 6z",
            "choice" => "M4 12l5 5L21 5",
            _ => "M5 3h14v18H5zM9 8h6M9 12h6M9 16h3"
        };
        using var image = new Image();
        var error = image.LoadSvgFromString($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\"><path d=\"{path}\" fill=\"none\" stroke=\"#eee5cf\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>");
        if (error != Error.Ok) throw new InvalidOperationException("Could not load a solver icon.");
        return Icons[name] = ImageTexture.CreateFromImage(image);
    }

    private static MarginContainer Padding(Control parent, int amount)
    {
        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, amount);
        parent.AddChild(margin);
        return margin;
    }

    private static StyleBoxFlat Surface(string background, string border, int padding) => new()
    {
        BgColor = new Color(background),
        BorderColor = new Color(border),
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        BorderWidthTop = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 7,
        CornerRadiusTopRight = 7,
        CornerRadiusBottomLeft = 7,
        CornerRadiusBottomRight = 7,
        ContentMarginLeft = padding,
        ContentMarginRight = padding,
        ContentMarginTop = padding,
        ContentMarginBottom = padding
    };

    private static Theme Theme()
    {
        var theme = new Theme { DefaultFontSize = 18 };
        if (ResourceLoader.Exists("res://themes/kreon_regular_shared.tres")) theme.DefaultFont = GD.Load<Font>("res://themes/kreon_regular_shared.tres");
        theme.SetStylebox("panel", "PanelContainer", Surface("1b2b35", "83918d", 0));
        foreach (string type in new[] { "Button", "OptionButton" })
        {
            theme.SetStylebox("normal", type, Surface("2e4351", "526c75", 5));
            theme.SetStylebox("hover", type, Surface("526575", "f2d68d", 5));
            theme.SetStylebox("pressed", type, Surface("15232d", "d1ac60", 5));
            theme.SetStylebox("disabled", type, Surface("23323a", "3c4c53", 5));
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetColor("font_color", type, new Color("eee5cf"));
            theme.SetColor("icon_disabled_color", type, new Color(1, 1, 1, .25f));
        }
        theme.SetStylebox("panel", "TooltipPanel", Surface("1b2b35", "83918d", 8));
        theme.SetStylebox("background", "ProgressBar", new StyleBoxFlat { BgColor = new Color("31464e") });
        theme.SetStylebox("fill", "ProgressBar", new StyleBoxFlat { BgColor = new Color("d1ac60") });
        theme.SetColor("font_color", "TooltipLabel", new Color("eee5cf"));
        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = new Color("405662"), Thickness = 1 });
        return theme;
    }
}
