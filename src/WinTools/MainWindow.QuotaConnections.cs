using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinTools.Services;

namespace WinTools;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _codexConnect, _claudeConnect;
    private ClaudeOAuthProtocol? _claudeFlow;
    private readonly CancellationTokenSource _quotaWindowLifetime = new();
    private bool _checkingCodex, _checkingClaude;

    private void InitializeQuotaConnections()
    {
        Closed += (_, _) => { _quotaWindowLifetime.Cancel(); _codexConnect?.Cancel(); _claudeConnect?.Cancel(); _claudeFlow = null; };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated || _claudeFlow == null) return;
            ConnectionStatus(false, _claudeFlow.IsExpired ? "本次授权已超时，请重新点击浏览器授权。" : "粘贴授权码后，点击“完成连接”。");
        };
    }

    private void ConnectionStatus(bool codex, string text) => TaskbarControl<SettingsCard>(codex ? "CodexConnectionCard" : "ClaudeConnectionCard").Description = text;

    internal async void QuotaConnect_Click(object sender, RoutedEventArgs e)
    {
        var codex = ((Button)sender).Tag as string == "codex";
        if (!codex)
        {
            if (_checkingClaude) return;
            try
            {
                var flow = new ClaudeOAuthProtocol();
                QuotaBrowser.Open(flow.AuthorizationUrl);
                _claudeFlow = flow;
                TaskbarControl<PasswordBox>("ClaudeAuthorizationCode").Password = "";
                TaskbarControl<StackPanel>("ClaudeCodeEntry").Visibility = Visibility.Visible;
                ConnectionStatus(false, "等待授权 · 完成后粘贴授权码");
            }
            catch (Exception ex) { ConnectionStatus(false, ConnectionError(ex)); }
            return;
        }
        if (_codexConnect != null || _checkingCodex) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_quotaWindowLifetime.Token);
        _codexConnect = cancellation;
        TaskbarControl<Button>("CodexConnectButton").IsEnabled = false;
        TaskbarControl<Button>("CodexCheckButton").IsEnabled = false;
        TaskbarControl<Button>("CodexCancelButton").Visibility = Visibility.Visible;
        ConnectionStatus(true, "等待授权 · 完成后自动检测");
        try
        {
            await Task.Run(() => CodexQuotaReader.LoginAsync(QuotaBrowser.Open, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_quotaWindowLifetime.IsCancellationRequested) { (App.Current as App)?.BringWindowToForeground(); await DetectConnectionAsync(true); }
        }
        catch (Exception ex) { if (!_quotaWindowLifetime.IsCancellationRequested) ConnectionStatus(true, ConnectionError(ex)); }
        finally
        {
            _codexConnect = null;
            if (!_quotaWindowLifetime.IsCancellationRequested)
            {
                TaskbarControl<Button>("CodexConnectButton").IsEnabled = true;
                TaskbarControl<Button>("CodexCheckButton").IsEnabled = true;
                TaskbarControl<Button>("CodexCancelButton").Visibility = Visibility.Collapsed;
            }
        }
    }

    internal async void QuotaCheck_Click(object sender, RoutedEventArgs e) => await DetectConnectionAsync(((Button)sender).Tag as string == "codex");

    private async Task DetectConnectionAsync(bool codex)
    {
        if (codex ? _checkingCodex : _checkingClaude) return;
        if (codex) _checkingCodex = true; else _checkingClaude = true;
        var button = TaskbarControl<Button>(codex ? "CodexCheckButton" : "ClaudeCheckButton");
        button.IsEnabled = false;
        ConnectionStatus(codex, "正在刷新…");
        try
        {
            var snapshot = codex
                ? await Task.Run(() => CodexQuotaReader.ReadAsync(_quotaWindowLifetime.Token))
                : await Task.Run(() => ClaudeAccountService.ReadAsync(_quotaWindowLifetime.Token));
            if (_quotaWindowLifetime.IsCancellationRequested) return;
            ConnectionStatus(codex, snapshot.Text.Replace(codex ? "Codex" : "Claude", "").Trim());
            ToolTipService.SetToolTip(TaskbarControl<SettingsCard>(codex ? "CodexConnectionCard" : "ClaudeConnectionCard"), snapshot.Detail);
            if (codex) (App.Current as App)?.TaskbarInfo?.Refresh();
            else (App.Current as App)?.TaskbarInfo?.SetClaudeSnapshot(snapshot);
        }
        catch (Exception ex) { if (!_quotaWindowLifetime.IsCancellationRequested) ConnectionStatus(codex, ConnectionError(ex)); }
        finally { if (codex) _checkingCodex = false; else _checkingClaude = false; button.IsEnabled = true; }
    }

    internal async void ClaudeComplete_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingClaude || _claudeFlow == null) return;
        var flow = _claudeFlow;
        var input = TaskbarControl<PasswordBox>("ClaudeAuthorizationCode");
        var code = input.Password;
        input.Password = "";
        _checkingClaude = true;
        SetClaudeBusy(true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_quotaWindowLifetime.Token);
        _claudeConnect = cancellation;
        ConnectionStatus(false, "正在连接…");
        try
        {
            var snapshot = await Task.Run(() => ClaudeAccountService.ConnectAsync(flow, code, cancellation.Token));
            if (_quotaWindowLifetime.IsCancellationRequested) return;
            _claudeFlow = null;
            TaskbarControl<StackPanel>("ClaudeCodeEntry").Visibility = Visibility.Collapsed;
            ConnectionStatus(false, snapshot.Text.Replace("Claude", "").Trim());
            ToolTipService.SetToolTip(TaskbarControl<SettingsCard>("ClaudeConnectionCard"), snapshot.Detail);
            (App.Current as App)?.TaskbarInfo?.SetClaudeSnapshot(snapshot);
        }
        catch (Exception ex) { if (!_quotaWindowLifetime.IsCancellationRequested) ConnectionStatus(false, ConnectionError(ex)); }
        finally { _claudeConnect = null; _checkingClaude = false; SetClaudeBusy(false); }
    }

    private void SetClaudeBusy(bool busy)
    {
        foreach (var name in new[] { "ClaudeConnectButton", "ClaudeCheckButton", "ClaudeDisconnectButton", "ClaudeCompleteButton" }) TaskbarControl<Button>(name).IsEnabled = !busy;
    }

    internal void QuotaCancel_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag as string == "codex") { _codexConnect?.Cancel(); return; }
        _claudeConnect?.Cancel();
        _claudeFlow = null;
        TaskbarControl<PasswordBox>("ClaudeAuthorizationCode").Password = "";
        TaskbarControl<StackPanel>("ClaudeCodeEntry").Visibility = Visibility.Collapsed;
        ConnectionStatus(false, "已取消连接");
    }

    internal async void ClaudeDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingClaude) return;
        _checkingClaude = true;
        SetClaudeBusy(true);
        try
        {
            await Task.Run(ClaudeAccountService.DisconnectAsync);
            _claudeFlow = null;
            TaskbarControl<PasswordBox>("ClaudeAuthorizationCode").Password = "";
            TaskbarControl<StackPanel>("ClaudeCodeEntry").Visibility = Visibility.Collapsed;
            ConnectionStatus(false, "已断开连接");
            (App.Current as App)?.TaskbarInfo?.SetClaudeSnapshot(new("Claude 额度暂不可用", "尚未连接 Claude"));
        }
        catch (Exception ex) { ConnectionStatus(false, ConnectionError(ex)); }
        finally { _checkingClaude = false; SetClaudeBusy(false); }
    }

    // Never surface raw HTTP responses, authorization URLs or credential-store errors.
    private static string ConnectionError(Exception ex) => ex switch
    {
        OperationCanceledException => "操作已取消或超时，请重新连接或检测。",
        InvalidOperationException => ex.Message,
        HttpRequestException => "网络连接失败，请检查网络后重试。",
        _ => "验证失败，请重试；若仍失败，请检查安装、Windows 凭据存储或服务是否可用。"
    };
}
