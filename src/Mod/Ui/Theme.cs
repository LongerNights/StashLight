using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;

namespace InventorySearch.Ui;

/// <summary>
/// Fixed type scale, borrowed from the Worlds tab overhaul (Discover) so both screens read as one family:
/// text uses one of these sizes rather than a size that follows its label's length.
/// </summary>
internal static class TypeScale
{
    public const float Caption = 14f;
    public const float Body = 18f;
    public const float Label = 22f;
    public const float Title = 28f;
    public const float Hero = 36f;
}

/// <summary>
/// The stock Worlds-tab look (same tokens as the Discover mod's Theme): Dark/Mid neutrals, the dash's cyan reserved for the
/// selected item and the one primary action, translucent pills over thumbnails. Every colour is a RadiantUI_Constants member,
/// so it follows any future palette change.
/// </summary>
internal static class Theme
{
    /// <summary>Aqua: the stock tabs already use cyan (Worlds, Session), so Search gets its own hue. Same brightness as the Hero colours.</summary>
    public static colorX Tab => colorX.FromHexCode("#4fd9bd");
    /// <summary>The stock tab's background tint: Dark at 96.5 % over a tiled texture, so the world just shows through.</summary>
    public static colorX Background => RadiantUI_Constants.Neutrals.DARK.SetA(0.965f);
    public static colorX Pane => RadiantUI_Constants.Neutrals.DARK;
    public static colorX Button => RadiantUI_Constants.Neutrals.MID;
    public static colorX ButtonSelected => colorX.FromHexCode("#3f9e8c");      // the aqua at MidLight brightness
    public static colorX Text => RadiantUI_Constants.TEXT_COLOR;
    public static colorX TextSecondary => RadiantUI_Constants.Neutrals.MIDLIGHT;
    public static colorX Warm => RadiantUI_Constants.Hero.ORANGE;
    /// <summary>Labels laid over thumbnails: translucent black so any thumbnail stays readable underneath.</summary>
    public static colorX Pill => colorX.Black.SetA(0.7f);
    /// <summary>Click-catcher behind a popover: almost invisible, dims nothing.</summary>
    public static colorX Scrim => colorX.Black.SetA(0.02f);

    public static readonly Uri StockBackground =
        new("resdb:///d54b9c04ef6fd23f0e2b65d3e14fbe036fa8e2b922f336e74c9f115a933cb198.png");
}

/// <summary>
/// The one place animation timing lives (same idea as Discover's UiMotion): the "Animations" and "AnimationSpeed" mod
/// settings decide whether things move and how fast. Every animation is a few field tweens (no per-frame work of ours),
/// guarded so a failed tween just snaps to the end state.
/// </summary>
internal static class UiMotion
{
    public static bool Enabled => InventorySearchMod.Get(InventorySearchMod.AnimationsKey, true);
    public static float Speed => InventorySearchMod.Get(InventorySearchMod.AnimationSpeedKey, 1f);
    public static float Seconds(float normalSeconds) => normalSeconds / MathF.Max(0.2f, Speed);

    /// <summary>Fades a colour field from transparent to <paramref name="target"/>.</summary>
    public static void FadeIn(Sync<colorX> color, colorX target, float normalSeconds = 0.2f)
    {
        if (!Enabled) return;
        try { color.TweenFromTo(target.SetA(0f), target, Seconds(normalSeconds), CurvePreset.SineOut); }
        catch (Exception ex) { UniLog.Warning($"[InventorySearch] fade failed: {ex.GetType().Name}"); }
    }

    /// <summary>Slides a rect into place by tweening its anchors from <paramref name="from"/> (fractions of its parent) to where they are.</summary>
    public static void SlideIn(RectTransform rect, float2 from, float normalSeconds = 0.30f, CurvePreset curve = CurvePreset.QuinticOut)
    {
        if (!Enabled) return;
        try
        {
            var min = rect.AnchorMin.Value; var max = rect.AnchorMax.Value;
            var s = Seconds(normalSeconds);
            rect.AnchorMin.TweenFromTo(min + from, min, s, curve);
            rect.AnchorMax.TweenFromTo(max + from, max, s, curve);
        }
        catch (Exception ex) { UniLog.Warning($"[InventorySearch] slide failed: {ex.GetType().Name}"); }
    }
}
