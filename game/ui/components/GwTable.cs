using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>A table column: header key and whether its values are numbers (right-aligned, D366 section 7).</summary>
/// <param name="HeaderKey">Translation key of the header.</param>
/// <param name="Numeric">Numbers are right-aligned and sorted numerically.</param>
public sealed record GwColumn(string HeaderKey, bool Numeric);

/// <summary>A table row: shown texts, numbers for numeric columns, plus the row's id.</summary>
/// <param name="Id">Row id (e.g. inhabitant id), never shown.</param>
/// <param name="Cells">Display texts (already translated where needed); text columns sort by these.</param>
/// <param name="Numbers">One value per column; numeric columns sort by it (others ignore it).</param>
public sealed record GwRow(string Id, IReadOnlyList<string> Cells, IReadOnlyList<double> Numbers);

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
    private readonly Dictionary<string, TreeItem> _items = new(StringComparer.Ordinal);
    private List<string> _order = [];
    private bool _rebuilding;
    private int _sortColumn;
    private ulong _emittedFrame = ulong.MaxValue;
    private bool _descending;

    /// <summary>Raised when the player selects a row (row id), also when the player clicks the row that is already selected.</summary>
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
            if (!_rebuilding && _tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id)
            {
                _emittedFrame = Engine.GetProcessFrames();
                EmitSignal(SignalName.RowSelected, id);
            }
        };

        // A click on the row that is already selected changes no selection, but the player still wants
        // it (B2569: every click gives feedback, the camera glides to the inhabitant again).
        _tree.ItemMouseSelected += (_, _) =>
        {
            if (!_rebuilding && _emittedFrame != Engine.GetProcessFrames() && _tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id)
            {
                _emittedFrame = Engine.GetProcessFrames();
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

    /// <summary>
    /// Sets the rows. If the sorted order of ids is unchanged, only changed cells are rewritten, so
    /// selection and scroll position stay (calm, keyboard-friendly); otherwise the table is rebuilt
    /// and the selected row stays selected.
    /// </summary>
    public void SetRows(IReadOnlyList<GwRow> rows)
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        var sorted = Sorted();
        if (sorted.Select(r => r.Id).SequenceEqual(_order, StringComparer.Ordinal))
        {
            foreach (var row in sorted)
            {
                var item = _items[row.Id];
                for (var i = 0; i < _columns.Count; i++)
                {
                    if (item.GetText(i) != row.Cells[i])
                    {
                        item.SetText(i, row.Cells[i]);
                    }
                }
            }

            return;
        }

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

    private IReadOnlyList<GwRow> Sorted()
    {
        // Numbers compare as numbers; texts in the game's language, not the operating system's.
        var column = _sortColumn;
        if (_columns.Count == 0)
        {
            return _rows;
        }

        return _columns[column].Numeric
            ? Presentation.Ui.TableSort.By(_rows, r => r.Numbers[column], _descending)
            : Presentation.Ui.TableSort.By(_rows, r => r.Cells[column], _descending, StringComparer.InvariantCultureIgnoreCase);
    }

    private void Rebuild()
    {
        var selected = _tree.GetSelected()?.GetMetadata(0).AsString();
        _tree.Clear();
        _items.Clear();
        var root = _tree.CreateItem();
        var sorted = Sorted();
        foreach (var row in sorted)
        {
            var item = _tree.CreateItem(root);
            item.SetMetadata(0, row.Id);
            for (var i = 0; i < _columns.Count; i++)
            {
                item.SetText(i, row.Cells[i]);
                item.SetTextAlignment(i, _columns[i].Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            }

            _items[row.Id] = item;
        }

        _order = [.. sorted.Select(r => r.Id)];
        if (selected is not null && _items.TryGetValue(selected, out var again))
        {
            // Keep the player's selection without raising RowSelected again.
            _rebuilding = true;
            again.Select(0);
            _tree.ScrollToItem(again);
            _rebuilding = false;
        }

        _empty.Visible = _rows.Count == 0;
        _tree.Visible = _rows.Count > 0;
    }
}
