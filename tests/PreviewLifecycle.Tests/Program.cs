using System.Reflection;
using Microsoft.UI.Dispatching;
using WinTools;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
Thread? Worker(ExplorerPreviewService service) => (Thread?)typeof(ExplorerPreviewService).GetField("_workerThread", flags)!.GetValue(service);
Thread? Hook(ExplorerPreviewService service) => (Thread?)typeof(ExplorerPreviewService).GetField("_hookThread", flags)!.GetValue(service);
var service = new ExplorerPreviewService(new DispatcherQueue()) { Enabled = false };
Thread? originalWorker = null;
for (var i = 0; i < 12; i++)
{
    service.Start();
    var worker = Worker(service) ?? throw new Exception("No selection worker");
    originalWorker ??= worker;
    if (!ReferenceEquals(worker, originalWorker) || !worker.IsAlive) throw new Exception("Restart multiplied or lost the parked worker");
    var hook = Hook(service) ?? throw new Exception("No hook thread");
    service.Stop();
    if (hook.IsAlive) throw new Exception("Stopped hook thread is still alive");
}
service.Dispose();
if (originalWorker!.IsAlive) throw new Exception("Dispose left a waiting selection worker");
service.Dispose();
try { service.Start(); throw new Exception("Disposed service restarted"); }
catch (ObjectDisposedException) { }
Console.WriteLine("Preview hook start/stop x12 reused one worker; disposal released worker and hooks; restart after disposal rejected.");

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue { public bool TryEnqueue(Action callback) { callback(); return true; } }
}
namespace WinTools
{
    internal static class FilePreviewService
    {
        internal static void ShowPending() => throw new Exception("Lifecycle test must not operate the UI");
        internal static void ShowSelection(IReadOnlyList<string> paths) => throw new Exception("Lifecycle test must not operate the UI");
    }
}
namespace WinTools.Services
{
    internal static class ErrorReporter { internal static void Log(string scope, Exception error) => throw new Exception(scope, error); }
}
