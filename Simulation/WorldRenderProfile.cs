using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// Camera-dependent visual detail. It affects rendering only, never simulation state.
/// A wide view favors readable silhouettes and fewer draw submissions; close views
/// restore wall bevels, glows, and fine grid lines.
/// </summary>
public readonly record struct WorldRenderProfile(
    int VisibleCellCount,
    int WallDetail,
    bool DetailedAgents,
    bool EntityGlow,
    bool FineGrid,
    int AtmosphereCellSpan)
{
    public static WorldRenderProfile Create(float zoom, Rectangle visibleBounds)
    {
        if (!float.IsFinite(zoom) || zoom <= 0f)
            zoom = 1f;

        var visibleWidth = Math.Clamp(visibleBounds.Width, 0, EmergentSimulationWorld.Width);
        var visibleHeight = Math.Clamp(visibleBounds.Height, 0, EmergentSimulationWorld.Height);
        var visibleCells = visibleWidth * visibleHeight;

        var closeDetail = zoom >= 2.25f && visibleCells <= 3_200;
        var mediumDetail = zoom >= 1.35f && visibleCells <= 6_000;
        var wallDetail = closeDetail ? 2 : mediumDetail ? 1 : 0;
        var detailedAgents = zoom >= 1.1f && visibleCells <= 7_000;
        var entityGlow = closeDetail;
        var fineGrid = zoom >= 1.65f && visibleCells <= 4_500;
        var atmosphereCellSpan = visibleCells > 7_500 ? 8 : visibleCells > 4_000 ? 6 : 4;

        return new WorldRenderProfile(visibleCells, wallDetail, detailedAgents, entityGlow,
            fineGrid, atmosphereCellSpan);
    }

    public bool ShouldGlowResources(int mapLens, float zoom) =>
        EntityGlow || (mapLens == 1 && zoom >= 0.9f && VisibleCellCount <= 5_000);
}
