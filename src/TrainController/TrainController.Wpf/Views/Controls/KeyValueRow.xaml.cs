using System.Windows;
using System.Windows.Controls;
using TrainController.Integration.Presentation;

namespace TrainController.Wpf.Views.Controls;

/// <summary>One "label ............ value" row of a status card. Pure view.</summary>
public partial class KeyValueRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(KeyValueRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DisplayProperty =
        DependencyProperty.Register(nameof(Display), typeof(DisplayValue), typeof(KeyValueRow), new PropertyMetadata(DisplayValue.Missing));

    public KeyValueRow()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public DisplayValue Display
    {
        get => (DisplayValue)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }
}
