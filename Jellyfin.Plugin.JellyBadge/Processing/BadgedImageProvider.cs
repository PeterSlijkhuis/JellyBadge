using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.JellyBadge.Processing;

/// <summary>
/// A scan or refresh takes the first local poster it finds. Offering the badged one before Jellyfin's own local images
/// means it keeps the badges, instead of switching to the poster next to the media and waiting for us to switch back.
/// </summary>
public sealed class BadgedImageProvider : ILocalImageProvider, IHasOrder
{
    private readonly PosterProcessor _processor;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="BadgedImageProvider"/> class.
    /// </summary>
    /// <param name="processor">Poster processor.</param>
    /// <param name="fileSystem">File system.</param>
    public BadgedImageProvider(PosterProcessor processor, IFileSystem fileSystem)
    {
        _processor = processor;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public string Name => "JellyBadge";

    /// <inheritdoc />
    public int Order => -1;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Movie or Series or Season or Episode or BoxSet;

    /// <inheritdoc />
    public IEnumerable<LocalImageInfo> GetImages(BaseItem item, IDirectoryService directoryService)
    {
        try
        {
            if (_processor.BadgedPoster(item) is { } path)
            {
                return [new LocalImageInfo { FileInfo = _fileSystem.GetFileInfo(path), Type = ImageType.Primary }];
            }
        }
        catch (IOException)
        {
            // A file mid-write or gone: let Jellyfin pick as usual, the other checks put the badges back.
        }

        return [];
    }
}
