using System.Linq;
using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// A <see cref="TabView"/> that always paints the <em>selected</em> tab's title in the "hot"
/// (highlighted) colour, even when the tab bar itself isn't focused.
///
/// Terminal.Gui v1 only uses <see cref="ColorScheme.HotNormal"/>/<see cref="ColorScheme.HotFocus"/>
/// for the selected tab while <c>TabView.HasFocus</c> is true. In this app the compose box holds
/// focus, so out of the box the selected tab would render in the plain (white) colour until the user
/// clicks it — there'd be no visible indication of the active pane on load. After the base draw we
/// re-colour the selected tab's title ourselves so the active pane is always obvious.
/// </summary>
internal sealed class HighlightedTabView : TabView
{
    public override void Redraw(Rect bounds)
    {
        base.Redraw(bounds);

        // When focused (the tab bar or any pane inside it has focus), the base already painted the
        // selected tab in the bright "hot" colour — yellow consistently means "focused" — so leave it.
        // When unfocused, paint the selected tab gold instead: it marks the active pane without
        // claiming the focus colour.
        if (HasFocus || SelectedTab is null || Application.Driver is null)
        {
            return;
        }

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
                Driver.SetAttribute(Driver.MakeAttribute(TuiColors.Gold, ColorScheme.Normal.Background));
                Move(x, y);
                Driver.AddStr(text);
                Driver.SetAttribute(GetNormalColor());
                break;
            }

            x += width + 1;
        }
    }
}
