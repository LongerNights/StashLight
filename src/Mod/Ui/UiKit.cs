using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace StashLight.Ui;

/// <summary>Small helpers over UIX so screen code reads as layout, not plumbing. World thread only.</summary>
internal static class UiKit
{
    // palette (from RadiantUI_Constants so we follow the dash theme)
    public static colorX Bg => Theme.Pane;
    public static colorX Surface => RadiantUI_Constants.Neutrals.MID;
    public static colorX Text => Theme.Text;
    public static colorX Muted => Theme.TextSecondary;
    public static colorX Accent => Theme.Tab;
    public static colorX Warm => Theme.Warm;
    public static colorX Clear => new colorX(0f, 0f, 0f, 0f);

    public static colorX WithAlpha(colorX c, float a) => new colorX(c.r, c.g, c.b, a);

    /// <summary>Child rect inside <paramref name="parent"/> positioned by anchors (0..1) and pixel offsets.</summary>
    public static RectTransform Rect(RectTransform parent, string name, float2 anchorMin, float2 anchorMax,
                                     float2? offsetMin = null, float2? offsetMax = null)
    {
        var slot = parent.Slot.AddSlot(name);
        var rt = slot.AttachComponent<RectTransform>();
        rt.AnchorMin.Value = anchorMin;
        rt.AnchorMax.Value = anchorMax;
        rt.OffsetMin.Value = offsetMin ?? float2.Zero;
        rt.OffsetMax.Value = offsetMax ?? float2.Zero;
        return rt;
    }

    public static RectTransform Fill(RectTransform parent, string name) => Rect(parent, name, float2.Zero, float2.One);

    /// <summary>Rounded panel (the dash's button sprite, nine-sliced), as the Discover design uses for panels and pills.</summary>
    public static Image RoundedPanel(RectTransform rect, colorX color)
    {
        var ui = new UIBuilder(rect);
        return ui.Panel(color, RadiantUI_Constants.GetButtonSprite(rect.World), NineSliceSizing.FixedSize);
    }

    /// <summary>A translucent rounded label laid over a thumbnail (copies, source). Returns the pill slot and its text.</summary>
    public static (Slot pill, FrooxEngine.UIX.Text text) Pill(RectTransform parent, string name, float2 aMin, float2 aMax,
                                                              float size, colorX textColor)
    {
        var r = Rect(parent, name, aMin, aMax);
        RoundedPanel(r, Theme.Pill);
        var t = Label(r, "", size, Alignment.MiddleCenter, textColor);
        return (r.Slot, t);
    }

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>Recolours a button (base colour; hover and press are derived by the engine).</summary>
    public static void SetButtonColor(Button b, colorX color) => b.ColorDrivers[0].SetColors(color);

    /// <summary>Invisible full-area press target used to dismiss popovers when you click away from them. Starts hidden.</summary>
    public static Slot Scrim(RectTransform parent, string name, Action onPressed)
    {
        var rt = Fill(parent, name);
        var image = new UIBuilder(rt).Image(Theme.Scrim);
        var button = image.Slot.AttachComponent<Button>();
        // all four colours equal: a see-through catcher would otherwise tint on hover (SetColors derives opaque colours)
        var d = button.ColorDrivers[0];
        d.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        d.NormalColor.Value = Theme.Scrim; d.HighlightColor.Value = Theme.Scrim;
        d.PressColor.Value = Theme.Scrim; d.DisabledColor.Value = Theme.Scrim;
        button.LocalPressed += (_, _) => onPressed();
        rt.Slot.ActiveSelf = false;
        return rt.Slot;
    }

    public static Image Panel(RectTransform rect, colorX color)
    {
        var ui = new UIBuilder(rect);
        return ui.Panel(color);
    }

    /// <param name="fit">shrink to fit the rect (long names/paths) between <paramref name="minSize"/> and <paramref name="size"/></param>
    public static FrooxEngine.UIX.Text Label(RectTransform rect, string text, float size,
                                             Alignment align = Alignment.MiddleLeft, colorX? color = null,
                                             bool fit = false, float minSize = 12f)
    {
        var ui = new UIBuilder(rect);
        var t = ui.Text((LocaleString)text, bestFit: fit, align, parseRTF: false);
        t.Size.Value = size;
        if (fit) { t.AutoSizeMax.Value = size; t.AutoSizeMin.Value = minSize; }
        t.Color.Value = color ?? Text;
        t.Content.Value = text;
        return t;
    }

    public static Button TextButton(RectTransform rect, string label, ButtonEventHandler onPress, float textSize = 26f)
    {
        var ui = new UIBuilder(rect);
        RadiantUI_Constants.SetupDefaultStyle(ui);
        // NOT ui.Button(text, action): that assigns the synced Pressed delegate, which rejects non-data-model methods.
        var b = ui.Button((LocaleString)label);
        b.LocalPressed += onPress;
        var t = b.Slot.GetComponentInChildren<FrooxEngine.UIX.Text>();
        if (t != null) { t.Size.Value = textSize; t.AutoSizeMax.Value = textSize; }
        return b;
    }

    private static readonly float2 BigMin = new(0.30f, 0.12f), BigMax = new(0.70f, 0.88f);     // centered, no thumbnail
    private static readonly float2 SmallMin = new(0.03f, 0.04f), SmallMax = new(0.27f, 0.40f);   // corner badge over a thumbnail

    /// <summary>Creates the folder (orange) and link (cyan chain) icons inside a thumbnail well, hidden until shown.</summary>
    public static void AddKindIcons(RectTransform well, out RectTransform folder, out RectTransform link)
    {
        folder = Rect(well, "FolderIcon", BigMin, BigMax);
        var fi = new UIBuilder(folder).Image(OfficialAssets.Graphics.Icons.Dash.Folder, Warm);
        fi.PreserveAspect.Value = true;
        folder.Slot.ActiveSelf = false;

        link = Rect(well, "LinkIcon", BigMin, BigMax);
        var li = new UIBuilder(link).Image(OfficialAssets.Graphics.Icons.General.Chainlink, Accent);
        li.PreserveAspect.Value = true;
        link.Slot.ActiveSelf = false;
    }

    /// <summary>Shows the right icon for a category: large when there is no thumbnail, a small corner badge when there is.</summary>
    public static void ShowKindIcon(RectTransform folder, RectTransform link, string category, bool hasThumbnail)
    {
        Place(folder, category == "folder", hasThumbnail);
        Place(link, category == "link", hasThumbnail);
    }

    private static void Place(RectTransform rt, bool show, bool small)
    {
        if (rt.Slot.ActiveSelf != show) rt.Slot.ActiveSelf = show;
        if (!show) return;
        rt.AnchorMin.Value = small ? SmallMin : BigMin;
        rt.AnchorMax.Value = small ? SmallMax : BigMax;
    }

    /// <summary>Keeps the tail (most specific part) of a long path/name: "…\Fast Draw\WIP new bullet calcs".</summary>
    public static string TailEllipsis(string s, int max)
        => s.Length <= max ? s : "…" + s.Substring(s.Length - (max - 1));

    public static string HeadEllipsis(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    public static void SetLabel(Button b, string text)
    {
        var t = b.Slot.GetComponentInChildren<FrooxEngine.UIX.Text>();
        if (t != null) t.Content.Value = text;
    }
}
