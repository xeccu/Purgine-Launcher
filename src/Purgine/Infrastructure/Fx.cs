using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Purgine.Infrastructure;

/// <summary>
/// Animaciones opcionales (solo con el perfil gráfico Alto, ver <see cref="GraphicsTheme"/>):
/// <list type="bullet">
///   <item><c>Fx.HoverLift="True"</c>: al pasar el cursor, el elemento se eleva y amplía ligeramente.</item>
///   <item><c>Fx.FadeIn="True"</c>: al mostrarse (o cambiar de contenido), entra con un fundido y un leve
///         desplazamiento vertical.</item>
/// </list>
/// Con los perfiles Bajo y Medio no se crea ninguna animación ni transformación: coste cero.
/// Al terminar, cada animación se retira para no dejar relojes vivos.
/// </summary>
public static class Fx
{
    private static readonly Duration HoverDuration = TimeSpan.FromMilliseconds(170);
    private static readonly Duration EnterDuration = TimeSpan.FromMilliseconds(220);
    private static readonly IEasingFunction Ease = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    // =====================================================================================
    // HoverLift
    // =====================================================================================

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject d) => (bool)d.GetValue(HoverLiftProperty);
    public static void SetHoverLift(DependencyObject d, bool value) => d.SetValue(HoverLiftProperty, value);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.MouseEnter -= OnLiftEnter;
        el.MouseLeave -= OnLiftLeave;
        if (e.NewValue is true)
        {
            el.MouseEnter += OnLiftEnter;
            el.MouseLeave += OnLiftLeave;
        }
    }

    private static void OnLiftEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!GraphicsTheme.AnimationsEnabled || sender is not FrameworkElement el) return;
        var (scale, move) = LiftTransforms(el);
        Animate(scale, ScaleTransform.ScaleXProperty, 1.04, HoverDuration);
        Animate(scale, ScaleTransform.ScaleYProperty, 1.04, HoverDuration);
        Animate(move, TranslateTransform.YProperty, -5, HoverDuration);
    }

    private static void OnLiftLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement el || el.RenderTransform is not TransformGroup { Children.Count: 2 } group) return;
        if (group.Children[0] is not ScaleTransform scale || group.Children[1] is not TranslateTransform move) return;
        if (!GraphicsTheme.AnimationsEnabled)
        {
            el.RenderTransform = Transform.Identity; // se cambió de perfil con el cursor encima
            return;
        }
        Animate(scale, ScaleTransform.ScaleXProperty, 1, HoverDuration);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, HoverDuration);
        Animate(move, TranslateTransform.YProperty, 0, HoverDuration);
    }

    private static (ScaleTransform Scale, TranslateTransform Move) LiftTransforms(FrameworkElement el)
    {
        if (el.RenderTransform is TransformGroup { Children.Count: 2, IsFrozen: false } g &&
            g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (s, t);
        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = new TransformGroup { Children = { scale, move } };
        return (scale, move);
    }

    // =====================================================================================
    // FadeIn
    // =====================================================================================

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject d) => (bool)d.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject d, bool value) => d.SetValue(FadeInProperty, value);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.IsVisibleChanged -= OnFadeVisibleChanged;
        el.DataContextChanged -= OnFadeDataContextChanged;
        if (e.NewValue is true)
        {
            el.IsVisibleChanged += OnFadeVisibleChanged;
            el.DataContextChanged += OnFadeDataContextChanged;
        }
    }

    private static void OnFadeVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is FrameworkElement el) PlayEnter(el);
    }

    private static void OnFadeDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsVisible: true } el && e.NewValue is not null) PlayEnter(el);
    }

    private static void PlayEnter(FrameworkElement el)
    {
        if (!GraphicsTheme.AnimationsEnabled) return;

        var move = el.RenderTransform as TranslateTransform;
        if (move is null || move.IsFrozen)
        {
            move = new TranslateTransform();
            el.RenderTransform = move;
        }

        var fade = new DoubleAnimation(0, 1, EnterDuration) { EasingFunction = Ease };
        fade.Completed += (_, _) => el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, fade);

        var slide = new DoubleAnimation(12, 0, EnterDuration) { EasingFunction = Ease };
        var target = move;
        slide.Completed += (_, _) => target.BeginAnimation(TranslateTransform.YProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // =====================================================================================

    private static void Animate(Animatable target, DependencyProperty property, double to, Duration duration) =>
        target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = Ease }, HandoffBehavior.SnapshotAndReplace);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
