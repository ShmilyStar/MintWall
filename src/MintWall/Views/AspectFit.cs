using System.Windows;

namespace MintWall.Views;

/// <summary>
/// 把元素的高度绑成「宽度 ÷ 宽高比」，预览框就能跟显示器同比例，整屏画面不用裁切。
/// </summary>
public static class AspectFit
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.RegisterAttached(
        "Ratio",
        typeof(double),
        typeof(AspectFit),
        new PropertyMetadata(0d, OnRatioChanged));

    public static void SetRatio(DependencyObject element, double value) => element.SetValue(RatioProperty, value);

    public static double GetRatio(DependencyObject element) => (double)element.GetValue(RatioProperty);

    private static void OnRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        Apply(element, (double)e.NewValue);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            Apply((FrameworkElement)sender, GetRatio((FrameworkElement)sender));
        }
    }

    private static void Apply(FrameworkElement element, double ratio)
    {
        if (ratio <= 0 || double.IsNaN(ratio) || element.ActualWidth <= 0)
        {
            return;
        }

        element.Height = element.ActualWidth / ratio;
    }
}
