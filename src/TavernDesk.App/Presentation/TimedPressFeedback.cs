using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TavernDesk.App.Presentation;

/// <summary>
/// 在切页或弹层覆盖控件前，保证短促的按下与回弹过程实际可见。
/// 仅用于确实需要等待视觉阶段完成的动作，不作为通用动画框架。
/// </summary>
internal sealed class TimedPressFeedback
{
    private readonly Dictionary<Button, long> _pressedAt = [];

    public void Press(
        object sender,
        double offset,
        TimeSpan duration)
    {
        if (sender is not Button button)
        {
            return;
        }

        _pressedAt[button] = Stopwatch.GetTimestamp();
        Animate(
            button,
            offset,
            duration,
            new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    public async Task ReleaseBeforeActionAsync(
        Button button,
        double pressedOffset,
        TimeSpan pressDuration,
        TimeSpan minimumPressedDuration,
        TimeSpan reboundDuration)
    {
        if (!_pressedAt.TryGetValue(button, out var startedAt))
        {
            startedAt = Stopwatch.GetTimestamp();
            Animate(
                button,
                pressedOffset,
                pressDuration,
                new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        // 快速点击时 WPF 可能尚未绘制按下帧，页面或弹层就已替换画面；
        // 因此补足最短按压时间，并等回弹完成后再把控制权交给业务动作。
        var pressedFor = Stopwatch.GetElapsedTime(startedAt);
        if (pressedFor < minimumPressedDuration)
        {
            await Task.Delay(minimumPressedDuration - pressedFor);
        }

        Animate(
            button,
            0,
            reboundDuration,
            new BackEase
            {
                EasingMode = EasingMode.EaseOut,
                Amplitude = 0.35
            });
        await Task.Delay(reboundDuration);
        _pressedAt.Remove(button);
    }

    public void Cancel(object sender, TimeSpan duration)
    {
        if (sender is not Button button)
        {
            return;
        }

        _pressedAt.Remove(button);
        Animate(
            button,
            0,
            duration,
            new BackEase
            {
                EasingMode = EasingMode.EaseOut,
                Amplitude = 0.25
            });
    }

    private static void Animate(
        Button button,
        double targetY,
        TimeSpan duration,
        IEasingFunction easing)
    {
        if (button.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            button.RenderTransform = transform;
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(targetY, duration)
            {
                EasingFunction = easing
            });
    }
}
