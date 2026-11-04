using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SSHExplorer.App;

/// <summary>Short presentation-only transitions. Commands, focus and connection identity never wait for a clock.</summary>
public static class UiMotion
{
    public static readonly DependencyProperty FeedbackLayerProperty = DependencyProperty.RegisterAttached("FeedbackLayer", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, LayerChanged));
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached("IsShown", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, ShownChanged));
    static readonly ConditionalWeakTable<FrameworkElement, State> States = new();
    static readonly List<WeakReference<FrameworkElement>> Targets = [];
    static readonly object TargetsGate = new();
    static bool? testPolicy;
    static bool enabled = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    static bool userReduced;
    /// <summary>Windows "Animation effects" and High Contrast; the app setting can only reduce further.</summary>
    public static bool SystemAnimationsEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    /// <summary>"Уменьшить анимацию" in Параметры.</summary>
    public static bool UserReduced { get => userReduced; set { userReduced = value; RefreshSystemPreferences(); } }
    public static bool Enabled => testPolicy ?? enabled;
    public static void SetFeedbackLayer(DependencyObject value, bool enabled) => value.SetValue(FeedbackLayerProperty, enabled);
    public static bool GetFeedbackLayer(DependencyObject value) => (bool)value.GetValue(FeedbackLayerProperty);
    public static void SetIsShown(DependencyObject value, bool shown) => value.SetValue(IsShownProperty, shown);
    public static bool GetIsShown(DependencyObject value) => (bool)value.GetValue(IsShownProperty);
    static UiMotion()
    {
        SystemParameters.StaticPropertyChanged += PreferencesChanged;
        Theme.Changed += RefreshSystemPreferences;
    }
    static void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) RefreshSystemPreferences();
    }
    public static void RefreshSystemPreferences()
    {
        enabled = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && !userReduced;
        // Theme/preferences may change during a transition. Finish current decoration immediately.
        FrameworkElement[] targets;
        lock (TargetsGate)
        {
            Targets.RemoveAll(reference => !reference.TryGetTarget(out _));
            targets = Targets.Select(reference => reference.TryGetTarget(out var target) ? target : null).OfType<FrameworkElement>().ToArray();
        }
        foreach (var target in targets)
        {
            if (target.Dispatcher.CheckAccess()) Stop(target);
            else if (!target.Dispatcher.HasShutdownStarted) target.Dispatcher.BeginInvoke(() => Stop(target));
        }
    }
    static State Track(FrameworkElement owner)
    {
        if (States.TryGetValue(owner, out var state)) return state;
        state = new(); States.Add(owner, state); lock (TargetsGate) Targets.Add(new(owner));
        owner.Unloaded += Unloaded;
        return state;
    }
    static void Unloaded(object sender, RoutedEventArgs args) => Stop((FrameworkElement)sender);
    static void LayerChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not FrameworkElement target) return;
        if ((bool)args.NewValue) { Track(target); target.Opacity = GetIsShown(target) ? 1 : 0; }
        else Stop(target);
    }
    static void ShownChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not FrameworkElement target || !GetFeedbackLayer(target)) return;
        var from = target.Opacity; Stop(target); var state = Track(target); var version = ++state.Version;
        Animate(state, version, target, UIElement.OpacityProperty, from, (bool)args.NewValue ? 1 : 0, Duration(target, "MotionFast", 83), false);
    }
    static TimeSpan Duration(FrameworkElement owner, string key, double fallback)
        => owner.TryFindResource(key) is TimeSpan duration ? duration : TimeSpan.FromMilliseconds(fallback);
    static void Animate(State state, int version, Animatable target, DependencyProperty property, double from, double to, TimeSpan duration, bool easeOut)
    {
        target.SetValue(property, to);
        if (!Enabled || duration <= TimeSpan.Zero || Math.Abs(from - to) < .001) return;
        var animation = new DoubleAnimation(from, to, duration) { FillBehavior = FillBehavior.Stop };
        if (easeOut) animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        var clock = (AnimationClock)animation.CreateClock(true); var slot = new Slot(target, property, to, clock); state.Slots.Add(slot);
        clock.Completed += (_, _) =>
        {
            if (state.Version != version) return;
            target.ApplyAnimationClock(property, null); target.SetValue(property, to); state.Slots.Remove(slot);
        };
        target.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
    }
    // UIElement and transforms are different WPF animation hosts; both use the same cancellable clock policy.
    static void Animate(State state, int version, FrameworkElement target, DependencyProperty property, double from, double to, TimeSpan duration, bool easeOut)
    {
        target.SetValue(property, to);
        if (!Enabled || duration <= TimeSpan.Zero || Math.Abs(from - to) < .001) return;
        var animation = new DoubleAnimation(from, to, duration) { FillBehavior = FillBehavior.Stop };
        if (easeOut) animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        var clock = (AnimationClock)animation.CreateClock(true); var slot = new Slot(target, property, to, clock); state.Slots.Add(slot);
        clock.Completed += (_, _) => { if (state.Version != version) return; target.ApplyAnimationClock(property, null); target.SetValue(property, to); state.Slots.Remove(slot); };
        target.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
    }
    public static void SurfaceChanged(FrameworkElement surface)
    {
        Stop(surface); var state = Track(surface); int version = ++state.Version;
        // Only a dedicated presentation host receives this transform; never apply it to WebView2/HWND content.
        var transform = new TranslateTransform(); surface.RenderTransform = transform;
        Animate(state, version, surface, UIElement.OpacityProperty, .96, 1, Duration(surface, "MotionSurface", 167), true);
        Animate(state, version, transform, TranslateTransform.YProperty, 4, 0, Duration(surface, "MotionSurface", 167), true);
    }
    public static void OpenFlyout(ContextMenu menu)
    {
        var surface = menu.Template?.FindName("FlyoutSurface", menu) as FrameworkElement ?? menu;
        Stop(surface); var state = Track(surface); int version = ++state.Version;
        // Placement and input capture are immediate. No delay is added to opening/closing commands.
        Animate(state, version, surface, UIElement.OpacityProperty, .96, 1, Duration(menu, "MotionFast", 83), false);
        menu.Closed -= FlyoutClosed; menu.Closed += FlyoutClosed;
    }
    static void FlyoutClosed(object sender, RoutedEventArgs args)
    {
        var menu = (ContextMenu)sender; menu.Closed -= FlyoutClosed;
        Stop(menu.Template?.FindName("FlyoutSurface", menu) as FrameworkElement ?? menu);
    }
    public static void Stop(FrameworkElement owner)
    {
        if (!States.TryGetValue(owner, out var state)) return;
        ++state.Version;
        foreach (var slot in state.Slots)
        {
            if (slot.Target is UIElement element) element.ApplyAnimationClock(slot.Property, null);
            else if (slot.Target is Animatable animated) animated.ApplyAnimationClock(slot.Property, null);
            slot.Target.SetValue(slot.Property, slot.Final);
        }
        state.Slots.Clear();
    }
    internal static int ActiveClockCount(FrameworkElement owner) => States.TryGetValue(owner, out var state) ? state.Slots.Count : 0;
    internal static void SetTestPolicy(bool? value) { testPolicy = value; RefreshSystemPreferences(); }
    internal static void FinishForTests(FrameworkElement owner)
    {
        if (States.TryGetValue(owner, out var state)) foreach (var slot in state.Slots.ToArray()) slot.Clock.Controller?.SkipToFill();
        Stop(owner);
    }
    internal static void SeekForTests(FrameworkElement owner, TimeSpan elapsed)
    {
        if (States.TryGetValue(owner, out var state)) foreach (var slot in state.Slots.ToArray()) slot.Clock.Controller?.SeekAlignedToLastTick(elapsed, TimeSeekOrigin.BeginTime);
    }
    sealed class State { public int Version; public List<Slot> Slots { get; } = []; }
    sealed record Slot(DependencyObject Target, DependencyProperty Property, double Final, AnimationClock Clock);
}
