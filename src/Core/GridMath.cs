namespace StashLight.Core;

/// <summary>
/// Window math for the virtualized grid, kept engine-free so it can be unit tested.
/// NOTE: do not use Elements.Core.MathX.Clamp for ints here — MathX.Clamp(5, 0, 0) returns -1 in the engine,
/// which made empty result sets index the cell pool with -1 (the "empty tab doesn't change" bug).
/// </summary>
public static class GridMath
{
    /// <summary>First visible row (0 = top). Always in [0, max(0, totalRows - 1)], whatever the inputs.</summary>
    public static int TopRow(int totalRows, float cellH, float viewportH, float normalizedY)
    {
        if (totalRows <= 0 || cellH <= 0f) return 0;
        float scrollable = Math.Max(0f, totalRows * cellH - viewportH);
        float row = float.IsNaN(normalizedY) ? 0f : normalizedY * scrollable / cellH;
        int top = (int)Math.Clamp(MathF.Floor(row), 0f, totalRows - 1);
        return top;
    }

    /// <summary>Pool rows needed to cover the viewport (floor keeps an unmeasured first layout from under-provisioning).</summary>
    public static int PoolRows(float viewportH, float minViewportH, float cellH)
        => (int)MathF.Ceiling(Math.Max(viewportH, minViewportH) / cellH) + 2;

    /// <summary>Index into a row-major cell pool for logical row <paramref name="row"/>, column <paramref name="col"/>.</summary>
    public static int PoolSlot(int row, int col, int poolRows, int cols)
    {
        int r = ((row % poolRows) + poolRows) % poolRows;    // never negative
        return r * cols + col;
    }
}
