namespace FileDock.ViewModels;

/// <summary>Which rows a drag that starts on a row should carry.</summary>
public static class DragSelection
{
    /// <summary>
    /// Dragging a selected row carries the whole selection (in shelf order). Dragging a row that
    /// is not selected carries just that row.
    /// </summary>
    public static IReadOnlyList<T> Resolve<T>(IReadOnlyList<T> all, IReadOnlyCollection<T> selected, T pressed)
    {
        if (!selected.Contains(pressed))
            return [pressed];
        var set = new HashSet<T>(selected);
        return all.Where(set.Contains).ToList();
    }
}
