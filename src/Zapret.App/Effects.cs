using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zapret.App;

public enum RevealKind { Rise, SlideLeft, SlideRight, Pop, LineUp }

/// <summary>
/// Появление элементов при открытии экрана: в разметке у элемента ставится Reveal.Delay (секунды)
/// и, при желании, Reveal.Kind. LineUp - строка заголовка выезжает снизу из-под маски
/// (у родителя должен быть ClipToBounds).
/// </summary>
public static class Reveal
{
    /// <summary>Выключается в превью-скриншотах, чтобы снимать готовый кадр.</summary>
    public static bool Enabled { get; set; } = true;

    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(double), typeof(Reveal), new PropertyMetadata(-1.0));

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(RevealKind), typeof(Reveal), new PropertyMetadata(RevealKind.Rise));

    public static double GetDelay(DependencyObject o) => (double)o.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject o, double value) => o.SetValue(DelayProperty, value);
    public static RevealKind GetKind(DependencyObject o) => (RevealKind)o.GetValue(KindProperty);
    public static void SetKind(DependencyObject o, RevealKind value) => o.SetValue(KindProperty, value);

    public static void Play(DependencyObject root)
    {
        // Уважаем системную настройку «Показывать анимацию в Windows».
        if (!Enabled || !SystemParameters.ClientAreaAnimation) return;
        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            var delay = GetDelay(element);
            if (delay >= 0) Animate(element, GetKind(element), TimeSpan.FromSeconds(delay));
        }
    }

    private static void Animate(FrameworkElement el, RevealKind kind, TimeSpan delay)
    {
        var (x, y, scale, seconds) = kind switch
        {
            RevealKind.SlideLeft => (-26.0, 0.0, 1.0, 0.92),
            RevealKind.SlideRight => (30.0, 0.0, 0.985, 0.95),
            RevealKind.Pop => (0.0, 0.0, 0.7, 0.6),
            RevealKind.LineUp => (0.0, Math.Max(el.ActualHeight, 40) * 1.15, 1.0, 1.05),
            _ => (0.0, 14.0, 1.0, 0.7),
        };

        var shift = new TranslateTransform(x, y);
        var zoom = new ScaleTransform(scale, scale);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = new TransformGroup { Children = { zoom, shift } };
        // Начальное состояние ставим сразу, иначе до начала анимации элемент мелькнёт на месте.
        el.Opacity = kind == RevealKind.LineUp ? 1 : 0;

        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var duration = new Duration(TimeSpan.FromSeconds(seconds));
        DoubleAnimation To(double value) => new(value, duration) { BeginTime = delay, EasingFunction = ease };

        if (kind != RevealKind.LineUp) el.BeginAnimation(UIElement.OpacityProperty, To(1));
        shift.BeginAnimation(TranslateTransform.XProperty, To(0));
        shift.BeginAnimation(TranslateTransform.YProperty, To(0));
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, To(1));
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, To(1));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}

/// <summary>Обрезает содержимое по скруглённому прямоугольнику (блик не вылезает за углы стекла).</summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double value) => o.SetValue(RadiusProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.SizeChanged -= Update;
        el.SizeChanged += Update;
        Update(el, null);
    }

    private static void Update(object sender, SizeChangedEventArgs? e)
    {
        var el = (FrameworkElement)sender;
        var r = GetRadius(el);
        el.Clip = new RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), r, r);
    }
}

/// <summary>Скруглённые углы окна, как у обычных окон Windows 11.</summary>
public static class WindowCorners
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Round(Window window)
    {
        const int cornerPreference = 33, round = 2;
        var value = round;
        var hwnd = new WindowInteropHelper(window).Handle;
        // На Windows 10 атрибута нет - вызов просто ничего не сделает.
        _ = DwmSetWindowAttribute(hwnd, cornerPreference, ref value, sizeof(int));
    }
}
