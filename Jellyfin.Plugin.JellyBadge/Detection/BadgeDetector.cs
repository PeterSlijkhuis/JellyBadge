using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyBadge.Detection;

/// <summary>
/// Badge groups, in the order they are drawn.
/// </summary>
public enum BadgeKind
{
    /// <summary>4K, 1080p, 720p, SD.</summary>
    Resolution,

    /// <summary>Dolby Vision, HDR10+, HDR10, HLG.</summary>
    DynamicRange,

    /// <summary>Atmos, DTS:X, TrueHD, DTS-HD MA.</summary>
    AudioFormat,

    /// <summary>7.1, 5.1.</summary>
    AudioChannels,

    /// <summary>Community rating, 0 to 10.</summary>
    CommunityRating,

    /// <summary>Critic rating, 0 to 100.</summary>
    CriticRating
}

/// <summary>
/// One badge to draw.
/// </summary>
/// <param name="Kind">The group.</param>
/// <param name="Text">The label.</param>
public sealed record Badge(BadgeKind Kind, string Text);

/// <summary>
/// Turns stream info and ratings into badges. Pure logic, no Jellyfin services.
/// </summary>
public static class BadgeDetector
{
    private static readonly BadgeKind[] TechnicalKinds = [BadgeKind.Resolution, BadgeKind.DynamicRange, BadgeKind.AudioFormat, BadgeKind.AudioChannels];

    /// <summary>
    /// Technical badges for the best of several versions of one item.
    /// </summary>
    /// <param name="versions">The stream lists, one per version.</param>
    /// <returns>The badges of the best version.</returns>
    public static List<Badge> BestVersion(IEnumerable<IReadOnlyList<MediaStream>> versions)
    {
        var best = versions
            .Select(Score)
            .OrderByDescending(s => s.Resolution.Rank)
            .ThenByDescending(s => s.Range.Rank)
            .ThenByDescending(s => s.Format.Rank)
            .ThenByDescending(s => s.Channels.Rank)
            .FirstOrDefault();

        if (best is null)
        {
            return [];
        }

        return new[]
        {
            (BadgeKind.Resolution, best.Resolution),
            (BadgeKind.DynamicRange, best.Range),
            (BadgeKind.AudioFormat, best.Format),
            (BadgeKind.AudioChannels, best.Channels)
        }
        .Where(x => x.Item2.Rank > 0)
        .Select(x => new Badge(x.Item1, x.Item2.Text))
        .ToList();
    }

    /// <summary>
    /// The most common value per group, used for a series from its episodes.
    /// </summary>
    /// <param name="perEpisode">Badges of each episode.</param>
    /// <returns>The most common badges.</returns>
    public static List<Badge> MostCommon(IReadOnlyCollection<IReadOnlyList<Badge>> perEpisode)
    {
        var result = new List<Badge>();
        foreach (var kind in TechnicalKinds)
        {
            // "No badge" counts as a value too, so a mostly SDR show gets no HDR badge.
            var winner = perEpisode
                .GroupBy(badges => badges.FirstOrDefault(b => b.Kind == kind)?.Text)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key is not null)
                .FirstOrDefault();

            if (winner?.Key is not null)
            {
                result.Add(new Badge(kind, winner.Key));
            }
        }

        return result;
    }

    /// <summary>
    /// Rating badges.
    /// </summary>
    /// <param name="community">Community rating, 0 to 10.</param>
    /// <param name="critic">Critic rating, 0 to 100.</param>
    /// <returns>The rating badges.</returns>
    public static List<Badge> Ratings(float? community, float? critic)
    {
        var result = new List<Badge>();
        if (community is > 0)
        {
            result.Add(new Badge(BadgeKind.CommunityRating, community.Value.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        if (critic is > 0)
        {
            result.Add(new Badge(BadgeKind.CriticRating, Math.Round(critic.Value).ToString(CultureInfo.InvariantCulture) + "%"));
        }

        return result;
    }

    private static VersionScore Score(IReadOnlyList<MediaStream> streams)
    {
        var video = streams.Where(s => s.Type == MediaStreamType.Video).MaxBy(s => (s.Width ?? 0) * (s.Height ?? 0));
        var audio = streams
            .Where(s => s.Type == MediaStreamType.Audio)
            .Select(s => (Format: AudioFormat(s), Channels: AudioChannels(s)))
            .OrderByDescending(a => a.Format.Rank)
            .ThenByDescending(a => a.Channels.Rank)
            .FirstOrDefault();

        return new VersionScore(
            video is null ? Ranked.None : Resolution(video),
            video is null ? Ranked.None : DynamicRange(video),
            audio.Format ?? Ranked.None,
            audio.Channels ?? Ranked.None);
    }

    private static Ranked Resolution(MediaStream video)
    {
        // Width first so scope films (1920x800) still count as 1080p, height for 4:3 content.
        int w = video.Width ?? 0, h = video.Height ?? 0;
        if (w == 0 && h == 0)
        {
            return Ranked.None;
        }

        return w >= 3200 || h >= 2000 ? new(4, "4K")
            : w >= 1700 || h >= 1000 ? new(3, "1080p")
            : w >= 1200 || h >= 700 ? new(2, "720p")
            : new(1, "SD");
    }

    private static Ranked DynamicRange(MediaStream video)
    {
        return video.VideoRangeType switch
        {
            VideoRangeType.DOVI or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithHLG or VideoRangeType.DOVIWithSDR
                or VideoRangeType.DOVIWithEL or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus => new(4, "DOLBY VISION"),
            VideoRangeType.HDR10Plus => new(3, "HDR10+"),
            VideoRangeType.HDR10 => new(2, "HDR10"),
            VideoRangeType.HLG => new(1, "HLG"),

            // Broken Dolby Vision metadata: fall back to what the base layer signals.
            VideoRangeType.DOVIInvalid when Is(video.ColorTransfer, "smpte2084") => new(2, "HDR10"),
            VideoRangeType.DOVIInvalid when Is(video.ColorTransfer, "arib-std-b67") => new(1, "HLG"),
            _ => Ranked.None
        };
    }

    private static Ranked AudioFormat(MediaStream audio)
    {
        var codec = audio.Codec ?? string.Empty;
        var profile = audio.Profile ?? string.Empty;
        var isDts = codec.Equals("dts", StringComparison.OrdinalIgnoreCase);

        if (Has(profile, "atmos") || Has(audio.Title, "atmos"))
        {
            return new(4, "ATMOS");
        }

        if (isDts && (Has(profile, "dts:x") || Has(profile, "dts-x")))
        {
            return new(3, "DTS:X");
        }

        if (codec.Equals("truehd", StringComparison.OrdinalIgnoreCase))
        {
            return new(2, "TRUEHD");
        }

        if (isDts && Has(profile, "ma"))
        {
            return new(1, "DTS-HD MA");
        }

        return Ranked.None;
    }

    private static Ranked AudioChannels(MediaStream audio) => audio.Channels switch
    {
        >= 8 => new(2, "7.1"),
        >= 6 => new(1, "5.1"),
        _ => Ranked.None
    };

    private static bool Has(string? value, string part) => value?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;

    private static bool Is(string? value, string other) => string.Equals(value, other, StringComparison.OrdinalIgnoreCase);

    private sealed record Ranked(int Rank, string Text)
    {
        public static readonly Ranked None = new(0, string.Empty);
    }

    private sealed record VersionScore(Ranked Resolution, Ranked Range, Ranked Format, Ranked Channels);
}
