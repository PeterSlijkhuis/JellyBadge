using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyBadge.Detection;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyBadge.Tests;

public class BadgeDetectorTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData("uhd-dolby-vision-atmos", "4K,DOLBY VISION,HEVC,ATMOS,7.1")]
    [InlineData("fhd-hdr10plus-dtsx", "1080p,HDR10+,HEVC,DTS:X,7.1")]
    [InlineData("scope-sdr-dtshdma", "1080p,H.264,DTS-HD MA,5.1")]
    [InlineData("hd-hlg-eac3-atmos", "720p,HLG,HEVC,ATMOS,5.1")]
    [InlineData("sd-stereo", "SD,MPEG-2,AAC,2.0")]
    [InlineData("dolby-vision-invalid", "4K,HDR10,HEVC,TRUEHD,7.1")]
    public void DetectsTechnicalBadges(string fixture, string expected)
    {
        var badges = BadgeDetector.BestVersion([Load(fixture)]);

        Assert.Equal(expected, Texts(badges));
    }

    [Fact]
    public void PicksBestVersion()
    {
        var badges = BadgeDetector.BestVersion([Load("sd-stereo"), Load("uhd-dolby-vision-atmos"), Load("fhd-hdr10plus-dtsx")]);

        Assert.Equal("4K,DOLBY VISION,HEVC,ATMOS,7.1", Texts(badges));
    }

    [Theory]
    [InlineData("/movies/Dune (2021)/Dune.2021.2160p.UHD.BluRay.REMUX.HDR.mkv", true)]
    [InlineData("/movies/Dune (2021) Remux/Dune.mkv", true)]
    [InlineData("/movies/Dune (2021)/Dune-remux.mkv", true)]
    [InlineData("/movies/Dune (2021)/Dune.2021.2160p.WEB-DL.mkv", false)]
    [InlineData("/movies/Remuxes/Dune.mkv", false)]
    [InlineData(null, false)]
    public void DetectsRemuxFromFileOrFolderName(string? path, bool remux)
    {
        var badges = BadgeDetector.BestVersion([(Load("uhd-dolby-vision-atmos"), path)]);

        Assert.Equal(remux, badges.Any(b => b.Kind == BadgeKind.Remux));
    }

    [Fact]
    public void PrefersRemuxOverEqualEncode()
    {
        var badges = BadgeDetector.BestVersion([(Load("uhd-dolby-vision-atmos"), "/m/Dune.WEB-DL.mkv"), (Load("uhd-dolby-vision-atmos"), "/m/Dune.REMUX.mkv")]);

        Assert.Equal("4K,DOLBY VISION,HEVC,REMUX,ATMOS,7.1", Texts(badges));
    }

    [Fact]
    public void DetectsAv1()
    {
        Assert.Equal("1080p,AV1,OPUS,2.0", Texts(BadgeDetector.BestVersion([Load("fhd-av1-stereo")])));
    }

    [Theory]
    [InlineData("eac3", null, "DD+")]
    [InlineData("ac3", null, "DD")]
    [InlineData("dts", "DTS-HD HRA", "DTS")]
    [InlineData("dts", "DTS-HD MA", "DTS-HD MA")]
    [InlineData("flac", null, "FLAC")]
    [InlineData("pcm_s24le", null, "PCM")]
    [InlineData("vorbis", null, "")]
    public void NamesAudioFormats(string codec, string? profile, string expected)
    {
        var audio = new MediaStream { Type = MediaStreamType.Audio, Codec = codec, Profile = profile };
        Assert.Equal(expected, Texts(BadgeDetector.BestVersion([new List<MediaStream> { audio }])));
    }

    [Theory]
    [InlineData(3, null, "2.1")]
    [InlineData(3, "2.1", "2.1")]
    [InlineData(6, "5.1(side)", "5.1")]
    [InlineData(5, "5.0", "5.0")]
    [InlineData(2, "stereo", "2.0")]
    [InlineData(1, "mono", "MONO")]
    public void ReadsChannelLayout(int channels, string? layout, string expected)
    {
        var audio = new MediaStream { Type = MediaStreamType.Audio, Channels = channels, ChannelLayout = layout };
        Assert.Equal(expected, Texts(BadgeDetector.BestVersion([new List<MediaStream> { audio }])));
    }

    [Theory]
    [InlineData("uhd-dolby-vision-atmos", "4K,DOLBY VISION,HEVC,ATMOS,7.1")]
    [InlineData("scope-sdr-dtshdma", "1080p,DTS-HD MA,5.1")]
    [InlineData("hd-hlg-eac3-atmos", "HLG,HEVC,ATMOS,5.1")]
    [InlineData("sd-stereo", "")]
    public void PremiumLeavesOutEverydayQuality(string fixture, string expected)
    {
        Assert.Equal(expected, Texts(BadgeDetector.BestVersion([Load(fixture)]).Where(BadgeDetector.IsPremium).ToList()));
    }

    [Fact]
    public void NoStreamsMeansNoBadges()
    {
        Assert.Empty(BadgeDetector.BestVersion(new List<IReadOnlyList<MediaStream>>()));
        Assert.Empty(BadgeDetector.BestVersion([[]]));
    }

    [Fact]
    public void SeriesUsesMostCommonValuePerGroup()
    {
        var hdr = BadgeDetector.BestVersion([Load("fhd-hdr10plus-dtsx")]);
        var sdr = BadgeDetector.BestVersion([Load("scope-sdr-dtshdma")]);
        var uhd = BadgeDetector.BestVersion([Load("uhd-dolby-vision-atmos")]);

        // Two 1080p SDR episodes, one 1080p HDR10+, one 4K DV: 1080p wins, and "no HDR" wins so there is no HDR badge.
        var badges = BadgeDetector.MostCommon([sdr, sdr, hdr, uhd]);

        Assert.Equal("1080p,H.264,DTS-HD MA,5.1", Texts(badges));
    }

    [Theory]
    [InlineData(7.84f, 92f, "7.8,92%")]
    [InlineData(null, 61.6f, "62%")]
    [InlineData(8f, null, "8.0")]
    [InlineData(0f, 0f, "")]
    public void FormatsRatings(float? community, float? critic, string expected)
    {
        Assert.Equal(expected, Texts(BadgeDetector.Ratings(community, critic)));
    }

    private static IReadOnlyList<MediaStream> Load(string name)
        => JsonSerializer.Deserialize<List<MediaStream>>(File.ReadAllText(Path.Combine("Fixtures", name + ".json")), Options)!;

    private static string Texts(IEnumerable<Badge> badges) => string.Join(',', badges.Select(b => b.Text));
}
