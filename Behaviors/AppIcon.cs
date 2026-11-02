using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace GitDeployPro.Behaviors
{
    /// <summary>
    /// Theme-able stroke icons. Usage:
    /// <code>&lt;Path behaviors:AppIcon.Kind="rocket" behaviors:AppIcon.Size="16" TextElement.Foreground="{DynamicResource Accent.Primary}" /&gt;</code>
    /// The geometry comes from the dynamic resource <c>Icon.&lt;Kind&gt;</c> (Themes/Icons.xaml), so a theme pack can
    /// replace any icon (<c>icons</c> section). Colour follows TextElement.Foreground (inherited from the parent Button/TextBlock when not set).
    /// </summary>
    public static class AppIcon
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
            "Kind", typeof(string), typeof(AppIcon), new PropertyMetadata(null, OnChanged));

        public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
            "Size", typeof(double), typeof(AppIcon), new PropertyMetadata(16.0, OnChanged));

        public static string? GetKind(DependencyObject d) => (string?)d.GetValue(KindProperty);
        public static void SetKind(DependencyObject d, string? value) => d.SetValue(KindProperty, value);
        public static double GetSize(DependencyObject d) => (double)d.GetValue(SizeProperty);
        public static void SetSize(DependencyObject d, double value) => d.SetValue(SizeProperty, value);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not WpfPath path)
            {
                return;
            }

            var kind = GetKind(path);
            if (string.IsNullOrWhiteSpace(kind))
            {
                return;
            }

            var size = GetSize(path);
            path.SetResourceReference(WpfPath.DataProperty, "Icon." + kind);
            path.Width = 24;
            path.Height = 24;
            path.Stretch = Stretch.None;
            path.StrokeThickness = 1.8;
            path.StrokeLineJoin = PenLineJoin.Round;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
            path.Fill = System.Windows.Media.Brushes.Transparent;
            path.SnapsToDevicePixels = false;
            path.VerticalAlignment = VerticalAlignment.Center;
            path.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            path.LayoutTransform = new ScaleTransform(size / 24.0, size / 24.0);
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding
            {
                Path = new PropertyPath("(TextElement.Foreground)"),
                RelativeSource = RelativeSource.Self
            });
        }
    }
}
