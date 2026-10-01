using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Ledger.App.Controls;

/// <summary>
/// Lays children out in equal-width columns, wrapping to more rows when each column would be
/// narrower than <see cref="MinColumnWidth"/>. Used for rows of summary cards.
/// </summary>
public sealed partial class EqualColumnsPanel : Panel
{
    public double Spacing { get; set; } = 12;
    public double MinColumnWidth { get; set; } = 200;

    /// <summary>Upper limit on columns, however wide the panel is.</summary>
    public int MaxColumns { get; set; } = int.MaxValue;

    /// <summary>
    /// True (default): a few items share the full width. False: columns keep the width they would have
    /// with a full row, so one card doesn't stretch across the page.
    /// </summary>
    public bool StretchFewItems { get; set; } = true;

    private int Columns(double width)
    {
        var count = Math.Max(1, Children.Count);
        if (double.IsInfinity(width))
        {
            return count;
        }
        var fit = Math.Clamp((int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing)), 1, MaxColumns);
        return StretchFewItems ? Math.Min(fit, count) : fit;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var columnWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, (availableSize.Width - Spacing * (columns - 1)) / columns);

        double height = 0, rowHeight = 0, widest = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            widest = Math.Max(widest, Children[i].DesiredSize.Width);
            if (i % columns == columns - 1 || i == Children.Count - 1)
            {
                height += rowHeight + (height > 0 ? Spacing : 0);
                rowHeight = 0;
            }
        }
        var width = double.IsInfinity(availableSize.Width) ? widest * columns + Spacing * (columns - 1) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var columnWidth = Math.Max(0, (finalSize.Width - Spacing * (columns - 1)) / columns);

        double y = 0;
        for (var start = 0; start < Children.Count; start += columns)
        {
            var row = Enumerable.Range(start, Math.Min(columns, Children.Count - start)).Select(i => Children[i]).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var c = 0; c < row.Count; c++)
            {
                row[c].Arrange(new Rect(c * (columnWidth + Spacing), y, columnWidth, rowHeight));
            }
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
