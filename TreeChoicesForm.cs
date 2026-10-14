using System.Drawing;
using System.Windows.Forms;

namespace TreeLODGen;

// Which trees keep a 3D model, and at which levels. Lists the trees of the last generation
// in the output folder; saving rewrites the DynDOLOD rules there without generating again.
sealed class TreeChoicesForm : Form
{
    static readonly string[] Labels = { "3D at LOD 4", "3D everywhere", "Billboard only" };
    static readonly string[] Values = { TreeRules.Lod4, TreeRules.Everywhere, TreeRules.Billboard };

    readonly List<TreeRules.Tree> _trees;
    readonly bool _far3d;
    readonly DataGridView _grid = new();
    readonly TextBox _filter = new() { BackColor = Theme.Field, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    readonly Label _summary = new() { ForeColor = Theme.Dim, AutoSize = true };

    public Dictionary<string, string> Choices { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TreeChoicesForm(List<TreeRules.Tree> trees, bool far3d, IReadOnlyDictionary<string, string> saved, IReadOnlyDictionary<string, int> placed)
    {
        _trees = trees;
        _far3d = far3d;
        Text = "Choose trees";
        ClientSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;

        var intro = new Label
        {
            Text = "3D models cost far more than billboards: copies placed x model size is what makes the LOD heavy. "
                 + "Saving only rewrites the rules in the output folder: run DynDOLOD again afterwards (TexGen is not needed).",
            ForeColor = Theme.Dim, Location = new Point(16, 12), Size = new Size(868, 36),
        };
        Controls.Add(intro);

        AddButton("Vanilla trees only", 16, 54, 150, () => SetAll(t => t.Vanilla ? TreeRules.Lod4 : TreeRules.Billboard));
        AddButton("All 3D at LOD 4", 174, 54, 130, () => SetAll(_ => TreeRules.Lod4));
        AddButton("All billboards", 312, 54, 120, () => SetAll(_ => TreeRules.Billboard));
        Controls.Add(new Label { Text = "Filter", ForeColor = Theme.Dim, AutoSize = true, Location = new Point(560, 60) });
        _filter.Location = new Point(600, 56);
        _filter.Width = 284;
        _filter.TextChanged += (_, _) => ApplyFilter();
        Controls.Add(_filter);

        BuildGrid();
        int Placed(TreeRules.Tree t) => placed.TryGetValue(t.Model, out var n) ? n : 0;
        foreach (var tree in trees.OrderByDescending(Placed).ThenBy(t => t.Model))
        {
            var row = _grid.Rows[_grid.Rows.Add(Path.GetFileNameWithoutExtension(tree.Model), tree.Source, tree.Vanilla ? "yes" : "", placed.Count > 0 ? Placed(tree) : null, tree.Lod4Kb,
                Labels[Array.IndexOf(Values, TreeRules.Effective(tree, far3d, saved))])];
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

        _summary.Location = new Point(16, 604);
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
        _grid.Location = new Point(16, 92);
        _grid.Size = new Size(868, 494);
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
            HeaderText = header, Width = width, ReadOnly = true,
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
        var choice = new DataGridViewComboBoxColumn { HeaderText = "Use", Width = 190, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
        choice.Items.AddRange(Labels);
        choice.DefaultCellStyle.BackColor = Theme.Field;
        _grid.Columns.Add(choice);
        Controls.Add(_grid);
    }

    Button AddButton(string text, int x, int y, int width, Action click)
    {
        var button = new Button
        {
            Text = text, Location = new Point(x, y), Size = new Size(width, 30), FlatStyle = FlatStyle.Flat,
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
        int lod4 = 0, everywhere = 0, billboard = 0;
        foreach (DataGridViewRow row in _grid.Rows)
            switch (Array.IndexOf(Labels, row.Cells[5].Value as string))
            {
                case 0: lod4++; break;
                case 1: everywhere++; break;
                default: billboard++; break;
            }
        _summary.Text = $"{_trees.Count} trees:  {lod4} 3D at LOD 4,  {everywhere} 3D everywhere,  {billboard} billboard only";
    }

    // Only choices that differ from the main window's default are kept, so that its
    // checkbox still decides for every tree left alone here.
    void Save()
    {
        var fallback = _far3d ? TreeRules.Everywhere : TreeRules.Lod4;
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
