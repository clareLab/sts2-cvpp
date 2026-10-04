using System.Globalization;
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
    private static Label _stats = null!;
    private static Label _memory = null!;
    private static Label _explored = null!;
    private static Label _speed = null!;
    private static Label _empty = null!;
    private static Label _handle = null!;
    private static Button _solve = null!;
    private static Button _step = null!;
    private static Button _turn = null!;
    private static Button _auto = null!;
    private static Button _reset = null!;
    private static Button _routeButton = null!;
    private static Button _settingsButton = null!;
    private static ProgressBar _progress = null!;
    private static BudgetEditor _timeBudget = null!;
    private static BudgetEditor _memoryBudget = null!;
    private static CombatPlan? _shown;
    private static SolveProgress? _shownProgress;
    private static bool _shownSearching;
    private static int _shownStep = -1;
    private static string? _error;
    private static Color _handleColor;
    private static Color _statusColor;
    private static bool _dragging;
    private static Vector2 _offset;
    private static Vector2 _view;
    private static Vector2 _position;
    internal static bool Editing => _layer != null && (_timeBudget.Input.HasFocus() || _memoryBudget.Input.HasFocus());

    internal static void Install()
    {
        if (_layer != null) return;
        HudSettings.Load();
        SolverController.Seconds = HudSettings.Seconds;
        SolverController.MemoryMiB = HudSettings.MemoryMiB;
        _position = HudSettings.Position;
        var tree = (SceneTree)Engine.GetMainLoop();
        _layer = new CanvasLayer { Layer = 210, Name = "Cvpp" };
        tree.Root.AddChild(_layer);
        _shield = new ColorRect { Color = new Color(0, 0, 0, .08f), Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _layer.AddChild(_shield);
        _shield.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _toolbar = new PanelContainer { Name = "CvppToolbar", Theme = Ui.Theme };
        _layer.AddChild(_toolbar);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        var padding = Ui.Padding(_toolbar, 6);
        padding.AddThemeConstantOverride("margin_top", 4);
        padding.AddThemeConstantOverride("margin_bottom", 4);
        padding.AddChild(column);
        var bar = new HBoxContainer { Name = "CvppCommands" };
        bar.AddThemeConstantOverride("separation", 4);
        column.AddChild(bar);
        _handle = Ui.Text("CV++", 16);
        _handle.CustomMinimumSize = new Vector2(48, 40);
        _handle.HorizontalAlignment = HorizontalAlignment.Center;
        _handle.VerticalAlignment = VerticalAlignment.Center;
        _handle.MouseFilter = Control.MouseFilterEnum.Stop;
        _handle.MouseDefaultCursorShape = Control.CursorShape.Drag;
        _handle.TooltipText = "Drag";
        _handle.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                _dragging = true;
                _offset = _handle.GetGlobalMousePosition() - _toolbar.Position;
            }
        };
        bar.AddChild(_handle);
        _solve = Ui.Command(Ui.Search, "Solve", "CvppSolve", () => SolverController.Solve());
        _step = Ui.Command(Ui.Step, "Step", "CvppStep", () => SolverController.Play(ExecutionRange.Step), flip: true);
        _turn = Ui.Command(Ui.Turn, "Turn", "CvppTurn", () => SolverController.Play(ExecutionRange.Turn));
        _auto = Ui.Command(Ui.Auto, "Take over", "CvppAuto", () => SolverController.Play(ExecutionRange.Combat));
        _reset = Ui.Icon(Ui.Reset, "Reset", "CvppReset", SolverController.Reset);
        foreach (var button in new[] { _solve, _step, _turn, _auto, _reset }) bar.AddChild(button);
        bar.AddChild(new VSeparator());
        _routeButton = Ui.Icon(Ui.Route, "Route (F10)", "CvppRouteToggle", Toggle);
        _routeButton.ToggleMode = true;
        bar.AddChild(_routeButton);
        _settingsButton = Ui.Icon(Ui.Settings, "Settings", "CvppSettings", () => Open(_settings, toggle: true));
        _settingsButton.ToggleMode = true;
        bar.AddChild(_settingsButton);
        _progress = new ProgressBar { Name = "CvppProgress", Theme = Ui.Theme, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 3), MaxValue = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_progress);
        var summary = new GridContainer { Name = "CvppSummary", Columns = 3 };
        summary.AddThemeConstantOverride("h_separation", 12);
        summary.AddThemeConstantOverride("v_separation", 0);
        column.AddChild(summary);
        _status = Ui.Text("Ready", 18);
        _status.Name = "CvppStatus";
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.ClipText = true;
        _status.MouseFilter = Control.MouseFilterEnum.Pass;
        _score = Ui.Text("—", 18);
        _score.Name = "CvppHp";
        _score.TooltipText = "Final HP";
        summary.AddChild(Metric(Ui.Heart, _score));
        _stats = Ui.Text("—", 18);
        _stats.Name = "CvppElapsed";
        _stats.TooltipText = "Search time";
        _stats.MouseFilter = Control.MouseFilterEnum.Pass;
        summary.AddChild(Metric(Ui.Timer, _stats));
        summary.AddChild(_status);
        _explored = Ui.Text("0", 18);
        _explored.Name = "CvppExplored";
        _explored.TooltipText = "Explored routes";
        _explored.MouseFilter = Control.MouseFilterEnum.Pass;
        summary.AddChild(Metric(Ui.Explored, _explored));
        _speed = Ui.Text("0/s", 18);
        _speed.Name = "CvppSpeed";
        _speed.TooltipText = "Simulations per second";
        _speed.MouseFilter = Control.MouseFilterEnum.Pass;
        summary.AddChild(Metric(Ui.Speed, _speed));
        _memory = Ui.Text("—", 18);
        _memory.Name = "CvppMemory";
        _memory.TooltipText = "Memory";
        _memory.MouseFilter = Control.MouseFilterEnum.Pass;
        _memory.AddThemeColorOverride("font_color", Ui.Muted);
        _memory.HorizontalAlignment = HorizontalAlignment.Right;
        summary.AddChild(_memory);
        _route = Flyout("CvppRoute");
        _steps = new Tree
        {
            Name = "CvppSteps",
            HideRoot = true,
            Columns = 4,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            HideFolding = true,
            ScrollHorizontalEnabled = false,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 304),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        foreach (var (index, title, width) in new[] { (0, "#", 40), (1, "Turn", 44), (2, "", 1), (3, "HP", 42) })
        {
            _steps.SetColumnTitle(index, title);
            _steps.SetColumnExpand(index, index == 2);
            _steps.SetColumnCustomMinimumWidth(index, width);
            _steps.SetColumnTitleAlignment(index, index == 2 ? HorizontalAlignment.Left : HorizontalAlignment.Center);
        }
        Ui.Padding(_route, 6).AddChild(_steps);
        var actionHeading = Ui.Text("Action", 18);
        actionHeading.Name = "CvppActionHeading";
        _steps.AddChild(actionHeading);
        _empty = Ui.Text("No route", 18);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.AddThemeColorOverride("font_color", Ui.Muted);
        _steps.AddChild(_empty);
        _empty.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, margin: 40);
        _settings = Flyout("CvppOptions");
        var options = new VBoxContainer();
        options.AddThemeConstantOverride("separation", 8);
        Ui.Padding(_settings, 6).AddChild(options);
        _timeBudget = new BudgetEditor("Time", "s", [("5", 5), ("15", 15), ("30", 30), ("60", 60), ("∞", 0)],
            86_400, () => SolverController.Seconds, value => { SolverController.Seconds = value; Save(); });
        options.AddChild(_timeBudget.Root);
        options.AddChild(new HSeparator());
        _memoryBudget = new BudgetEditor("Memory", "MiB", [("1 GiB", 1024), ("2 GiB", 2048), ("4 GiB", 4096), ("8 GiB", 8192), ("∞", 0)],
            1_048_576, () => SolverController.MemoryMiB, value => { SolverController.MemoryMiB = value; Save(); });
        options.AddChild(_memoryBudget.Root);
    }

    private static HBoxContainer Metric(string icon, Label value) => Metric(Ui.Texture(icon), value);

    private static HBoxContainer Metric(Texture2D icon, Label value)
    {
        var metric = new HBoxContainer { CustomMinimumSize = new Vector2(96, 0), TooltipText = value.TooltipText, MouseFilter = Control.MouseFilterEnum.Pass };
        metric.AddThemeConstantOverride("separation", 4);
        metric.AddChild(Ui.Image(icon, 16));
        metric.AddChild(value);
        return metric;
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
        bool searching = busy && !SolverController.Executing && !SolverController.Resetting;
        var plan = searching ? SolverController.Preview : SolverController.Plan;
        int step = searching ? 0 : SolverController.Step;
        if (_shown != plan && (plan == null || _route.Visible)) Populate(plan);
        if (_shownStep != step && _shown == plan) UpdateStep(step);
        RefreshMetrics(searching);
        _layer.Visible = (CombatManager.Instance.IsInProgress || busy || plan != null)
            && !RunManager.Instance.IsPaused && NGame.Instance?.Transition.InTransition != true;
        if (!_layer.Visible) return;
        bool ready = SolverController.Ready;
        _shield.Visible = SolverController.Executing;
        Button? active = !busy || SolverController.Resetting ? null : SolverController.ActiveRange switch
        {
            ExecutionRange.Step => _step,
            ExecutionRange.Turn => _turn,
            ExecutionRange.Combat => _auto,
            _ => _solve
        };
        bool executable = ready && plan != null && step < plan.Steps.Length;
        Ui.Running(_solve, active == _solve, active == _solve || (!busy && ready));
        Ui.Running(_step, active == _step, active == _step || (!busy && executable));
        Ui.Running(_turn, active == _turn, active == _turn || (!busy && executable));
        Ui.Running(_auto, active == _auto, active == _auto || (!busy && ready && (plan == null || step < plan.Steps.Length)));
        Ui.Enabled(_reset, !SolverController.Resetting && (busy || plan != null || SolverController.Progress != null || SolverController.Error != null));
        _timeBudget.Refresh(busy);
        _memoryBudget.Refresh(busy);
        _routeButton.SetPressedNoSignal(_route.Visible);
        _settingsButton.SetPressedNoSignal(_settings.Visible);
        _error = SolverController.Error;
        Color statusColor = _error != null ? Ui.Loss.Lightened(.35f)
            : SolverController.Status is "Time limit" or "Memory limit" or "Search limit" ? Ui.Gold : new Color("eee5cf");
        if (_statusColor != statusColor) { _statusColor = statusColor; _status.AddThemeColorOverride("font_color", statusColor); }
        _status.Text = SolverController.Resetting ? "Resetting" : _error != null ? "Error" : SolverController.Executing ? "Playing" : SolverController.Status;
        _status.TooltipText = _error ?? SolverController.Status;
        Color handleColor = _error != null ? Ui.Loss : busy ? Ui.Gold : new Color("eee5cf");
        if (_handleColor != handleColor) { _handleColor = handleColor; _handle.AddThemeColorOverride("font_color", handleColor); }
        var progress = SolverController.Progress;
        _score.Text = plan?.FinalHp.ToString() ?? "—";
        _progress.SelfModulate = busy ? Colors.White : Colors.Transparent;
        _progress.Indeterminate = searching && (progress == null || SolverController.SearchSeconds == 0);
        _progress.Value = SolverController.Executing && plan != null ? (double)step / plan.Steps.Length
            : searching && SolverController.SearchSeconds > 0 ? Math.Min(1, (progress?.ElapsedMs ?? 0) / (SolverController.SearchSeconds * 1000)) : 0;
        Layout();
        if (_route.Visible) Ui.FitTree(_steps);
    }

    private static void RefreshMetrics(bool searching)
    {
        var progress = SolverController.Progress;
        if (!ReferenceEquals(_shownProgress, progress) || _shownSearching != searching)
        {
            _shownProgress = progress;
            _shownSearching = searching;
            double seconds = (progress?.ElapsedMs ?? 0) / 1000;
            _stats.Text = progress == null && !searching ? "—" : SolverController.SearchSeconds > 0
                ? $"{seconds:F0}/{SolverController.SearchSeconds}s" : $"{seconds:F0}s";
            _explored.Text = (progress?.Simulations ?? 0).ToString("N0", CultureInfo.InvariantCulture);
            _speed.Text = SolverController.SimulationsPerSecond.ToString("N0", CultureInfo.InvariantCulture) + "/s";
            long bytes = progress?.MemoryBytes ?? 0;
            _memory.Text = bytes == 0 ? "—" : (bytes / (1024 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + " MiB";
        }
    }

    private static void Populate(CombatPlan? plan)
    {
        _steps.Clear();
        _shown = plan;
        _shownStep = -1;
        _empty.Visible = plan == null;
        if (plan == null) return;
        var root = _steps.CreateItem();
        for (int index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            var item = _steps.CreateItem(root);
            item.SetText(0, (index + 1).ToString());
            item.SetText(1, step.Turn.ToString());
            item.SetText(2, step.Label);
            item.SetText(3, step.HpDelta > 0 ? "+" + step.HpDelta : step.HpDelta.ToString());
            foreach (int column in new[] { 0, 1, 3 })
            {
                item.SetTextAlignment(column, HorizontalAlignment.Center);
                item.SetCustomFontSize(column, 16);
            }
            item.SetCustomColor(3, step.HpDelta < 0 ? Ui.Loss : Ui.Gain);
            string path = step.Portrait ?? (step.Kind == "turn" ? Ui.Turn : step.Kind == "choice" ? Ui.Choice : Ui.Card);
            item.SetIcon(2, Ui.Texture(ResourceLoader.Exists(path) ? path : Ui.Card));
            item.SetIconMaxWidth(2, 28);
            item.SetTooltipText(2, step.Label);
            item.SetCustomFontSize(2, 18);
            item.CustomMinimumHeight = 28;
        }
    }

    private static void UpdateStep(int step)
    {
        int previous = Math.Max(0, _shownStep);
        _shownStep = step;
        var root = _steps.GetRoot();
        if (root == null || _shown == null || _shown.Steps.Length == 0) return;
        _steps.DeselectAll();
        for (int index = previous; index < step; index++)
            for (int column = 0; column < 3; column++) root.GetChild(index).SetCustomColor(column, Ui.Muted);
        if (step < _shown.Steps.Length) root.GetChild(step).Select(0);
        _steps.ScrollToItem(root.GetChild(Math.Min(step, _shown.Steps.Length - 1)));
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
        _progress.Scale = _toolbar.Scale;
        _progress.Position = _toolbar.Position + new Vector2(8, _toolbar.Size.Y - 4) * scale;
        _progress.Size = new Vector2(_toolbar.Size.X - 16, 3);
        foreach (var panel in new[] { _route, _settings })
        {
            panel.CustomMinimumSize = new Vector2(_toolbar.Size.X, 0);
            panel.Size = new Vector2(_toolbar.Size.X, panel.Size.Y);
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
        HudSettings.Save(_position, SolverController.Seconds, SolverController.MemoryMiB);
    }

    internal static void Disable() { if (_layer != null) _layer.Visible = false; }
}
