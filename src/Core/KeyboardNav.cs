namespace StashLight.Core;

public enum NavKey { Up, Down, Left, Right, PageUp, PageDown, Home, End }

/// <summary>Where a navigation key moves the selection in a grid (or a one-column list). Pure, so it is unit tested.</summary>
public static class GridNavigation
{
    /// <summary>
    /// The new selected index. <paramref name="current"/> &lt; 0 means nothing is selected: any key then selects the first item
    /// (End selects the last). Moves are clamped to the ends rather than wrapping; Left/Right stay inside their row; Up/Down
    /// step by <paramref name="cols"/>; Page keys step by whole visible rows. Returns -1 when there are no items.
    /// </summary>
    public static int Move(int current, int count, int cols, int visibleRows, NavKey key)
    {
        if (count <= 0) return -1;
        cols = Math.Max(1, cols);
        visibleRows = Math.Max(1, visibleRows);
        if (current < 0 || current >= count) return key == NavKey.End ? count - 1 : 0;

        int row = current / cols, col = current % cols;
        int next = key switch
        {
            NavKey.Left => col > 0 ? current - 1 : current,
            NavKey.Right => col < cols - 1 && current + 1 < count ? current + 1 : current,
            NavKey.Up => current - cols >= 0 ? current - cols : current,
            NavKey.Down => current + cols < count ? current + cols
                           : (row < (count - 1) / cols ? count - 1 : current),      // from the row above a short last row: land on its last item
            NavKey.PageUp => Math.Max(col, current - visibleRows * cols),
            NavKey.PageDown => Math.Min(count - 1, current + visibleRows * cols),
            NavKey.Home => 0,
            NavKey.End => count - 1,
            _ => current,
        };
        return Math.Clamp(next, 0, count - 1);
    }
}

/// <summary>
/// Turns "is the key down right now" (polled every frame) into discrete steps: one on the press, then, after a delay, a steady
/// repeat while it stays held — like a text editor's arrow keys. The clock is passed in so it is unit tested without sleeping.
/// </summary>
public sealed class KeyRepeater
{
    private readonly double _delayMs, _intervalMs;
    private double _pressedAt = -1, _nextAt;

    public KeyRepeater(double delayMs = 350, double intervalMs = 60) { _delayMs = delayMs; _intervalMs = intervalMs; }

    /// <summary>Call every frame. Returns true when the key should act this frame.</summary>
    public bool Poll(bool isDown, double nowMs)
    {
        if (!isDown) { _pressedAt = -1; return false; }
        if (_pressedAt < 0) { _pressedAt = nowMs; _nextAt = nowMs + _delayMs; return true; }
        if (nowMs >= _nextAt)
        {
            // on time: keep the steady rhythm; more than an interval late (the game hitched): one step, then restart from now
            _nextAt = nowMs - _nextAt > _intervalMs ? nowMs + _intervalMs : _nextAt + _intervalMs;
            return true;
        }
        return false;
    }
}
