using System;
using System.IO;
using Jellyfin.Plugin.JellyBadge.Configuration;
using Jellyfin.Plugin.JellyBadge.Detection;
using Jellyfin.Plugin.JellyBadge.Rendering;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.JellyBadge.Tests;

public class BadgeRendererTests
{
    private static readonly Badge[] Badges =
    [
        new(BadgeKind.Resolution, "4K"),
        new(BadgeKind.DynamicRange, "DOLBY VISION"),
        new(BadgeKind.AudioFormat, "ATMOS"),
        new(BadgeKind.AudioChannels, "7.1"),
        new(BadgeKind.CommunityRating, "7.8"),
        new(BadgeKind.CriticRating, "92%")
    ];

    [Theory]
    [InlineData(BadgePosition.TopLeft, BadgeStyle.Pill)]
    [InlineData(BadgePosition.BottomRight, BadgeStyle.Square)]
    [InlineData(BadgePosition.BottomStrip, BadgeStyle.Minimal)]
    [InlineData(BadgePosition.TopStrip, BadgeStyle.Pill)]
    public void KeepsSizeAndFormat(BadgePosition position, BadgeStyle style)
    {
        var original = Poster(SKEncodedImageFormat.Jpeg);
        var config = new PluginConfiguration { Position = position, Style = style };

        var output = BadgeRenderer.Render(original, Badges, config, out var mime);

        using var bitmap = SKBitmap.Decode(output);
        Assert.Equal((600, 900), (bitmap.Width, bitmap.Height));
        Assert.Equal("image/jpeg", mime);
        Assert.NotEqual(original, output);

        // Set JELLYBADGE_SAMPLES to a folder to look at the results.
        var samples = Environment.GetEnvironmentVariable("JELLYBADGE_SAMPLES");
        if (!string.IsNullOrEmpty(samples))
        {
            File.WriteAllBytes(Path.Combine(samples, $"{position}-{style}.jpg"), output);
        }
    }

    [Theory]
    [InlineData(BadgePosition.TopLeft, 1)]
    [InlineData(BadgePosition.TopLeft, 3)]
    [InlineData(BadgePosition.TopLeft, 6)]
    [InlineData(BadgePosition.TopStrip, 1)]
    [InlineData(BadgePosition.TopStrip, 3)]
    [InlineData(BadgePosition.TopStrip, 6)]
    public void FitsAnyNumberOfBadges(BadgePosition position, int count)
    {
        var output = BadgeRenderer.Render(Poster(SKEncodedImageFormat.Jpeg), Badges[..count], new PluginConfiguration { Position = position }, out _);

        using var decoded = SKBitmap.Decode(output);
        Assert.Equal(600, decoded.Width);

        var samples = Environment.GetEnvironmentVariable("JELLYBADGE_SAMPLES");
        if (!string.IsNullOrEmpty(samples))
        {
            File.WriteAllBytes(Path.Combine(samples, $"fit-{position}-{count}.jpg"), output);
        }
    }

    [Fact]
    public void KeepsPng()
    {
        BadgeRenderer.Render(Poster(SKEncodedImageFormat.Png), Badges, new PluginConfiguration(), out var mime);

        Assert.Equal("image/png", mime);
    }

    private static byte[] Poster(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(600, 900);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(600, 900), [new SKColor(240, 220, 180), new SKColor(40, 60, 110)], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, 600, 900, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
