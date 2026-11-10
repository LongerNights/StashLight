using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using InventorySearch.Core;
using Renderite.Shared;
using ResoniteModLoader;
using UText = FrooxEngine.UIX.Text;

namespace InventorySearch.Ui;

/// <summary>
/// The "Search" dash screen. Layout (1920x1080 canvas): header (search box, scope, sort) + category chips,
/// virtualized grid on the left, details/actions pane on the right, status bar at the bottom.
/// All UI mutation is on the userspace world thread; queries run on the thread pool and post back.
/// </summary>
internal sealed class SearchScreen
{
    private static SearchScreen? _instance;

    public static void TryBuild(UserspaceScreensManager manager)
    {
        try
        {
            if (manager.World != Userspace.UserspaceWorld) return;
            if (_instance != null && !_instance._screen.IsRemoved) return;
            var dash = manager.Slot.GetComponentInParents<RadiantDash>(null, true, false);
            if (dash == null) return;
            _instance = new SearchScreen(dash);
            ResoniteMod.Msg("[InventorySearch] Search screen created");
        }
        catch (Exception ex) { ResoniteMod.Error("[InventorySearch] failed to build Search screen: " + ex); }
    }

    private static readonly (string label, string? cat)[] Chips =
    {
        ("All", null), ("Avatars", "avatar"), ("Worlds", "world"), ("Objects", "object"), ("Tools", "tool"),
        ("Facets", "facet"), ("Media", "media"), ("Folders", "folder"), ("Links", "link"),
    };

    private readonly RadiantDashScreen _screen;
    private readonly World _world;
    private ResultGrid? _grid;
    private UText? _status;
    private Button _scopeButton = null!, _sortButton = null!, _linkedButton = null!, _linksButton = null!, _viewButton = null!;
    private LinksPanel? _linksPanel;
    private TextField? _searchField;
    private bool _wasEditing, _keyboardWarned;
    private readonly Dictionary<Key, KeyRepeater> _repeaters = new();
    private readonly Button[] _chipButtons = new Button[Chips.Length];
    private readonly Slot[] _chipBars = new Slot[Chips.Length];

    // details pane
    private UText _detailName = null!, _detailMeta = null!;
    private StaticTexture2D _detailTex = null!;
    private Image _detailThumb = null!;
    private RectTransform _detailFolderIcon = null!, _detailLinkIcon = null!;
    private Button _spawnBtn = null!, _ctxBtn = null!, _folderBtn = null!, _copyBtn = null!;
    private Slot _detailContent = null!, _detailEmpty = null!;

    // state
    private volatile string _text = "";
    private volatile string? _category;
    private volatile string _scopeKey = "mine";   // all | mine | owner:<ownerId>
    private string _scopeName = "Mine";
    private Slot? _scopeMenu, _scopeScrim;
    private RectTransform? _scopeMenuRect, _scopeButtonRect, _headerRect, _rootRect;
    private Image? _scopeMenuImage;
    private Slot? _scopeMenuBg;
    private bool _detailsShownOnce;
    private long _lastClickTick;
    private int _sortIndex;             // 0 relevance, 1 name, 2 newest
    private int _version;
    private SearchHit? _selected;
    private bool _resetScrollNext = true;
    private SearchResult? _last;

    private static readonly SortMode[] Sorts = { SortMode.Relevance, SortMode.Name, SortMode.DateModified };
    private static readonly string[] SortLabels = { "Sort: Relevance", "Sort: Name", "Sort: Newest" };

    // ---- diagnostics: the same code paths a click / key press runs, callable from Api (McpLink) for verification ----
    internal static string DiagOpenLinks() => Diag(s => s._linksPanel?.Open());
    internal static string DiagCloseLinks() => Diag(s => s._linksPanel?.Close());
    internal static string DiagSelect(int index) => Diag(s =>
    {
        if (s._grid?.HitAt(index) is { } hit) { s.SelectHit(hit); s._grid.ScrollToIndex(index); }
    });

    private static string Diag(Action<SearchScreen> act)
    {
        var s = _instance;
        if (s == null || s._screen.IsRemoved) return "no Search screen";
        s._world.RunSynchronously(() => { try { act(s); } catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] diagnostic failed: " + ex); } });
        return "requested";
    }

    /// <summary>The Inventory tab may not exist yet when this screen is built (it is added by the dash's own setup), so retry
    /// for a few seconds instead of giving up.</summary>
    private void PlaceNextToInventory(RadiantDash dash)
    {
        var screen = _screen;
        screen.Slot.StartTask(async () =>
        {
            for (int i = 0; i < 600 && !screen.IsRemoved; i++)
            {
                long? order = OrderRightOfInventory(dash, screen);
                if (order != null) { screen.Slot.OrderOffset = order.Value; ResoniteMod.Msg($"[InventorySearch] Search tab placed next to Inventory (order {order})"); return; }
                await default(NextUpdate);
            }
            ResoniteMod.Warn("[InventorySearch] no Inventory tab found; Search tab stays at the end");
        });
    }

    /// <summary>The dash orders its tabs by slot OrderOffset. Put Search halfway between the Inventory tab and whichever tab
    /// comes next (the stock tabs are 10 apart), so it sits directly to the right of Inventory. Falls back to the end.</summary>
    private static long? OrderRightOfInventory(RadiantDash dash, RadiantDashScreen exclude)
    {
        try
        {
            var inventory = dash.GetScreen<LegacyRadiantScreenWrapper<InventoryBrowser>>();
            if (inventory == null) return null;
            long inv = inventory.Slot.OrderOffset;
            long next = long.MaxValue;
            foreach (var other in dash.Screens)
            {
                if (other == inventory || other == exclude) continue;
                long o = other.Slot.OrderOffset;
                if (o > inv && o < next) next = o;
            }
            return next == long.MaxValue ? inv + 10 : inv + Math.Max(1, (next - inv) / 2);
        }
        catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] could not place the Search tab next to Inventory: " + ex.Message); return null; }
    }

    private SearchScreen(RadiantDash dash)
    {
        _screen = dash.AttachScreen("Search", Theme.Tab, OfficialAssets.Common.Icons.Search);
        _world = _screen.World;
        _screen.Slot.OrderOffset = 200L;
        PlaceNextToInventory(dash);
        _screen.Slot.PersistentSelf = false;     // dash screens autosave to the cloud workspace; never persist ours

        try { Build(_screen.ScreenCanvas); }
        catch { _screen.Slot.Destroy(); throw; }     // don't leave a half-built screen/button on the dash
        IndexService.Published += OnIndexPublished;
        StartKeyboard();
        ScheduleQuery(resetScroll: true);
    }

    // ---------------------------------------------------------------- layout

    private void Build(Canvas canvas)
    {
        var ui = new UIBuilder(canvas);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        var root = ui.Panel();
        Guard("background", () =>
        {
            // the stock Worlds tab look: its tiled texture under a Dark tint at 96.5 %
            var bg = new UIBuilder(root).Next("Background");
            var tex = bg.AttachComponent<StaticTexture2D>();
            tex.URL.Value = Theme.StockBackground;
            tex.WrapModeU.Value = Renderite.Shared.TextureWrapMode.Repeat;
            tex.WrapModeV.Value = Renderite.Shared.TextureWrapMode.Repeat;
            var tiled = bg.AttachComponent<TiledRawImage>();
            tiled.Texture.Target = tex;
            tiled.Tint.Value = Theme.Background;
            tiled.TileSize.Value = new float2(768f, 768f);
        });

        // Each section is built independently: a failure is logged by name and the rest still builds,
        // so one bug doesn't hide every other problem behind a single exception.
        Guard("header", () => BuildHeader(root));
        Guard("chips", () => BuildChips(root));
        Guard("grid", () => BuildGrid(root));
        Guard("details", () =>
        {
            var details = UiKit.Rect(root, "Details", new float2(0.70f, 0), new float2(1, 1), new float2(8, 60), new float2(-24, -190));
            UiKit.RoundedPanel(details, UiKit.WithAlpha(UiKit.Surface, 0.35f));
            BuildDetails(details);
            _detailsOk = true;
        });
        Guard("scope menu", () =>
        {
            // drawn last = on top of the grid; sits under the Scope button (x range matches the header layout)
            // click-away catcher first (so it sits under the menu), then the menu itself
            _scopeScrim = UiKit.Scrim(root, "ScopeScrim", CloseScopeMenu);
            var menu = UiKit.Rect(root, "ScopeMenu", new float2(0, 1), new float2(0, 1), new float2(0, -300), new float2(0, -102));   // x is set from the Scope button when opened (PositionScopeMenu)
            _rootRect = root;
            // background in its own child ("Bg") so rebuilding the option rows never deletes it; opaque with a thin outline
            var bg = UiKit.Fill(menu, "Bg");
            UiKit.RoundedPanel(bg, RadiantUI_Constants.Neutrals.MIDLIGHT);
            var inner = UiKit.Rect(bg, "Inner", float2.Zero, float2.One, new float2(2f, 2f), new float2(-2f, -2f));
            _scopeMenuImage = UiKit.RoundedPanel(inner, Theme.Pane);
            _scopeMenuBg = bg.Slot;
            _scopeMenuRect = menu;
            _scopeMenu = menu.Slot;
            _scopeMenu.ActiveSelf = false;
        });
        Guard("links panel", () => _linksPanel = new LinksPanel(root));
        Guard("status", () =>
        {
            var foot = UiKit.Rect(root, "Status", new float2(0, 0), new float2(1, 0), new float2(24, 8), new float2(-24, 52));
            _status = UiKit.Label(foot, "Indexing…", TypeScale.Body, Alignment.MiddleLeft, UiKit.Muted);
        });
    }

    private bool _detailsOk;

    private static void Guard(string section, Action build)
    {
        try { build(); }
        catch (Exception ex) { ResoniteMod.Error($"[InventorySearch] UI section '{section}' failed: {ex}"); }
    }

    private void BuildHeader(RectTransform root)
    {
        var h1 = _headerRect = UiKit.Rect(root, "Header", new float2(0, 1), new float2(1, 1), new float2(24, -100), new float2(-24, -20));
        Guard("header/search", () =>
        {
            var fieldRt = UiKit.Rect(h1, "Search", new float2(0, 0), new float2(0.42f, 1));
            var fui = new UIBuilder(fieldRt);
            RadiantUI_Constants.SetupDefaultStyle(fui);
            var tf = fui.TextField("", false, null, false);
            _searchField = tf;
            // The prompt (NullContent) is only drawn while Content is null, and TextField starts it as "" -> null it,
            // and have the editor turn an emptied field back into null when editing finishes.
            tf.TargetString = null;
            tf.Editor.Target.FinishHandling.Value = TextEditor.FinishAction.NullOnWhitespace;
            var ft = tf.Text;
            ft.NullContent.Value = "Search your inventory…";
            ft.Color.Value = UiKit.Text;
            // fixed larger size (no best-fit shrinking), left aligned with some padding
            ft.AlignmentMode.Value = AlignmentMode.LineBased;
            ft.Size.Value = TypeScale.Hero;
            ft.AutoSizeMin.Value = TypeScale.Hero;
            ft.AutoSizeMax.Value = TypeScale.Hero;
            ft.HorizontalAlign.Value = TextHorizontalAlignment.Left;
            ft.VerticalAlign.Value = TextVerticalAlignment.Middle;
            // Line-based text sits low in its box (the Worlds tab mod measured it and raises each text by 0.25 x the font
            // size, see Discover's ApplyTextLift). Raise this one the same amount so it is vertically centered.
            float lift = TypeScale.Hero * 0.25f;
            ft.RectTransform.OffsetMin.Value = new float2(22f, lift);
            ft.RectTransform.OffsetMax.Value = new float2(-12f, lift);
            tf.Editor.Target.LocalEditingChanged += ed =>
            {
                _text = ed.TargetString ?? "";
                ScheduleQuery(resetScroll: true);
            };
        });
        Guard("header/scope", () =>
            _scopeButton = UiKit.TextButton(_scopeButtonRect = UiKit.Rect(h1, "Scope", new float2(0.44f, 0), new float2(0.58f, 1)), "Scope: Mine", (b, e) => ToggleScopeMenu(), TypeScale.Label));
        // Linked content is not an inventory, so it is a separate on/off toggle (accent colour = on), remembered in the mod settings.
        Guard("header/linked", () =>
        {
            _linkedButton = UiKit.TextButton(UiKit.Rect(h1, "Linked", new float2(0.60f, 0), new float2(0.70f, 1)), "Linked: Off", (b, e) => ToggleLinked(), TypeScale.Label);
            RefreshLinkedButton();
        });
        // the control centre for linked content: per-link on/off, status, re-crawl (all or one)
        Guard("header/links", () =>
            _linksButton = UiKit.TextButton(UiKit.Rect(h1, "Links", new float2(0.71f, 0), new float2(0.78f, 1)), "Links…", (b, e) => _linksPanel?.Toggle(), TypeScale.Label));
        Guard("header/sort", () =>
            _sortButton = UiKit.TextButton(UiKit.Rect(h1, "Sort", new float2(0.80f, 0), new float2(0.91f, 1)), SortLabels[0], (b, e) =>
            {
                _sortIndex = (_sortIndex + 1) % Sorts.Length;
                UiKit.SetLabel(_sortButton, SortLabels[_sortIndex]);
                ScheduleQuery(resetScroll: true);
            }, TypeScale.Label));
        Guard("header/view", () =>
        {
            _viewButton = UiKit.TextButton(UiKit.Rect(h1, "View", new float2(0.92f, 0), new float2(1, 1)), "View: Grid", (b, e) => ToggleView(), TypeScale.Label);
            RefreshViewButton();
        });
    }

    private void BuildChips(RectTransform root)
    {
        var h2 = UiKit.Rect(root, "Chips", new float2(0, 1), new float2(1, 1), new float2(24, -176), new float2(-24, -108));
        for (int i = 0; i < Chips.Length; i++)
        {
            int idx = i;
            var cr = UiKit.Rect(h2, "Chip " + Chips[i].label, new float2(i / (float)Chips.Length, 0), new float2((i + 1) / (float)Chips.Length, 1),
                                new float2(3, 0), new float2(-3, 0));
            _chipButtons[i] = UiKit.TextButton(cr, Chips[i].label, (b, e) =>
            {
                _category = Chips[idx].cat;
                UpdateChipBars();
                ScheduleQuery(resetScroll: true);
            }, TypeScale.Label);
        }
        UpdateChipBars();
    }

    private void BuildGrid(RectTransform root)
    {
        var gridArea = UiKit.Rect(root, "Grid", new float2(0, 0), new float2(0.70f, 1), new float2(24, 60), new float2(-8, -190));
        var grid = new ResultGrid(gridArea, InventorySearchMod.Get(InventorySearchMod.ListViewKey, false));
        grid.Clicked += OnCellClicked;
        _grid = grid;
        RefreshViewButton();           // the header is built before the grid, so label the View button now
    }

    private void BuildDetails(RectTransform details)
    {
        _detailEmpty = UiKit.Rect(details, "Empty", float2.Zero, float2.One).Slot;
        UiKit.Label(_detailEmpty.GetComponent<RectTransform>(), "Select an item\n\nDouble-click to spawn it", TypeScale.Title, Alignment.MiddleCenter, UiKit.Muted);

        var c = UiKit.Fill(details, "Content");
        _detailContent = c.Slot;

        var thumbRt = UiKit.Rect(c, "Thumb", new float2(0.06f, 0.60f), new float2(0.94f, 0.97f));
        UiKit.RoundedPanel(thumbRt, UiKit.WithAlpha(UiKit.Bg, 0.55f));
        var tui = new UIBuilder(thumbRt);
        _detailThumb = tui.Image();
        _detailTex = thumbRt.Slot.AttachComponent<StaticTexture2D>();
        var sp = thumbRt.Slot.AttachComponent<SpriteProvider>();
        sp.Texture.Target = _detailTex;
        _detailThumb.Sprite.Target = sp;
        _detailThumb.PreserveAspect.Value = true;
        UiKit.AddKindIcons(thumbRt, out _detailFolderIcon, out _detailLinkIcon);

        _detailName = UiKit.Label(UiKit.Rect(c, "Name", new float2(0.06f, 0.52f), new float2(0.94f, 0.60f)), "", TypeScale.Title, Alignment.MiddleLeft, null, fit: true, minSize: 18f);
        _detailMeta = UiKit.Label(UiKit.Rect(c, "Meta", new float2(0.06f, 0.30f), new float2(0.94f, 0.52f)), "", TypeScale.Body, Alignment.TopLeft, UiKit.Muted, fit: true, minSize: 12f);

        _spawnBtn = UiKit.TextButton(UiKit.Rect(c, "Spawn", new float2(0.06f, 0.21f), new float2(0.94f, 0.29f)), "Spawn", (b, e) => RunPrimary(), TypeScale.Label);
        UiKit.SetButtonColor(_spawnBtn, Theme.ButtonSelected);       // the one primary action in the panel
        _ctxBtn = UiKit.TextButton(UiKit.Rect(c, "Context", new float2(0.06f, 0.12f), new float2(0.94f, 0.20f)), "Equip", (b, e) => RunContext(), TypeScale.Label);
        _folderBtn = UiKit.TextButton(UiKit.Rect(c, "Folder", new float2(0.06f, 0.03f), new float2(0.48f, 0.11f)), "Open folder", (b, e) => { if (_selected != null) ItemActions.OpenFolder(_selected.Entry); }, TypeScale.Body);
        _copyBtn = UiKit.TextButton(UiKit.Rect(c, "Copy", new float2(0.52f, 0.03f), new float2(0.94f, 0.11f)), "Copy link", (b, e) => { if (_selected != null) ItemActions.CopyLink(_selected.Entry); }, TypeScale.Body);

        ShowDetails(null);
    }

    private void UpdateChipBars()
    {
        // the selected chip is the dash's cyan accent, the rest the stock Mid grey (as on the Worlds tab)
        for (int i = 0; i < Chips.Length; i++)
            if (_chipButtons[i] != null) UiKit.SetButtonColor(_chipButtons[i], Chips[i].cat == _category ? Theme.ButtonSelected : Theme.Button);
    }

    // ---------------------------------------------------------------- selection & actions

    private void OnCellClicked(SearchHit hit)
    {
        // single click only selects; a double click (same item within 0.5 s) does the primary action.
        long now = Environment.TickCount64;
        bool doubleClick = _selected != null && _selected.Entry.Uri == hit.Entry.Uri && now - _lastClickTick < 500;
        _lastClickTick = now;
        if (doubleClick) { RunPrimary(); return; }
        SelectHit(hit);
    }

    private void SelectHit(SearchHit hit)
    {
        _selected = hit;
        _grid?.SetSelected(hit.Entry.Uri);
        ShowDetails(hit);
    }

    private static bool Spawnable(IndexEntry e) => e.Category != "folder" && e.Category != "link";

    private void RunPrimary()
    {
        if (_selected == null) return;
        var e = _selected.Entry;
        if (Spawnable(e)) ItemActions.Spawn(e); else ItemActions.OpenFolder(e);
    }

    private void RunContext()
    {
        if (_selected == null) return;
        var e = _selected.Entry;
        if (e.Category == "avatar") ItemActions.Equip(e);
        else if (e.Category == "world") ItemActions.OpenWorld(e);
        else if (ItemActions.CanSpawnFacet(e)) ItemActions.SpawnFacet(e, _world);
    }

    private void ShowDetails(SearchHit? hit)
    {
        if (!_detailsOk && hit != null) return;
        _detailEmpty.ActiveSelf = hit == null;
        _detailContent.ActiveSelf = hit != null;
        if (hit == null) return;
        var e = hit.Entry;

        _detailName.Content.Value = Core.Text.StripRichText(e.NameRaw);
        Uri? thumb = null;
        if (!string.IsNullOrEmpty(e.ThumbnailUri)) Uri.TryCreate(e.ThumbnailUri, UriKind.Absolute, out thumb);
        _detailTex.URL.Value = thumb;
        _detailThumb.Tint.Value = thumb != null ? colorX.White : UiKit.Clear;
        if (thumb != null) UiMotion.FadeIn(_detailThumb.Tint, colorX.White, 0.22f);
        if (!_detailsShownOnce)
        {
            _detailsShownOnce = true;      // rise in once (like a Worlds tab panel opening), not on every click
            UiMotion.SlideIn(_detailContent.GetComponent<RectTransform>(), new float2(0f, -0.03f), 0.30f);
        }
        UiKit.ShowKindIcon(_detailFolderIcon, _detailLinkIcon, e.Category, hasThumbnail: thumb != null);

        var lines = new List<string>
        {
            UiKit.Capitalize(e.Category) + (e.RecordType != e.Category && e.RecordType.Length > 0 ? $" ({e.RecordType})" : ""),
            "in " + ResultGrid.SubtitleOf(e),
            "from " + (e.Source == Source.Personal ? "your inventory" : GroupInventories.NameOf(e.OwnerId) + (e.Source == Source.Linked ? " (linked)" : "")),
            "modified " + e.LastModified.ToLocalTime().ToString("yyyy-MM-dd"),
        };
        if (hit.Copies > 1) lines.Add($"{hit.Copies} saved copies in this folder (the newest is used)");
        if (e.Tags.Length > 0) lines.Add("tags: " + UiKit.HeadEllipsis(string.Join(", ", e.Tags.Take(5)), 60));
        _detailMeta.Content.Value = string.Join("\n", lines);

        bool spawnable = Spawnable(e);
        _spawnBtn.Slot.ActiveSelf = true;
        UiKit.SetLabel(_spawnBtn, spawnable ? "Spawn" : "Open folder");
        bool avatar = e.Category == "avatar", world = ItemActions.CanOpenWorld(e);
        bool facet = ItemActions.CanSpawnFacet(e);
        _ctxBtn.Slot.ActiveSelf = avatar || world || facet;
        if (facet) UiKit.SetLabel(_ctxBtn, "Spawn facet"); else if (avatar) UiKit.SetLabel(_ctxBtn, "Equip avatar"); else if (world) UiKit.SetLabel(_ctxBtn, "Open world");
        _folderBtn.Slot.ActiveSelf = spawnable;      // for folders the primary button already opens them
    }

    // ---------------------------------------------------------------- queries

    private void OnIndexPublished()
    {
        if (_screen.IsRemoved) return;
        ScheduleQuery(resetScroll: false);
        if (_linksPanel != null) _world.RunSynchronously(() => { if (!_screen.IsRemoved) _linksPanel?.Refresh(); });
    }

    private void ScheduleQuery(bool resetScroll)
    {
        if (resetScroll) _resetScrollNext = true;
        int v = Interlocked.Increment(ref _version);
        string text = _text; string? cat = _category; string scopeKey = _scopeKey; int sortIdx = _sortIndex;
        bool includeLinked = IncludeLinked;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60).ConfigureAwait(false);           // debounce keystrokes
                if (v != Volatile.Read(ref _version)) return;
                var q = Query.Parse(text);
                ApplyScope(q, scopeKey);
                q.ExcludeLinked = !includeLinked;
                var index = IndexService.Index;
                var res = QueryEngine.Search(index, q, Sorts[sortIdx], 3000, default, true, cat);
                string status = BuildStatus(res, index, includeLinked);
                _world.RunSynchronously(() =>
                {
                    if (_screen.IsRemoved || v != Volatile.Read(ref _version)) return;
                    Apply(res, status);
                });
            }
            catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] query failed: " + ex.Message); }
        });
    }

    private static string BuildStatus(SearchResult res, SearchIndex index, bool includeLinked)
    {
        string results = res.Total == res.TotalEntries || res.TotalEntries == 0
            ? $"{res.Total:N0} results"
            : $"{res.Total:N0} results ({res.TotalEntries:N0} items)";
        string phase = IndexService.Phase == "ready" ? "" : $" · indexing: {IndexService.Phase}";
        return $"{results}{(res.Fuzzy ? " · fuzzy" : "")} · {res.ElapsedMs:F1} ms · index {index.PersonalCount:N0} mine{(index.SharedCount > 0 ? $" + {index.SharedCount:N0} groups" : "")} + {index.LinkedCount:N0} linked{(includeLinked ? "" : " (hidden)")}{phase}{(LinkCrawler.Progress.Length > 0 ? " · " + LinkCrawler.Progress : "")}";
    }

    private void Apply(SearchResult res, string status)
    {
        _last = res;
        if (_status != null) _status.Content.Value = status;

        // chips show per-category row counts for the current text query
        for (int i = 0; i < Chips.Length; i++)
        {
            int n = Chips[i].cat == null ? res.CategoryCounts.Values.Sum() : res.CategoryCounts.GetValueOrDefault(Chips[i].cat!);
            if (_chipButtons[i] != null) UiKit.SetLabel(_chipButtons[i], n > 0 ? $"{Chips[i].label} {n:N0}" : Chips[i].label);
        }

        bool reset = _resetScrollNext; _resetScrollNext = false;
        try { _grid?.SetResults(res.Hits, reset); }
        catch (Exception ex) { ResoniteMod.Error("[InventorySearch] updating the result grid failed: " + ex); }
    }

    // ---------------------------------------------------------------- view toggle (grid / list)

    private void ToggleView()
    {
        if (_grid == null) return;
        bool list = !_grid.ListMode;
        try { InventorySearchMod.Cfg?.Set(InventorySearchMod.ListViewKey, list); }
        catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] could not save the View setting: " + ex.Message); }
        _grid.SetListMode(list);
        RefreshViewButton();
    }

    private void RefreshViewButton()
    {
        if (_viewButton != null && _grid != null) UiKit.SetLabel(_viewButton, _grid.ListMode ? "View: List" : "View: Grid");
    }

    // ---------------------------------------------------------------- keyboard navigation

    private static readonly (Key key, NavKey nav)[] NavKeys =
    {
        (Key.UpArrow, NavKey.Up), (Key.DownArrow, NavKey.Down), (Key.LeftArrow, NavKey.Left), (Key.RightArrow, NavKey.Right),
        (Key.PageUp, NavKey.PageUp), (Key.PageDown, NavKey.PageDown), (Key.Home, NavKey.Home), (Key.End, NavKey.End),
    };

    /// <summary>
    /// Arrow keys / Page keys / Home / End move the selection (the view scrolls to keep it visible), Enter does the primary
    /// action (spawn, or open for folders), Esc clears the search text, then the selection (and closes menus first).
    /// Polled once per frame on this screen's world; only active while the dash is open on this screen, and never while you
    /// are typing in the search box (nor on the frame right after, so Enter/Esc that ended the typing don't also act).
    /// </summary>
    private void StartKeyboard()
    {
        _screen.Slot.StartTask(async () =>
        {
            while (!_screen.IsRemoved)
            {
                await default(NextUpdate);
                try { PollKeys(); }
                catch (Exception ex)
                {
                    if (!_keyboardWarned) { _keyboardWarned = true; ResoniteMod.Warn("[InventorySearch] keyboard navigation error: " + ex.Message); }
                }
            }
        });
    }

    private void PollKeys()
    {
        var grid = _grid;
        if (grid == null || !_screen.IsShown) return;
        var dash = _screen.Slot.GetComponentInParents<RadiantDash>(null, true, false);
        if (dash == null || !dash.Open.Value) return;

        bool editing = _searchField?.Editor.Target?.IsEditing == true;
        bool justFinishedTyping = _wasEditing && !editing;
        _wasEditing = editing;
        if (editing || justFinishedTyping) return;

        var input = Engine.Current.InputInterface;

        // overlays take Esc first
        if (_linksPanel != null && _linksPanel.IsOpen) { if (input.GetKeyDown(Key.Escape)) _linksPanel.Close(); return; }
        if (_scopeMenu != null && _scopeMenu.ActiveSelf) { if (input.GetKeyDown(Key.Escape)) CloseScopeMenu(); return; }

        double now = Environment.TickCount64;
        foreach (var (key, nav) in NavKeys)
        {
            if (!_repeaters.TryGetValue(key, out var repeater)) _repeaters[key] = repeater = new KeyRepeater();
            if (!repeater.Poll(input.GetKey(key), now)) continue;
            int current = grid.IndexOf(_selected?.Entry.Uri);
            int next = GridNavigation.Move(current, grid.Count, grid.Columns, grid.VisibleRows, nav);
            if (next >= 0 && next != current && grid.HitAt(next) is { } hit) { SelectHit(hit); grid.ScrollToIndex(next); }
        }

        if ((input.GetKeyDown(Key.Return) || input.GetKeyDown(Key.KeypadEnter)) && _selected != null) RunPrimary();

        if (input.GetKeyDown(Key.Escape))
        {
            if (_searchField != null && !string.IsNullOrEmpty(_searchField.TargetString))
            {
                _searchField.TargetString = null;          // back to the prompt
                _text = "";
                ScheduleQuery(resetScroll: true);
            }
            else if (_selected != null)
            {
                _selected = null; _grid?.SetSelected(null); ShowDetails(null);
            }
        }
    }

    // ---------------------------------------------------------------- linked toggle

    private static bool IncludeLinked => InventorySearchMod.Get(InventorySearchMod.IncludeLinkedKey, false);

    private void ToggleLinked()
    {
        bool now = !IncludeLinked;
        try { InventorySearchMod.Cfg?.Set(InventorySearchMod.IncludeLinkedKey, now); }
        catch (Exception ex) { ResoniteMod.Warn("[InventorySearch] could not save the Linked setting: " + ex.Message); }
        RefreshLinkedButton();
        if (now) LinkCrawler.Wake(); else LinkCrawler.Cancel();     // turning it on starts crawling; off stops it
        ScheduleQuery(resetScroll: true);
    }

    private void RefreshLinkedButton()
    {
        if (_linkedButton == null) return;
        bool on = IncludeLinked;
        UiKit.SetLabel(_linkedButton, on ? "Linked: On" : "Linked: Off");
        UiKit.SetButtonColor(_linkedButton, on ? Theme.ButtonSelected : Theme.Button);
    }

    // ---------------------------------------------------------------- scope dropdown

    private static void ApplyScope(Query q, string key)
    {
        if (key == "mine") q.ExcludeGroups = true;       // your inventory plus what your links lead to (the Linked toggle still decides whether linked content is shown)
        else if (key.StartsWith("owner:", StringComparison.Ordinal)) q.ScopeOwner = key.Substring(6);
    }

    private void ToggleScopeMenu()
    {
        if (_scopeMenu == null) return;
        if (_scopeMenu.ActiveSelf) { CloseScopeMenu(); return; }
        RebuildScopeMenu();
        PositionScopeMenu();
        if (_scopeScrim != null) _scopeScrim.ActiveSelf = true;
        _scopeMenu.ActiveSelf = true;
        // drops in from slightly above and fades up (the Worlds tab popover motion)
        if (_scopeMenuRect != null) UiMotion.SlideIn(_scopeMenuRect, new float2(0f, 0.03f), 0.30f);
        if (_scopeMenuImage != null) UiMotion.FadeIn(_scopeMenuImage.Tint, Theme.Pane, 0.20f);
    }

    /// <summary>Lines the menu up under the Scope button from the button's own anchors and the header's offsets, so it follows
    /// any change to the header layout. The Scope button's x range is resolved against the header's pixel width.</summary>
    private void PositionScopeMenu()
    {
        if (_scopeMenuRect == null || _scopeButtonRect == null || _headerRect == null || _rootRect == null) return;
        float rootW = _rootRect.Canvas?.Size.Value.x ?? 0f;
        if (rootW <= 0f) return;
        float left = _headerRect.OffsetMin.Value.x, right = _headerRect.OffsetMax.Value.x;      // header inset (24, -24)
        float headerW = rootW + right - left;
        float x0 = left + headerW * _scopeButtonRect.AnchorMin.Value.x + _scopeButtonRect.OffsetMin.Value.x;
        float x1 = left + headerW * _scopeButtonRect.AnchorMax.Value.x + _scopeButtonRect.OffsetMax.Value.x;
        _scopeMenuRect.OffsetMin.Value = new float2(x0, _scopeMenuRect.OffsetMin.Value.y);
        _scopeMenuRect.OffsetMax.Value = new float2(x1, _scopeMenuRect.OffsetMax.Value.y);
    }

    private void CloseScopeMenu()
    {
        if (_scopeMenu != null) _scopeMenu.ActiveSelf = false;
        if (_scopeScrim != null) _scopeScrim.ActiveSelf = false;
    }

    private void RebuildScopeMenu()
    {
        var menu = _scopeMenu!;
        foreach (var child in menu.Children.ToList())          // fresh option rows each open: groups/counts may have changed
            if (child != _scopeMenuBg) child.Destroy();            // ...but keep the background
        var rt = menu.GetComponent<RectTransform>();

        // Counts are the number of result rows that scope would show (the same number the All chip shows once it is selected):
        // saved copies of one item collapse into one row, and linked content only counts while the Linked toggle is on.
        var index = IndexService.Index;
        bool includeLinked = IncludeLinked;
        int RowsIn(string scopeKey)
        {
            var q = new Query(); ApplyScope(q, scopeKey); q.ExcludeLinked = !includeLinked;
            return QueryEngine.Search(index, q, SortMode.Relevance, 1, default, true, null).Total;
        }

        var options = new List<(string key, string name, string label)>
        {
            ("all", "All", $"All  {RowsIn("all"):N0}"),
            ("mine", "Mine", $"Mine  {RowsIn("mine"):N0}"),
        };
        foreach (var t in IndexService.Targets)
            options.Add(("owner:" + t.OwnerId, t.Name, $"{t.Name}  {RowsIn("owner:" + t.OwnerId):N0}"));

        const float rowH = 58f;
        rt.OffsetMin.Value = new float2(0, -102f - options.Count * rowH - 10f);
        for (int i = 0; i < options.Count; i++)
        {
            var (key, name, label) = options[i];
            var row = UiKit.Rect(rt, "Option " + name, new float2(0, 1), new float2(1, 1),
                                 new float2(6, -(i + 1) * rowH - 2f), new float2(-6, -i * rowH - 6f));
            string k = key, n = name;
            var opt = UiKit.TextButton(row, label, (b, e) =>
            {
                _scopeKey = k; _scopeName = n;
                if (_scopeButton != null) UiKit.SetLabel(_scopeButton, "Scope: " + n);
                CloseScopeMenu();
                ScheduleQuery(resetScroll: true);
            }, TypeScale.Label);
            if (k == _scopeKey) UiKit.SetButtonColor(opt, Theme.ButtonSelected);   // current choice = accent fill
        }
    }
}
