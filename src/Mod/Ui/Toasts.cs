using System.Reflection;
using Elements.Core;
using FrooxEngine;
using ResoniteModLoader;

namespace StashLight.Ui;

/// <summary>
/// The dash toast (the small popup at the top of the dash, like the Worlds tab uses) for notices and errors. Safe from any thread.
/// The public <c>NotificationPanel.ShowNotification</c> always titles the toast "N/A", so the private overload that takes the
/// title is called directly; if it cannot be found (an engine update) the public call is used instead.
/// </summary>
internal static class Toasts
{
    private const string Title = "StashLight";

    private static readonly MethodInfo? AddNotification = typeof(NotificationPanel).GetMethod(
        "AddNotification",
        BindingFlags.Instance | BindingFlags.NonPublic,
        null,
        [typeof(string), typeof(string), typeof(Uri), typeof(colorX), typeof(NotificationType), typeof(string), typeof(Uri), typeof(IAssetProvider<AudioClip>)],
        null);

    public static void Show(string message, bool error = false)
    {
        var color = error ? RadiantUI_Constants.Sub.RED : RadiantUI_Constants.Neutrals.DARK;
        var panel = NotificationPanel.Current;
        if (panel is null) return;

        if (AddNotification is not null)
        {
            try
            {
                panel.RunSynchronously(() =>
                {
                    try { AddNotification.Invoke(panel, [null, message, null, color, NotificationType.ToastOnly, Title, null, null]); }
                    catch (Exception ex) { ResoniteMod.Warn("[StashLight] toast failed: " + ex.GetType().Name); }
                });
                return;
            }
            catch (Exception ex) { ResoniteMod.Warn("[StashLight] toast failed: " + ex.GetType().Name); }
        }
        NotificationPanel.ShowNotification(null!, message, null!, color, NotificationType.ToastOnly);
    }
}
