using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TreeLODGen;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // numbers are written the same way on every system
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // Without arguments the tool opens its window; with arguments it works in a console,
        // for scripts and for people who prefer it.
        if (args.Length == 0)
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        EnsureConsole();
        if (args[0] is "-h" or "--help" or "/?")
        {
            Console.WriteLine(Generate.Usage);
            return 0;
        }

        // Development commands, kept for checking the tool's work on single files.
        if (args.Length > 1 && !args[0].StartsWith('-'))
        {
            switch (args[0].ToLowerInvariant())
            {
                case "inspect": return Inspect.Run(args[1]);
                case "split": return Split.Run(args);
                case "islands": return Islands.Report(args[1]);
                case "vcolor": return VColor.Report(args[1]);
                case "batch": return Batch.Run(args);
                case "wood": return WoodSplit.Report(args[1], args[2].Split(';'));
                case "hybrid": return Hybrid.Run(args);
                case "render": return Render.Run(args);
                case "mo2ini": Console.WriteLine(Mo2.DynDOLODFolderIn(args[1]) ?? "(no DynDOLOD executable found)"); return 0;
            }
        }

        // A console that belongs to this process alone would close before anything can be
        // read: ask before starting and wait for a key at the end in that case.
        var ownConsole = !Console.IsInputRedirected && !Console.IsOutputRedirected && GetConsoleProcessList(new uint[2], 2) == 1;
        var pause = ownConsole && !args.Contains("--no-pause");
        args = args.Where(a => a != "--no-pause").ToArray();
        Ui.Interactive = pause;

        using var log = OpenLog();
        Ui.Screen = Console.Out;
        if (log != null) Console.SetOut(new Tee(Console.Out, log));
        Ui.Banner(Version);
        try
        {
            return Generate.Run(args);
        }
        catch (Exception e)
        {
            Console.WriteLine($"TreeLODGen stopped on an error: {e}");
            return 1;
        }
        finally
        {
            Console.Out.Flush();
            if (pause)
            {
                Ui.Screen.WriteLine();
                Ui.Screen.WriteLine("Press any key to close this window.");
                Console.ReadKey(true);
            }
        }
    }

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "";

    // Everything printed is also kept in a log file next to the tool.
    public static StreamWriter? OpenLog()
    {
        try { return new StreamWriter(Path.Combine(AppContext.BaseDirectory, "TreeLODGen.log"), false, Encoding.UTF8) { AutoFlush = true }; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // A windowed program has no console of its own: use the one it was started from, or
    // open one. Redirected output (pipes, files) needs neither.
    static void EnsureConsole()
    {
        if (Console.IsOutputRedirected) return;
        if (!AttachConsole(-1)) AllocConsole();
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}

sealed class Tee(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;
    public override void Write(char value) { first.Write(value); second.Write(value); }
    public override void Write(string? value) { first.Write(value); second.Write(value); }
    public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
    public override void Flush() { first.Flush(); second.Flush(); }
}
