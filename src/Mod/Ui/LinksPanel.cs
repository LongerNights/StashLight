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
        public Button Toggle = null!;
    }

    private readonly RectTransform _host;
    private Slot _scrim = null!, _panel = null!;
    private RectTransform _panelRect = null!, _content = null!;
    private UText _summary = null!;
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

        UiKit.Label(UiKit.Rect(_panelRect, "Title", new float2(0.03f, 0.90f), new float2(0.55f, 0.98f)), "Linked folders", TypeScale.Title);
        _summary = UiKit.Label(UiKit.Rect(_panelRect, "Summary", new float2(0.03f, 0.83f), new float2(0.97f, 0.90f)), "", TypeScale.Body,
                               Alignment.MiddleLeft, UiKit.Muted, fit: true, minSize: 12f);

        UiKit.TextButton(UiKit.Rect(_panelRect, "RecrawlAll", new float2(0.58f, 0.905f), new float2(0.80f, 0.975f)), "Re-crawl all", (b, e) => RecrawlAll(), TypeScale.Body);
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
    }

    public void Close()
    {
        _panel.ActiveSelf = false;
        _scrim.ActiveSelf = false;
        _buildVersion++;                       // stop any rows still being built
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

        _seeds = CrawlPlanner.SeedsFrom(IndexService.Index.Entries)
                             .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        SetContentHeight(_seeds.Count * RowH);
        RefreshSummary();

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
        UiKit.TextButton(UiKit.Rect(rt, "Recrawl", new float2(0.86f, 0.14f), new float2(0.985f, 0.86f)), "Re-crawl", (b, e) => RecrawlRow(row), TypeScale.Body);

        _rows.Add(row);
        Paint(row, CrawlPlanner.ParseDisabled(Setting), IndexService.Parts.CrawlState());
    }

    private static string Setting => StashLightMod.Get(StashLightMod.DisabledLinksKey, "");

    private static void Paint(Row row, ISet<string> disabled, IReadOnlyDictionary<string, CrawlInfo> state)
    {
        bool off = CrawlPlanner.IsDisabled(row.Seed, disabled);
        state.TryGetValue(row.Seed.Key, out var info);
        row.Info.Content.Value = LinkRows.Describe(info, off, DateTime.UtcNow);
        row.Name.Color.Value = off ? UiKit.Muted : UiKit.Text;
        UiKit.SetLabel(row.Toggle, off ? "Off" : "On");
        UiKit.SetButtonColor(row.Toggle, off ? Theme.Button : Theme.ButtonSelected);       // accent = on, like the Linked button
    }

    /// <summary>Updates the summary and every row's text (called while open, whenever the index or a crawl changes).</summary>
    public void Refresh()
    {
        if (!IsOpen) return;
        RefreshSummary();
        var off = CrawlPlanner.ParseDisabled(Setting);
        var state = IndexService.Parts.CrawlState();
        foreach (var r in _rows) Paint(r, off, state);
    }

    private void RefreshSummary()
    {
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
        LinkCrawler.Recrawl(row.Seed.Key);
        ItemActions.Toast("Re-crawling " + row.Seed.Name);
        Refresh();
    }

    private void RecrawlAll()
    {
        if (!StashLightMod.IncludeLinked) { ItemActions.Toast("Turn Linked on first: links are only crawled while it is on", true); return; }
        LinkCrawler.Recrawl(null);
        ItemActions.Toast("Re-crawling all links (runs in the background)");
        Refresh();
    }
}
