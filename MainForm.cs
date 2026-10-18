using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TreeLODGen;

// The tool's window: where to read, where to write, a few choices, one button. Progress is
// shown with a bar, the tool's own messages, and a wireframe of the model just generated.
sealed class MainForm : Form
{
    static readonly (string name, string levels)[] Details =
    {
        ("Light  (smaller LOD files)", "0.3:0.8:300:1800,0.1:0.7:100:700,0.04:0.6:40:300"),
        ("Balanced  (recommended)", "0.4:0.8:400:2500,0.15:0.7:150:1000,0.06:0.6:60:400"),
        ("Detailed  (heavier LOD files)", "0.55:0.85:600:3500,0.25:0.75:250:1500,0.1:0.65:100:700"),
    };

    readonly TextBox _data = Field(), _output = Field(), _dyndolod = Field(), _log = new();
    string? _modsFolder;
    readonly Label _outputNote = Caption(""), _status = Caption("Ready.");
    readonly ComboBox _detail = new();
    readonly CheckBox _all = Check("Also redo trees that already have a 3D LOD");
    readonly Button _generate = Flat("Generate", true), _cancel = Flat("Cancel", false), _open = Flat("Open output folder", false), _choose = Flat("Choose trees...", false);
    Dictionary<string, string> _choices = new(StringComparer.OrdinalIgnoreCase);
    readonly ProgressLine _progress = new();
    readonly PreviewPanel _preview = new();
    bool _outputEdited;

    public MainForm()
    {
        Text = $"TreeLODGen {Program.Version}";
        ClientSize = Dpi.S(1000, 682);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = Dpi.Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.None;
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch (ArgumentException) { } catch (IOException) { }

        Add(new Label { Text = "TreeLODGen", Font = Dpi.Font("Segoe UI Semibold", 18f), ForeColor = Theme.Accent, AutoSize = true }, 20, 14);
        Add(Caption("3D tree LOD models generated from the trees of your load order. Run it before your LOD generation."), 24, 54);

        Add(Caption("Game Data folder  (start the tool from your mod manager so that it shows your mods)"), 24, 90);
        Add(_data, 24, 110, 528);
        Add(Browse(_data, "Skyrim Special Edition's Data folder"), 558, 109, 42, 25);

        Add(Caption("Output folder  (the mod that will hold the LOD models)"), 24, 146);
        Add(_output, 24, 166, 528);
        Add(Browse(_output, "Folder to write the mod into"), 558, 165, 42, 25);
        Add(_outputNote, 24, 194);

        Add(Caption("DynDOLOD folder  (found from your MO2 executables; gives animated-only trees a billboard)"), 24, 222);
        Add(_dyndolod, 24, 242, 528);
        Add(Browse(_dyndolod, "DynDOLOD's folder (the one holding TexGenx64.exe)"), 558, 241, 42, 25);

        Add(Caption("Detail"), 24, 282);
        _detail.DropDownStyle = ComboBoxStyle.DropDownList;
        _detail.FlatStyle = FlatStyle.Flat;
        _detail.BackColor = Theme.Field;
        _detail.ForeColor = Theme.Text;
        _detail.Items.AddRange(Details.Select(d => (object)d.name).ToArray());
        _detail.SelectedIndex = 1;
        Add(_detail, 24, 302, 230);
        Add(_all, 280, 284);

        Add(_generate, 24, 350, 170, 40);
        Add(_cancel, 204, 350, 100, 40);
        Add(_open, 314, 350, 140, 40);
        Add(_choose, 464, 350, 136, 40);
        _cancel.Enabled = false;
        _open.Enabled = false;
        Add(_progress, 24, 404, 576, 6);
        Add(_status, 24, 418);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Theme.Panel;
        _log.ForeColor = Theme.Dim;
        _log.Font = Dpi.Font("Consolas", 9f);
        _log.WordWrap = true;
        Add(_log, 24, 444, 576, 214);

        _preview.Font = Font;
        Add(_preview, 624, 24, 352, 634);

        // defaults: the game from the registry, the output in the mods folder of the Mod
        // Organizer 2 instance that started the tool when there is one
        _data.Text = Generate.FindGameData() ?? "";
        _modsFolder = Mo2.ModsFolderOfLauncher();
        var (folder, note) = Generate.DefaultOutput(_modsFolder);
        _output.Text = folder;
        _outputNote.Text = $"Default: {note}.";
        // TexGen only reads stand-in models from its own folder, so the tool has to know where
        // DynDOLOD is; when it is set up as an executable in Mod Organizer 2 nobody has to say
        _dyndolod.Text = Mo2.DynDOLODFolderOfLauncher() ?? "";
        // then whatever was used at the last Generate from this same launcher
        if (Settings.Load(_modsFolder) is { } saved)
        {
            if (File.Exists(Path.Combine(saved.Data, "Skyrim.esm"))) _data.Text = saved.Data;
            if (saved.Output != "") { _output.Text = saved.Output; _outputNote.Text = "As at your last run."; }
            if (saved.DynDOLOD == "" || Directory.Exists(Path.Combine(saved.DynDOLOD, "Edit Scripts", "DynDOLOD"))) _dyndolod.Text = saved.DynDOLOD;
            _detail.SelectedIndex = Math.Clamp(saved.Detail, 0, Details.Length - 1);
            _all.Checked = saved.All;
            _choices = new Dictionary<string, string>(saved.Choices ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        _output.TextChanged += (_, _) => { if (_output.Focused) { _outputEdited = true; _outputNote.Text = ""; } UpdateChoose(); };
        UpdateChoose();
        _choose.Click += (_, _) => ChooseTrees();

        ActiveControl = _generate;
        _generate.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) =>
        {
            Ui.CancelRequested = true;
            _cancel.Enabled = false;
            _status.ForeColor = Theme.Orange;
            _status.Text = "Cancelling after the current tree...";
        };
        _open.Click += (_, _) => { if (Directory.Exists(_output.Text)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_output.Text}\"") { UseShellExecute = true }); };
    }

    // dark title bar, to match the rest of the window
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    async Task RunAsync()
    {
        if (!File.Exists(Path.Combine(_data.Text, "Skyrim.esm")))
        {
            _status.ForeColor = Theme.Orange;
            _status.Text = "That folder is not Skyrim Special Edition's Data folder (no Skyrim.esm in it).";
            return;
        }
        if (_output.Text.Trim() == "")
        {
            _status.ForeColor = Theme.Orange;
            _status.Text = "Choose an output folder.";
            return;
        }

        var args = new List<string> { "--data", _data.Text, "--output", _output.Text, "--levels", Details[_detail.SelectedIndex].levels };
        if (_dyndolod.Text.Trim() != "")
        {
            if (!Directory.Exists(Path.Combine(_dyndolod.Text, "Edit Scripts", "DynDOLOD")))
            {
                _status.ForeColor = Theme.Orange;
                _status.Text = "That is not DynDOLOD's folder. Leave the field empty if unsure.";
                return;
            }
            args.AddRange(new[] { "--dyndolod", _dyndolod.Text.Trim() });
        }
        if (_all.Checked) args.Add("--all");
        SaveSettings();
        if (_choices.Count > 0)
        {
            var choicesFile = Path.Combine(Path.GetTempPath(), "TreeLODGen_choices.json");
            File.WriteAllText(choicesFile, System.Text.Json.JsonSerializer.Serialize(_choices));
            args.AddRange(new[] { "--tree-choices", choicesFile });
        }

        SetBusy(true);
        _log.Clear();
        _status.ForeColor = Theme.Dim;
        _progress.Value = 0;
        var clock = Stopwatch.StartNew();

        Ui.Live = false;
        Ui.Interactive = false;
        Ui.CancelRequested = false;
        Ui.OnStep = (number, count, title) => Post(() => { _status.Text = $"Step {number} of {count}: {title}..."; if (number < count) _progress.Value = 0; });
        Ui.OnDetail = detail => Post(() => { if (!_status.Text.Contains("Generating")) _status.Text = _status.Text.Split("...")[0] + "...  " + detail; });
        Ui.OnTree = (done, total, name) => Post(() =>
        {
            _progress.Value = total == 0 ? 1f : done / (float)total;
            if (name != "" && !Ui.CancelRequested) _status.Text = $"Generating LOD models...  {done + 1} of {total}:  {name}";
        });
        Ui.OnTreeDone = (name, lodPath) => { var model = PreviewPanel.Load(name, lodPath); Post(() => _preview.Show(model)); };

        using var logFile = Program.OpenLog();
        var previousOut = Console.Out;
        var writer = new LogWriter(text => Post(() => _log.AppendText(text)), logFile);
        Console.SetOut(writer);
        int code;
        try
        {
            Console.WriteLine($"TreeLODGen {Program.Version}");
            Console.WriteLine();
            code = await Task.Run(() =>
            {
                try { return Generate.Run(args.ToArray()); }
                catch (Exception e) { Console.WriteLine($"TreeLODGen stopped on an error: {e}"); return 1; }
            });
        }
        finally
        {
            Console.Out.Flush();
            Console.SetOut(previousOut);
            Ui.OnStep = null; Ui.OnDetail = null; Ui.OnTree = null; Ui.OnTreeDone = null;
        }

        SetBusy(false);
        if (code == 2)
        {
            _progress.Value = 0;
            _status.ForeColor = Theme.Orange;
            _status.Text = "Cancelled. Nothing was changed in the output folder.";
        }
        else if (code == 0)
        {
            _progress.Value = 1f;
            _status.ForeColor = Theme.Accent;
            _status.Text = $"Done in {clock.Elapsed.TotalSeconds:0} s. Enable the mod, then run your LOD generation.";
            _open.Enabled = true;
        }
        else
        {
            _status.ForeColor = Theme.Orange;
            _status.Text = "Stopped on an error, see the messages below (also saved in TreeLODGen.log).";
        }
        UpdateChoose();
    }

    void SaveSettings() => Settings.Save(_modsFolder, new Settings.Entry { Data = _data.Text, Output = _output.Text, DynDOLOD = _dyndolod.Text.Trim(), Detail = _detail.SelectedIndex, All = _all.Checked, Choices = _choices });

    // The tree list comes from the report of the last generation in the output folder.
    void UpdateChoose() => _choose.Enabled = !_cancel.Enabled && File.Exists(Path.Combine(_output.Text, "TreeLODGen_report.csv"));

    void ChooseTrees()
    {
        var trees = TreeRules.ReadReport(_output.Text);
        if (trees.Count == 0)
        {
            _status.ForeColor = Theme.Orange;
            _status.Text = "No generated trees in that output folder yet: click Generate first.";
            return;
        }
        // choices saved for trees that are no longer generated would only clutter the settings
        var known = trees.Select(t => t.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var dialog = new TreeChoicesForm(trees, _choices.Where(c => known.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase), TreeRules.PlacedCounts(_dyndolod.Text.Trim()));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _choices = dialog.Choices;
        SaveSettings();
        var own = TreeRules.Write(_output.Text, _choices);
        _status.ForeColor = Theme.Accent;
        _status.Text = $"Rules updated: {own} of {trees.Count} trees off DynDOLOD's tree rule. Run DynDOLOD again to apply them.";
    }

    void SetBusy(bool busy)
    {
        foreach (Control control in new Control[] { _data, _output, _dyndolod, _detail, _all, _generate, _choose })
            control.Enabled = !busy;
        _generate.Text = busy ? "Generating..." : "Generate";
        _cancel.Enabled = busy;
    }

    void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    void Add(Control control, int x, int y, int width = 0, int height = 0)
    {
        control.Location = Dpi.P(x, y);
        if (width > 0) control.Width = Dpi.P(width);
        if (height > 0) control.Height = Dpi.P(height);
        Controls.Add(control);
    }

    static TextBox Field() => new() { BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    static Label Caption(string text) => new() { Text = text, ForeColor = Theme.Dim, AutoSize = true };
    static CheckBox Check(string text) => new() { Text = text, ForeColor = Theme.Text, AutoSize = true, FlatStyle = FlatStyle.Flat };

    static Button Flat(string text, bool primary)
    {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Theme.Grid;
        button.BackColor = primary ? Theme.Accent : Theme.Field;
        button.ForeColor = primary ? Theme.Back : Theme.Text;
        if (primary) button.Font = Dpi.Font("Segoe UI Semibold", 10.5f);
        return button;
    }

    Button Browse(TextBox target, string description)
    {
        var button = Flat("...", false);
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
            if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            target.Text = dialog.SelectedPath;
            if (target == _output) { _outputEdited = true; _outputNote.Text = ""; }
        };
        return button;
    }

    // A thin progress line in the accent colour.
    sealed class ProgressLine : Control
    {
        float _value;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float Value { get => _value; set { _value = Math.Clamp(value, 0f, 1f); Invalidate(); } }
        public ProgressLine() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Field);
            using var brush = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(brush, 0, 0, Width * _value, Height);
        }
    }

    // Sends what the tool prints to the window's message box and to the log file.
    sealed class LogWriter(Action<string> show, TextWriter? file) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            file?.Write(value);
            show(value.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }
        public override void WriteLine(string? value) => Write(value + "\n");
        public override void WriteLine() => Write("\n");
    }
}
