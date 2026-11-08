using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.System;
using WinTools.Services;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private const string ModelHost = "wintools-model.local";
    private const string ModelFilesHost = "wintools-model-files.local";
    private Task<CoreWebView2>? _webInitialization;
    private TaskCompletionSource<bool>? _webReady;
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _webStarting;
    private TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _webCompleted;
    private TypedEventHandler<CoreWebView2, CoreWebView2DOMContentLoadedEventArgs>? _webDomReady;
    private bool _modelActive, _modelReady;
    private string? _modelSession, _modelUri, _webUri;
    private int _modelBlockedRequests;

    // One lazy initialization, shared by HTML and models, including script injection.
    private async Task<CoreWebView2> EnsurePreviewWebViewAsync()
    {
        try { return await (_webInitialization ??= InitializePreviewWebViewAsync()); }
        catch { _webInitialization = null; throw; }
    }

    private async Task<CoreWebView2> InitializePreviewWebViewAsync()
    {
        await HtmlView.EnsureCoreWebView2Async();
        var core = HtmlView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 unavailable");
        core.NewWindowRequested += (_, args) => args.Handled = true;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(HtmlKeyScript);
        core.WebMessageReceived += OnPreviewWebMessage;
        core.NavigationStarting += (_, args) =>
        {
            if (_modelActive && !string.Equals(args.Uri, _modelUri, StringComparison.Ordinal)) args.Cancel = true;
            else if (_currentKind == Kind.Html) _webUri = args.Uri;
        };
        core.WebResourceRequested += OnModelResourceRequested;
        return core;
    }

    private Task LoadHtmlAsync(string path, int token) => LoadWebPreviewAsync(path, token, model: false);
    private Task LoadModelAsync(string path, int token) => LoadWebPreviewAsync(path, token, model: true);

    private async Task LoadWebPreviewAsync(string path, int token, bool model)
    {
        var cancellation = _contentCancellation.Token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(model ? 30 : 10));
        try
        {
            if (model && _currentFileInfo?.Length > 128L * 1024 * 1024)
            {
                ShowFallback(path, "模型较大", "快速预览支持 128 MB 以内的模型。请用默认程序打开。");
                return;
            }
            var core = await EnsurePreviewWebViewAsync().WaitAsync(timeout.Token);
            if (token != _loadToken) return;
            var dark = ThemeService.EffectiveTheme == ElementTheme.Dark;
            HtmlView.DefaultBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 245, 245, 245);
            Uri target;
            if (model)
            {
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "ModelPreview");
                if (!File.Exists(Path.Combine(assets, "viewer.js"))) throw new FileNotFoundException("Missing model viewer");
                core.SetVirtualHostNameToFolderMapping(ModelHost, assets, CoreWebView2HostResourceAccessKind.DenyCors);
                core.SetVirtualHostNameToFolderMapping(ModelFilesHost, Path.GetDirectoryName(Path.GetFullPath(path))!, CoreWebView2HostResourceAccessKind.Allow);
                _modelSession = Guid.NewGuid().ToString("N");
                target = new Uri($"https://{ModelHost}/index.html?session={_modelSession}&file={Uri.EscapeDataString(Path.GetFileName(path))}&dark={(dark ? 1 : 0)}");
                _modelUri = target.AbsoluteUri;
                _modelActive = true;
                _modelReady = false;
                _modelBlockedRequests = 0;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            }
            else target = ToFileUri(path);
            _webUri = target.AbsoluteUri;

            ClearWebNavigation();
            var ready = _webReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ulong navigationId = 0;
            _webStarting = (_, args) =>
            {
                if (token == _loadToken && string.Equals(args.Uri, target.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) navigationId = args.NavigationId;
            };
            _webDomReady = (_, args) =>
            {
                if (token != _loadToken || navigationId == 0 || args.NavigationId != navigationId || model) return;
                // Local HTML can be read as soon as its DOM exists; images may continue loading.
                ShowOnly(HtmlView);
                RevealWhenReady();
                ready.TrySetResult(true);
            };
            _webCompleted = (_, args) =>
            {
                if (token != _loadToken || navigationId == 0 || args.NavigationId != navigationId) return;
                if (!args.IsSuccess) ready.TrySetResult(false);
                else if (!model) ready.TrySetResult(true);
            };
            core.NavigationStarting += _webStarting;
            core.DOMContentLoaded += _webDomReady;
            core.NavigationCompleted += _webCompleted;
            HtmlView.Source = target;
            var succeeded = await ready.Task.WaitAsync(timeout.Token);
            if (token != _loadToken) return;
            ClearWebNavigation();
            if (succeeded) { ShowOnly(HtmlView); RevealWhenReady(); }
            else ShowFallback(path, model ? "无法显示模型" : "网页加载失败", "请用默认程序打开此文件。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || token != _loadToken) { }
        catch (OperationCanceledException)
        {
            if (token == _loadToken) ShowFallback(path, model ? "模型加载超时" : "网页加载超时", "请用默认程序打开此文件。");
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.Web", ex.GetType().Name);
            if (token == _loadToken) ShowFallback(path, model ? "无法显示模型" : "无法显示网页", "文件格式或 WebView2 组件暂不可用。请用默认程序打开。");
        }
    }

    private void OnPreviewWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!_isOpen || _currentKind is not (Kind.Html or Kind.Model)
            || !string.Equals(args.Source, _webUri, StringComparison.Ordinal)) return;
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            if (message.RootElement.ValueKind == JsonValueKind.String)
            {
                OnHtmlKeyMessage(message.RootElement.GetString());
                return;
            }
            var data = message.RootElement;
            if (!_modelActive || !data.TryGetProperty("session", out var session) || session.GetString() != _modelSession) return;
            if (data.GetProperty("type").GetString() == "model-ready" && !_modelReady)
            {
                _modelReady = true;
                var meshes = data.GetProperty("meshes").GetInt32();
                var triangles = data.GetProperty("triangles").GetInt32();
                ViewStatus.Text = $"3D · {meshes} 个网格 · {triangles:N0} 三角面";
                ShowOnly(HtmlView);
                RevealWhenReady();
                _webReady?.TrySetResult(true);
            }
            else if (data.GetProperty("type").GetString() == "model-error") _webReady?.TrySetResult(false);
        }
        catch (Exception ex) { ErrorReporter.Log("FilePreviewWindow.WebMessage", ex.GetType().Name); }
    }

    private void OnModelResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!_modelActive) return;
        if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)
            && (uri.Scheme is "data" or "blob" || uri.Scheme == "https" && uri.IsDefaultPort && uri.Host is ModelHost or ModelFilesHost)) return;
        _modelBlockedRequests++;
        args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
    }

    private void ClearWebNavigation()
    {
        var core = HtmlView.CoreWebView2;
        if (core != null)
        {
            if (_webStarting != null) core.NavigationStarting -= _webStarting;
            if (_webCompleted != null) core.NavigationCompleted -= _webCompleted;
            if (_webDomReady != null) core.DOMContentLoaded -= _webDomReady;
        }
        _webStarting = null; _webCompleted = null; _webDomReady = null;
        _webReady?.TrySetCanceled();
        _webReady = null;
    }

    private void ReleaseWebPreview()
    {
        HtmlView.Visibility = Visibility.Collapsed;
        try
        {
            ClearWebNavigation();
            var core = HtmlView.CoreWebView2;
            if (core != null)
            {
                core.Stop();
                if (_modelActive)
                {
                    core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    core.ClearVirtualHostNameToFolderMapping(ModelFilesHost);
                }
                _modelActive = false; _modelReady = false; _modelSession = _modelUri = _webUri = null;
                HtmlView.Source = new Uri("about:blank");
            }
        }
        catch (Exception ex) { ErrorReporter.Log("FilePreviewWindow.ReleaseWeb", ex.GetType().Name); }
        finally { _modelActive = false; _modelReady = false; _modelSession = _modelUri = _webUri = null; }
    }

    private const string HtmlKeyScript = @"
document.addEventListener('keydown', function (e) {
  var target = e.target;
  if (e.key !== 'Escape' && target && (target.isContentEditable || target.closest('input,textarea,select,button,a,[role=""slider""]'))) return;
  if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
  if (['Escape', ' ', 'ArrowLeft', 'ArrowRight', 'Enter'].includes(e.key)) {
    e.preventDefault(); window.chrome.webview.postMessage(e.key);
  }
}, true);";

    private void OnHtmlKeyMessage(string? key)
    {
        var mapped = key switch { "Escape" => VirtualKey.Escape, " " => VirtualKey.Space,
            "ArrowLeft" => VirtualKey.Left, "ArrowRight" => VirtualKey.Right, "Enter" => VirtualKey.Enter, _ => VirtualKey.None };
        if (mapped != VirtualKey.None) HandleKey(mapped);
    }
}
