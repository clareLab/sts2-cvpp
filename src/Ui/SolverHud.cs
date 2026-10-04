using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class SolverHud
{
    private static CanvasLayer? _layer;
    private static PanelContainer _toolbar = null!;
    private static PanelContainer _route = null!;
    private static PanelContainer _settings = null!;
    private static ColorRect _shield = null!;
    private static Tree _steps = null!;
    private static Label _status = null!;
    private static Label _score = null!;
    private static Label _turns = null!;
    private static Label _stats = null!;
    private static Label _empty = null!;
    private static Label _handle = null!;
    private static Button _solve = null!;
    private static Button _step = null!;
    private static Button _turn = null!;
    private static Button _auto = null!;
    private static Button _stop = null!;
    private static Button _routeButton = null!;
    private static Button _settingsButton = null!;
    private static ProgressBar _progress = null!;
    private static readonly List<Button> Budgets = [];
    private static CombatPlan? _shown;
    private static int _shownStep = -1;
    private static string? _error;
    private static bool _dragging;
    private static Vector2 _offset;
    private static Vector2 _view;
    private static Vector2 _position;

    internal static void Install()
    {
        if (_layer != null) return;
        HudSettings.Load();
        SolverController.Seconds = HudSettings.Seconds;
        _position = HudSettings.Position;
        var tree = (SceneTree)Engine.GetMainLoop();
        _layer = new CanvasLayer { Layer = 210, Name = "Cvpp" };
        tree.Root.AddChild(_layer);
        _shield = new ColorRect { Color = new Color(0, 0, 0, .08f), Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _layer.AddChild(_shield);
        _shield.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _toolbar = new PanelContainer { Name = "CvppToolbar", Theme = Ui.Theme };
        _layer.AddChild(_toolbar);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 4);
        Ui.Padding(_toolbar, 4).AddChild(bar);
        _handle = Ui.Text("CV++", 16);
        _handle.CustomMinimumSize = new Vector2(48, 40);
        _handle.HorizontalAlignment = HorizontalAlignment.Center;
        _handle.VerticalAlignment = VerticalAlignment.Center;
        _handle.MouseFilter = Control.MouseFilterEnum.Stop;
        _handle.MouseDefaultCursorShape = Control.CursorShape.Drag;
        _handle.TooltipText = "Combat Solver ++\nDrag to move · F10 route";
        _handle.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                _dragging = true;
                _offset = _handle.GetGlobalMousePosition() - _toolbar.Position;
            }
        };
        bar.AddChild(_handle);
        _solve = Ui.Icon(Ui.Search, "Solve · maximize final HP\nPotions are excluded", "CvppSolve", () => { Open(_route); SolverController.Solve(); });
        _step = Ui.Icon(Ui.Step, "Play one action", "CvppStep", () => SolverController.Play(ExecutionRange.Step), flip: true);
        _turn = Ui.Icon(Ui.Turn, "Play this turn", "CvppTurn", () => SolverController.Play(ExecutionRange.Turn));
        _auto = Ui.Icon(Ui.Auto, "Take over this combat", "CvppAuto", () => { Open(_route); SolverController.Play(ExecutionRange.Combat); });
        _stop = Ui.Icon(Ui.Close, "Stop · Esc", "CvppStop", SolverController.Stop);
        foreach (var button in new[] { _solve, _step, _turn, _auto, _stop }) bar.AddChild(button);
        bar.AddChild(new VSeparator());
        _routeButton = Ui.Icon(Ui.Route, "Route · F10", "CvppRouteToggle", Toggle);
        _routeButton.ToggleMode = true;
        bar.AddChild(_routeButton);
        _settingsButton = Ui.Icon(Ui.Settings, "Settings", "CvppSettings", () => Open(_settings, toggle: true));
        _settingsButton.ToggleMode = true;
        bar.AddChild(_settingsButton);
        _route = Flyout("CvppRoute");
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        Ui.Padding(_route, 6).AddChild(column);
        var summary = new HBoxContainer();
        summary.AddThemeConstantOverride("separation", 8);
        column.AddChild(summary);
        summary.AddChild(Ui.Image(Ui.Heart));
        _score = Ui.Text("—");
        _score.TooltipText = "Best final HP found among complete winning routes. Optimality is not guaranteed.";
        _score.MouseFilter = Control.MouseFilterEnum.Pass;
        summary.AddChild(_score);
        summary.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        summary.AddChild(Ui.Image(Ui.Timer));
        _turns = Ui.Text("—", 18);
        summary.AddChild(_turns);
        _steps = new Tree
        {
            Name = "CvppSteps",
            HideRoot = true,
            Columns = 3,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            HideFolding = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 304),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        foreach (var (index, title, width) in new[] { (0, "Step", 50), (1, "Turn", 50), (2, "Action", 220) })
        {
            _steps.SetColumnTitle(index, title);
            _steps.SetColumnExpand(index, index == 2);
            _steps.SetColumnCustomMinimumWidth(index, width);
            _steps.SetColumnTitleAlignment(index, index == 2 ? HorizontalAlignment.Left : HorizontalAlignment.Center);
        }
        column.AddChild(_steps);
        _empty = Ui.Text("Solve to preview a route", 18);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.AddThemeColorOverride("font_color", Ui.Muted);
        _steps.AddChild(_empty);
        _empty.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 45);
        _progress = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, 3), MaxValue = 1 };
        column.AddChild(_progress);
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        column.AddChild(footer);
        _status = Ui.Text("Ready", 16);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.ClipText = true;
        _status.MouseFilter = Control.MouseFilterEnum.Pass;
        footer.AddChild(_status);
        _stats = Ui.Text("", 16);
        _stats.AddThemeColorOverride("font_color", Ui.Muted);
        footer.AddChild(_stats);
        _settings = Flyout("CvppOptions");
        var options = new VBoxContainer();
        options.AddThemeConstantOverride("separation", 8);
        Ui.Padding(_settings, 8).AddChild(options);
        var heading = new HBoxContainer();
        heading.AddThemeConstantOverride("separation", 8);
        heading.AddChild(Ui.Image(Ui.Timer));
        heading.AddChild(Ui.Text("Search time"));
        options.AddChild(heading);
        var budgets = new HBoxContainer();
        budgets.AddThemeConstantOverride("separation", 4);
        options.AddChild(budgets);
        foreach (int seconds in new[] { 5, 15, 30, 60 })
        {
            var button = new Button
            {
                Name = "CvppBudget" + seconds,
                Text = seconds + " s",
                ToggleMode = true,
                FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(0, 40),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            button.Pressed += () => { SolverController.Seconds = seconds; Save(); };
            button.SetMeta("seconds", seconds);
            Budgets.Add(button);
            budgets.AddChild(button);
        }
        options.AddChild(new HSeparator());
        var goal = new HBoxContainer();
        goal.AddThemeConstantOverride("separation", 8);
        goal.AddChild(Ui.Image(Ui.Heart));
        goal.AddChild(Ui.Text("Maximize final HP", 18));
        options.AddChild(goal);
        var potions = Ui.Text("No potions", 16);
        potions.AddThemeColorOverride("font_color", Ui.Muted);
        options.AddChild(potions);
    }

    private static PanelContainer Flyout(string name)
    {
        var panel = new PanelContainer { Name = name, Theme = Ui.Theme, Visible = false };
        _layer!.AddChild(panel);
        return panel;
    }

    private static void Open(PanelContainer panel, bool toggle = false)
    {
        bool visible = !toggle || !panel.Visible;
        _route.Visible = _settings.Visible = false;
        panel.Visible = visible;
    }

    internal static void Toggle() { if (_layer is { Visible: true }) Open(_route, toggle: true); }

    internal static void Pointer(InputEvent input)
    {
        if (_layer is not { Visible: true } || input is not InputEventMouseButton { Pressed: true }) return;
        var pointer = _toolbar.GetGlobalMousePosition();
        if (!_toolbar.GetGlobalRect().HasPoint(pointer)
            && (!_route.Visible || !_route.GetGlobalRect().HasPoint(pointer))
            && (!_settings.Visible || !_settings.GetGlobalRect().HasPoint(pointer))) Close();
    }

    internal static bool Close()
    {
        if (_layer is not { Visible: true } || (!_route.Visible && !_settings.Visible)) return false;
        _route.Visible = _settings.Visible = false;
        return true;
    }

    internal static void Tick()
    {
        if (_layer == null) return;
        bool busy = SolverController.Busy;
        bool ready = SolverController.Ready;
        var plan = SolverController.Plan;
        _layer.Visible = (CombatManager.Instance.IsInProgress || busy || plan != null)
            && !RunManager.Instance.IsPaused && NGame.Instance?.Transition.InTransition != true;
        _shield.Visible = SolverController.Executing;
        Ui.Enabled(_solve, !busy && ready);
        Ui.Enabled(_step, !busy && ready && plan != null && SolverController.Step < plan.Steps.Length);
        Ui.Enabled(_turn, !_step.Disabled);
        Ui.Enabled(_auto, !busy && ready && (plan == null || SolverController.Step < plan.Steps.Length));
        Ui.Enabled(_stop, busy);
        foreach (var button in Budgets)
        {
            button.Disabled = busy;
            button.SetPressedNoSignal(button.GetMeta("seconds").AsInt32() == SolverController.Seconds);
        }
        _routeButton.SetPressedNoSignal(_route.Visible);
        _settingsButton.SetPressedNoSignal(_settings.Visible);
        if (_error != SolverController.Error)
        {
            _error = SolverController.Error;
            if (_error != null) Open(_route);
        }
        _status.Text = _error == null ? SolverController.Status : "Unable to continue · hover for details";
        _status.TooltipText = _error ?? SolverController.Status;
        _status.AddThemeColorOverride("font_color", _error == null ? Ui.Muted : new Color("e8a39b"));
        _handle.AddThemeColorOverride("font_color", _error != null ? new Color("e8a39b") : busy ? Ui.Gold : new Color("eee5cf"));
        _stats.Text = busy ? $"{SolverController.Elapsed:F1}s" : "";
        int? hp = busy && !SolverController.Executing ? SolverController.Progress?.BestHp : plan?.FinalHp;
        _score.Text = hp.HasValue ? $"{hp} HP" : "—";
        _turns.Text = plan == null ? "—" : $"{plan.Turns} turns";
        _progress.Visible = busy;
        _progress.Value = SolverController.Executing && plan != null ? (double)SolverController.Step / plan.Steps.Length
            : busy ? Math.Min(.98, (SolverController.Progress?.ElapsedMs ?? 0) / (SolverController.Seconds * 1000)) : 0;
        if (_shown != plan || _shownStep != SolverController.Step) Populate();
        Layout();
    }

    private static void Populate()
    {
        _steps.Clear();
        _shown = SolverController.Plan;
        _shownStep = SolverController.Step;
        _empty.Visible = _shown == null;
        if (_shown == null) return;
        var root = _steps.CreateItem();
        for (int index = 0; index < _shown.Steps.Length; index++)
        {
            var step = _shown.Steps[index];
            var item = _steps.CreateItem(root);
            item.SetText(0, (index + 1).ToString());
            item.SetText(1, step.Turn.ToString());
            item.SetText(2, step.Label);
            item.SetTextAlignment(0, HorizontalAlignment.Center);
            item.SetTextAlignment(1, HorizontalAlignment.Center);
            string path = step.Portrait ?? (step.Kind == "turn" ? Ui.Turn : step.Kind == "choice" ? Ui.Choice : Ui.Card);
            item.SetIcon(2, Ui.Texture(ResourceLoader.Exists(path) ? path : Ui.Card));
            item.SetIconMaxWidth(2, 28);
            item.SetTooltipText(2, step.Label);
            item.SetCustomFontSize(2, 18);
            item.CustomMinimumHeight = 28;
            if (index < _shownStep)
                for (int column = 0; column < 3; column++) item.SetCustomColor(column, Ui.Muted);
            if (index == _shownStep) item.Select(0);
        }
        if (_shown.Steps.Length != 0) _steps.ScrollToItem(root.GetChild(Math.Min(_shownStep, _shown.Steps.Length - 1)));
    }

    private static void Layout()
    {
        Vector2 view = ((SceneTree)Engine.GetMainLoop()).Root.GetVisibleRect().Size;
        float scale = Math.Min(1, (view.X - 16) / Math.Max(1, _toolbar.Size.X));
        _toolbar.Scale = _route.Scale = _settings.Scale = Vector2.One * scale;
        var available = (view - _toolbar.Size * scale - new Vector2(32, 32)).Max(Vector2.One);
        if (_view != view)
        {
            _view = view;
            _toolbar.Position = new Vector2(16, 16) + available * _position;
        }
        if (_dragging)
        {
            _toolbar.Position = _handle.GetGlobalMousePosition() - _offset;
            if (!Input.IsMouseButtonPressed(MouseButton.Left)) { _dragging = false; Save(); }
        }
        _toolbar.Position = _toolbar.Position.Clamp(new Vector2(8, 8), (view - _toolbar.Size * scale - new Vector2(8, 8)).Max(new Vector2(8, 8)));
        foreach (var panel in new[] { _route, _settings })
        {
            float width = panel == _route ? Math.Min(500, (view.X - 16) / scale) : _toolbar.Size.X;
            panel.CustomMinimumSize = new Vector2(width, 0);
            panel.Size = new Vector2(width, panel.Size.Y);
            float below = _toolbar.Position.Y + _toolbar.Size.Y * scale + 6;
            float above = _toolbar.Position.Y - panel.Size.Y * scale - 6;
            panel.Position = new Vector2(Math.Min(_toolbar.Position.X, view.X - panel.Size.X * scale - 8),
                below + panel.Size.Y * scale <= view.Y - 8 ? below : Math.Max(8, above));
        }
    }

    private static void Save()
    {
        var available = (_view - _toolbar.Size * _toolbar.Scale - new Vector2(32, 32)).Max(Vector2.One);
        _position = ((_toolbar.Position - new Vector2(16, 16)) / available).Clamp(Vector2.Zero, Vector2.One);
        HudSettings.Save(_position, SolverController.Seconds);
    }

    internal static void Disable() { if (_layer != null) _layer.Visible = false; }
}
