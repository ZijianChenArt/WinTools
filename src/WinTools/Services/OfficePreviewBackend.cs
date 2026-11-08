using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinTools.Services;

internal sealed record OfficePreviewBackend(string Id, string Name, bool IsWps, Guid ClassId, uint Context, string Executable)
{
    internal string ProcessName => Path.GetFileNameWithoutExtension(Executable);
    internal string CacheIdentity => Id + ":" + Executable + ":" + File.GetLastWriteTimeUtc(Executable).Ticks;

    internal object CreateApplication()
    {
        var classId = ClassId;
        var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, Context, ref dispatch, out var value));
        try { return Marshal.GetObjectForIUnknown(value); }
        finally { Marshal.Release(value); }
    }

    internal static IReadOnlyList<OfficePreviewBackend> Find(bool word)
    {
        var found = new List<OfficePreviewBackend>();
        Add(found, word ? "KWPS.Application" : "KWPP.Application", true, word);
        Add(found, word ? "Word.Application" : "PowerPoint.Application", false, word);
        return found;
    }

    private static void Add(List<OfficePreviewBackend> found, string progId, bool wps, bool word)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using var type = root.OpenSubKey(progId + "\\CLSID");
                if (!Guid.TryParse(type?.GetValue("") as string, out var classId)) continue;
                using var server = root.OpenSubKey("CLSID\\{" + classId + "}\\LocalServer32");
                var command = Environment.ExpandEnvironmentVariables(server?.GetValue("") as string ?? "").Trim();
                var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end < 0) continue;
                var executable = command[..(end + 4)].Trim('"', ' ');
                if (!File.Exists(executable)) continue;
                // WPS can also register Microsoft's ProgIDs in the 32-bit registry. Do not label that as Office.
                if (!wps && !string.Equals(Path.GetFileName(executable), word ? "WINWORD.EXE" : "POWERPNT.EXE", StringComparison.OrdinalIgnoreCase)) continue;
                var bits = view == RegistryView.Registry32 ? "32" : "64";
                found.Add(new((wps ? "wps" : "office") + bits, wps ? "WPS" : "Microsoft Office", wps, classId,
                    4u | (view == RegistryView.Registry32 ? 0x40000u : 0x80000u), executable));
                return;
            }
            catch { /* Missing or stale registration is not a usable renderer. */ }
        }
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid iid, out IntPtr value);
}
