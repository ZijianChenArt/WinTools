using System.Threading.Tasks;
using System.IO;
using WinTools.Services;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private async Task LoadOfficeDocumentAsync(string path, int token)
    {
        var rendered = await OfficePreviewRenderer.RenderAsync(path, _contentCancellation.Token);
        if (token != _loadToken) return;
        if (rendered.PdfPath is { } pdf)
        {
            ViewStatus.Text = "页面预览";
            await LoadPdfAsync(pdf, token, path);
            return;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var text = extension is ".docx" or ".pptx"
            ? await Task.Run(() => OfficeTextExtractor.Extract(path)) : null;
        if (token != _loadToken) return;
        if (text == null)
        {
            ShowFallback(path, "无法生成页面预览", rendered.Error ?? "请用默认程序打开此文档。");
            return;
        }
        SetTextPresentation(wrap: true, "Segoe UI");
        TextBody.Text = text;
        ViewStatus.Text = "文字预览 · " + (rendered.Error ?? "页面预览不可用");
        ShowOnly(TextView);
        FitTextWindow(760);
        UpdateFooter(path);
    }
}
