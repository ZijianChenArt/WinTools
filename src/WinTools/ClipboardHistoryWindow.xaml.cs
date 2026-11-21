using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinTools.Services;

namespace WinTools;

public sealed partial class ClipboardHistoryWindow : Window, IUiStyleShell
{
    private bool _shown;
    private bool _revealed;
    private int _transition;
    private double _popupScale = 1;
    private readonly PopupAnimator _animator;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    internal ClipboardHistoryWindow(ClipboardHistoryService history)
    {
        InitializeComponent();
        _animator = new PopupAnimator(this, slideDip: 24, fade: false);
        Closed += (_, _) =>
        {
            _transition++;
            _animator.Reset();
            ThemeService.Unregister(this);
            UiStyleService.Unregister(this);
        };
        History.Bind(history);
        History.Copied += Hide;
        History.CloseRequested += Hide;
        History.UsePopupToolbar();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        WindowHelper.EnablePersistentMica(this);
        WindowHelper.ConfigurePopupTitleBar(this, History.PopupDragRegion);
        WindowHelper.DisableWindowTransitions(this);
        ThemeService.Register(this);
        UiStyleService.Register(this);
        ApplyUiStyleSurfaces();
        AppWindow.Closing += (_, args) =>
        {
            if (App.IsShuttingDown) return;
            args.Cancel = true;
            Hide();
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) Hide();
        };
    }

    internal void Toggle((int CenterX, int Top)? anchor)
    {
        if (_shown) { Hide(); return; }
        var work = anchor is { } point
            ? DisplayArea.GetFromPoint(new PointInt32(point.CenterX, point.Top - 1), DisplayAreaFallback.Nearest).WorkArea
            : DisplayArea.Primary.WorkArea;
        var scale = WindowHelper.GetPopupScale(this, new PointInt32(anchor?.CenterX ?? (work.X + work.Width / 2),
            (anchor?.Top ?? (work.Y + work.Height)) - 1));
        _popupScale = scale;
        var width = Math.Min((int)(360 * scale), work.Width);
        var height = Math.Min((int)(400 * scale), work.Height);
        var gap = (int)Math.Round(WindowHelper.TaskbarPopupGapDip * scale);
        var x = anchor is { } button ? button.CenterX - width / 2 : work.X + work.Width - width - gap;
        var bottom = anchor?.Top ?? work.Y + work.Height;
        var restY = Math.Clamp(bottom - height - gap, work.Y, work.Y + work.Height - height);
        var animate = _uiSettings.AnimationsEnabled;
        var openingY = restY;
        if (animate)
        {
            // 快速重开时从收起动画的当前位置继续。
            var resume = _animator.IsHiding && _revealed;
            _animator.PrepareReveal(restY, scale);
            openingY = resume ? AppWindow.Position.Y : _animator.RevealStartY;
        }
        else _animator.Reset();
        if (!_revealed && animate) WindowHelper.SetWindowCloak(this, true);
        AppWindow.MoveAndResize(new RectInt32(
            Math.Clamp(x, work.X, work.X + work.Width - width), openingY, width, height));
        var transition = ++_transition;
        _shown = true;
        Activate();
        if (!animate) { _revealed = true; return; }
        if (_revealed) { _animator.PlayReveal(); return; }
        WindowHelper.WhenRendered(this, () =>
        {
            if (!_shown || transition != _transition) return;
            _revealed = true;
            WindowHelper.SetWindowCloak(this, false);
            _animator.PlayReveal();
        }, framesToWait: 1);
    }

    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        var isMica = UiStyleService.IsMica;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = WindowHelper.GetMicaPopupBrush(theme, isMica);
        var panelBackground = isMica
            ? WindowHelper.GetMicaPanelBrush(theme)
            : WindowHelper.GetMicaPopupBrush(theme, isMica, elevated: true);
        History.ApplyPopupSurface(panelBackground, WindowHelper.GetCodexBorderBrush(theme));
        WindowHelper.ApplyWindowBackdrop(this);
    }

    private void Hide()
    {
        if (!_shown) return;
        _shown = false;
        _transition++;
        if (_revealed && _uiSettings.AnimationsEnabled) _animator.PlayDismiss(_popupScale, HideImmediately);
        else
        {
            _animator.Reset();
            HideImmediately();
        }
    }

    private void HideImmediately()
    {
        AppWindow.Hide();
        WindowHelper.SetWindowCloak(this, false);
        _revealed = false;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        Hide();
    }
}
