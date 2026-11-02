using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinTools.Services;

internal readonly record struct OfficePreviewResult(string? PdfPath, string? Error);

/// <summary>Local Office-to-PDF rendering in a cancellable worker, with a cache keyed by the copied file's contents.</summary>
internal static class OfficePreviewRenderer
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTools", "OfficePreview");

    internal static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".doc" or ".docx" or ".ppt" or ".pptx";

    internal static string? GetWorkerDirectory(string jobId) =>
        Guid.TryParseExact(jobId, "N", out _) ? Path.Combine(CacheRoot, jobId) : null;

    internal static async Task<OfficePreviewResult> RenderAsync(string sourcePath, CancellationToken cancellation)
    {
        if (!IsSupported(sourcePath)) return new(null, "此类型暂不支持原版式预览");
        await RenderGate.WaitAsync(cancellation);
        string? jobDirectory = null;
        Process? worker = null;
        try
        {
            Directory.CreateDirectory(CacheRoot);
            PruneCache();
            var jobId = Guid.NewGuid().ToString("N");
            jobDirectory = Path.Combine(CacheRoot, jobId);
            Directory.CreateDirectory(jobDirectory);
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            var snapshot = Path.Combine(jobDirectory, "source" + extension);
            var before = new FileInfo(sourcePath);
            var originalLength = before.Length;
            var originalWriteTime = before.LastWriteTimeUtc;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes("office-pdf-v1|" + extension + "|"));
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    hash.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
            }
            CopyZoneInformation(sourcePath, snapshot);
            var after = new FileInfo(sourcePath);
            if (after.Length != originalLength || after.LastWriteTimeUtc != originalWriteTime)
                return new(null, "文档正在更新，请稍后重新预览");

            var cacheDirectory = Path.Combine(CacheRoot, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            var cachedPdf = Path.Combine(cacheDirectory, "preview.pdf");
            if (IsPdf(cachedPdf))
            {
                Directory.SetLastWriteTimeUtc(cacheDirectory, DateTime.UtcNow);
                return new(cachedPdf, null);
            }

            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位预览程序");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("--render-office");
            start.ArgumentList.Add(jobId);
            // Debug render capture variables must never launch another capture inside the worker.
            start.Environment.Remove("WINTOOLS_PREVIEW_QA_DIR");
            start.Environment.Remove("WINTOOLS_NAV_QA_DIR");
            start.Environment.Remove("WINTOOLS_NAV_QA_PAGE");
            worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动文档预览进程");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await worker.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                StopWorker(worker, jobDirectory);
                cancellation.ThrowIfCancellationRequested();
                return new(null, "原版式预览超时");
            }

            cancellation.ThrowIfCancellationRequested();
            var pdf = Path.Combine(jobDirectory, "preview.pdf");
            if (worker.ExitCode != 0 || !IsPdf(pdf))
            {
                var error = "无法生成原版式预览";
                var resultPath = Path.Combine(jobDirectory, "result.json");
                if (File.Exists(resultPath))
                {
                    using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
                    if (result.RootElement.TryGetProperty("Error", out var detail) && detail.GetString() is { Length: > 0 } message)
                        error = message;
                }
                return new(null, error);
            }

            // Publish only a completed PDF. The private Office copy is no longer needed.
            TryDeleteFile(snapshot);
            TryDeleteFile(Path.Combine(jobDirectory, "owner.json"));
            if (Directory.Exists(cacheDirectory)) DeleteOwnedDirectory(cacheDirectory);
            if (Directory.Exists(cacheDirectory))
            {
                jobDirectory = null; // Preserve this completed job if an old cache directory is still locked.
                return new(pdf, null);
            }
            try { Directory.Move(jobDirectory, cacheDirectory); }
            catch (IOException)
            {
                jobDirectory = null;
                return new(pdf, null);
            }
            jobDirectory = null;
            return new(cachedPdf, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorReporter.Log("OfficePreview.Render", ex.GetType().Name + ": " + ex.Message);
            return new(null, "原版式预览暂不可用");
        }
        finally
        {
            if (worker != null)
            {
                if (!worker.HasExited && jobDirectory != null) StopWorker(worker, jobDirectory);
                worker.Dispose();
            }
            if (jobDirectory != null) DeleteOwnedDirectory(jobDirectory);
            RenderGate.Release();
        }
    }

    private static bool IsPdf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5];
            return stream.Read(header) == header.Length && header.SequenceEqual("%PDF-"u8);
        }
        catch { return false; }
    }

    private static void PruneCache()
    {
        try
        {
            var directories = new DirectoryInfo(CacheRoot).EnumerateDirectories()
                .OrderByDescending(directory => directory.LastWriteTimeUtc).ToArray();
            for (var index = 0; index < directories.Length; index++)
                if (index >= 24 || directories[index].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                    DeleteOwnedDirectory(directories[index].FullName);
        }
        catch { /* Busy previews remain cached until a later visit. */ }
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var name = Path.GetFileName(full);
            var allowedName = Guid.TryParseExact(name, "N", out _) || name.Length == 64 && name.All(Uri.IsHexDigit);
            if (!allowedName || !string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(CacheRoot), StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(full) && !new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(full, true);
        }
        catch { /* Never force deletion of a document still held by Office or PdfDocument. */ }
    }

    private static void TryDeleteFile(string file) { try { File.Delete(file); } catch { } }

    private static void CopyZoneInformation(string source, string destination)
    {
        try
        {
            // Preserve Windows' downloaded-file marker on the private copy.
            using var zone = File.OpenRead(source + ":Zone.Identifier");
            if (zone.Length > 64 * 1024) return;
            using var target = File.Create(destination + ":Zone.Identifier");
            zone.CopyTo(target);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (NotSupportedException) { }
    }

    private static void StopWorker(Process worker, string jobDirectory)
    {
        try { if (!worker.HasExited) worker.Kill(); } catch { }
        // Only reap a proven new renderer with matching identity and no visible user windows.
        try
        {
            using var owner = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDirectory, "owner.json")));
            var data = owner.RootElement;
            if (!data.GetProperty("NewInstance").GetBoolean()) return;
            using var office = Process.GetProcessById(data.GetProperty("ProcessId").GetInt32());
            if (office.StartTime.ToUniversalTime().Ticks != data.GetProperty("StartTimeUtcTicks").GetInt64()
                || !string.Equals(office.MainModule?.FileName, data.GetProperty("ExecutablePath").GetString(), StringComparison.OrdinalIgnoreCase)) return;
            var visible = false;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                if (processId == office.Id && IsWindowVisible(hwnd)) visible = true;
                return !visible;
            }, IntPtr.Zero);
            if (!visible && !office.HasExited) office.Kill();
        }
        catch { /* Unknown or reused Office instances belong to the user. */ }
    }

    private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
