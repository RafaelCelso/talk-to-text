using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;

namespace TalkToText.App.Design;

/// <summary>
/// The hand cursor over everything that can be clicked — buttons, toggles, radio buttons,
/// check boxes, selects and their items, menu items, sliders — and the arrow again when it is
/// disabled.
/// </summary>
/// <remarks>
/// One app-wide rule rather than a <c>Cursor</c> on each control: the controls built by hand
/// had it, while every stock toggle, radio button and select showed the arrow. <c>Is&lt;Button&gt;</c>
/// matches subclasses, so it also covers ToggleButton, RadioButton, CheckBox, ToggleSwitch and
/// HyperlinkButton.
/// </remarks>
public static class PointerCursors
{
    /// <summary>The styles; added once, to the application.</summary>
    public static Styles Create() =>
    [
        new Style(x => Clickable(x, disabled: false)) { Setters = { new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Hand)) } },
        new Style(x => Clickable(x, disabled: true)) { Setters = { new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Arrow)) } },
    ];

    private static Selector Clickable(Selector? x, bool disabled)
    {
        Selector[] each =
        [
            x.Is<Button>(),
            x.OfType<ComboBox>(),
            x.OfType<ComboBoxItem>(),
            x.OfType<ListBoxItem>(),
            x.OfType<MenuItem>(),
            x.OfType<TabItem>(),
            x.OfType<Slider>(),
            x.OfType<Expander>().Template().OfType<ToggleButton>(),
        ];
        return Selectors.Or(disabled ? [.. each.Select(s => s.Class(":disabled"))] : each);
    }
}
