using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SSHExplorer.App;

internal static class MotionTests
{
    public static int Run()
    {
        App? app = null;
        try
        {
            app = TestApplication.Create(); Theme.Apply("Light"); UiMotion.SetTestPolicy(true);
            var count = 0;
            void Check(bool condition, string description) { if (!condition) throw new Exception(description); count++; Console.WriteLine("PASS " + description); }
            Check((TimeSpan)app.FindResource("MotionFast") == TimeSpan.FromMilliseconds(83) && (TimeSpan)app.FindResource("MotionSurface") == TimeSpan.FromMilliseconds(167) && (TimeSpan)app.FindResource("MotionSlow") == TimeSpan.FromMilliseconds(250), "real motion resources use project durations 83/167/250 ms");
            var layer = new Border { IsHitTestVisible = false };
            UiMotion.SetFeedbackLayer(layer, true); UiMotion.SetIsShown(layer, true);
            Check(UiMotion.ActiveClockCount(layer) == 1 && Equals(layer.GetAnimationBaseValue(UIElement.OpacityProperty), 1d), "hover attaches one real opacity clock with the final base value already set");
            UiMotion.SeekForTests(layer, TimeSpan.FromMilliseconds(35)); var partial = layer.Opacity;
            Check(partial is > 0 and < 1, "real WPF hover clock reaches an intermediate opacity without a Window");
            UiMotion.SetIsShown(layer, false); UiMotion.SeekForTests(layer, TimeSpan.Zero);
            Check(UiMotion.ActiveClockCount(layer) == 1 && Math.Abs(layer.Opacity - partial) < .02 && Equals(layer.GetAnimationBaseValue(UIElement.OpacityProperty), 0d), "opposite hover replaces the old clock and retargets from its current value");
            UiMotion.SeekForTests(layer, TimeSpan.FromMilliseconds(25)); UiMotion.SetIsShown(layer, true);
            Check(UiMotion.ActiveClockCount(layer) == 1, "rapid hover reversal does not accumulate clocks");
            UiMotion.FinishForTests(layer);
            Check(layer.Opacity == 1 && UiMotion.ActiveClockCount(layer) == 0, "hover finishes at its latest requested value without retained clocks");
            var child = new Button { Content = "Same command target" }; var surface = new Border { Child = child, Focusable = true, IsHitTestVisible = true };
            var focused = Keyboard.FocusedElement; var input = (IInputElement)child;
            for (int i = 0; i < 64; i++) UiMotion.SurfaceChanged(surface);
            Check(UiMotion.ActiveClockCount(surface) == 2 && surface.RenderTransform is TranslateTransform, "64 repeated section transitions retain only the current opacity/translation clocks");
            UiMotion.SeekForTests(surface, TimeSpan.FromMilliseconds(70));
            Check(surface.Opacity is > .96 and < 1 && surface.RenderTransform is TranslateTransform { Y: > 0 and < 4 }, "real section clocks advance opacity and small presentation translation");
            Check(ReferenceEquals(surface.Child, child) && ReferenceEquals(input, child) && surface.Focusable && surface.IsHitTestVisible && child.IsEnabled && ReferenceEquals(Keyboard.FocusedElement, focused) && surface.LayoutTransform.Value.IsIdentity, "motion preserves command object, focusability, hit-test flags and layout transform; no real keyboard input is claimed");
            UiMotion.SetTestPolicy(false);
            Check(surface.Opacity == 1 && ((TranslateTransform)surface.RenderTransform).Y == 0 && UiMotion.ActiveClockCount(surface) == 0, "disabling motion during a section transition snaps final values and removes clocks");
            UiMotion.SetIsShown(layer, false);
            Check(layer.Opacity == 0 && UiMotion.ActiveClockCount(layer) == 0, "disabled hover applies its final state immediately");
            UiMotion.SurfaceChanged(surface);
            Check(surface.Opacity == 1 && ((TranslateTransform)surface.RenderTransform).Y == 0 && UiMotion.ActiveClockCount(surface) == 0, "disabled section transition has no residual opacity, transform or command delay");
            UiMotion.SetTestPolicy(true); UiMotion.SurfaceChanged(surface); surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, surface));
            Check(UiMotion.ActiveClockCount(surface) == 0 && surface.Opacity == 1 && ((TranslateTransform)surface.RenderTransform).Y == 0, "Unloaded cancels a real transition and restores its final presentation state");
            UiMotion.SetIsShown(layer, true); UiMotion.SeekForTests(layer, TimeSpan.FromMilliseconds(20)); Theme.Apply("Dark");
            Check(layer.Opacity == 1 && UiMotion.ActiveClockCount(layer) == 0, "theme change during an active hover transition snaps and clears clocks");
            var menu = new ContextMenu(); UiMotion.OpenFlyout(menu);
            Check(UiMotion.ActiveClockCount(menu) == 1 && !menu.IsOpen, "flyout presentation clock can be tested without opening a Popup");
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, menu));
            Check(menu.Opacity == 1 && UiMotion.ActiveClockCount(menu) == 0 && !menu.IsOpen, "flyout Closed cleanup removes its clock without opening a Window or Popup");
            Check(app.Windows.Count == 0, "headless motion test created no Window");
            UiMotion.SetTestPolicy(null); app.Shutdown(); Console.WriteLine($"RESULT: {count} real clock/contract assertions passed; no Windows or Popups opened; live motion/focus/HC remain separate"); return 0;
        }
        catch (Exception ex) { UiMotion.SetTestPolicy(null); app?.Shutdown(); Console.WriteLine(ex); return 1; }
    }
}
