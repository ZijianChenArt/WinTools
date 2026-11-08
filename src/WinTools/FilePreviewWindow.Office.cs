using System;
using System.Threading.Tasks;
using System.IO;
using Microsoft.UI.Xaml;
using WinTools.Services;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private string? _officePreviewBackend;

    private async Task LoadOfficeDocumentAsync(string path, int token)
    {
        _officePreviewBackend = null;
        var cancellation = _contentCancellation.Token;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var textTask = extension is ".docx" or ".pptx"
            ? Task.Run(() => OfficeTextExtractor.Extract(path), cancellation) : Task.FromResult<string?>(null);
        var renderTask = OfficePreviewRenderer.RenderAsync(path, cancellation);
        await Task.WhenAny(textTask, renderTask);
        if (token != _loadToken) return;
        string? text = null;
        if (textTask.IsCompletedSuccessfully) text = textTask.Result;
        if (!renderTask.IsCompleted)
        {
            if (text != null) ShowOfficeText(text, "文字预览 · 正在加载页面…");
            else
            {
                // This is an interim view, not a failure: keep the current load token and renderer alive.
                FallbackTitle.Text = Path.GetFileName(path);
                FallbackHint.Text = "正在准备页面预览，完成后会自动显示。";
                ShowOnly(FallbackPanel);
                SetPreviewBounds(520, 280);
                _ = LoadFallbackIconAsync(path, token);
            }
        }
        var rendered = await renderTask;
        if (token != _loadToken) return;
        _officePreviewBackend = rendered.Renderer;
        if (rendered.PdfPath is { } pdf)
        {
            try
            {
                await LoadPdfAsync(pdf, token, path);
                if (token != _loadToken) return;
                ViewStatus.Text = "页面预览";
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (token != _loadToken) return;
                ErrorReporter.Log("OfficePreview.Page", ex);
                _pdf = null;
                _pdfPath = null;
                rendered = new(null, "页面暂时无法显示");
            }
        }
        text ??= await textTask;
        if (token != _loadToken) return;
        if (text == null)
        {
            ShowFallback(path, "无法生成页面预览", rendered.Error ?? "请用默认程序打开此文档。");
            return;
        }
        ShowOfficeText(text, "文字预览 · " + (rendered.Error ?? "页面预览不可用"));
        UpdateFooter(path);
    }

    private void ShowOfficeText(string text, string status)
    {
        SetTextPresentation(wrap: true, "Segoe UI");
        TextBody.Text = string.IsNullOrWhiteSpace(text) ? "此文档没有可提取的文字。" : text;
        ViewStatus.Text = status;
        ShowOnly(TextView);
        FitTextWindow(760);
    }
}
