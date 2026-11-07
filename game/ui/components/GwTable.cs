using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>A table column: header key and whether its values are numbers (right-aligned, D366 section 7).</summary>
/// <param name="HeaderKey">Translation key of the header.</param>
/// <param name="Numeric">Numbers are right-aligned and sorted numerically.</param>
public sealed record GwColumn(string HeaderKey, bool Numeric);

/// <summary>A table row: shown texts and sort values per column, plus the row's id.</summary>
/// <param name="Id">Row id (e.g. inhabitant id), never shown.</param>
/// <param name="Cells">Display texts (already translated where needed).</param>
/// <param name="SortValues">Values used for sorting, one per column.</param>
public sealed record GwRow(string Id, IReadOnlyList<string> Cells, IReadOnlyList<IComparable> SortValues);

/// <summary>
/// Table building block (D366 section 7) on Godot's <see cref="Tree"/>, which only draws visible
/// rows (100 inhabitants stay cheap): header row stays put, click on a header sorts (stable), numbers
/// right-aligned, a calm text when empty.
/// </summary>
public partial class GwTable : VBoxContainer
{
    private readonly Tree _tree = new() { HideRoot = true, ColumnTitlesVisible = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Row };
    private readonly Label _empty = new() { ThemeTypeVariation = UiTheme.SmallLabel, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private IReadOnlyList<GwColumn> _columns = [];
    private IReadOnlyList<GwRow> _rows = [];
    private int _sortColumn;
    private bool _descending;

    /// <summary>Raised when the player selects a row (row id).</summary>
    [Signal]
    public delegate void RowSelectedEventHandler(string id);

    /// <summary>Creates the table.</summary>
    public GwTable()
    {
        AddChild(_tree);
        AddChild(_empty);
        _tree.ColumnTitleClicked += (column, _) => SortBy((int)column);
        _tree.ItemSelected += () =>
        {
            if (_tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id)
            {
                EmitSignal(SignalName.RowSelected, id);
            }
        };
    }

    /// <summary>Sets the columns and the empty-state text key.</summary>
    public void Setup(IReadOnlyList<GwColumn> columns, string emptyKey)
    {
        _columns = columns ?? throw new ArgumentNullException(nameof(columns));
        _tree.Columns = columns.Count;
        _empty.Text = emptyKey;
        UpdateTitles();
    }

    /// <summary>Replaces the rows (keeps the current sort).</summary>
    public void SetRows(IReadOnlyList<GwRow> rows)
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        Rebuild();
    }

    /// <summary>Sorts by a column; clicking the same column again reverses the order.</summary>
    public void SortBy(int column)
    {
        _descending = column == _sortColumn && !_descending;
        _sortColumn = column;
        Rebuild();
    }

    /// <summary>Smallest width the header row needs (for the layout check).</summary>
    public float HeaderMinimumWidth() =>
        _columns.Select((c, i) => GetThemeFont("font").GetStringSize(Tr(c.HeaderKey), HorizontalAlignment.Left, -1, GetThemeFontSize("font_size")).X).Sum();

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged)
        {
            UpdateTitles();
        }
    }

    private void UpdateTitles()
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            _tree.SetColumnTitle(i, Tr(_columns[i].HeaderKey));
            _tree.SetColumnTitleAlignment(i, _columns[i].Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            _tree.SetColumnExpand(i, !_columns[i].Numeric);
        }
    }

    private void Rebuild()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        var sorted = Presentation.Ui.TableSort.By(_rows, r => r.SortValues[_sortColumn], _descending);
        foreach (var row in sorted)
        {
            var item = _tree.CreateItem(root);
            item.SetMetadata(0, row.Id);
            for (var i = 0; i < _columns.Count; i++)
            {
                item.SetText(i, row.Cells[i]);
                item.SetTextAlignment(i, _columns[i].Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            }
        }

        _empty.Visible = _rows.Count == 0;
        _tree.Visible = _rows.Count > 0;
    }
}
