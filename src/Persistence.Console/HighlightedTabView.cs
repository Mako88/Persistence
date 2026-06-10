using System.Linq;
using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// A <see cref="TabView"/> that always paints the <em>selected</em> tab's title itself: yellow when a
/// pane inside the tab bar holds focus, gold otherwise. "Yellow means focused"; gold marks the active
/// tab when focus is elsewhere (e.g. the compose box).
///
/// We paint it unconditionally rather than leaning on Terminal.Gui's own highlight, because v1 only
/// applies its "hot" colour while <c>TabView.HasFocus</c> is true — and that flag doesn't reliably
/// track focus moving into a pane inside the tab, which made the highlight flicker between gold and
/// yellow. Driving it from the focus state directly keeps it consistent.
/// </summary>
internal sealed class HighlightedTabView : TabView
{
    public override void Redraw(Rect bounds)
    {
        // The four short tabs always fit, but TG can leave TabScrollOffset > 0 from an early layout
        // pass and then draw a spurious left-scroll "◄". Pin it to 0 so that arrow never shows.
        if (TabScrollOffset != 0)
        {
            TabScrollOffset = 0;
        }

        base.Redraw(bounds);

        if (SelectedTab is null || Application.Driver is null)
        {
            return;
        }

        // Focused when this tab bar — or any pane nested inside the selected tab — holds focus. (TG's
        // HasFocus alone doesn't always reflect a focused descendant, so also consult the focus chain.)
        var focused = HasFocus || MostFocused is not null || Focused is not null;
        var colour = focused ? TuiColors.Label : TuiColors.TabUnfocused;

        // Mirror TabRowView's layout: titles sit on row 1 when the top line is shown (the default),
        // starting one column in, each separated by a single column. We only need the selected one.
        var y = Style.ShowTopLine ? 1 : 0;
        var x = 1;

        foreach (var tab in Tabs.Skip(TabScrollOffset))
        {
            var text = tab.Text?.ToString() ?? string.Empty;
            var width = text.Length;

            // Same overflow guard CalculateViewport uses; stop if it wouldn't have been drawn.
            if (x + width >= Bounds.Width)
            {
                break;
            }

            if (tab == SelectedTab)
            {
                Driver.SetAttribute(Driver.MakeAttribute(colour, ColorScheme.Normal.Background));
                Move(x, y);
                Driver.AddStr(text);
                Driver.SetAttribute(GetNormalColor());
                break;
            }

            x += width + 1;
        }
    }
}
