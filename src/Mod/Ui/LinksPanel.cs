using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using StashLight.Core;
using ResoniteModLoader;
using UText = FrooxEngine.UIX.Text;

namespace StashLight.Ui;

/// <summary>
/// The "Links" panel: every link of yours with what its crawl found, a per-link On/Off switch (writes the DisabledLinks
/// setting), a per-link Re-crawl, and "Re-crawl all". The texts and setting edits come from Core's LinkRows (unit tested);
/// this class only draws. Rows are built a few per frame so opening it with 150+ links does not hitch. World thread only.
/// </summary>
internal sealed class LinksPanel
{
    private const float RowH = 64f;

    private sealed class Row
    {
        public LinkSeed Seed = null!;
        public Slot Root = null!;
        public UText Name = null!, Info = null!;
        public Button Toggle = null!, Recrawl = null!;
    }

    private readonly RectTransform _host;
    private Slot _scrim = null!, _panel = null!;
    private RectTransform _panelRect = null!, _content = null!;
    private UText _summary = null!, _depthLabel = null!;
    private Button _recrawlAll = null!;
    private int _depthVersion, _tickVersion;
    private readonly List<Row> _rows = new();
    private List<LinkSeed> _seeds = new();
    private int _buildVersion;

    public bool IsOpen => _panel.ActiveSelf;

    public LinksPanel(RectTransform host)
    {
        _host = host;

        _scrim = UiKit.Scrim(_host, "LinksScrim", Close);                        // click anywhere outside to close
        _panelRect = UiKit.Rect(_host, "LinksPanel", new float2(0.16f, 0.08f), new float2(0.84f, 0.92f));
        _panel = _panelRect.Slot;

        // opaque body with a thin outline (own "Bg" child; nothing here ever clears the panel's children)
        var bg = UiKit.Fill(_panelRect, "Bg");
        UiKit.RoundedPanel(bg, RadiantUI_Constants.Neutrals.MIDLIGHT);
        var inner = UiKit.Rect(bg, "Inner", float2.Zero, float2.One, new float2(2f, 2f), new float2(-2f, -2f));
        UiKit.RoundedPanel(inner, Theme.Pane);

        UiKit.Label(UiKit.Rect(_panelRect, "Title", new float2(0.03f, 0.90f), new float2(0.28f, 0.98f)), "Linked folders", TypeScale.Title,
                    Alignment.MiddleLeft, null, fit: true, minSize: 18f);

        // crawl depth: how many links deep the crawler follows (your link = 1, a link inside it = 2, ...), left of Re-crawl all
        UiKit.TextButton(UiKit.Rect(_panelRect, "DepthDown", new float2(0.29f, 0.905f), new float2(0.33f, 0.975f)), "-", (b, e) => ChangeDepth(-1), TypeScale.Body);
        _depthLabel = UiKit.Label(UiKit.Rect(_panelRect, "Depth", new float2(0.335f, 0.90f), new float2(0.50f, 0.98f)), "", TypeScale.Body,
                                  Alignment.MiddleCenter, null, fit: true, minSize: 12f);
        UiKit.TextButton(UiKit.Rect(_panelRect, "DepthUp", new float2(0.505f, 0.905f), new float2(0.545f, 0.975f)), "+", (b, e) => ChangeDepth(+1), TypeScale.Body);
        _summary = UiKit.Label(UiKit.Rect(_panelRect, "Summary", new float2(0.03f, 0.83f), new float2(0.97f, 0.90f)), "", TypeScale.Body,
                               Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 12f);

        _recrawlAll = UiKit.TextButton(UiKit.Rect(_panelRect, "RecrawlAll", new float2(0.56f, 0.905f), new float2(0.80f, 0.975f)), "Re-crawl all", (b, e) => RecrawlAll(), TypeScale.Body);
        UiKit.TextButton(UiKit.Rect(_panelRect, "Close", new float2(0.82f, 0.905f), new float2(0.97f, 0.975f)), "Close", (b, e) => Close(), TypeScale.Body);

        var list = UiKit.Rect(_panelRect, "List", new float2(0.02f, 0.03f), new float2(0.98f, 0.82f));
        var ui = new UIBuilder(list);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        var scroll = ui.ScrollArea(Alignment.TopLeft);
        _content = scroll.Slot.GetComponentOrAttach<RectTransform>();
        SetContentHeight(0f);

        _scrim.ActiveSelf = false;
        _panel.ActiveSelf = false;
    }

    // ---------------------------------------------------------------- open / close

    public void Toggle() { if (IsOpen) Close(); else Open(); }

    public void Open()
    {
        if (IsOpen) return;
        _scrim.ActiveSelf = true;
        _panel.ActiveSelf = true;
        Rebuild();
        UiMotion.SlideIn(_panelRect, new float2(0f, -0.03f), 0.25f);
        StartTicker();
    }

    /// <summary>While the panel is open, redraws the crawl state (queued / crawling / counts) once a second.</summary>
    private void StartTicker()
    {
        int version = _tickVersion;
        var world = _host.World;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (version != Volatile.Read(ref _tickVersion)) return;
                world.RunSynchronously(() => { if (version == _tickVersion && IsOpen && !_host.Slot.IsRemoved) Refresh(); });
            }
        });
    }

    public void Close()
    {
        _panel.ActiveSelf = false;
        _scrim.ActiveSelf = false;
        _buildVersion++;                       // stop any rows still being built
        Interlocked.Increment(ref _tickVersion);
    }

    // ---------------------------------------------------------------- rows

    private void SetContentHeight(float h)
    {
        _content.AnchorMin.Value = new float2(0f, 1f);
        _content.AnchorMax.Value = new float2(1f, 1f);
        _content.OffsetMin.Value = new float2(0f, -h);
        _content.OffsetMax.Value = float2.Zero;
    }

    private void Rebuild()
    {
        int version = ++_buildVersion;
        foreach (var r in _rows) r.Root.Destroy();
        _rows.Clear();

        _seeds = CrawlPlanner.Distinct(CrawlPlanner.SeedsFrom(IndexService.Index.Entries))
                             .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        SetContentHeight(_seeds.Count * RowH);
        RefreshSummary();
        PaintRecrawlAll();

        // a dozen rows per frame
        _host.Slot.StartTask(async () =>
        {
            for (int i = 0; i < _seeds.Count; i++)
            {
                if (version != _buildVersion || !IsOpen || _host.Slot.IsRemoved) return;
                try { CreateRow(i, _seeds[i]); }
                catch (Exception ex) { ResoniteMod.Warn("[StashLight] links panel row failed: " + ex.Message); return; }
                if (i % 12 == 11) await default(NextUpdate);
            }
        });
    }

    private void CreateRow(int i, LinkSeed seed)
    {
        var row = new Row { Seed = seed };
        var rt = UiKit.Rect(_content, "Row " + i, new float2(0, 1), new float2(1, 1),
                            new float2(4, -(i + 1) * RowH + 3), new float2(-4, -i * RowH - 3));
        row.Root = rt.Slot;
        UiKit.RoundedPanel(rt, UiKit.WithAlpha(Theme.Button, 0.45f));

        row.Name = UiKit.Label(UiKit.Rect(rt, "Name", new float2(0.015f, 0.08f), new float2(0.42f, 0.92f)), seed.Name, TypeScale.Label,
                               Alignment.MiddleLeft, null, fit: true, minSize: 14f);
        row.Info = UiKit.Label(UiKit.Rect(rt, "Info", new float2(0.43f, 0.08f), new float2(0.70f, 0.92f)), "", TypeScale.Body,
                               Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 11f);
        row.Toggle = UiKit.TextButton(UiKit.Rect(rt, "Toggle", new float2(0.71f, 0.14f), new float2(0.84f, 0.86f)), "On", (b, e) => ToggleRow(row), TypeScale.Body);
        row.Recrawl = UiKit.TextButton(UiKit.Rect(rt, "Recrawl", new float2(0.86f, 0.14f), new float2(0.985f, 0.86f)), "Re-crawl", (b, e) => RecrawlRow(row), TypeScale.Body);

        _rows.Add(row);
        Paint(row, CrawlPlanner.ParseDisabled(Setting), IndexService.Parts.CrawlState());
    }

    private static string Setting => StashLightMod.Get(StashLightMod.DisabledLinksKey, "");

    private static void Paint(Row row, ISet<string> disabled, IReadOnlyDictionary<string, CrawlInfo> state)
    {
        bool off = CrawlPlanner.IsDisabled(row.Seed, disabled);
        state.TryGetValue(row.Seed.Key, out var info);
        string text = LinkRows.Describe(info, off, DateTime.UtcNow);
        var st = off ? LinkCrawler.LinkState.Idle : LinkCrawler.StateOf(row.Seed.Key, out _);
        int folders = 0;
        if (st == LinkCrawler.LinkState.Crawling) LinkCrawler.StateOf(row.Seed.Key, out folders);
        row.Info.Content.Value = st == LinkCrawler.LinkState.Crawling ? (folders > 0 ? $"crawling now… {folders} folders" : "crawling now…")
                               : st == LinkCrawler.LinkState.Queued ? "queued · " + text : text;
        // the button says what is going on, so it is not pressed again and again
        UiKit.SetLabel(row.Recrawl, st == LinkCrawler.LinkState.Crawling ? "Crawling…" : st == LinkCrawler.LinkState.Queued ? "Queued" : "Re-crawl");
        UiKit.SetButtonColor(row.Recrawl, st == LinkCrawler.LinkState.Idle ? Theme.Button : UiKit.WithAlpha(Theme.Button, 0.5f));
        row.Name.Color.Value = off ? UiKit.Muted : UiKit.Text;
        UiKit.SetLabel(row.Toggle, off ? "Off" : "On");
        UiKit.SetButtonColor(row.Toggle, off ? Theme.Button : Theme.ButtonSelected);       // accent = on, like the Linked button
    }

    /// <summary>Updates the summary and every row's text (called while open, whenever the index or a crawl changes).</summary>
    public void Refresh()
    {
        if (!IsOpen) return;
        RefreshSummary();
        PaintRecrawlAll();
        var off = CrawlPlanner.ParseDisabled(Setting);
        var state = IndexService.Parts.CrawlState();
        foreach (var r in _rows) Paint(r, off, state);
    }

    private static int Depth => Math.Clamp(StashLightMod.Get(StashLightMod.LinkMaxDepthKey, 5), 1, 20);

    private void ChangeDepth(int delta)
    {
        int depth = Math.Clamp(Depth + delta, 1, 20);
        if (depth == Depth) return;
        try { StashLightMod.Cfg?.Set(StashLightMod.LinkMaxDepthKey, depth); }
        catch (Exception ex) { ResoniteMod.Warn("[StashLight] could not save the link depth: " + ex.Message); return; }
        RefreshSummary();
        if (!StashLightMod.IncludeLinked) return;                       // nothing is crawled while Linked is off; the new depth is used when it is turned on
        // a deeper (or shallower) limit only shows up after the links are crawled again; wait until the +/- clicking stops, then do it
        int version = ++_depthVersion;
        var world = _host.World;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            world.RunSynchronously(() =>
            {
                if (version != _depthVersion || _host.Slot.IsRemoved) return;
                LinkCrawler.Recrawl(null);
                ItemActions.Toast($"Link depth {Depth}: re-crawling all links in the background");
                Refresh();
            });
        });
    }

    private void PaintRecrawlAll()
    {
        var (done, total) = LinkCrawler.Counts;
        bool busy = LinkCrawler.Active;
        UiKit.SetLabel(_recrawlAll, busy ? (total > 0 ? $"Crawling {done}/{total}…" : "Crawling…") : "Re-crawl all");
        UiKit.SetButtonColor(_recrawlAll, busy ? UiKit.WithAlpha(Theme.Button, 0.5f) : Theme.Button);
    }

    private void RefreshSummary()
    {
        _depthLabel.Content.Value = $"Depth: {Depth}";
        _summary.Content.Value = LinkRows.Summary(_seeds, IndexService.Parts.CrawlState(), CrawlPlanner.ParseDisabled(Setting),
                                                  StashLightMod.IncludeLinked, LinkCrawler.Active);
    }

    // ---------------------------------------------------------------- actions

    private void ToggleRow(Row row)
    {
        string current = Setting;
        bool disableNow = !CrawlPlanner.IsDisabled(row.Seed, CrawlPlanner.ParseDisabled(current));
        string updated = LinkRows.ToggleDisabled(current, row.Seed, _seeds, disableNow);
        try { StashLightMod.Cfg?.Set(StashLightMod.DisabledLinksKey, updated); }
        catch (Exception ex) { ResoniteMod.Warn("[StashLight] could not save DisabledLinks: " + ex.Message); }
        LinkCrawler.ApplyDisabledChange();        // drops its items now when switched off; crawls it when switched back on
        Refresh();
    }

    private void RecrawlRow(Row row)
    {
        if (!StashLightMod.IncludeLinked) { ItemActions.Toast("Turn Linked on first: links are only crawled while it is on", true); return; }
        if (CrawlPlanner.IsDisabled(row.Seed, CrawlPlanner.ParseDisabled(Setting))) { ItemActions.Toast("This link is switched off; switch it on first", true); return; }
        if (LinkCrawler.StateOf(row.Seed.Key, out _) != LinkCrawler.LinkState.Idle) { ItemActions.Toast(row.Seed.Name + " is already queued or being crawled"); return; }
        LinkCrawler.Recrawl(row.Seed.Key, restart: false);          // joins the next pass; the running one is not interrupted
        ItemActions.Toast(LinkCrawler.Active ? row.Seed.Name + " will be crawled right after the current pass" : "Re-crawling " + row.Seed.Name);
        Refresh();
    }

    private void RecrawlAll()
    {
        if (!StashLightMod.IncludeLinked) { ItemActions.Toast("Turn Linked on first: links are only crawled while it is on", true); return; }
        if (LinkCrawler.Active) { var (d, t) = LinkCrawler.Counts; ItemActions.Toast($"A crawl is already running ({d}/{t} links done)"); return; }
        LinkCrawler.Recrawl(null);
        ItemActions.Toast("Re-crawling all links (runs in the background)");
        Refresh();
    }
}
