using System.Windows;
using System.Windows.Media;

namespace WgSwitch;

/// <summary>Ui.Round="18" — обрезает элемент по скруглённому прямоугольнику его текущего размера.</summary>
public static class Ui
{
    public static readonly DependencyProperty RoundProperty = DependencyProperty.RegisterAttached(
        "Round", typeof(double), typeof(Ui), new PropertyMetadata(0d, OnRoundChanged));

    public static double GetRound(DependencyObject d) => (double)d.GetValue(RoundProperty);
    public static void SetRound(DependencyObject d, double value) => d.SetValue(RoundProperty, value);

    private static void OnRoundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= Update;
        fe.SizeChanged += Update;
        Apply(fe);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        double r = GetRound(fe);
        fe.Clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
    }
}
