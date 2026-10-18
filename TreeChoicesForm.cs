using System.Drawing;
using System.Windows.Forms;

namespace TreeLODGen;

// Which trees keep a 3D model, and at which levels. Lists the trees of the last generation
// in the output folder; saving rewrites the DynDOLOD rules there without generating again.
sealed class TreeChoicesForm : Form
{
    static readonly string[] Labels = { "DynDOLOD tree rule", "3D at LOD 4", "3D everywhere", "Billboard only" };
    static readonly string[] Values = { TreeRules.Default, TreeRules.Lod4, TreeRules.Everywhere, TreeRules.Billboard };

    readonly List<TreeRules.Tree> _trees;
    readonly DataGridView _grid = new();
    readonly TextBox _filter = new() { BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    readonly Label _summary = new() { ForeColor = Theme.Dim, AutoSize = true };

    public Dictionary<string, string> Choices { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TreeChoicesForm(List<TreeRules.Tree> trees, IReadOnlyDictionary<string, string> saved, IReadOnlyDictionary<string, int> placed)
    {
        _trees = trees;
        Text = "Choose trees";
        ClientSize = Dpi.S(900, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = Dpi.Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.None;

        var intro = new Label
        {
            Text = "3D models cost far more than billboards: copies placed x model size is what makes the LOD heavy. "
                 + "Trees left on \"DynDOLOD tree rule\" follow the tree rule set in DynDOLOD. Saving only rewrites the rules: run DynDOLOD again afterwards (TexGen is not needed).",
            ForeColor = Theme.Dim, Location = Dpi.P(16, 12), Size = Dpi.S(868, 36),
        };
        Controls.Add(intro);

        AddButton("Vanilla trees only", 16, 54, 150, () => SetAll(t => t.Vanilla ? TreeRules.Default : TreeRules.Billboard));
        AddButton("All DynDOLOD rule", 174, 54, 130, () => SetAll(_ => TreeRules.Default));
        AddButton("All billboards", 312, 54, 120, () => SetAll(_ => TreeRules.Billboard));
        Controls.Add(new Label { Text = "Filter", ForeColor = Theme.Dim, AutoSize = true, Location = Dpi.P(560, 60) });
        _filter.Location = Dpi.P(600, 56);
        _filter.Width = Dpi.P(284);
        _filter.TextChanged += (_, _) => ApplyFilter();
        Controls.Add(_filter);

        BuildGrid();
        int Placed(TreeRules.Tree t) => placed.TryGetValue(t.Model, out var n) ? n : 0;
        foreach (var tree in trees.OrderByDescending(Placed).ThenBy(t => t.Model))
        {
            var row = _grid.Rows[_grid.Rows.Add(Path.GetFileNameWithoutExtension(tree.Model), tree.Source, tree.Vanilla ? "yes" : "", placed.Count > 0 ? Placed(tree) : null, tree.Lod4Kb,
                Labels[Array.IndexOf(Values, TreeRules.Effective(tree, saved))])];
            row.Tag = tree;
            row.Cells[0].ToolTipText = tree.Model;
            if (!tree.HasBillboard)
            {
                row.Cells[5].ReadOnly = true;
                row.Cells[5].ToolTipText = "Made of animated shapes only: TexGen cannot make a billboard for it, so it keeps 3D everywhere. Fill in the DynDOLOD folder in the main window to change that.";
            }
        }
        _grid.CellValueChanged += (_, _) => UpdateSummary();
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        _summary.Location = Dpi.P(16, 604);
        Controls.Add(_summary);
        AddButton("Cancel", 784, 596, 100, () => { DialogResult = DialogResult.Cancel; Close(); });
        var save = AddButton("Save and update rules", 604, 596, 172, Save);
        save.BackColor = Theme.Accent;
        save.ForeColor = Theme.Back;
        save.FlatAppearance.BorderSize = 0;
        AcceptButton = save;
        UpdateSummary();
    }

    void BuildGrid()
    {
        _grid.Location = Dpi.P(16, 92);
        _grid.Size = Dpi.S(868, 494);
        _grid.RowTemplate.Height = Dpi.P(25);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = Dpi.P(25);
        _grid.BackgroundColor = Theme.Panel;
        _grid.GridColor = Theme.Grid;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Field;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text;
        _grid.DefaultCellStyle.BackColor = Theme.Panel;
        _grid.DefaultCellStyle.ForeColor = Theme.Text;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.Field;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Accent;
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;

        DataGridViewTextBoxColumn Text(string header, int width, bool number = false) => new()
        {
            HeaderText = header, Width = Dpi.P(width), ReadOnly = true,
            DefaultCellStyle = { Alignment = number ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft },
        };
        _grid.Columns.Add(Text("Tree", 250));
        _grid.Columns.Add(Text("From", 190));
        _grid.Columns.Add(Text("Vanilla", 60));
        var records = Text("Placed", 70, true);
        records.ToolTipText = "Copies placed in the world at the last DynDOLOD run (empty before DynDOLOD has run). Many copies times the model size is what makes LOD heavy.";
        records.ValueType = typeof(int);
        _grid.Columns.Add(records);
        var size = Text("LOD 4 KB", 80, true);
        size.ToolTipText = "Size of the LOD 4 model; every placed copy of the tree adds about this much to the LOD.";
        size.ValueType = typeof(int);
        _grid.Columns.Add(size);
        var choice = new DataGridViewComboBoxColumn { HeaderText = "Use", Width = Dpi.P(190), FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
        choice.Items.AddRange(Labels);
        choice.DefaultCellStyle.BackColor = Theme.Field;
        _grid.Columns.Add(choice);
        Controls.Add(_grid);
    }

    Button AddButton(string text, int x, int y, int width, Action click)
    {
        var button = new Button
        {
            Text = text, Location = Dpi.P(x, y), Size = Dpi.S(width, 30), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Field, ForeColor = Theme.Text, Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderColor = Theme.Grid;
        button.Click += (_, _) => click();
        Controls.Add(button);
        return button;
    }

    void SetAll(Func<TreeRules.Tree, string> pick)
    {
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is TreeRules.Tree tree && tree.HasBillboard)
                row.Cells[5].Value = Labels[Array.IndexOf(Values, pick(tree))];
        UpdateSummary();
    }

    void ApplyFilter()
    {
        var text = _filter.Text.Trim();
        foreach (DataGridViewRow row in _grid.Rows)
            row.Visible = text == "" || row.Tag is TreeRules.Tree tree
                && (tree.Model.Contains(text, StringComparison.OrdinalIgnoreCase) || tree.Source.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    void UpdateSummary()
    {
        var counts = new int[Labels.Length];
        foreach (DataGridViewRow row in _grid.Rows)
            counts[Math.Max(0, Array.IndexOf(Labels, row.Cells[5].Value as string))]++;
        _summary.Text = $"{_trees.Count} trees:  {counts[0]} DynDOLOD rule,  {counts[1]} 3D at LOD 4,  {counts[2]} 3D everywhere,  {counts[3]} billboard only";
    }

    // Only trees taken off DynDOLOD's tree rule are kept.
    void Save()
    {
        var fallback = TreeRules.Default;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not TreeRules.Tree tree || !tree.HasBillboard) continue;
            var value = Values[Math.Max(0, Array.IndexOf(Labels, row.Cells[5].Value as string))];
            if (value != fallback) Choices[tree.Model] = value;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
