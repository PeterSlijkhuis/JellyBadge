using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyBadge.Configuration;
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
    private readonly PosterProcessor _processor;
    private readonly Series _item;
    private readonly string _mediaPoster;
    private int _saves;

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
        _processor = new PosterProcessor(_library.Object, _providers.Object, paths, fileSystem.Object, NullLogger<PosterProcessor>.Instance);

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
    public async Task RebadgesWhenInputsChange()
    {
        await _processor.ProcessAsync(_item, CancellationToken.None);
        _item.CommunityRating = 9.1f;
        await _processor.ProcessAsync(_item, CancellationToken.None);

        Assert.Equal(2, _saves);
        Assert.Equal(Bytes(_mediaPoster), Bytes(Directory.GetFiles(Path.Combine(DataDir, "originals")).Single()));
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
}
