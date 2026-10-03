using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyBadge.Detection;

/// <summary>
/// Badge groups.
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
    CriticRating,

    // Added later, so they go last here: the numbers are part of the stored hashes.

    /// <summary>AV1, HEVC, H.264. Drawn after the dynamic range.</summary>
    VideoCodec,

    /// <summary>Remux, from the file name. Drawn after the codec.</summary>
    Remux
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
public static partial class BadgeDetector
{
    // The order technical badges are drawn in.
    private static readonly BadgeKind[] TechnicalKinds = [BadgeKind.Resolution, BadgeKind.DynamicRange, BadgeKind.VideoCodec, BadgeKind.Remux, BadgeKind.AudioFormat, BadgeKind.AudioChannels];

    /// <summary>
    /// Technical badges for the best of several versions of one item.
    /// </summary>
    /// <param name="versions">The stream lists, one per version.</param>
    /// <returns>The badges of the best version.</returns>
    public static List<Badge> BestVersion(IEnumerable<IReadOnlyList<MediaStream>> versions)
        => BestVersion(versions.Select(v => (v, (string?)null)));

    /// <summary>
    /// Technical badges for the best of several versions of one item.
    /// </summary>
    /// <param name="versions">The stream list and file path of each version.</param>
    /// <returns>The badges of the best version.</returns>
    public static List<Badge> BestVersion(IEnumerable<(IReadOnlyList<MediaStream> Streams, string? Path)> versions)
    {
        var best = versions
            .Select(v => Score(v.Streams, v.Path))
            .OrderByDescending(s => s.Resolution.Rank)
            .ThenByDescending(s => s.Range.Rank)
            .ThenByDescending(s => s.Format.Rank)
            .ThenByDescending(s => s.Channels.Rank)
            .ThenByDescending(s => s.Remux.Rank)
            .ThenByDescending(s => s.Codec.Rank)
            .FirstOrDefault();

        if (best is null)
        {
            return [];
        }

        return new[]
        {
            (BadgeKind.Resolution, best.Resolution),
            (BadgeKind.DynamicRange, best.Range),
            (BadgeKind.VideoCodec, best.Codec),
            (BadgeKind.Remux, best.Remux),
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

    private static VersionScore Score(IReadOnlyList<MediaStream> streams, string? path)
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
            audio.Channels ?? Ranked.None,
            video is null ? Ranked.None : VideoCodec(video),
            IsRemux(path) ? new(1, "REMUX") : Ranked.None);
    }

    /// <summary>
    /// Whether a badge marks something that stands out. Everyday quality (720p, SD, stereo, mono, lossy audio, older codecs) does not.
    /// </summary>
    /// <param name="badge">The badge.</param>
    /// <returns>False for everyday quality.</returns>
    public static bool IsPremium(Badge badge) => (badge.Kind, badge.Text) switch
    {
        (BadgeKind.Resolution, "720p" or "SD") => false,
        (BadgeKind.AudioChannels, "2.0" or "2.1" or "MONO") => false,
        (BadgeKind.AudioFormat, "DTS" or "DD+" or "DD" or "AAC" or "OPUS" or "MP3") => false,
        (BadgeKind.VideoCodec, "H.264" or "VC-1" or "MPEG-2" or "MPEG-4") => false,
        _ => true
    };

    private static Ranked VideoCodec(MediaStream video) => (video.Codec ?? string.Empty).ToLowerInvariant() switch
    {
        "av1" => new(7, "AV1"),
        "hevc" or "h265" => new(6, "HEVC"),
        "vp9" => new(5, "VP9"),
        "h264" or "avc" => new(4, "H.264"),
        "vc1" => new(3, "VC-1"),
        "mpeg2video" => new(2, "MPEG-2"),
        "mpeg4" or "msmpeg4v3" => new(1, "MPEG-4"),
        _ => Ranked.None
    };

    // Release names say "Remux" (Movie.2019.2160p.UHD.BluRay.REMUX.mkv), in the file or its folder; the streams alone cannot tell.
    private static bool IsRemux(string? path)
        => path is not null && (RemuxWord().IsMatch(Path.GetFileName(path)) || RemuxWord().IsMatch(Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty));

    [GeneratedRegex(@"(^|[^a-z])remux([^a-z]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex RemuxWord();

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
            return new(11, "ATMOS");
        }

        if (isDts && (Has(profile, "dts:x") || Has(profile, "dts-x")))
        {
            return new(10, "DTS:X");
        }

        if (isDts && Has(profile, "ma"))
        {
            return new(8, "DTS-HD MA");
        }

        return codec.ToLowerInvariant() switch
        {
            "truehd" => new(9, "TRUEHD"),
            "flac" => new(7, "FLAC"),
            _ when codec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) => new(7, "PCM"),
            "dts" => new(6, "DTS"),
            "eac3" => new(5, "DD+"),
            "ac3" => new(4, "DD"),
            "aac" => new(3, "AAC"),
            "opus" => new(2, "OPUS"),
            "mp3" => new(1, "MP3"),
            _ => Ranked.None
        };
    }

    // The layout ffprobe reports ("5.1(side)", "2.1", "stereo") is most exact; the channel count covers files without one.
    private static Ranked AudioChannels(MediaStream audio)
    {
        var layout = ChannelLayout().Match(audio.ChannelLayout ?? string.Empty);
        var text = layout.Success ? layout.Value : audio.Channels switch
        {
            >= 8 => "7.1",
            >= 6 => "5.1",
            3 => "2.1",
            2 => "2.0",
            1 => "MONO",
            _ => null
        };

        return text is null ? Ranked.None : new(Math.Max(1, audio.Channels ?? 0), text);
    }

    [GeneratedRegex(@"^\d\.\d")]
    private static partial Regex ChannelLayout();

    private static bool Has(string? value, string part) => value?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;

    private static bool Is(string? value, string other) => string.Equals(value, other, StringComparison.OrdinalIgnoreCase);

    private sealed record Ranked(int Rank, string Text)
    {
        public static readonly Ranked None = new(0, string.Empty);
    }

    private sealed record VersionScore(Ranked Resolution, Ranked Range, Ranked Format, Ranked Channels, Ranked Codec, Ranked Remux);
}
