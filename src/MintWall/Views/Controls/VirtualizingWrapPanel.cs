using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MintWall.Views.Controls;

/// <summary>
/// 按行虚拟化的等宽等高 WrapPanel：只创建可见行附近的条目。
/// 壁纸库上千张时，普通 WrapPanel 会把每张卡的完整视觉树都实例化，内存和滚动都吃不消。
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private Size _itemSize = new(220, 210);
    private int _columns = 1;

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private double LineScroll => Math.Max(1, _itemSize.Height / 3);

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineScroll);

    public void LineDown() => SetVerticalOffset(VerticalOffset + LineScroll);

    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - LineScroll);

    public void LineRight() => SetHorizontalOffset(HorizontalOffset + LineScroll);

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);

    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);

    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);

    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - LineScroll * 3);

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + LineScroll * 3);

    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - LineScroll * 3);

    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + LineScroll * 3);

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        double clamped = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(clamped - _offset.Y) < 0.5)
        {
            return;
        }

        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int count = owner?.Items.Count ?? 0;

        // 条目样式一致，量一个就够；只用来得到卡片宽高和行高。
        if (InternalChildren.Count > 0)
        {
            InternalChildren[0].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size measured = InternalChildren[0].DesiredSize;
            if (measured.Width > 0 && measured.Height > 0)
            {
                _itemSize = measured;
            }
        }

        double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? _itemSize.Width
            : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0
            ? _itemSize.Height
            : availableSize.Height;

        _columns = Math.Max(1, (int)(width / Math.Max(1, _itemSize.Width)));
        int rows = count == 0 ? 0 : (count + _columns - 1) / _columns;
        _extent = new Size(_columns * _itemSize.Width, rows * _itemSize.Height);
        _viewport = new Size(width, height);

        double maxOffset = Math.Max(0, _extent.Height - _viewport.Height);
        if (_offset.Y > maxOffset)
        {
            _offset.Y = maxOffset;
            ScrollOwner?.InvalidateScrollInfo();
        }

        int firstRow = Math.Max(0, (int)(_offset.Y / _itemSize.Height));
        int lastRow = (int)((_offset.Y + _viewport.Height) / _itemSize.Height) + 1;
        int firstIndex = firstRow * _columns;
        int endIndex = Math.Min(count, (lastRow + 1) * _columns);

        for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < firstIndex || itemIndex >= endIndex)
            {
                ItemContainerGenerator.Remove(new GeneratorPosition(childIndex, 0), 1);
                RemoveInternalChildRange(childIndex, 1);
            }
        }

        if (endIndex > firstIndex)
        {
            GeneratorPosition startPosition = ItemContainerGenerator.GeneratorPositionFromIndex(firstIndex);
            int childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;
            using (ItemContainerGenerator.StartAt(startPosition, GeneratorDirection.Forward, true))
            {
                for (int itemIndex = firstIndex; itemIndex < endIndex; itemIndex++, childIndex++)
                {
                    UIElement? child = ItemContainerGenerator.GenerateNext(out bool newlyRealized) as UIElement;
                    if (child is null)
                    {
                        continue;
                    }

                    if (newlyRealized)
                    {
                        if (childIndex >= InternalChildren.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }

                        // 缺了这一步容器不会绑定内容，量出来是空卡片，整块网格都会是空白。
                        ItemContainerGenerator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(_itemSize.Width, _itemSize.Height));
                }
            }
        }

        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            UIElement child = InternalChildren[childIndex];
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0)
            {
                child.Arrange(new Rect());
                continue;
            }

            int row = itemIndex / _columns;
            int column = itemIndex % _columns;
            child.Arrange(new Rect(
                column * _itemSize.Width,
                row * _itemSize.Height - _offset.Y,
                _itemSize.Width,
                _itemSize.Height));
        }

        return finalSize;
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        UIElement? child = visual as UIElement;
        while (child is not null && !InternalChildren.Contains(child))
        {
            child = VisualTreeHelper.GetParent(child) as UIElement;
        }

        if (child is not null)
        {
            int childIndex = InternalChildren.IndexOf(child);
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex >= 0)
            {
                double top = (itemIndex / _columns) * _itemSize.Height;
                if (top < _offset.Y)
                {
                    SetVerticalOffset(top);
                }
                else if (top + _itemSize.Height > _offset.Y + _viewport.Height)
                {
                    SetVerticalOffset(top + _itemSize.Height - _viewport.Height);
                }
            }
        }

        return rectangle;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                _offset.Y = 0;
                break;
        }

        InvalidateMeasure();
    }
}
