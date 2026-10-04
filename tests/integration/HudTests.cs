using Godot;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal static class HudTests
{
    private static SceneTree Scene => (SceneTree)Engine.GetMainLoop();
    private static T Node<T>(string name) where T : Node => (T)Scene.Root.FindChild(name, true, false);

    internal static void StatusAlignment()
    {
        var hp = Node<Label>("CvppHp");
        var elapsed = Node<Label>("CvppElapsed");
        var status = Node<Label>("CvppStatus");
        var explored = Node<Label>("CvppExplored");
        var speed = Node<Label>("CvppSpeed");
        var memory = Node<Label>("CvppMemory");
        float Baseline(Label label)
        {
            var font = label.GetThemeFont("font");
            int size = label.GetThemeFontSize("font_size");
            return label.GlobalPosition.Y + (label.Size.Y - font.GetHeight(size)) / 2 + font.GetAscent(size);
        }
        if (Math.Abs(hp.GlobalPosition.X - explored.GlobalPosition.X) > .5f
            || Math.Abs(elapsed.GlobalPosition.X - speed.GlobalPosition.X) > .5f
            || Math.Abs(status.GetGlobalRect().End.X - memory.GetGlobalRect().End.X) > .5f
            || Math.Abs(Baseline(hp) - Baseline(elapsed)) > .5f || Math.Abs(Baseline(hp) - Baseline(status)) > .5f
            || Math.Abs(Baseline(explored) - Baseline(speed)) > .5f || Math.Abs(Baseline(explored) - Baseline(memory)) > .5f)
            throw new InvalidOperationException("Status numbers, right edges or text baselines are misaligned.");
        var tree = Node<Godot.Tree>("CvppSteps");
        if (tree.IsVisibleInTree()
            && (Math.Abs(hp.GlobalPosition.X - tree.GlobalPosition.X - Ui.RouteTextOffset(tree, 0)) > 1
                || Math.Abs(elapsed.GlobalPosition.X - Node<Label>("CvppActionHeading").GlobalPosition.X) > 1))
            throw new InvalidOperationException("Status numbers do not align with the route headings.");
    }

    internal static void Health(CombatPlan plan, int step)
    {
        SolverHud.Tick();
        int delta = plan.Steps.Skip(step).Sum(action => action.HpDelta);
        var hp = Node<Label>("CvppHp");
        if (hp.Text != (delta > 0 ? "+" + delta : delta.ToString())
            || hp.GetThemeColor("font_color") != (delta < 0 ? Ui.Loss : Ui.Gain))
            throw new InvalidOperationException("Status HP does not match the remaining route delta and colour.");
    }

    internal static async Task Run()
    {
        var time = Node<LineEdit>("CvppTimeCustom");
        string before = CombatFingerprint.Capture(RunManager.Instance.DebugOnlyGetState()!);
        time.GrabFocus();
        time.SelectAll();
        foreach (var (key, unicode) in new[] { (Key.Key3, '3'), (Key.Key7, '7'), (Key.Enter, '\0') })
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = unicode, Pressed = true });
            await Scene.ToSignal(Scene, SceneTree.SignalName.ProcessFrame);
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
            await Scene.ToSignal(Scene, SceneTree.SignalName.ProcessFrame);
        }
        if (SolverController.Seconds != 37 || CombatFingerprint.Capture(RunManager.Instance.DebugOnlyGetState()!) != before)
            throw new InvalidOperationException("Budget keyboard input changed the combat or did not submit.");
        var memory = Node<LineEdit>("CvppMemoryCustom");
        var timeUnit = Node<Control>("CvppTimeUnit").GetGlobalRect();
        var memoryUnit = Node<Control>("CvppMemoryUnit").GetGlobalRect();
        if (Math.Abs(timeUnit.Position.X - memoryUnit.Position.X) > 1
            || Math.Abs(time.GetGlobalRect().End.X - memory.GetGlobalRect().End.X) > 1
            || Math.Abs(timeUnit.GetCenter().Y - time.GetGlobalRect().GetCenter().Y) > 1
            || Math.Abs(memoryUnit.GetCenter().Y - memory.GetGlobalRect().GetCenter().Y) > 1)
            throw new InvalidOperationException("Custom budget fields or units are misaligned.");
        var timePresets = Node<Control>("CvppTimePresets").GetChildren().Cast<Button>().ToArray();
        var memoryPresets = Node<Control>("CvppMemoryPresets").GetChildren().Cast<Button>().ToArray();
        if (timePresets.Length != 5 || memoryPresets.Length != 5
            || timePresets.Where((button, index) => Math.Abs(button.GlobalPosition.X - memoryPresets[index].GlobalPosition.X) > 1
                || Math.Abs(button.Size.X - memoryPresets[index].Size.X) > 1).Any())
            throw new InvalidOperationException("Time and memory presets do not share five aligned columns.");
        memory.Text = "1536";
        memory.EmitSignal(LineEdit.SignalName.TextSubmitted, memory.Text);
        HudSettings.Load();
        if (SolverController.Seconds != 37 || HudSettings.Seconds != 37 || SolverController.MemoryMiB != 1536 || HudSettings.MemoryMiB != 1536)
            throw new InvalidOperationException("Custom budgets did not persist.");
        Node<Button>("CvppTime0").EmitSignal(BaseButton.SignalName.Pressed);
        Node<Button>("CvppMemory0").EmitSignal(BaseButton.SignalName.Pressed);
        HudSettings.Load();
        if (HudSettings.Seconds != 0 || HudSettings.MemoryMiB != 0) throw new InvalidOperationException("Unlimited budgets did not persist.");
        time.Text = "-10";
        time.EmitSignal(LineEdit.SignalName.TextSubmitted, time.Text);
        if (SolverController.Seconds != 0) throw new InvalidOperationException("Negative budget was accepted.");
        time.GrabFocus();
        time.Text = "51";
        Node<Button>("CvppTime60").EmitSignal(BaseButton.SignalName.Pressed);
        if (time.HasFocus() || SolverController.Seconds != 60 || time.Text != "60")
            throw new InvalidOperationException("Uncommitted custom input overwrote the selected preset.");
        time.Text = "37";
        time.EmitSignal(LineEdit.SignalName.TextSubmitted, time.Text);
        Node<Button>("CvppMemory2048").EmitSignal(BaseButton.SignalName.Pressed);
        SolverHud.Close();
        SolverHud.Toggle();
        for (int frame = 0; frame < 3; frame++) await Scene.ToSignal(Scene, SceneTree.SignalName.ProcessFrame);
        var toolbar = Node<Control>("CvppToolbar");
        var route = Node<Control>("CvppRoute");
        StatusAlignment();
        foreach (var key in new[] { Key.F10, Key.Escape })
        {
            using var input = new InputEventKey { Keycode = key, Pressed = true };
            if (SolverController.Input(input) || !route.Visible)
                throw new InvalidOperationException("The solver still handles a global keyboard shortcut.");
        }
        if (Math.Abs(toolbar.Size.X - route.Size.X) > 1) throw new InvalidOperationException("Route width differs from the toolbar.");
        var tree = Node<Godot.Tree>("CvppSteps");
        var root = tree.GetRoot()!;
        var plan = SolverController.Plan!;
        for (int index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            var item = root.GetChild(index);
            if (item.GetCustomColor(3) != (step.HpDelta < 0 ? Ui.Loss : Ui.Gain)
                || item.GetText(3) != (step.HpDelta > 0 ? "+" + step.HpDelta : step.HpDelta.ToString()))
                throw new InvalidOperationException("Route HP delta or colour is incorrect.");
        }
        var longRow = tree.CreateItem(root);
        longRow.SetText(2, "Choose a generated card from the discard pile and return another generated card to the draw pile before the enemy turn begins");
        longRow.SetTooltipText(2, longRow.GetText(2));
        tree.SetMeta("cvpp_layout", "");
        Ui.FitTree(tree);
        if (longRow.GetCustomFontSize(2) >= 18 || longRow.CustomMinimumHeight <= tree.GetThemeFont("font").GetHeight(18) * 3
            || longRow.GetTextOverrunBehavior(2) != TextServer.OverrunBehavior.NoTrimming)
            throw new InvalidOperationException("Long route text did not wrap and shrink without truncation.");
        longRow.Free();
        SolverHud.Close();
        if (!Node<Control>("CvppSummary").IsVisibleInTree() || Node<Label>("CvppStatus").Text != "Time limit")
            throw new InvalidOperationException("Search status is hidden with route details closed.");
        var explored = Node<Label>("CvppExplored");
        var speed = Node<Label>("CvppSpeed");
        string countText = explored.Text;
        string speedText = speed.Text;
        Vector2 toolbarSize = toolbar.Size;
        explored.Text = "23,020";
        speed.Text = "1,240/s";
        for (int frame = 0; frame < 3; frame++) await Scene.ToSignal(Scene, SceneTree.SignalName.ProcessFrame);
        StatusAlignment();
        if (toolbar.Size != toolbarSize) throw new InvalidOperationException("Grouped counters changed the toolbar size.");
        explored.Text = countText;
        speed.Text = speedText;
        Node<Button>("CvppSettings").EmitSignal(BaseButton.SignalName.Pressed);
    }
}
