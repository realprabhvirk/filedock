using System.Windows.Media;
using FileDock.Models;
using FileDock.Services;
using FileDock.Themes;
using FileDock.ViewModels;

namespace FileDock.Tests;

public class DragSelectionTests
{
    private static readonly string[] All = ["a", "b", "c", "d"];

    [Fact]
    public void Dragging_a_selected_item_carries_the_selection_in_shelf_order()
    {
        Assert.Equal(["b", "d"], DragSelection.Resolve(All, ["d", "b"], "d"));
    }

    [Fact]
    public void Dragging_an_unselected_item_carries_only_that_item()
    {
        Assert.Equal(["c"], DragSelection.Resolve(All, ["a", "b"], "c"));
    }

    [Fact]
    public void Dragging_with_nothing_selected_carries_the_pressed_item()
    {
        Assert.Equal(["a"], DragSelection.Resolve(All, [], "a"));
    }
}

public class WindowPlacementTests
{
    // A 1920x1080 display with a 50 px taskbar at the bottom, at 125% scaling.
    private static readonly PixelRect Work = new(0, 0, 1920, 1030);

    [Fact]
    public void Right_edge_handle_is_flush_with_the_work_area_and_centred()
    {
        DockLayout layout = WindowPlacementService.Compute(Work, 1.25, DockEdge.Right, 0.5);

        Assert.Equal(1920, layout.Window.Right);
        Assert.Equal(425, layout.Window.Width);       // 340 DIP at 125%
        Assert.Equal(600, layout.Window.Height);      // 480 DIP at 125%
        Assert.Equal(12, layout.Handle.Width);        // 10 DIP = 12.5 px, rounded to even
        Assert.Equal(80, layout.Handle.Height);       // 64 DIP
        Assert.Equal(layout.Window.Width - layout.Handle.Width, layout.Handle.X);

        int handleCentre = layout.Window.Y + layout.Handle.Y + layout.Handle.Height / 2;
        Assert.InRange(handleCentre, 510, 520);
    }

    [Fact]
    public void Left_edge_expands_rightwards()
    {
        DockLayout layout = WindowPlacementService.Compute(Work, 1.0, DockEdge.Left, 0.5);
        Assert.Equal(0, layout.Window.X);
        Assert.Equal(0, layout.Handle.X);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void Window_stays_inside_the_work_area_and_contains_the_handle(double position)
    {
        DockLayout layout = WindowPlacementService.Compute(Work, 1.0, DockEdge.Right, position);

        Assert.True(layout.Window.Y >= Work.Y && layout.Window.Bottom <= Work.Bottom);
        Assert.True(layout.Handle.Y >= 0 && layout.Handle.Bottom <= layout.Window.Height);
    }

    [Fact]
    public void Respects_a_work_area_that_does_not_start_at_zero()
    {
        // Taskbar on the left of a secondary monitor placed to the right of the primary.
        var work = new PixelRect(1920 + 60, 0, 1860, 1080);
        DockLayout layout = WindowPlacementService.Compute(work, 1.0, DockEdge.Left, 0.5);
        Assert.Equal(work.X, layout.Window.X);
    }

    [Fact]
    public void Small_screens_get_a_shorter_panel_that_still_fits()
    {
        var work = new PixelRect(0, 0, 1024, 300);
        DockLayout layout = WindowPlacementService.Compute(work, 1.0, DockEdge.Right, 0.9);

        Assert.True(layout.Window.Height <= work.Height);
        Assert.True(layout.Window.Y >= work.Y && layout.Window.Bottom <= work.Bottom);
        Assert.True(layout.Handle.Bottom <= layout.Window.Height);
    }
}

public class ThemeContrastTests
{
    [Theory]
    [InlineData("#FFB900", "#FAFAFA")] // a pale yellow accent on the light panel
    [InlineData("#0063B1", "#202020")] // a dark blue accent on the dark panel
    public void Accent_text_is_adjusted_to_readable_contrast(string accent, string background)
    {
        var a = (Color)ColorConverter.ConvertFromString(accent);
        var b = (Color)ColorConverter.ConvertFromString(background);
        Color toward = b.R > 128 ? Colors.Black : Colors.White;

        Color adjusted = ThemeManager.EnsureContrast(a, b, 4.5, toward);

        Assert.True(ThemeManager.Contrast(adjusted, b) >= 4.5);
    }
}
