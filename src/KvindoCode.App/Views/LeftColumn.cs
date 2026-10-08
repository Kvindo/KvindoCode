using Avalonia;
using Avalonia.Controls;

namespace KvindoCode.App.Views;

/// <summary>Hosts one child at the left edge, as wide as available but never wider than <see cref="MaxContentWidth"/>.</summary>
public sealed class LeftColumn : Decorator
{
    public double MaxContentWidth { get; set; } = 1000;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return default;
        var w = Math.Min(availableSize.Width, MaxContentWidth);
        Child.Measure(new Size(w, availableSize.Height));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null) return finalSize;
        var w = Math.Min(finalSize.Width, MaxContentWidth);
        Child.Arrange(new Rect(0, 0, w, finalSize.Height));
        return finalSize;
    }
}
