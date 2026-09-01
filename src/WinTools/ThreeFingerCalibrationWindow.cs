using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace WinTools;

/// <summary>Full-screen native pointer calibration guide.</summary>
internal sealed class ThreeFingerCalibrationWindow : Window
{
    private readonly Grid _root;
    private readonly Canvas _canvas;
    private readonly Ellipse _ball;
    private readonly TextBlock _stageText;
    private readonly TextBlock _hintText;
    private readonly ProgressBar _progress;
    private bool _cancelled;
    private bool _closingAfterCompletion;

    public ThreeFingerCalibrationWindow()
    {
        Title = "三指拖拽速度校准";

        _root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 15, 23, 42)),
            IsTabStop = true
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.KeyDown += Root_KeyDown;

        var header = new Grid { Margin = new Thickness(40, 28, 40, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new TextBlock
        {
            Text = "速度校准",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center
        };
        var cancelButton = new Button
        {
            Content = "取消（Esc）",
            Padding = new Thickness(18, 9, 18, 9),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        cancelButton.Click += (_, _) => Cancel();
        Grid.SetColumn(cancelButton, 1);
        header.Children.Add(heading);
        header.Children.Add(cancelButton);
        Grid.SetRow(header, 0);
        _root.Children.Add(header);

        var guideArea = new Grid { Margin = new Thickness(24, 0, 24, 0) };
        _canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _ball = new Ellipse
        {
            Width = 34,
            Height = 34,
            Fill = new SolidColorBrush(Colors.DodgerBlue),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 3,
            Shadow = new ThemeShadow()
        };
        _canvas.Children.Add(_ball);
        guideArea.Children.Add(_canvas);
        guideArea.Children.Add(new TextBlock
        {
            Text = "触控板左侧   ───────────────→   触控板右侧",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(guideArea, 1);
        _root.Children.Add(guideArea);

        var footer = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(40, 12, 40, 34)
        };
        _stageText = new TextBlock
        {
            Text = "准备",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _hintText = new TextBlock
        {
            Text = "每一档只滑动一次：从触控板最左侧滑到最右侧，然后抬起",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 203, 213, 225)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _progress = new ProgressBar { Minimum = 0, Maximum = 3, Value = 0, Height = 5 };
        footer.Children.Add(_stageText);
        footer.Children.Add(_hintText);
        footer.Children.Add(_progress);
        Grid.SetRow(footer, 2);
        _root.Children.Add(footer);

        Content = _root;
        Closed += (_, _) =>
        {
            if (!_closingAfterCompletion)
                _cancelled = true;
        };
    }

    public async Task<bool> RunAsync(
        Action beginCapture,
        Func<bool> isSingleFingerDown,
        Func<int> capturedFrameCount,
        Action<double>? progressChanged = null)
    {
        Activate();
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        await Task.Delay(350);
        _root.Focus(FocusState.Programmatic);

        beginCapture();
        var stages = new (string Name, string Instruction, int MinimumFrames)[]
        {
            ("第 1 档 · 慢速", "从触控板最左侧缓慢滑到最右侧，然后抬起", 20),
            ("第 2 档 · 正常速度", "从触控板最左侧以平时的速度滑到最右侧，然后抬起", 8),
            ("第 3 档 · 快速", "从触控板最左侧尽可能快地甩到最右侧，然后抬起", 3)
        };

        for (var stageIndex = 0; stageIndex < stages.Length && !_cancelled; stageIndex++)
        {
            var stage = stages[stageIndex];
            var accepted = false;
            while (!accepted && !_cancelled)
            {
                while (isSingleFingerDown() && !_cancelled)
                {
                    _stageText.Text = $"{stage.Name} · 请先抬起手指";
                    _hintText.Text = "下一档会在手指完全抬起后开始";
                    await Task.Delay(16);
                }
                if (_cancelled) break;

                MoveCursorToStartPosition();
                PositionBall(0);
                _stageText.Text = stage.Name;
                _hintText.Text = stage.Instruction;
                await Task.Delay(600);

                var framesBefore = capturedFrameCount();
                while (!isSingleFingerDown() && !_cancelled)
                    await Task.Delay(8);
                if (_cancelled) break;

                _stageText.Text = $"{stage.Name} · 正在记录";
                while (isSingleFingerDown() && !_cancelled)
                    await Task.Delay(8);
                if (_cancelled) break;

                var capturedFrames = capturedFrameCount() - framesBefore;
                if (capturedFrames < stage.MinimumFrames)
                {
                    _stageText.Text = $"{stage.Name} · 请重试";
                    _hintText.Text = "这次滑动距离太短，请确保从触控板最左侧完整滑到最右侧";
                    await Task.Delay(1000);
                    continue;
                }

                var travel = Math.Max(300, _canvas.ActualWidth - _ball.Width - 80);
                PositionBall(travel);
                accepted = true;
                _progress.Value = stageIndex + 1;
                progressChanged?.Invoke(stageIndex + 1);
                _stageText.Text = $"{stage.Name} · 完成";
                _hintText.Text = stageIndex + 1 < stages.Length ? "请抬起手指，准备下一档" : "三档滑动均已完成";
                await Task.Delay(700);
            }
        }

        if (_cancelled) return false;
        _stageText.Text = "采集完成";
        _hintText.Text = "正在计算这台电脑的原生速度曲线……";
        _progress.Value = 3;
        progressChanged?.Invoke(3);
        await Task.Delay(250);

        _closingAfterCompletion = true;
        Close();
        return true;
    }

    private void PositionBall(double x)
    {
        Canvas.SetLeft(_ball, 40 + x);
        Canvas.SetTop(_ball, Math.Max(20, (_canvas.ActualHeight - _ball.Height) / 2));
    }

    private void MoveCursorToStartPosition()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (!GetWindowRect(hwnd, out var rect)) return;
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        SetCursorPos(rect.Left + width / 10, rect.Top + height / 2);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Cancel();
    }

    private void Cancel()
    {
        _cancelled = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}
