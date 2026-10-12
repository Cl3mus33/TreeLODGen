using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TreeLODGen;

// Finds the mods folder of the Mod Organizer 2 instance that started this tool, so that
// the output can be written straight where MO2 will list it as a mod.
static class Mo2
{
    public static string? ModsFolderOfLauncher()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var ini = SettingsOfLauncher();
            if (ini == null) return null;

            var settings = File.ReadAllLines(ini)
                .Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.First()[1].Trim().Replace(@"\\", @"\").Replace('/', '\\'));
            var baseDir = settings.TryGetValue("base_directory", out var b) && b != "" ? b : Path.GetDirectoryName(ini)!;
            var mods = settings.TryGetValue("mod_directory", out var m) && m != "" ? m.Replace("%BASE_DIR%", baseDir) : Path.Combine(baseDir, "mods");
            return Directory.Exists(mods) ? mods : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // DynDOLOD's folder, when TexGen or DynDOLOD is one of the executables set up in the
    // Mod Organizer 2 instance that started this tool.
    public static string? DynDOLODFolderOfLauncher()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return SettingsOfLauncher() is { } ini ? DynDOLODFolderIn(ini) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? DynDOLODFolderIn(string ini)
    {
        foreach (var line in File.ReadAllLines(ini))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || !parts[0].TrimEnd().EndsWith(@"\binary", StringComparison.OrdinalIgnoreCase)) continue;
            var binary = parts[1].Trim().Trim('"').Replace(@"\\", @"\").Replace('/', '\\');
            var name = Path.GetFileNameWithoutExtension(binary);
            if (!name.StartsWith("TexGen", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("DynDOLOD", StringComparison.OrdinalIgnoreCase)) continue;
            var folder = Path.GetDirectoryName(binary);
            if (folder != null && Directory.Exists(Path.Combine(folder, "Edit Scripts", "DynDOLOD"))) return folder;
        }
        return null;
    }

    // A portable instance keeps its settings next to ModOrganizer.exe, a global one under
    // the user's local application data, named in the registry.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? SettingsOfLauncher()
    {
        var organizer = LaunchingOrganizer();
        if (organizer == null) return null;
        var ini = Path.Combine(Path.GetDirectoryName(organizer)!, "ModOrganizer.ini");
        if (File.Exists(ini)) return ini;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Mod Organizer Team\Mod Organizer");
        if (key?.GetValue("CurrentInstance") is not string instance || instance == "") return null;
        ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer", instance, "ModOrganizer.ini");
        return File.Exists(ini) ? ini : null;
    }

    // Walks up the parent processes looking for ModOrganizer.exe.
    static string? LaunchingOrganizer()
    {
        var parents = ParentIds();
        var id = Environment.ProcessId;
        for (var depth = 0; depth < 5 && parents.TryGetValue(id, out var parent); depth++)
        {
            id = parent;
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.ProcessName.Equals("ModOrganizer", StringComparison.OrdinalIgnoreCase)) return process.MainModule?.FileName;
            }
            catch (ArgumentException) { return null; }
        }
        return null;
    }

    static Dictionary<int, int> ParentIds()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
