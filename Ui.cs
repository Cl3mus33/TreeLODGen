using System.Diagnostics;

namespace TreeLODGen;

// Console presentation: numbered steps with their duration, and a live progress line so the
// user can always see that work is going on. The live line goes to the screen only, the log
// file keeps the plain messages.
static class Ui
{
    // The real console, set before output is duplicated into the log file.
    public static TextWriter Screen = Console.Out;
    public static bool Live = !Console.IsOutputRedirected;

    // True when the tool has a console window of its own and may ask before starting.
    public static bool Interactive;

    // Set by the window, which shows the same progress with its own controls.
    public static Action<int, int, string>? OnStep;
    public static Action<string>? OnDetail;
    public static Action<int, int, string>? OnTree;
    public static Action<string, string>? OnTreeDone;

    // Raised by the window's Cancel button; the work stops at the next tree.
    public static volatile bool CancelRequested;
    public static void ThrowIfCancelled() { if (CancelRequested) throw new OperationCanceledException(); }

    static Stopwatch Total = Stopwatch.StartNew();
    public static void ResetClock() => Total = Stopwatch.StartNew();
    static Stopwatch _step = Stopwatch.StartNew();
    static string _stepTitle = "";
    static int _lastLength;

    public static void Banner(string version)
    {
        Color(ConsoleColor.Green, () => Console.WriteLine($"TreeLODGen {version}  -  3D tree LOD models from your load order"));
        Console.WriteLine();
    }

    public static void Step(int number, int count, string title)
    {
        EndStep();
        _stepTitle = $"[{number}/{count}] {title}";
        _step = Stopwatch.StartNew();
        OnStep?.Invoke(number, count, title);
        Progress("");
    }

    // Rewrites the current step's line in place.
    public static void Progress(string detail)
    {
        if (detail != "") OnDetail?.Invoke(detail);
        if (!Live || _stepTitle == "") return;
        var width = 100;
        try { width = Math.Max(40, Console.WindowWidth - 1); } catch (IOException) { }
        var text = $"{_stepTitle}{(detail == "" ? " ..." : "  " + detail)}";
        if (text.Length > width) text = text[..width];
        Screen.Write("\r" + text.PadRight(Math.Max(_lastLength, text.Length)));
        _lastLength = text.Length;
    }

    // Progress through the trees: the one being worked on, then its finished LOD model.
    public static void Tree(int done, int total, string name)
    {
        OnTree?.Invoke(done, total, name);
        if (Live && _stepTitle != "") Progress($"{Bar(done, total)}  {name}");
    }

    public static void TreeDone(string name, string lodPath) => OnTreeDone?.Invoke(name, lodPath);

    public static string Bar(int done, int total, int size = 24)
    {
        var filled = total == 0 ? size : done * size / total;
        return $"{new string('#', filled)}{new string('.', size - filled)} {done}/{total}";
    }

    public static void EndStep(string result = "")
    {
        if (_stepTitle == "") return;
        if (Live) Screen.Write("\r" + new string(' ', _lastLength) + "\r");
        _lastLength = 0;
        var line = $"{_stepTitle}{(result == "" ? "" : "  " + result)}";
        Console.Write(line);
        Color(ConsoleColor.DarkGray, () => Console.WriteLine($"  ({_step.Elapsed.TotalSeconds:0.0} s)"));
        _stepTitle = "";
    }

    public static string Elapsed => $"{Total.Elapsed.TotalSeconds:0} s";

    public static void Color(ConsoleColor color, Action write)
    {
        if (!Live) { write(); return; }
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        try { write(); } finally { Console.ForegroundColor = previous; }
    }
}
