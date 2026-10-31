using System;

namespace ComfyPromptViewer;

internal static class ThumbnailSizing
{
    // Buckets are physical-pixel widths. The top two only come into play on HiDPI displays: a 320 DIP tile
    // needs 640 real pixels at 200% scaling, and decoding 320 there is the blur this ladder exists to avoid.
    private static readonly int[] Buckets = [120, 180, 240, 320, 480, 640];

    // Scaling above this decodes at the cap instead; past 320 DIP tiles on a 200% display the extra pixels
    // cost more cache than they buy.
    private const double MaxRenderScaling = 4.0;

    // The target is the tile's width in real device pixels, which is what the renderer actually samples. The
    // ladder rounds that up, so a tile never decodes far wider than it draws and a tile-size or DPI change
    // lands on a stable cache key instead of a new width per pixel.
    public static int DecodeWidth(double tileSize, double renderScaling)
    {
        var targetWidth = (int)Math.Ceiling(tileSize * Math.Clamp(renderScaling, 1.0, MaxRenderScaling));
        foreach (var bucket in Buckets)
        {
            if (targetWidth <= bucket)
            {
                return bucket;
            }
        }

        return Buckets[^1];
    }
}
