using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyBadge.Configuration;
using Jellyfin.Plugin.JellyBadge.Detection;
using Jellyfin.Plugin.JellyBadge.Processing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.JellyBadge.Tests;

/// <summary>
/// Backup, skip, replace and restore against a fake server. A series is used because its badges come
/// from the library query (empty here) plus its rating, so no media source plumbing is needed.
/// </summary>
public sealed class PosterProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellybadge-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IProviderManager> _providers = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Func<PosterProcessor> _newProcessor;
    private PosterProcessor _processor;
    private readonly Series _item;
    private readonly string _mediaPoster;
    private int _saves;
    private readonly Premieres _premieres;
    private readonly List<string> _tvMazeCalls = [];
    private Func<string, (HttpStatusCode Status, string Body)> _tvMaze = _ => (HttpStatusCode.NotFound, string.Empty);

    public PosterProcessorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        var appPaths = Mock.Of<IApplicationPaths>(p => p.PluginsPath == Path.Combine(_root, "plugins") && p.PluginConfigurationsPath == Path.Combine(_root, "config"));
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration { Enabled = true });
        _ = new Plugin(appPaths, xml.Object);

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetFileInfo(It.IsAny<string>())).Returns((string p) => new FileSystemMetadata { FullName = p, Exists = true, LastWriteTimeUtc = File.GetLastWriteTimeUtc(p) });
        fileSystem.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<FileSystemMetadata>())).Returns((FileSystemMetadata m) => m.LastWriteTimeUtc);
        BaseItem.LibraryManager = _library.Object;
        BaseItem.FileSystem = fileSystem.Object;

        // Like Jellyfin: copy into the internal metadata folder, point the item at it, delete the source.
        _providers
            .Setup(p => p.SaveImage(It.IsAny<BaseItem>(), It.IsAny<string>(), It.IsAny<string>(), ImageType.Primary, 0, false, It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, string source, string _, ImageType _, int? _, bool? _, CancellationToken _) =>
            {
                var target = Path.Combine(_root, "metadata", item.Id.ToString("N"), "poster.jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
                File.Delete(source);
                SetPoster(item, target);
                _saves++;
                return Task.CompletedTask;
            });

        var paths = Mock.Of<IServerApplicationPaths>(p => p.InternalMetadataPath == Path.Combine(_root, "metadata"));
        var http = new Mock<IHttpClientFactory>();
        http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new FakeTvMaze(this)));
        _premieres = new Premieres(http.Object, NullLogger<Premieres>.Instance);
        _newProcessor = () => new PosterProcessor(_library.Object, _providers.Object, paths, fileSystem.Object, _premieres, NullLogger<PosterProcessor>.Instance);
        _processor = _newProcessor();

        _mediaPoster = WritePoster(Path.Combine(_root, "media", "poster.jpg"), SKColors.SteelBlue);
        _item = new Series { Id = Guid.NewGuid(), Name = "Harbor Watch", CommunityRating = 8.4f };
        SetPoster(_item, _mediaPoster);
        _library.Setup(l => l.GetItemById(_item.Id)).Returns(_item);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
    }

    private static string DataDir => Plugin.Instance!.DataFolderPath;

    [Fact]
    public async Task BacksUpOriginalAndLeavesMediaFolderAlone()
    {
        var before = Bytes(_mediaPoster);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(1, _saves);
        Assert.Equal(before, Bytes(_mediaPoster));
        Assert.Equal(before, Bytes(Directory.GetFiles(Path.Combine(DataDir, "originals")).Single()));
        Assert.NotEqual(before, Bytes(CurrentPath()));
        Assert.StartsWith("Badged Harbor Watch: ", Activity.Read()[0].Split('\t')[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipsFinishedItemWithoutReadingIt()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);

        // Swap the bytes behind Jellyfin's back. A run that read the image would take it for a
        // replaced poster and badge it again; a run that trusts the done flag leaves it.
        WritePoster(CurrentPath(), SKColors.DarkOrange);
        File.SetLastWriteTimeUtc(CurrentPath(), _item.GetImageInfo(ImageType.Primary, 0)!.DateModified);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task RebadgesPosterRewrittenOnDiskBehindJellyfinsBack()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);

        // Another tool puts the original back in place; Jellyfin's record of the image stays the same.
        File.Copy(_mediaPoster, CurrentPath(), true);
        File.SetLastWriteTimeUtc(CurrentPath(), DateTime.UtcNow.AddMinutes(5));
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(2, _saves);
        Assert.NotEqual(Bytes(_mediaPoster), Bytes(CurrentPath()));
    }

    [Fact]
    public async Task ScanPointingBackAtMediaPosterReusesBadgedFile()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        var badged = CurrentPath();

        // What a library scan does when a poster.jpg sits next to the media.
        SetPoster(_item, _mediaPoster);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(1, _saves);
        Assert.Equal(badged, CurrentPath());
    }

    [Fact]
    public async Task RebadgesWhenInputsChange()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);

        // Jellyfin stamps DateLastSaved whenever it saves an item, a new rating included.
        _item.CommunityRating = 9.1f;
        _item.DateLastSaved = DateTime.UtcNow.AddMinutes(1);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(2, _saves);
        Assert.Equal(Bytes(_mediaPoster), Bytes(Directory.GetFiles(Path.Combine(DataDir, "originals")).Single()));
    }

    [Fact]
    public async Task SkipsUnchangedItemWithoutWorkingOutItsBadges()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);

        // Not saved by Jellyfin, so not a change: a repeat run does not even look at the rating.
        _item.CommunityRating = 9.1f;
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task ANewEpisodeMakesTheSeriesCheckAgain()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        _item.CommunityRating = 9.1f;
        var episode = new Episode { Id = Guid.NewGuid() };
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([episode]);

        await _processor.ProcessAsync(_item, CancellationToken.None, Sweep(episode, [new Badge(BadgeKind.Resolution, "4K")]));

        Assert.Equal(2, _saves);
    }

    [Fact]
    public async Task ReplacedPosterBecomesTheNewOriginal()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        var replacement = WritePoster(Path.Combine(_root, "media", "new.jpg"), SKColors.DarkOrange);
        SetPoster(_item, replacement);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(2, _saves);
        Assert.Equal(Bytes(replacement), Bytes(Directory.GetFiles(Path.Combine(DataDir, "originals")).Single()));
    }

    [Fact]
    public async Task RestoreAllPutsOriginalBackAndCleansUp()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        var badged = CurrentPath();

        Assert.Equal(1, await _processor.RestoreAllAsync(CancellationToken.None));
        Assert.Equal(_mediaPoster, CurrentPath());
        Assert.False(File.Exists(badged));
        Assert.Empty(Directory.GetFiles(Path.Combine(DataDir, "originals")));
        Assert.Empty(Directory.GetFiles(Path.Combine(DataDir, "state")));
        Assert.False(Plugin.Instance!.Configuration.Enabled);
    }

    [Fact]
    public async Task RestoreKeepsPosterReplacedAfterBadging()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        var replacement = WritePoster(Path.Combine(_root, "media", "new.jpg"), SKColors.DarkOrange);
        SetPoster(_item, replacement);

        Assert.Equal(0, await _processor.RestoreAllAsync(CancellationToken.None));
        Assert.Equal(replacement, CurrentPath());
    }

    [Fact]
    public async Task DamagedStateIsRebuiltFromBackupWithoutBadgingTwice()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        await File.WriteAllTextAsync(Directory.GetFiles(Path.Combine(DataDir, "state")).Single(), "{\"OriginalHa", TestContext.Current.CancellationToken);

        // Found damaged after a restart, then the rating changes.
        _processor = _newProcessor();
        _item.CommunityRating = 9.1f;
        _item.DateLastSaved = DateTime.UtcNow.AddMinutes(1);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(2, _saves);
        Assert.Equal(Bytes(_mediaPoster), Bytes(Directory.GetFiles(Path.Combine(DataDir, "originals")).Single()));
    }

    [Fact]
    public async Task RestoreAllGetsPastADamagedStateFile()
    {
        var other = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(DataDir, "state"));
        await File.WriteAllTextAsync(Path.Combine(DataDir, "state", other.ToString("N") + ".json"), string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(DataDir, "state", "not-an-id.json"), "{}", TestContext.Current.CancellationToken);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(1, await _processor.RestoreAllAsync(CancellationToken.None));
        Assert.Equal(_mediaPoster, CurrentPath());
    }

    [Fact]
    public async Task ForgetsRemovedItems()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        _library.Setup(l => l.GetItemById(_item.Id)).Returns((BaseItem?)null);

        Assert.Equal(1, _processor.ForgetRemoved());
        Assert.Empty(Directory.GetFiles(Path.Combine(DataDir, "originals")));
        Assert.Empty(Directory.GetFiles(Path.Combine(DataDir, "state")));
    }

    [Fact]
    public async Task ExcludedItemGetsItsOriginalBack()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        Plugin.Instance!.Configuration.ExcludedItems = [_item.Id.ToString("N")];
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(_mediaPoster, CurrentPath());
        Assert.Empty(Directory.GetFiles(Path.Combine(DataDir, "state")));
    }

    [Fact]
    public void LeavingASeriesAloneCoversItsSeasonsAndEpisodes()
    {
        Plugin.Instance!.Configuration.BadgeSeasons = true;
        Plugin.Instance.Configuration.BadgeEpisodes = true;
        Plugin.Instance.Configuration.ExcludedItems = [_item.Id.ToString("N")];

        Assert.False(_processor.IsCandidate(new Season { Id = Guid.NewGuid(), SeriesId = _item.Id }));
        Assert.False(_processor.IsCandidate(new Episode { Id = Guid.NewGuid(), SeriesId = _item.Id }));
        Assert.True(_processor.IsCandidate(new Episode { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task SeasonRatedZeroFallsBackToTheSeries()
    {
        Plugin.Instance!.Configuration.BadgeSeasons = true;
        var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", CommunityRating = 0, SeriesId = _item.Id };
        SetPoster(season, WritePoster(Path.Combine(_root, "media", "season.jpg"), SKColors.SteelBlue));

        await _processor.ProcessAsync(season, CancellationToken.None);

        Assert.Equal("Badged Season 1: 8.4", Activity.Read()[0].Split('\t')[2]);
    }

    [Fact]
    public async Task FindsPostersChangedBehindOurBack()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        Assert.Empty(_processor.FindChanged());

        SetPoster(_item, _mediaPoster);
        Assert.Equal(_item.Id, Assert.Single(_processor.FindChanged()).Id);
    }

    [Fact]
    public async Task APosterTouchedOnDiskIsFlaggedOnce()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);

        // Same bytes, newer timestamp, and Jellyfin still has the old one on record.
        File.SetLastWriteTimeUtc(CurrentPath(), DateTime.UtcNow.AddMinutes(1));
        Assert.Single(_processor.FindChanged());

        await _processor.ProcessAsync(_item, CancellationToken.None);
        Assert.Empty(_processor.FindChanged());
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task AScanSwappingTheOriginalBackMidSaveDoesNotLeaveThePosterBare()
    {
        // The scan points the item back at the poster next to the media right after our save.
        _providers
            .Setup(p => p.SaveImage(It.IsAny<BaseItem>(), It.IsAny<string>(), It.IsAny<string>(), ImageType.Primary, 0, false, It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, string source, string _, ImageType _, int? _, bool? _, CancellationToken _) =>
            {
                File.Delete(source);
                SetPoster(item, _mediaPoster);
                _saves++;
                return Task.CompletedTask;
            });
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Single(_processor.FindChanged());
    }

    [Fact]
    public async Task AnOriginalRecordedAsBadgedIsBadgedAgain()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);

        // State as an older version could leave it: the original poster on record as the badged one.
        var file = Path.Combine(DataDir, "state", _item.Id.ToString("N") + ".json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        json["OutputPath"] = _mediaPoster;
        json["OutputModified"] = File.GetLastWriteTimeUtc(_mediaPoster);
        await File.WriteAllTextAsync(file, json.ToJsonString());
        SetPoster(_item, _mediaPoster);

        // The 5-minute check comes first after a restart and reads every state at once.
        _processor.Dispose();
        _processor = _newProcessor();
        Assert.Single(_processor.FindChanged());
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(2, _saves);
        Assert.NotEqual(Bytes(_mediaPoster), Bytes(CurrentPath()));
    }

    [Fact]
    public async Task AnnouncedSeasonRedrawsTheSeriesTheSameDay()
    {
        Plugin.Instance!.Configuration.ShowStatus = true;
        _item.Status = SeriesStatus.Continuing;
        await _processor.ProcessAsync(_item, CancellationToken.None);

        // Jellyfin adds the premiere as an episode without a file; the series itself is not saved.
        var premiere = new Episode { Id = Guid.NewGuid(), IndexNumber = 1, ParentIndexNumber = 2, PremiereDate = DateTime.UtcNow.AddDays(1) };
        _library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.MinPremiereDate != null))).Returns([premiere]);
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(2, _saves);
        Assert.Contains("SEASON 2 SOON", Activity.Read()[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TvMazePremiereShowsSeasonSoonWithoutUpcomingEpisodesInJellyfin()
    {
        Plugin.Instance!.Configuration.ShowStatus = true;
        _item.Status = SeriesStatus.Continuing;
        _item.SetProviderId(MetadataProvider.Tvdb, "81189");
        await _processor.ProcessAsync(_item, CancellationToken.None);

        _tvMaze = url => url.Contains("lookup", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"id\":169}") : (HttpStatusCode.OK, NextEpisode(3, 1, 2));
        Assert.True(await _premieres.RefreshAsync(_item, CancellationToken.None));
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(["https://api.tvmaze.com/lookup/shows?thetvdb=81189", "https://api.tvmaze.com/shows/169?embed=nextepisode"], _tvMazeCalls);
        Assert.Equal(2, _saves);
        Assert.Contains("SEASON 3 SOON", Activity.Read()[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, 5, 2, null)]
    [InlineData(3, 1, 10, null)]
    [InlineData(3, 1, 2, 3)]
    public async Task OnlyAPremiereWithinAWeekCounts(int season, int number, int inDays, int? expected)
    {
        _item.SetProviderId(MetadataProvider.Tvdb, "81189");
        _tvMaze = url => url.Contains("lookup", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"id\":169}") : (HttpStatusCode.OK, NextEpisode(season, number, inDays));
        await _premieres.RefreshAsync(_item, CancellationToken.None);

        Assert.Equal(expected, _premieres.Season(_item.Id, DateTime.UtcNow, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task TvMazeIsAskedOnceADayAndOutagesKeepTheLastAnswer()
    {
        _item.SetProviderId(MetadataProvider.Tvdb, "81189");
        _tvMaze = url => url.Contains("lookup", StringComparison.Ordinal) ? (HttpStatusCode.OK, "{\"id\":169}") : (HttpStatusCode.OK, NextEpisode(3, 1, 2));
        await _premieres.RefreshAsync(_item, CancellationToken.None);
        Assert.False(_premieres.IsStale(_item.Id, DateTime.UtcNow));
        Assert.True(_premieres.IsStale(_item.Id, DateTime.UtcNow.AddHours(25)));

        _tvMaze = _ => (HttpStatusCode.ServiceUnavailable, string.Empty);
        Assert.False(await _premieres.RefreshAsync(_item, CancellationToken.None));
        Assert.Equal(3, _premieres.Season(_item.Id, DateTime.UtcNow, TimeSpan.FromDays(7)));

        // Remembered across a restart, with the TVmaze id so the lookup is not repeated.
        _premieres.Save();
        var http = new Mock<IHttpClientFactory>();
        http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new FakeTvMaze(this)));
        var restarted = new Premieres(http.Object, NullLogger<Premieres>.Instance);
        Assert.Equal(3, restarted.Season(_item.Id, DateTime.UtcNow, TimeSpan.FromDays(7)));
        _tvMazeCalls.Clear();
        _tvMaze = _ => (HttpStatusCode.OK, "{\"id\":169}");
        await restarted.RefreshAsync(_item, CancellationToken.None);
        Assert.Equal(["https://api.tvmaze.com/shows/169?embed=nextepisode"], _tvMazeCalls);
    }

    [Fact]
    public async Task SweepSkipsAnUnchangedSeriesWithoutAskingForItsEpisodes()
    {
        var episode = new Episode { Id = Guid.NewGuid() };
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([episode]);
        await _processor.ProcessAsync(_item, CancellationToken.None, Sweep(episode, [new Badge(BadgeKind.Resolution, "4K")]));
        _library.Invocations.Clear();

        await _processor.ProcessAsync(_item, CancellationToken.None, Sweep(episode, [new Badge(BadgeKind.Resolution, "4K")]));

        _library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task OffersTheBadgedPosterToScansUntilTheOriginalIsReplaced()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        Assert.Equal(CurrentPath(), _processor.BadgedPoster(_item));

        Plugin.Instance!.Configuration.Enabled = false;
        Assert.Null(_processor.BadgedPoster(_item));
        Plugin.Instance.Configuration.Enabled = true;

        // A new poster next to the media: Jellyfin should switch to it, and it gets badged as the new original.
        WritePoster(_mediaPoster, SKColors.OrangeRed);
        Assert.Null(_processor.BadgedPoster(_item));
    }

    [Fact]
    public async Task KeepsBadgesWhileMediaInfoIsMissing()
    {
        var episode = new Episode { Id = Guid.NewGuid() };
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([episode]);
        await _processor.ProcessAsync(_item, CancellationToken.None, Sweep(episode, [new Badge(BadgeKind.Resolution, "4K")]));
        var badged = Bytes(CurrentPath());

        // Mid-scan: the episode has no media info and the rating is not back yet.
        _item.CommunityRating = null;
        await _processor.ProcessAsync(_item, CancellationToken.None, Sweep(episode, []));

        Assert.Equal(1, _saves);
        Assert.Equal(badged, Bytes(CurrentPath()));
    }

    [Fact]
    public async Task PutsBadgedPosterBackRightAfterARefreshSwappedIt()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        var badged = CurrentPath();

        SetPoster(_item, _mediaPoster);

        Assert.True(await _processor.RepointAsync(_item, CancellationToken.None));
        Assert.Equal(badged, CurrentPath());
        Assert.Equal(1, _saves);
        Assert.Empty(_processor.FindChanged());
        Assert.False(await _processor.RepointAsync(_item, CancellationToken.None));
    }

    [Fact]
    public async Task SwappedPosterGetsItsBadgesBackEvenWhileMediaInfoIsMissing()
    {
        var episode = new Episode { Id = Guid.NewGuid() };
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([episode]);
        await _processor.ProcessAsync(_item, CancellationToken.None, Sweep(episode, [new Badge(BadgeKind.Resolution, "4K")]));
        var badged = CurrentPath();

        // A scan swaps in the original and has the episode's media info half read.
        SetPoster(_item, _mediaPoster);
        await _processor.ProcessAsync(_item, CancellationToken.None, Sweep(episode, []));

        Assert.Equal(badged, CurrentPath());
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task ARealNewPosterIsNotSwappedBack()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        SetPoster(_item, WritePoster(Path.Combine(_root, "media", "new.jpg"), SKColors.DarkOrange));

        Assert.False(await _processor.RepointAsync(_item, CancellationToken.None));
    }

    [Fact]
    public async Task KeepsBadgesWhenTheLibraryCannotBeTold()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        var badged = CurrentPath();
        Plugin.Instance!.Configuration.Libraries = [Guid.NewGuid().ToString()];
        _library.Setup(l => l.GetCollectionFolders(_item)).Returns([]);

        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(badged, CurrentPath());
        Assert.Single(Directory.GetFiles(Path.Combine(DataDir, "state")));
    }

    [Fact]
    public async Task DoesNothingWhenOff()
    {
        Plugin.Instance!.Configuration.Enabled = false;
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(0, _saves);
        Assert.Equal(_mediaPoster, CurrentPath());
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);

    private string CurrentPath() => _item.GetImageInfo(ImageType.Primary, 0)!.Path;

    private static void SetPoster(BaseItem item, string path)
        => item.ImageInfos = [new ItemImageInfo { Path = path, Type = ImageType.Primary, DateModified = File.GetLastWriteTimeUtc(path) }];

    private static string WritePoster(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(300, 450);
        bitmap.Erase(color);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private SweepCache Sweep(Episode episode, List<Badge> badges)
    {
        episode.SeriesId = _item.Id;
        var sweep = new SweepCache([episode]);
        sweep.Episodes[episode.Id] = badges;
        return sweep;
    }

    private static string NextEpisode(int season, int number, int inDays)
        => "{\"id\":169,\"_embedded\":{\"nextepisode\":{\"season\":" + season + ",\"number\":" + number
            + ",\"airstamp\":\"" + DateTime.UtcNow.AddDays(inDays).ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\"}}}";

    private sealed class FakeTvMaze(PosterProcessorTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            test._tvMazeCalls.Add(url);
            var (status, body) = test._tvMaze(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
