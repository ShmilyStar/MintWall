using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MintWall.Views.Sections;

public partial class ScheduleSection : UserControl
{
    public static readonly DependencyProperty ExtraTextProperty = DependencyProperty.Register(
        nameof(ExtraText),
        typeof(string),
        typeof(ScheduleSection),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ExtraCommandProperty = DependencyProperty.Register(
        nameof(ExtraCommand),
        typeof(ICommand),
        typeof(ScheduleSection),
        new PropertyMetadata(null));

    public ScheduleSection()
    {
        InitializeComponent();
    }

    /// <summary>概览页放系统设置快捷入口；桌面/锁屏页留空，不显示这个按钮。</summary>
    public string ExtraText
    {
        get => (string)GetValue(ExtraTextProperty);
        set => SetValue(ExtraTextProperty, value);
    }

    public ICommand? ExtraCommand
    {
        get => (ICommand?)GetValue(ExtraCommandProperty);
        set => SetValue(ExtraCommandProperty, value);
    }
}
