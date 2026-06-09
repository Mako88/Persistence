using Terminal.Gui;

namespace Persistence.Console;

/// <summary>
/// A <see cref="FrameView"/> whose title is drawn yellow while the frame holds focus and white
/// otherwise — "yellow means focused", consistent with the tabs — with the border left white either
/// way.
///
/// Terminal.Gui draws a frame's title and border from the same colour-scheme slot (Normal when
/// focused), so a scheme alone can't colour the title without also colouring the border. Instead we
/// keep the frame on the plain white scheme (white border) and paint the title ourselves on the top
/// border row after the base draw, picking the colour from the focus state.
/// </summary>
internal sealed class FocusTitleFrameView : FrameView
{
    private readonly string title;

    public FocusTitleFrameView(string title) : base(string.Empty) => this.title = title;

    public override void Redraw(Rect bounds)
    {
        base.Redraw(bounds);

        if (Application.Driver is null || string.IsNullOrEmpty(title))
        {
            return;
        }

        // "Focused" means the focused view lives inside this frame — HasFocus alone doesn't reliably
        // propagate to the container, so also consult the focused-subview chain.
        var focused = HasFocus || MostFocused is not null || Focused is not null;
        var colour = focused ? TuiColors.Label : TuiColors.Body;
        Driver.SetAttribute(Driver.MakeAttribute(colour, ColorScheme.Normal.Background));

        // Sit the title on the top border, a couple of columns in (matching TG's own title placement),
        // with a space either side so it reads as a label cut into the border line.
        Move(2, 0);
        Driver.AddStr($" {title} ");
    }
}
