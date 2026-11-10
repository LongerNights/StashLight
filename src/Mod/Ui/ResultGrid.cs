using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using InventorySearch.Core;
using UText = FrooxEngine.UIX.Text;

namespace InventorySearch.Ui;

/// <summary>
/// Virtualized results view in two layouts: a thumbnail grid (5 columns) or a compact list (1 column). Only a small pool of
/// cells exists (visible rows + a buffer); scrolling re-binds cells instead of creating slots, so 10,000 results cost the same
/// as 10. Scroll model (verified in ScrollRect source): NormalizedPosition.y = 0 is the top, 1 the bottom;
/// pixel offset from top = y * (contentHeight - viewportHeight). World thread only.
/// </summary>
internal sealed class ResultGrid
{
    private const float GridCellH = 250f, ListCellH = 84f;
    private const int GridCols = 5;
    private const float DefaultViewportH = 700f;
    private const float MinPoolViewportH = 900f;
    private static colorX SelTint => UiKit.WithAlpha(UiKit.Accent, 0.13f);

    private sealed class Cell
    {
        public Slot Root = null!;
        public RectTransform Rect = null!;
        public Button Button = null!;
        public Slot Selected = null!;          // highlight overlay (ActiveSelf toggled)
        public Image SelectedTint = null!;
        public StaticTexture2D Texture = null!;
        public Image Thumb = null!;
        public RectTransform FolderIcon = null!, LinkIcon = null!;   // kind icons (folder / link), toggled per bind
        public UText Name = null!;
        public UText Sub = null!;
        public UText Copies = null!;
        public Slot CopiesBg = null!;
        public UText Badge = null!;            // grid only
        public Slot BadgeBg = null!;           // grid only
        public UText Category = null!, Owner = null!, Date = null!;   // list only
        public int HitIndex = -1;              // which result this cell shows
    }

    private readonly ScrollRect _scroll;
    private readonly RectTransform _content;
    private readonly RectTransform _viewport;
    private readonly List<Cell> _pool = new();
    private IReadOnlyList<SearchHit> _hits = Array.Empty<SearchHit>();
    private bool _dirty = true;
    private int _poolRows;
    private string? _selectedUri;
    private bool _list;

    public event Action<SearchHit>? Clicked;

    public ResultGrid(RectTransform area, bool listMode = false)
    {
        _list = listMode;
        var ui = new UIBuilder(area);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        _scroll = ui.ScrollArea(Alignment.TopLeft);
        // viewport = the "Scroll Area" slot (UIBuilder.Next gave it a RectTransform); content = ScrollRect's own slot
        _viewport = _scroll.Slot.Parent.GetComponentOrAttach<RectTransform>();
        _content = _scroll.Slot.GetComponentOrAttach<RectTransform>();
        SetContentHeight(0f);
        _scroll.NormalizedPosition.Changed += _ => Refresh();
    }

    // ---------------------------------------------------------------- layout mode

    public bool ListMode => _list;
    private int Cols => _list ? 1 : GridCols;
    private float CellH => _list ? ListCellH : GridCellH;
    private float Gap => _list ? 4f : 6f;

    /// <summary>Items per row (the keyboard's up/down step).</summary>
    public int Columns => Cols;
    /// <summary>How many whole rows fit in the viewport (the keyboard's page step).</summary>
    public int VisibleRows => Math.Max(1, (int)(ViewportHeight() / CellH));
    public int Count => _hits.Count;

    /// <summary>Switches between grid and list: the cell pool is rebuilt for the new layout, keeping the selection in view.</summary>
    public void SetListMode(bool list)
    {
        if (_list == list) return;
        _list = list;
        foreach (var c in _pool) c.Root.Destroy();
        _pool.Clear();
        _poolRows = 0;
        _dirty = true;
        SetContentHeight(RowCount * CellH);
        _scroll.NormalizedPosition.Value = new float2(0f, 0f);
        int sel = IndexOf(_selectedUri);
        Refresh();
        if (sel >= 0) ScrollToIndex(sel);
    }

    private int RowCount => (_hits.Count + Cols - 1) / Cols;

    // ---------------------------------------------------------------- data and selection

    public void SetResults(IReadOnlyList<SearchHit> hits, bool resetScroll)
    {
        _hits = hits;
        _dirty = true;
        foreach (var c in _pool) c.HitIndex = -1;
        SetContentHeight(RowCount * CellH);
        if (resetScroll) _scroll.NormalizedPosition.Value = new float2(0f, 0f);
        Refresh();
    }

    public SearchHit? HitAt(int index) => index >= 0 && index < _hits.Count ? _hits[index] : null;

    public int IndexOf(string? uri)
    {
        if (uri == null) return -1;
        for (int i = 0; i < _hits.Count; i++) if (_hits[i].Entry.Uri == uri) return i;
        return -1;
    }

    /// <summary>Scrolls just enough to bring the item's row fully into view (no-op when it already is).</summary>
    public void ScrollToIndex(int index)
    {
        if (index < 0 || index >= _hits.Count) return;
        float viewport = ViewportHeight();
        float scrollable = Math.Max(0f, RowCount * CellH - viewport);
        if (scrollable <= 0f) return;
        float rowTop = (index / Cols) * CellH;
        float offset = _scroll.NormalizedPosition.Value.y * scrollable;
        if (rowTop < offset) offset = rowTop;
        else if (rowTop + CellH > offset + viewport) offset = rowTop + CellH - viewport;
        else return;
        _scroll.NormalizedPosition.Value = new float2(0f, Math.Clamp(offset / scrollable, 0f, 1f));
    }

    public void SetSelected(string? uri)
    {
        _selectedUri = uri;
        foreach (var c in _pool)
        {
            if (c.HitIndex < 0) continue;
            bool on = c.HitIndex < _hits.Count && _hits[c.HitIndex].Entry.Uri == uri;
            bool wasOn = c.Selected.ActiveSelf;
            c.Selected.ActiveSelf = on;
            if (on && !wasOn)    // only the cell that just became selected animates
            {
                UiMotion.FadeIn(c.SelectedTint.Tint, SelTint, 0.12f);
            }
        }
    }

    // ---------------------------------------------------------------- scrolling window

    private void SetContentHeight(float h)
    {
        _content.AnchorMin.Value = new float2(0f, 1f);
        _content.AnchorMax.Value = new float2(1f, 1f);
        _content.OffsetMin.Value = new float2(0f, -h);
        _content.OffsetMax.Value = float2.Zero;
    }

    private float ViewportHeight()
    {
        float h = _viewport.LocalComputeRect.size.y;
        return h > 50f ? h : DefaultViewportH;
    }

    public void Refresh()
    {
        if (_content.Slot.IsRemoved) return;
        float viewportH = ViewportHeight();
        int cols = Cols; float cellH = CellH;
        // size the pool from a floor so an unmeasured first layout pass can't under-provision it
        int neededPoolRows = GridMath.PoolRows(viewportH, MinPoolViewportH, cellH);
        if (neededPoolRows > _poolRows) GrowPool(neededPoolRows);

        int topRow = GridMath.TopRow(RowCount, cellH, viewportH, _scroll.NormalizedPosition.Value.y);

        for (int r = topRow; r < topRow + _poolRows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int slot = GridMath.PoolSlot(r, c, _poolRows, cols);
                if (slot < 0 || slot >= _pool.Count) continue;      // defensive; GridMath keeps this in range
                var cell = _pool[slot];
                int idx = r * cols + c;
                if (idx < 0 || idx >= _hits.Count) { if (cell.Root.ActiveSelf) cell.Root.ActiveSelf = false; cell.HitIndex = -1; continue; }
                if (cell.HitIndex == idx && !_dirty) continue;
                Bind(cell, idx, r, c);
            }
        }
        _dirty = false;
    }

    private void GrowPool(int rows)
    {
        // Growing changes the row%pool mapping, so everything must be re-bound.
        for (int i = _pool.Count; i < rows * Cols; i++) _pool.Add(CreateCell(i));
        _poolRows = rows;
        _dirty = true;
        foreach (var c in _pool) c.HitIndex = -1;
    }

    // ---------------------------------------------------------------- cells

    private Cell CreateCell(int n)
    {
        var cell = new Cell();
        var rt = UiKit.Rect(_content, "Cell " + n, float2.Zero, float2.One);
        cell.Rect = rt; cell.Root = rt.Slot;

        var ui = new UIBuilder(rt);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        cell.Button = ui.Button();
        // let vertical drags/wheel through to the ScrollRect underneath
        cell.Button.PassThroughVerticalMovement.Value = true;
        cell.Button.PassThroughHorizontalMovement.Value = true;
        var br = cell.Button.RectTransform;
        cell.Button.LocalPressed += (b, e) =>
        {
            if (cell.HitIndex >= 0 && cell.HitIndex < _hits.Count) Clicked?.Invoke(_hits[cell.HitIndex]);
        };

        if (_list) BuildListCell(cell, br); else BuildGridCell(cell, br);

        cell.Root.ActiveSelf = false;
        return cell;
    }

    private static void BuildThumb(Cell cell, RectTransform well)
    {
        UiKit.RoundedPanel(well, UiKit.WithAlpha(UiKit.Bg, 0.55f));
        var tui = new UIBuilder(well);
        cell.Thumb = tui.Image();
        cell.Texture = well.Slot.AttachComponent<StaticTexture2D>();
        var sprite = well.Slot.AttachComponent<SpriteProvider>();
        sprite.Texture.Target = cell.Texture;
        cell.Thumb.Sprite.Target = sprite;
        cell.Thumb.PreserveAspect.Value = true;
        cell.Thumb.Tint.Value = UiKit.Clear;
        UiKit.AddKindIcons(well, out cell.FolderIcon, out cell.LinkIcon);
    }

    private static void BuildGridCell(Cell cell, RectTransform br)
    {
        // thumbnail well + image
        var thumbRt = UiKit.Rect(br, "Thumb", new float2(0.05f, 0.29f), new float2(0.95f, 0.97f));
        BuildThumb(cell, thumbRt);

        // copies and source pills (rounded translucent black, like the Worlds tab cards)
        var copies = UiKit.Pill(thumbRt, "Copies", new float2(0.62f, 0.80f), new float2(0.98f, 0.98f), TypeScale.Body, UiKit.Accent);
        cell.CopiesBg = copies.pill; cell.Copies = copies.text;
        var badge = UiKit.Pill(thumbRt, "Badge", new float2(0.02f, 0.80f), new float2(0.42f, 0.98f), TypeScale.Caption, UiKit.Text);       // colour is set per bind: folder = orange, link = aqua
        cell.BadgeBg = badge.pill; cell.Badge = badge.text;

        // name + subtitle (fit=true: long names/paths shrink instead of wrapping into each other)
        cell.Name = UiKit.Label(UiKit.Rect(br, "Name", new float2(0.05f, 0.14f), new float2(0.95f, 0.29f)), "", TypeScale.Label, Alignment.MiddleLeft, null, fit: true, minSize: 14f);
        cell.Sub = UiKit.Label(UiKit.Rect(br, "Sub", new float2(0.05f, 0.02f), new float2(0.95f, 0.14f)), "", TypeScale.Body, Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 11f);

        // selection overlay: a faint accent tint (the highlight alone marks the selected cell)
        var selRt = UiKit.Rect(br, "Selected", float2.Zero, float2.One);
        cell.SelectedTint = UiKit.RoundedPanel(selRt, SelTint);
        cell.Selected = selRt.Slot;
        cell.Selected.ActiveSelf = false;
    }

    private static void BuildListCell(Cell cell, RectTransform br)
    {
        // thumbnail at the left, name over path, then category / owner / date columns and the ×N pill on the right
        var thumbRt = UiKit.Rect(br, "Thumb", new float2(0.010f, 0.10f), new float2(0.070f, 0.90f));
        BuildThumb(cell, thumbRt);

        cell.Name = UiKit.Label(UiKit.Rect(br, "Name", new float2(0.085f, 0.50f), new float2(0.50f, 0.96f)), "", TypeScale.Label, Alignment.MiddleLeft, null, fit: true, minSize: 14f);
        cell.Sub = UiKit.Label(UiKit.Rect(br, "Sub", new float2(0.085f, 0.04f), new float2(0.50f, 0.50f)), "", TypeScale.Body, Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 11f);
        cell.Category = UiKit.Label(UiKit.Rect(br, "Category", new float2(0.52f, 0.10f), new float2(0.62f, 0.90f)), "", TypeScale.Body, Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 11f);
        cell.Owner = UiKit.Label(UiKit.Rect(br, "Owner", new float2(0.63f, 0.10f), new float2(0.80f, 0.90f)), "", TypeScale.Body, Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 11f);
        cell.Date = UiKit.Label(UiKit.Rect(br, "Date", new float2(0.81f, 0.10f), new float2(0.92f, 0.90f)), "", TypeScale.Body, Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 11f);

        var copies = UiKit.Pill(br, "Copies", new float2(0.925f, 0.26f), new float2(0.99f, 0.74f), TypeScale.Body, UiKit.Accent);
        cell.CopiesBg = copies.pill; cell.Copies = copies.text;

        // selection: a faint accent tint
        var selRt = UiKit.Rect(br, "Selected", float2.Zero, float2.One);
        cell.SelectedTint = UiKit.RoundedPanel(selRt, SelTint);
        cell.Selected = selRt.Slot;
        cell.Selected.ActiveSelf = false;
    }

    private void Bind(Cell cell, int idx, int row, int col)
    {
        var hit = _hits[idx];
        var e = hit.Entry;
        cell.HitIndex = idx;

        {
            int cols = Cols; float cellH = CellH, gap = Gap;
            var rt = cell.Rect;
            rt.AnchorMin.Value = new float2(col / (float)cols, 1f);
            rt.AnchorMax.Value = new float2((col + 1) / (float)cols, 1f);
            rt.OffsetMin.Value = new float2(gap * 0.5f, -(row + 1) * cellH + gap * 0.5f);
            rt.OffsetMax.Value = new float2(-gap * 0.5f, -row * cellH - gap * 0.5f);
        }

        cell.Name.Content.Value = UiKit.HeadEllipsis(Core.Text.StripRichText(e.NameRaw), 48);
        cell.Sub.Content.Value = _list ? SubtitleOf(e, 60, includeOwner: false) : SubtitleOf(e, 34);

        // thumbnail (lazy: only bound cells ever request a texture)
        Uri? thumb = null;
        if (!string.IsNullOrEmpty(e.ThumbnailUri)) Uri.TryCreate(e.ThumbnailUri, UriKind.Absolute, out thumb);
        cell.Texture.URL.Value = thumb;
        cell.Thumb.Tint.Value = thumb != null ? colorX.White : UiKit.Clear;
        UiKit.ShowKindIcon(cell.FolderIcon, cell.LinkIcon, e.Category, hasThumbnail: thumb != null);

        bool many = hit.Copies > 1;
        cell.CopiesBg.ActiveSelf = many;
        if (many) cell.Copies.Content.Value = "×" + hit.Copies;

        if (_list)
        {
            cell.Category.Content.Value = UiKit.Capitalize(e.Category);
            cell.Owner.Content.Value = e.Source == Source.Personal ? "yours" : GroupInventories.NameOf(e.OwnerId);
            cell.Date.Content.Value = e.LastModified.Year > 2000 ? e.LastModified.ToLocalTime().ToString("yyyy-MM-dd") : "";
        }
        else
        {
            bool shared = e.Source != Source.Personal;
            cell.BadgeBg.ActiveSelf = shared || e.Category == "folder" || e.Category == "link";
            cell.Badge.Content.Value = e.Source == Source.Group ? "Group" : e.Source == Source.Linked ? "Linked"
                                     : e.Category == "folder" ? "Folder" : "Link";
            // same colours as the big icons: folders orange, links aqua; the source badges stay neutral
            cell.Badge.Color.Value = shared ? UiKit.Text : e.Category == "folder" ? UiKit.Warm : UiKit.Accent;
        }

        cell.Selected.ActiveSelf = _selectedUri != null && e.Uri == _selectedUri;
        cell.Root.ActiveSelf = true;
    }

    internal static string SubtitleOf(IndexEntry e, int maxLen = int.MaxValue, bool includeOwner = true)
    {
        string path = e.Path;
        const string root = "Inventory\\";
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) path = path.Substring(root.Length);
        else if (path.Equals("Inventory", StringComparison.OrdinalIgnoreCase)) path = "";
        string owner = includeOwner && e.Source != Source.Personal ? GroupInventories.NameOf(e.OwnerId) + " · " : "";
        string p = path.Length == 0 ? "/" : Core.Text.StripRichText(path);
        return owner + UiKit.TailEllipsis(p, Math.Max(8, maxLen - owner.Length));
    }
}
