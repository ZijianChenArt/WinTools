using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinTools.Services;

/// <summary>Runs only in the isolated STA preview worker; opens an owned copy and exports its layout to PDF.</summary>
internal static class OfficePreviewWorker
{
    private static string _stage = "prepare";

    internal static int Run(string jobDirectory)
    {
        _stage = "validate-source";
        var success = false;
        string? error = null;
        OfficePreviewBackend? backend = null;
        try
        {
            jobDirectory = Path.GetFullPath(jobDirectory);
            ThrowIfCancelled(jobDirectory);
            var sources = Directory.EnumerateFiles(jobDirectory, "source.*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).ToLowerInvariant() is "source.doc" or "source.docx" or "source.ppt" or "source.pptx").ToArray();
            if (sources.Length != 1) throw new InvalidOperationException("Invalid preview source");
            if (Path.GetExtension(sources[0]).ToLowerInvariant() is ".docx" or ".pptx")
            {
                using var input = File.OpenRead(sources[0]);
                Span<byte> signature = stackalloc byte[4];
                if (input.Read(signature) != 4 || !signature.SequenceEqual(new byte[] { 0x50, 0x4b, 3, 4 }))
                    throw new PreviewException("此文档已加密或格式无效，无法生成原版式预览。");
            }
            var word = Path.GetExtension(sources[0]).ToLowerInvariant() is ".doc" or ".docx";
            var requested = File.ReadAllText(Path.Combine(jobDirectory, "engine.txt")).Trim();
            backend = OfficePreviewBackend.Find(word).FirstOrDefault(candidate => candidate.Id == requested)
                ?? throw new PreviewException("未找到可用的 WPS 或 Microsoft Office 预览组件。");
            using (var session = new Session(word, jobDirectory, backend)) session.Export(sources[0]);
            _stage = "verify-pdf";
            using var pdf = File.OpenRead(Path.Combine(jobDirectory, "preview.pdf"));
            Span<byte> header = stackalloc byte[5];
            if (pdf.Read(header) != header.Length || !header.SequenceEqual("%PDF-"u8))
                throw new InvalidDataException("Invalid preview output");
            success = true;
        }
        catch (Exception ex)
        {
            // Office errors can quote document text or paths; only the exception category/code leaves the worker.
            ErrorReporter.Log("OfficePreview.Worker", $"{_stage}: {ex.GetType().Name} (0x{ex.HResult:X8})");
#if DEBUG
            if (Environment.GetEnvironmentVariable("WINTOOLS_OFFICE_WORKER_DIAGNOSTICS") == "1")
            {
                try { File.WriteAllText(Path.Combine(jobDirectory, "failure-debug.txt"), _stage + Environment.NewLine + ex); }
                catch { }
            }
#endif
            error = ex is PreviewException ? ex.Message : "Office 无法生成文档预览。";
        }
        try { WriteJson(Path.Combine(jobDirectory, "result.json"), new { Success = success, Error = error, Renderer = backend?.Name, RendererId = backend?.Id }); }
        catch (Exception ex) { ErrorReporter.Log("OfficePreview.WorkerResult", ex.GetType().Name); return 1; }
        return success ? 0 : 1;
    }

    private sealed class Session : IDisposable
    {
        private readonly bool _word;
        private readonly string _directory;
        private readonly OfficePreviewBackend _backend;
        private readonly HashSet<int> _previousProcesses = new();
        private object? _application, _collection, _document, _bootstrap, _options;
        private object? _oldSecurity, _oldAlerts, _oldUpdateLinks;
        private bool _securityChanged, _alertsChanged, _linksChanged, _newInstance;
        private int _processId;
        private long _processStartTicks;
        private DateTime _activationStarted;

        internal Session(bool word, string directory, OfficePreviewBackend backend) { _word = word; _directory = directory; _backend = backend; }

        internal void Export(string source)
        {
            foreach (var process in Process.GetProcessesByName(_backend.ProcessName))
            {
                using (process) _previousProcesses.Add(process.Id);
            }
            _activationStarted = DateTime.UtcNow;
            ThrowIfCancelled(_directory);
            _stage = "activate-office";
            try
            {
                _application = _backend.CreateApplication();
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("OfficePreview.Activation", $"{ex.GetType().Name} (0x{ex.HResult:X8})");
                throw new PreviewException(_backend.Name + " 暂时无法生成页面预览。");
            }
            dynamic app = _application;
            ThrowIfCancelled(_directory);
            _stage = "configure-security";
            _oldSecurity = app.AutomationSecurity;
            app.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
            _securityChanged = true;
            _stage = "configure-alerts";
            _oldAlerts = app.DisplayAlerts;
            app.DisplayAlerts = _word ? 0 : 1; // wdAlertsNone / ppAlertsNone
            _alertsChanged = true;
            var output = Path.Combine(_directory, "preview.pdf");
            if (_word) ExportWord(source, output); else ExportPowerPoint(source, output);
        }

        private void ExportWord(string source, string output)
        {
            dynamic app = _application!;
            _collection = app.Documents;
            dynamic documents = _collection;
            if (_backend.IsWps)
            {
                // WPS may share its frontend with the user's documents; no bootstrap window or global visibility change.
                _document = documents.Open(source, false, true, false, Type.Missing, Type.Missing, false,
                    Type.Missing, Type.Missing, 0, Type.Missing, false);
                TryRecordWpsWordOwner();
                RestoreSecurity();
                ThrowIfCancelled(_directory);
                ((dynamic)_document).ExportAsFixedFormat(output, 17);
                return;
            }
            // Word has no useful HWND before a document window exists. This empty document belongs only to us.
            _bootstrap = documents.Add(Type.Missing, false, 0, true);
            object? windows = null, window = null;
            try
            {
                windows = ((dynamic)_bootstrap).Windows;
                window = ((dynamic)windows)[1];
                RecordOwner(new IntPtr(Convert.ToInt64(((dynamic)window).Hwnd)));
            }
            finally { Release(window); Release(windows); }
            ((dynamic)_bootstrap).Close(0);
            Release(_bootstrap);
            _bootstrap = null;
            if (_newInstance)
            {
                app.Visible = false;
                _options = app.Options;
                _oldUpdateLinks = ((dynamic)_options).UpdateLinksAtOpen;
                ((dynamic)_options).UpdateLinksAtOpen = false;
                _linksChanged = true;
            }
            // Supply password arguments so encrypted files fail rather than opening an interactive password prompt.
            const string noPassword = "WtPreview!42";
            _document = documents.Open(source, false, true, false, noPassword, noPassword, false,
                noPassword, noPassword, 0, Type.Missing, false, false, Type.Missing, true, Type.Missing);
            RestoreSecurity();
            ThrowIfCancelled(_directory);
            ((dynamic)_document).ExportAsFixedFormat(output, 17, false); // wdExportFormatPDF
        }

        private void ExportPowerPoint(string source, string output)
        {
            dynamic app = _application!;
            _stage = "powerpoint-window";
            if (!_backend.IsWps)
            {
                var hwnd = IntPtr.Zero;
                try { hwnd = new IntPtr(Convert.ToInt64(app.HWND)); }
                catch (MissingMemberException) { }
                catch (COMException ex) when ((uint)ex.HResult is 0x80020003 or 0x80020006) { }
                if (hwnd != IntPtr.Zero) RecordOwner(hwnd); else TryRecordHiddenPowerPoint();
            }
            _stage = "powerpoint-collection";
            _collection = app.Presentations;
            // Hiding PowerPoint.Application is unsupported. WithWindow=false keeps this presentation hidden.
            _stage = "powerpoint-open";
            _document = ((dynamic)_collection).Open(source, -1, 0, 0);
            RestoreSecurity();
            ThrowIfCancelled(_directory);
            _stage = "powerpoint-save-pdf";
            ((dynamic)_document).SaveAs(output, 32); // ppSaveAsPDF; only our copied presentation is affected.
        }

        private void RecordOwner(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0)
                throw new InvalidOperationException("Office process could not be identified");
            using var process = Process.GetProcessById(checked((int)processId));
            RecordOwner(process);
        }

        private void TryRecordWpsWordOwner()
        {
            object? windows = null, window = null;
            try
            {
                windows = ((dynamic)_document!).Windows;
                if (Convert.ToInt32(((dynamic)windows).Count) != 1) return;
                window = ((dynamic)windows)[1];
                var hwnd = new IntPtr(Convert.ToInt64(((dynamic)window).Hwnd));
                if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0) return;
                using var process = Process.GetProcessById(checked((int)processId));
                if (_previousProcesses.Contains(process.Id) || process.StartTime.ToUniversalTime() < _activationStarted
                    || !string.Equals(process.MainModule?.FileName, _backend.Executable, StringComparison.OrdinalIgnoreCase)) return;
                _processId = process.Id;
                _processStartTicks = process.StartTime.ToUniversalTime().Ticks;
                _newInstance = true;
                // WPS ownership permits a guarded COM Quit only; never publish it as a force-kill target.
            }
            catch (Exception ex) { LogCleanup(ex); }
            finally { Release(window); Release(windows); }
        }

        private bool HasVisibleOwnerWindow()
        {
            var visible = false;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                if (processId == _processId && IsWindowVisible(hwnd)) visible = true;
                return !visible;
            }, IntPtr.Zero);
            return visible;
        }

        private void RecordOwner(Process process)
        {
            var executable = process.MainModule?.FileName ?? "";
            var expected = _word ? "WINWORD.EXE" : "POWERPNT.EXE";
            _processId = process.Id;
            _processStartTicks = process.StartTime.ToUniversalTime().Ticks;
            _newInstance = !_previousProcesses.Contains(process.Id)
                && string.Equals(Path.GetFileName(executable), expected, StringComparison.OrdinalIgnoreCase);
            WriteJson(Path.Combine(_directory, "owner.json"), new
            {
                ProcessId = process.Id, StartTimeUtcTicks = _processStartTicks,
                ExecutablePath = executable, NewInstance = _newInstance,
            });
        }

        private void TryRecordHiddenPowerPoint()
        {
            // Hidden automation often has HWND=0. Only an unambiguous new automation server can be owned.
            if (_previousProcesses.Count != 0) return;
            var candidates = Process.GetProcessesByName("POWERPNT");
            try
            {
                if (candidates.Length != 1) return;
                var process = candidates[0];
                if (process.StartTime.ToUniversalTime() < _activationStarted) return;
                var executable = process.MainModule?.FileName;
                if (!string.Equals(Path.GetFileName(executable), "POWERPNT.EXE", StringComparison.OrdinalIgnoreCase)) return;
                var applicationPath = (string)((dynamic)_application!).Path;
                if (!string.Equals(Path.GetDirectoryName(executable)?.TrimEnd('\\'), applicationPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                using var query = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={process.Id}");
                using var matches = query.Get();
                foreach (ManagementObject match in matches)
                {
                    using (match)
                    {
                        var arguments = match["CommandLine"] as string ?? "";
                        if (!System.Text.RegularExpressions.Regex.IsMatch(arguments, @"(?i)(?:^|\s)[/-](?:automation|embedding)(?:\s|$)")) continue;
                        RecordOwner(process);
                        return;
                    }
                }
            }
            catch (Exception ex) { LogCleanup(ex); } // Unproven ownership must not prevent a read-only conversion.
            finally { foreach (var process in candidates) process.Dispose(); }
        }

        private void RestoreSecurity()
        {
            if (!_securityChanged || _application == null) return;
            try { ((dynamic)_application).AutomationSecurity = _oldSecurity; _securityChanged = false; }
            catch (Exception ex) { LogCleanup(ex); }
        }

        public void Dispose()
        {
            CloseOwnedDocument(ref _document);
            CloseOwnedDocument(ref _bootstrap);
            RestoreSecurity();
            if (_linksChanged && _options != null)
            {
                try { ((dynamic)_options).UpdateLinksAtOpen = _oldUpdateLinks; }
                catch (Exception ex) { LogCleanup(ex); }
            }
            if (_alertsChanged && _application != null)
            {
                try { ((dynamic)_application).DisplayAlerts = _oldAlerts; }
                catch (Exception ex) { LogCleanup(ex); }
            }
            if (_newInstance && _application != null && _collection != null && IsSameProcess())
            {
                try
                {
                    if (Convert.ToInt32(((dynamic)_collection).Count) == 0)
                    {
                        if (!_backend.IsWps) ((dynamic)_application).Quit();
                        else if (_word && !HasVisibleOwnerWindow() && !Convert.ToBoolean(((dynamic)_application).Visible))
                            ((dynamic)_application).Quit(0);
                    }
                }
                catch (Exception ex) { LogCleanup(ex); }
            }
            Release(_options); Release(_collection); Release(_application);
            _options = _collection = _application = null;
        }

        private void CloseOwnedDocument(ref object? document)
        {
            if (document == null) return;
            try
            {
                if (_word) ((dynamic)document).Close(0); else ((dynamic)document).Close();
            }
            catch (Exception ex) { LogCleanup(ex); }
            finally { Release(document); document = null; }
        }

        private bool IsSameProcess()
        {
            try
            {
                using var process = Process.GetProcessById(_processId);
                return process.StartTime.ToUniversalTime().Ticks == _processStartTicks;
            }
            catch { return false; }
        }
    }

    private static void WriteJson(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class PreviewException(string message) : Exception(message) { }

    private static void ThrowIfCancelled(string directory)
    {
        if (File.Exists(Path.Combine(directory, "cancel.flag"))) throw new OperationCanceledException();
    }

    private static void LogCleanup(Exception ex) =>
        ErrorReporter.Log("OfficePreview.WorkerCleanup", $"{ex.GetType().Name} (0x{ex.HResult:X8})");

    private static void Release(object? value)
    {
        try { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        catch (Exception ex) { LogCleanup(ex); }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
}
