using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MultiSpeaker;

/// <summary>Integer box with − / + buttons; also takes typing, Up/Down keys and the mouse wheel.</summary>
public partial class NumberBox : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberBox)d).ShowValue()));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int Step { get; set; } = 1;

    public NumberBox()
    {
        InitializeComponent();
        ShowValue();
        Input.LostKeyboardFocus += (_, _) => Commit();
        Input.PreviewKeyDown += OnInputKeyDown;
        Input.PreviewMouseWheel += (_, e) =>
        {
            if (!Input.IsKeyboardFocusWithin)
                return;  // let the list scroll unless the user is editing this box
            Nudge(e.Delta > 0 ? Step : -Step);
            e.Handled = true;
        };
    }

    void OnIncrease(object sender, RoutedEventArgs e) => Nudge(Step);
    void OnDecrease(object sender, RoutedEventArgs e) => Nudge(-Step);

    void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Commit(); break;
            case Key.Up: Nudge(Step); break;
            case Key.Down: Nudge(-Step); break;
            default: return;
        }
        e.Handled = true;
    }

    void Nudge(int delta)
    {
        Commit();
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
    }

    void Commit()
    {
        if (int.TryParse(Input.Text, out int typed))
            Value = Math.Clamp(typed, Minimum, Maximum);
        ShowValue();
    }

    void ShowValue() => Input.Text = Value.ToString();
}
