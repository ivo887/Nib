using System;
using Nib.Core.Geometry;

namespace Nib.Core.Rendering;

/// <summary>
/// Tile addressing for the GPU tile cache. Tiles are rasterized at a quantized zoom ("zoom key", 1/32 of
/// an octave ≈ 2.2%), so at rest every tile maps 1:1 to device pixels and stays crisp, while during a pinch
/// existing tiles are reused scaled until sharper ones are ready.
/// </summary>
public static class TileMath
{
    public const int TileSize = 512;
    private const float KeysPerOctave = 32f;

    /// <summary>Quantizes device pixels per page unit to a zoom key.</summary>
    public static int ZoomKey(float pixelsPerUnit) => (int)MathF.Round(MathF.Log2(MathF.Max(pixelsPerUnit, 1e-4f)) * KeysPerOctave);

    public static float PixelsPerUnit(int zoomKey) => MathF.Pow(2f, zoomKey / KeysPerOctave);

    /// <summary>Snaps a zoom to the nearest value that maps exactly onto a zoom key (applied when a gesture ends).</summary>
    public static float SnapZoom(float zoom, float rasterScale) => PixelsPerUnit(ZoomKey(zoom * rasterScale)) / rasterScale;

    /// <summary>Page-space rectangle covered by tile (tx, ty) at the given device pixels per unit.</summary>
    public static RectF TileRect(int tx, int ty, float pixelsPerUnit)
    {
        float size = TileSize / pixelsPerUnit;
        return new RectF(tx * size, ty * size, (tx + 1) * size, (ty + 1) * size);
    }

    /// <summary>Inclusive tile index range covering a page-space rectangle.</summary>
    public static void TileRange(RectF pageRect, float pixelsPerUnit, out int tx0, out int ty0, out int tx1, out int ty1)
    {
        float inv = pixelsPerUnit / TileSize;
        tx0 = (int)MathF.Floor(pageRect.Left * inv);
        ty0 = (int)MathF.Floor(pageRect.Top * inv);
        tx1 = (int)MathF.Floor(pageRect.Right * inv);
        ty1 = (int)MathF.Floor(pageRect.Bottom * inv);
    }

    public static long TileId(int tx, int ty) => ((long)tx << 32) | (uint)ty;
}
