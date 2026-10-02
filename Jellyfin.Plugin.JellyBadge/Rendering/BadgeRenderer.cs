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

    private static void Draw(SKCanvas canvas, int width, int height, IReadOnlyList<Badge> badges, PluginConfiguration config)
    {
        if (badges.Count == 0)
        {
            return;
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
        var isStrip = config.Position is BadgePosition.TopStrip or BadgePosition.BottomStrip;

        // Fit the badges to their area: few badges grow, many shrink. Growth stops at 2 times the
        // chosen size, so a lone "4K" never turns into a banner.
        const float maxGrow = 2f;
        float fit;
        List<Badge[]> rows = [badges.ToArray()];
        if (isStrip)
        {
            // A strip may use the full width and 25% of the height.
            float StripFit(List<Badge[]> lines)
                => Math.Min(
                    (width - (2 * margin)) / lines.Max(r => r.Sum(b => Measure(b, h)) + (gap * (r.Length - 1))),
                    height * 0.25f / ((lines.Count * h) + ((lines.Count - 1) * gap)));

            fit = StripFit(rows);
            if (badges.Count > 3)
            {
                // Many badges: a second row keeps them big enough to read on a phone. Keep whichever gives bigger badges.
                var half = (badges.Count + 1) / 2;
                List<Badge[]> lines = [badges.Take(half).ToArray(), badges.Skip(half).ToArray()];
                if (StripFit(lines) > fit)
                {
                    rows = lines;
                    fit = StripFit(lines);
                }
            }
        }
        else
        {
            // A corner stack may use 45% of the height and 60% of the width (90% for two columns).
            float StackFit(List<Badge[]> columns)
            {
                var tallest = columns.Max(c => c.Length);
                var stackWidth = columns.Sum(c => c.Max(b => Measure(b, h))) + (gap * (columns.Count - 1));
                return Math.Min(height * 0.45f / ((tallest * h) + ((tallest - 1) * gap)), width * (columns.Count == 1 ? 0.6f : 0.9f) / stackWidth);
            }

            fit = StackFit(rows);
            if (badges.Count > 3)
            {
                // Many badges: two columns keep them big enough to read on a phone. Keep whichever gives bigger badges.
                var half = (badges.Count + 1) / 2;
                List<Badge[]> columns = [badges.Take(half).ToArray(), badges.Skip(half).ToArray()];
                var columnFit = StackFit(columns);
                if (columnFit > fit)
                {
                    rows = columns;
                    fit = columnFit;
                }
            }
        }

        var scale = Math.Min(maxGrow, fit);
        h *= scale;
        gap *= scale;

        if (isStrip)
        {
            var top = config.Position == BadgePosition.TopStrip;
            var bandHeight = (rows.Count * h) + ((rows.Count - 1) * gap) + (2 * margin);
            using var shade = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, top ? 0 : height),
                    new SKPoint(0, top ? bandHeight * 1.4f : height - (bandHeight * 1.4f)),
                    [new SKColor(0, 0, 0, 170), SKColors.Transparent],
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(0, top ? 0 : height - (bandHeight * 1.4f), width, bandHeight * 1.4f, shade);

            float y = top ? margin : height - margin - (rows.Count * h) - ((rows.Count - 1) * gap);
            foreach (var row in rows)
            {
                float x = (width - (row.Sum(b => Measure(b, h)) + (gap * (row.Length - 1)))) / 2;
                foreach (var badge in row)
                {
                    x += DrawBadge(canvas, badge, x, y, h, config.Style) + gap;
                }

                y += h + gap;
            }

            return;
        }

        var right = config.Position is BadgePosition.TopRight or BadgePosition.BottomRight;
        var bottom = config.Position is BadgePosition.BottomLeft or BadgePosition.BottomRight;
        var widths = rows.Select(c => c.Max(b => Measure(b, h))).ToList();
        float left = right ? width - margin - widths.Sum() - (gap * (rows.Count - 1)) : margin;
        for (var c = 0; c < rows.Count; c++)
        {
            var stack = bottom ? rows[c].Reverse() : rows[c];
            float cy = bottom ? height - margin - h : margin;
            foreach (var badge in stack)
            {
                var bx = right ? left + widths[c] - Measure(badge, h) : left;
                DrawBadge(canvas, badge, bx, cy, h, config.Style);
                cy += bottom ? -(h + gap) : h + gap;
            }

            left += widths[c] + gap;
        }
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
