using System.Globalization;
using Godot;

namespace cvpp;

internal sealed class BudgetEditor
{
    private readonly List<(Button Button, int Value)> _buttons = [];
    private readonly Func<int> _value;
    private readonly Action<int> _save;
    private readonly int _maximum;
    private int _shown = -1;
    internal VBoxContainer Root { get; } = new();
    internal LineEdit Input { get; }

    internal BudgetEditor(string name, string unit, (string Label, int Value)[] presets, int maximum, Func<int> value, Action<int> save)
    {
        _value = value;
        _save = save;
        _maximum = maximum;
        Root.AddThemeConstantOverride("separation", 6);
        Root.AddChild(Ui.Text(name, 18));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        Root.AddChild(row);
        foreach (var preset in presets)
        {
            var button = new Button
            {
                Name = "Cvpp" + name + preset.Value,
                Text = preset.Label,
                ToggleMode = true,
                FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(0, 34),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            button.AddThemeFontSizeOverride("font_size", 18);
            button.Pressed += () => Select(preset.Value);
            row.AddChild(button);
            _buttons.Add((button, preset.Value));
        }
        var custom = new HBoxContainer();
        custom.AddThemeConstantOverride("separation", 8);
        Root.AddChild(custom);
        Input = new LineEdit
        {
            Name = "Cvpp" + name + "Custom",
            PlaceholderText = "Custom",
            MaxLength = 7,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Right,
            SelectAllOnFocus = true
        };
        Input.AddThemeFontSizeOverride("font_size", 18);
        Input.TextSubmitted += _ => { Commit(); Input.ReleaseFocus(); };
        Input.FocusExited += Commit;
        custom.AddChild(Input);
        custom.AddChild(Ui.Text(unit, 16));
    }

    private void Select(int value)
    {
        Input.ReleaseFocus();
        _save(value);
        _shown = -1;
        Refresh(false);
    }

    private void Commit()
    {
        if (int.TryParse(Input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0 && value <= _maximum && value != _value()) _save(value);
        _shown = -1;
        Refresh(false);
    }

    internal void Refresh(bool busy)
    {
        Input.Editable = !busy;
        int value = _value();
        foreach (var (button, preset) in _buttons)
        {
            button.Disabled = busy;
            button.SetPressedNoSignal(value == preset);
        }
        if (_shown == value || Input.HasFocus()) return;
        _shown = value;
        Input.Text = value == 0 ? "" : value.ToString(CultureInfo.InvariantCulture);
    }
}
