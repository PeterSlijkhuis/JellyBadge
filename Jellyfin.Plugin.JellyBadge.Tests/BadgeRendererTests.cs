using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // One badge per group, with the longest texts each group can have.
    private static readonly Badge[] AllKinds =
    [
        new(BadgeKind.Status, "SEASON 12 SOON"),
        new(BadgeKind.Edition, "DIRECTOR'S CUT"),
        new(BadgeKind.Resolution, "1080p"),
        new(BadgeKind.DynamicRange, "DOLBY VISION"),
        new(BadgeKind.VideoCodec, "MPEG-2"),
        new(BadgeKind.Remux, "REMUX"),
        new(BadgeKind.AudioFormat, "DTS-HD MA"),
        new(BadgeKind.AudioChannels, "MONO"),
        new(BadgeKind.Language, "NL SUBS"),
        new(BadgeKind.CommunityRating, "10.0"),
        new(BadgeKind.CriticRating, "100%")
    ];

    [Theory]
    [InlineData(600, 900, BadgeSize.Large)]
    [InlineData(600, 900, BadgeSize.Small)]
    [InlineData(1280, 720, BadgeSize.Medium)]
    public void SpotsNeverOverlapOrLeaveThePoster(int width, int height, BadgeSize size)
    {
        var positions = Enum.GetValues<BadgePosition>();
        for (var mask = 1; mask < 1 << positions.Length; mask++)
        {
            var used = positions.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            for (var count = 1; count <= AllKinds.Length; count++)
            {
                var badges = AllKinds[..count];
                var config = new PluginConfiguration
                {
                    Size = size,
                    SpotPerBadge = true,
                    Spots = badges.Select((b, i) => new BadgeSpot { Kind = b.Kind, Position = used[i % used.Length] }).ToList()
                };

                var placed = BadgeRenderer.Layout(width, height, badges, config).Badges;
                var where = $"spots {string.Join('+', used)}, {count} badges";
                Assert.Equal(count, placed.Count);
                Assert.Single(placed.Select(p => p.Height).Distinct());
                foreach (var p in placed)
                {
                    Assert.True(p.X >= 0 && p.Y >= 0 && p.Box.Right <= width && p.Box.Bottom <= height, $"{p.Badge.Text} leaves the poster with {where}");
                    var hit = placed.FirstOrDefault(o => !o.Equals(p) && o.Box.IntersectsWith(p.Box));
                    Assert.True(hit.Badge is null, $"{p.Badge.Text} overlaps {hit.Badge?.Text} with {where}");
                }
            }
        }
    }

    [Fact]
    public void SpotPerBadgeOffIgnoresSpots()
    {
        var spots = new List<BadgeSpot> { new() { Kind = BadgeKind.CommunityRating, Position = BadgePosition.BottomRight } };
        var plain = BadgeRenderer.Render(Poster(SKEncodedImageFormat.Jpeg), Badges, new PluginConfiguration(), out _);
        var off = BadgeRenderer.Render(Poster(SKEncodedImageFormat.Jpeg), Badges, new PluginConfiguration { Spots = spots }, out _);
        var on = BadgeRenderer.Render(Poster(SKEncodedImageFormat.Jpeg), Badges, new PluginConfiguration { Spots = spots, SpotPerBadge = true }, out _);

        Assert.Equal(plain, off);
        Assert.NotEqual(plain, on);

        var samples = Environment.GetEnvironmentVariable("JELLYBADGE_SAMPLES");
        if (!string.IsNullOrEmpty(samples))
        {
            File.WriteAllBytes(Path.Combine(samples, "spot-per-badge.jpg"), on);
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
