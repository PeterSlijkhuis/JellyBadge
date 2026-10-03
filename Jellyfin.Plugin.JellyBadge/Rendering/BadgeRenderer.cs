using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyBadge.Configuration;
using Jellyfin.Plugin.JellyBadge.Detection;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyBadge.Rendering;

/// <summary>
/// Draws badges onto a poster.
/// </summary>
public static class BadgeRenderer
{
    private static readonly SKTypeface Typeface = SKTypeface.FromStream(
        typeof(BadgeRenderer).Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyBadge.Rendering.Fonts.BarlowCondensed-Bold.ttf")!);

    private static readonly SKColor Fill = new(12, 12, 12, 190);
    private static readonly SKColor Border = new(255, 255, 255, 60);
    private static readonly SKColor Star = new(255, 200, 61);

    /// <summary>
    /// Renders the badges onto the image.
    /// </summary>
    /// <param name="original">Encoded original poster.</param>
    /// <param name="badges">Badges to draw.</param>
    /// <param name="config">Layout settings.</param>
    /// <param name="mimeType">Mime type of the result, same format as the original where possible.</param>
    /// <returns>The encoded result.</returns>
    public static byte[] Render(byte[] original, IReadOnlyList<Badge> badges, PluginConfiguration config, out string mimeType)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data) ?? throw new ArgumentException("Unsupported image format", nameof(original));
        using var bitmap = SKBitmap.Decode(codec);
        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0);
        Draw(canvas, bitmap.Width, bitmap.Height, badges, config);

        var format = codec.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp ? codec.EncodedFormat : SKEncodedImageFormat.Jpeg;
        mimeType = MimeOf(format);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(format, 92);
        return encoded.ToArray();
    }

    /// <summary>
    /// Gets the mime type of an encoded image.
    /// </summary>
    /// <param name="image">Encoded image.</param>
    /// <returns>The mime type, image/jpeg if unknown.</returns>
    public static string MimeType(byte[] image)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data);
        return MimeOf(codec?.EncodedFormat ?? SKEncodedImageFormat.Jpeg);
    }

    private static string MimeOf(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => "image/png",
        SKEncodedImageFormat.Webp => "image/webp",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Works out where every badge goes, without drawing. Every spot gets the same badge size.
    /// </summary>
    /// <param name="width">Poster width.</param>
    /// <param name="height">Poster height.</param>
    /// <param name="badges">Badges in draw order.</param>
    /// <param name="config">Layout settings.</param>
    /// <returns>Each badge with its box, and the shaded band behind each strip.</returns>
    public static (List<PlacedBadge> Badges, List<SKRect> Bands) Layout(int width, int height, IReadOnlyList<Badge> badges, PluginConfiguration config)
    {
        var placed = new List<PlacedBadge>();
        var bands = new List<SKRect>();
        if (badges.Count == 0)
        {
            return (placed, bands);
        }

        // Size from the width a 2:3 poster of this height would have, so wide episode thumbnails get the same proportions as posters.
        var basis = Math.Min(width, height * 2f / 3f);
        float h = basis * config.Size switch
        {
            BadgeSize.Small => 0.065f,
            BadgeSize.Large => 0.10f,
            _ => 0.08f
        };
        float margin = basis * 0.035f;
        float gap = h * 0.2f;

        var spots = badges.GroupBy(b => SpotOf(b, config)).ToDictionary(g => g.Key, g => g.ToArray());
        bool Used(BadgePosition p) => spots.ContainsKey(p);

        // Each spot picks one or two rows (strips) or columns (corners), whichever lets its badges be bigger.
        var layouts = new Dictionary<BadgePosition, List<Badge[]>>();
        var fit = float.MaxValue;
        foreach (var (spot, group) in spots)
        {
            float Fit(List<Badge[]> lines) => IsStrip(spot)
                ? StripFit(lines, width, height, margin, gap, h)
                : StackFit(lines, height, gap, h, CornerWidth(spot, lines.Count, Used, width, margin, gap), CornerHeight(spot, Used));

            List<Badge[]> lines = [group];
            var best = Fit(lines);
            if (group.Length > 3)
            {
                // Many badges: a second row or column keeps them big enough to read on a phone.
                var half = (group.Length + 1) / 2;
                List<Badge[]> split = [group.Take(half).ToArray(), group.Skip(half).ToArray()];
                if (Fit(split) > best)
                {
                    lines = split;
                    best = Fit(split);
                }
            }

            layouts[spot] = lines;
            fit = Math.Min(fit, best);
        }

        // Few badges grow, many shrink. Growth stops at 2 times the chosen size, so a lone "4K" never turns into a banner.
        const float maxGrow = 2f;
        var scale = Math.Min(maxGrow, fit);
        h *= scale;
        gap *= scale;

        // Strips first: corners on the same edge start below (or above) their band.
        float topBand = 0, bottomBand = 0;
        foreach (var spot in new[] { BadgePosition.TopStrip, BadgePosition.BottomStrip }.Where(Used))
        {
            var rows = layouts[spot];
            var top = spot == BadgePosition.TopStrip;
            var bandHeight = (rows.Count * h) + ((rows.Count - 1) * gap) + (2 * margin);
            bands.Add(new SKRect(0, top ? 0 : height - (bandHeight * 1.4f), width, top ? bandHeight * 1.4f : height));
            if (top)
            {
                topBand = bandHeight - margin;
            }
            else
            {
                bottomBand = bandHeight - margin;
            }

            float y = top ? margin : height - margin - (rows.Count * h) - ((rows.Count - 1) * gap);
            foreach (var row in rows)
            {
                float x = (width - (row.Sum(b => Measure(b, h)) + (gap * (row.Length - 1)))) / 2;
                foreach (var badge in row)
                {
                    var w = Measure(badge, h);
                    placed.Add(new PlacedBadge(badge, x, y, w, h));
                    x += w + gap;
                }

                y += h + gap;
            }
        }

        foreach (var spot in new[] { BadgePosition.TopLeft, BadgePosition.TopRight, BadgePosition.BottomLeft, BadgePosition.BottomRight }.Where(Used))
        {
            var columns = layouts[spot];
            var right = spot is BadgePosition.TopRight or BadgePosition.BottomRight;
            var bottom = spot is BadgePosition.BottomLeft or BadgePosition.BottomRight;
            var widths = columns.Select(c => c.Max(b => Measure(b, h))).ToList();
            float left = right ? width - margin - widths.Sum() - (gap * (columns.Count - 1)) : margin;
            for (var c = 0; c < columns.Count; c++)
            {
                var stack = bottom ? columns[c].Reverse() : columns[c];
                float cy = bottom ? height - margin - bottomBand - h : margin + topBand;
                foreach (var badge in stack)
                {
                    var w = Measure(badge, h);
                    var bx = right ? left + widths[c] - w : left;
                    placed.Add(new PlacedBadge(badge, bx, cy, w, h));
                    cy += bottom ? -(h + gap) : h + gap;
                }

                left += widths[c] + gap;
            }
        }

        return (placed, bands);
    }

    private static void Draw(SKCanvas canvas, int width, int height, IReadOnlyList<Badge> badges, PluginConfiguration config)
    {
        var (placed, bands) = Layout(width, height, badges, config);
        foreach (var band in bands)
        {
            var top = band.Top == 0;
            using var shade = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, top ? 0 : height),
                    new SKPoint(0, top ? band.Bottom : band.Top),
                    [new SKColor(0, 0, 0, 170), SKColors.Transparent],
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(band, shade);
        }

        foreach (var badge in placed)
        {
            DrawBadge(canvas, badge.Badge, badge.X, badge.Y, badge.Height, config.Style);
        }
    }

    private static BadgePosition SpotOf(Badge badge, PluginConfiguration config)
        => config.SpotPerBadge && config.Spots.FirstOrDefault(s => s.Kind == badge.Kind) is { } spot ? spot.Position : config.Position;

    private static bool IsStrip(BadgePosition spot) => spot is BadgePosition.TopStrip or BadgePosition.BottomStrip;

    // A strip may use the full width and 25% of the height.
    private static float StripFit(List<Badge[]> lines, int width, int height, float margin, float gap, float h)
        => Math.Min(
            (width - (2 * margin)) / lines.Max(r => r.Sum(b => Measure(b, h)) + (gap * (r.Length - 1))),
            height * 0.25f / ((lines.Count * h) + ((lines.Count - 1) * gap)));

    // A corner stack may use 60% of the width (90% for two columns), or its half when the other corner on its edge is used too.
    private static float CornerWidth(BadgePosition spot, int columns, Func<BadgePosition, bool> used, int width, float margin, float gap)
    {
        var neighbour = spot switch
        {
            BadgePosition.TopLeft => BadgePosition.TopRight,
            BadgePosition.TopRight => BadgePosition.TopLeft,
            BadgePosition.BottomLeft => BadgePosition.BottomRight,
            _ => BadgePosition.BottomLeft
        };
        return used(neighbour) ? (width - (2 * margin) - gap) / 2 : width * (columns == 1 ? 0.6f : 0.9f);
    }

    // A corner stack may use 45% of the height, less the 25% a strip on its edge may take.
    private static float CornerHeight(BadgePosition spot, Func<BadgePosition, bool> used)
    {
        var strip = spot is BadgePosition.TopLeft or BadgePosition.TopRight ? BadgePosition.TopStrip : BadgePosition.BottomStrip;
        return used(strip) ? 0.2f : 0.45f;
    }

    private static float StackFit(List<Badge[]> columns, int height, float gap, float h, float maxWidth, float heightShare)
    {
        var tallest = columns.Max(c => c.Length);
        var stackWidth = columns.Sum(c => c.Max(b => Measure(b, h))) + (gap * (columns.Count - 1));
        return Math.Min(height * heightShare / ((tallest * h) + ((tallest - 1) * gap)), maxWidth / stackWidth);
    }

    private static float Measure(Badge badge, float h)
    {
        using var font = new SKFont(Typeface, h * 0.66f);
        var icon = HasIcon(badge) ? h * 0.62f : 0;
        return (h * 0.32f * 2) + icon + font.MeasureText(badge.Text);
    }

    private static bool HasIcon(Badge badge) => badge.Kind is BadgeKind.CommunityRating or BadgeKind.CriticRating;

    private static float DrawBadge(SKCanvas canvas, Badge badge, float x, float y, float h, BadgeStyle style)
    {
        var w = Measure(badge, h);
        var rect = new SKRect(x, y, x + w, y + h);
        var shadow = SKImageFilter.CreateDropShadow(0, h * 0.04f, h * 0.08f, h * 0.08f, new SKColor(0, 0, 0, 150));

        if (style != BadgeStyle.Minimal)
        {
            var radius = style == BadgeStyle.Pill ? h / 2 : h * 0.16f;
            using var fill = new SKPaint { Color = Fill, IsAntialias = true, ImageFilter = shadow };
            canvas.DrawRoundRect(rect, radius, radius, fill);
            using var border = new SKPaint { Color = Border, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, h * 0.03f) };
            canvas.DrawRoundRect(rect, radius, radius, border);
        }

        // Minimal has no background, so the text carries a heavier shadow instead.
        var textShadow = style == BadgeStyle.Minimal
            ? SKImageFilter.CreateDropShadow(0, 0, h * 0.12f, h * 0.12f, new SKColor(0, 0, 0, 230))
            : null;
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true, ImageFilter = textShadow };
        using var font = new SKFont(Typeface, h * 0.66f) { Edging = SKFontEdging.SubpixelAntialias };

        var tx = x + (h * 0.32f);
        if (HasIcon(badge))
        {
            var size = h * 0.48f;
            var icon = new SKRect(tx, y + ((h - size) / 2), tx + size, y + ((h + size) / 2));
            if (badge.Kind == BadgeKind.CommunityRating)
            {
                DrawStar(canvas, icon, textShadow);
            }
            else
            {
                DrawCheck(canvas, icon, textShadow);
            }

            tx += h * 0.62f;
        }

        var metrics = font.Metrics;
        var baseline = y + (h / 2) - ((metrics.Ascent + metrics.Descent) / 2);
        canvas.DrawText(badge.Text, tx, baseline, SKTextAlign.Left, font, text);
        shadow.Dispose();
        textShadow?.Dispose();
        return w;
    }

    private static void DrawStar(SKCanvas canvas, SKRect r, SKImageFilter? shadow)
    {
        using var path = new SKPath();
        float cx = r.MidX, cy = r.MidY + (r.Height * 0.04f), outer = r.Width / 2, inner = outer * 0.45f;
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? outer : inner;
            var angle = (Math.PI / 5 * i) - (Math.PI / 2);
            var p = new SKPoint(cx + (float)(radius * Math.Cos(angle)), cy + (float)(radius * Math.Sin(angle)));
            if (i == 0)
            {
                path.MoveTo(p);
            }
            else
            {
                path.LineTo(p);
            }
        }

        path.Close();
        using var paint = new SKPaint { Color = Star, IsAntialias = true, ImageFilter = shadow };
        canvas.DrawPath(path, paint);
    }

    private static void DrawCheck(SKCanvas canvas, SKRect r, SKImageFilter? shadow)
    {
        var stroke = r.Width * 0.14f;
        using var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, ImageFilter = shadow };
        canvas.DrawCircle(r.MidX, r.MidY, (r.Width - stroke) / 2, ring);
        using var path = new SKPath();
        path.MoveTo(r.Left + (r.Width * 0.28f), r.MidY + (r.Height * 0.02f));
        path.LineTo(r.Left + (r.Width * 0.45f), r.Top + (r.Height * 0.68f));
        path.LineTo(r.Left + (r.Width * 0.74f), r.Top + (r.Height * 0.34f));
        ring.StrokeCap = SKStrokeCap.Round;
        ring.StrokeJoin = SKStrokeJoin.Round;
        canvas.DrawPath(path, ring);
    }
}

/// <summary>
/// A badge with its place on the poster.
/// </summary>
/// <param name="Badge">The badge.</param>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct PlacedBadge(Badge Badge, float X, float Y, float Width, float Height)
{
    /// <summary>Gets the box the badge fills.</summary>
    public SKRect Box => new(X, Y, X + Width, Y + Height);
}
