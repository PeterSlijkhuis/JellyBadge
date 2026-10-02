using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyBadge.Configuration;
using Jellyfin.Plugin.JellyBadge.Detection;
using Jellyfin.Plugin.JellyBadge.Rendering;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBadge.Processing;

/// <summary>
/// Backs up, badges and restores posters. All poster writes go through here.
/// </summary>
public sealed class PosterProcessor : IDisposable
{
    // Bump when the drawing changes, so every poster gets re-rendered once.
    private const int RenderVersion = 1;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerApplicationPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<PosterProcessor> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly int _slots;
    private readonly ConcurrentDictionary<Guid, byte> _busy = new();
    private readonly ConcurrentDictionary<Guid, (string Path, DateTime Modified)> _written = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterProcessor"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="paths">Server paths.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="logger">Logger.</param>
    public PosterProcessor(ILibraryManager libraryManager, IProviderManager providerManager, IServerApplicationPaths paths, IFileSystem fileSystem, ILogger<PosterProcessor> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _paths = paths;
        _fileSystem = fileSystem;
        _logger = logger;

        // ponytail: limit is read once at startup, a change needs a server restart.
        _slots = Math.Max(1, Plugin.Instance?.Configuration.MaxConcurrency ?? 2);
        _gate = new SemaphoreSlim(_slots);
        Current = this;
    }

    /// <summary>
    /// Gets the running instance, for code outside dependency injection (uninstalling).
    /// </summary>
    internal static PosterProcessor? Current { get; private set; }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static string DataDir => Plugin.Instance!.DataFolderPath;

    /// <summary>
    /// Returns true if the item's current poster is exactly the one we last set, so its update event can be ignored.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether the current poster is our own write.</returns>
    public bool IsOwnWrite(BaseItem item)
    {
        var info = item.GetImageInfo(ImageType.Primary, 0);
        return info is not null && _written.TryGetValue(item.Id, out var w) && w.Path == info.Path && w.Modified == info.DateModified;
    }

    /// <summary>
    /// Whether this item is a movie, series or (when switched on) episode or collection in an included library.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True if it should be badged.</returns>
    public bool IsCandidate(BaseItem item)
    {
        if (item is not (Movie or Series or Episode or BoxSet) || item.IsVirtualItem
            || (item is Episode && !Config.BadgeEpisodes) || (item is BoxSet && !Config.BadgeCollections))
        {
            return false;
        }

        var libraries = Config.Libraries;
        return libraries.Length == 0
            || _libraryManager.GetCollectionFolders(item).Any(f => libraries.Any(l => Guid.TryParse(l, out var id) && id == f.Id));
    }

    /// <summary>
    /// Badges one item if anything changed since last time.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ProcessAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (!Config.Enabled || !_busy.TryAdd(item.Id, 0))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Config.Enabled)
            {
                // Switched off while we waited: a restore is running or about to.
                return;
            }

            if (IsCandidate(item))
            {
                await ProcessCoreAsync(item, cancellationToken).ConfigureAwait(false);
            }
            else if (LoadState(item.Id) is { } state)
            {
                // Excluded since we badged it (episodes switched off, library deselected): put its original back.
                await RestoreAsync(item, state, cancellationToken).ConfigureAwait(false);
                File.Delete(OriginalFile(item.Id, state));
                File.Delete(StateFile(item.Id));
            }
        }
        finally
        {
            _gate.Release();
            _busy.TryRemove(item.Id, out _);
        }
    }

    /// <summary>
    /// Renders the poster with the given settings without saving anything.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="config">Settings to preview.</param>
    /// <returns>The image and its mime type, or null if the item has no poster.</returns>
    public (byte[] Image, string MimeType)? Preview(BaseItem item, PluginConfiguration config)
    {
        var current = ReadPrimary(item);
        if (current is null)
        {
            return null;
        }

        var state = LoadState(item.Id);
        var original = state is not null && state.Ours.Contains(Hash(current.Value.Bytes)) && File.Exists(OriginalFile(item.Id, state))
            ? File.ReadAllBytes(OriginalFile(item.Id, state))
            : current.Value.Bytes;

        var image = BadgeRenderer.Render(original, GetBadges(item, config), config, out var mime);
        return (image, mime);
    }

    /// <summary>
    /// Turns badging off, puts every original poster back and deletes all plugin data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many posters were restored.</returns>
    public async Task<int> RestoreAllAsync(CancellationToken cancellationToken)
    {
        // Off first so no event re-badges a poster we just restored.
        if (Config.Enabled)
        {
            Config.Enabled = false;
            Plugin.Instance!.SaveConfiguration();
        }

        // Take every slot, so no poster is being badged while we restore.
        for (var i = 0; i < _slots; i++)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await RestoreAllCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release(_slots);
        }
    }

    private async Task<int> RestoreAllCoreAsync(CancellationToken cancellationToken)
    {
        var restored = 0;
        var stateDir = Path.Combine(DataDir, "state");
        if (!Directory.Exists(stateDir))
        {
            return 0;
        }

        foreach (var file in Directory.GetFiles(stateDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.Parse(Path.GetFileNameWithoutExtension(file));
            var state = JsonSerializer.Deserialize<PosterState>(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false))!;
            var item = _libraryManager.GetItemById(id);

            try
            {
                if (item is not null && await RestoreAsync(item, state, cancellationToken).ConfigureAwait(false))
                {
                    restored++;
                }

                File.Delete(OriginalFile(id, state));
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the backup so a retry can still restore it.
                _logger.LogError(ex, "Could not restore poster for {Item}", item?.Name ?? id.ToString());
            }
        }

        _logger.LogInformation("Restored {Count} original posters", restored);
        return restored;
    }

    private async Task ProcessCoreAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Config;
        var badges = GetBadges(item, config);
        var state = LoadState(item.Id);
        var info = item.GetImageInfo(ImageType.Primary, 0);

        // Done before: same image file as we left it and same badges and settings. Skip without reading the image.
        if (state is not null && info is not null && info.Path == state.OutputPath && info.DateModified == state.OutputModified
            && InputHash(state, badges, config) == state.InputHash)
        {
            return;
        }

        var current = ReadPrimary(item);
        if (current is null)
        {
            return;
        }

        var (currentBytes, currentPath) = current.Value;
        var currentHash = Hash(currentBytes);
        byte[] original;

        if (state is not null && state.Ours.Contains(currentHash))
        {
            // The poster is one we made.
            var file = OriginalFile(item.Id, state);
            if (!File.Exists(file))
            {
                _logger.LogWarning("Backup for {Item} is missing, leaving its poster alone", item.Name);
                return;
            }

            original = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // First run, or something (a metadata refresh, a user upload) replaced the poster: it is the new original.
            original = currentBytes;
            if (state is null || currentHash != state.OriginalHash)
            {
                if (state is not null)
                {
                    File.Delete(OriginalFile(item.Id, state));
                    _logger.LogInformation("Poster of {Item} was replaced, using it as the new original", item.Name);
                }

                state = new PosterState { OriginalHash = currentHash, OriginalExtension = Path.GetExtension(currentPath) };
                Directory.CreateDirectory(Path.Combine(DataDir, "originals"));
                await File.WriteAllBytesAsync(OriginalFile(item.Id, state), original, cancellationToken).ConfigureAwait(false);
            }

            state.OriginalPath = currentPath;
            state.OutputHash = string.Empty;
        }

        var inputHash = InputHash(state, badges, config);
        if (inputHash == state.InputHash && currentHash == state.OutputHash)
        {
            MarkDone(item, state);
            return;
        }

        if (badges.Count == 0 && currentHash == state.OriginalHash)
        {
            // Nothing to show and the poster is untouched: record that and leave the file alone.
            state.OutputHash = currentHash;
            state.Ours.Add(currentHash);
            state.InputHash = inputHash;
            MarkDone(item, state);
            return;
        }

        var output = badges.Count == 0 ? original : BadgeRenderer.Render(original, badges, config, out _);

        // State first: if the save below dies halfway, the next run still knows which image is ours and never mistakes it for an original.
        state.OutputHash = Hash(output);
        state.Ours.Add(state.OutputHash);
        state.InputHash = inputHash;
        SaveState(item.Id, state);
        await SavePrimaryAsync(item, output, BadgeRenderer.MimeType(output), cancellationToken).ConfigureAwait(false);
        MarkDone(item, state);
        _logger.LogDebug("Badged {Item}: {Badges}", item.Name, string.Join(", ", badges.Select(b => b.Text)));
    }

    private static string InputHash(PosterState state, List<Badge> badges, PluginConfiguration config)
        => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            RenderVersion,
            state.OriginalHash,
            badges,
            config.Position,
            config.Style,
            config.Size
        })));

    // Remember which file and timestamp we left, so later runs can skip this item without reading the image.
    private static void MarkDone(BaseItem item, PosterState state)
    {
        var info = item.GetImageInfo(ImageType.Primary, 0);
        state.OutputPath = info?.Path ?? string.Empty;
        state.OutputModified = info?.DateModified ?? default;
        SaveState(item.Id, state);
    }

    private async Task<bool> RestoreAsync(BaseItem item, PosterState state, CancellationToken cancellationToken)
    {
        var current = ReadPrimary(item);
        if (current is null || !state.Ours.Contains(Hash(current.Value.Bytes)))
        {
            // Replaced since we badged it, so the current poster is newer than our backup. Leave it.
            return false;
        }

        if (state.OriginalPath != current.Value.Path && File.Exists(state.OriginalPath) && Hash(await File.ReadAllBytesAsync(state.OriginalPath, cancellationToken).ConfigureAwait(false)) == state.OriginalHash)
        {
            // The original file is still where it was (for example next to the media): point the item back at it.
            item.SetImagePath(ImageType.Primary, 0, _fileSystem.GetFileInfo(state.OriginalPath));
            RememberWrite(item);
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            if (current.Value.Path.StartsWith(_paths.InternalMetadataPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(current.Value.Path);
            }

            return true;
        }

        var bytes = await File.ReadAllBytesAsync(OriginalFile(item.Id, state), cancellationToken).ConfigureAwait(false);
        await SavePrimaryAsync(item, bytes, BadgeRenderer.MimeType(bytes), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task SavePrimaryAsync(BaseItem item, byte[] image, string mime, CancellationToken cancellationToken)
    {
        // SaveImage deletes this file when done. saveLocallyWithMedia: false keeps us out of the user's media folders.
        Directory.CreateDirectory(DataDir);
        var temp = Path.Combine(DataDir, "tmp-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(temp, image, cancellationToken).ConfigureAwait(false);
        await _providerManager.SaveImage(item, temp, mime, ImageType.Primary, 0, false, cancellationToken).ConfigureAwait(false);
        RememberWrite(item);
        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
    }

    // Recorded before UpdateToRepositoryAsync, because that is what fires the ItemUpdated event.
    private void RememberWrite(BaseItem item)
    {
        var info = item.GetImageInfo(ImageType.Primary, 0);
        if (info is not null)
        {
            _written[item.Id] = (info.Path, info.DateModified);
        }
    }

    private List<Badge> GetBadges(BaseItem item, PluginConfiguration config)
    {
        List<Badge> technical;
        if (item is Series series)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false
            });
            technical = BadgeDetector.MostCommon(episodes.Select(e => (IReadOnlyList<Badge>)BestVersion(e)).ToList());
        }
        else if (item is BoxSet boxSet)
        {
            // Like a series: the most common quality of the movies in it.
            technical = BadgeDetector.MostCommon(boxSet.GetLinkedChildren().OfType<Movie>().Select(m => (IReadOnlyList<Badge>)BestVersion(m)).ToList());
        }
        else
        {
            technical = BestVersion(item);
        }

        return technical
            .Concat(BadgeDetector.Ratings(item.CommunityRating, item.CriticRating))
            .Where(b => b.Kind switch
            {
                BadgeKind.Resolution => config.ShowResolution,
                BadgeKind.DynamicRange => config.ShowDynamicRange,
                BadgeKind.AudioFormat => config.ShowAudioFormat,
                BadgeKind.AudioChannels => config.ShowAudioChannels,
                BadgeKind.VideoCodec => config.ShowVideoCodec,
                BadgeKind.Remux => config.ShowRemux,
                BadgeKind.CommunityRating => config.ShowCommunityRating,
                _ => config.ShowCriticRating
            })
            .ToList();
    }

    private static List<Badge> BestVersion(BaseItem item)
        => BadgeDetector.BestVersion(item.GetMediaSources(false).Select(s => ((IReadOnlyList<MediaStream>)s.MediaStreams, (string?)s.Path)));

    private static (byte[] Bytes, string Path)? ReadPrimary(BaseItem item)
    {
        var info = item.GetImageInfo(ImageType.Primary, 0);
        if (info is null || !info.IsLocalFile || !File.Exists(info.Path))
        {
            return null;
        }

        return (File.ReadAllBytes(info.Path), info.Path);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string OriginalFile(Guid id, PosterState state) => Path.Combine(DataDir, "originals", id.ToString("N") + state.OriginalExtension);

    private static string StateFile(Guid id) => Path.Combine(DataDir, "state", id.ToString("N") + ".json");

    private static PosterState? LoadState(Guid id)
    {
        var file = StateFile(id);
        return File.Exists(file) ? JsonSerializer.Deserialize<PosterState>(File.ReadAllText(file)) : null;
    }

    private static void SaveState(Guid id, PosterState state)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "state"));
        File.WriteAllText(StateFile(id), JsonSerializer.Serialize(state));
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private sealed class PosterState
    {
        public string OriginalHash { get; set; } = string.Empty;

        public string OriginalPath { get; set; } = string.Empty;

        public string OriginalExtension { get; set; } = string.Empty;

        public string OutputHash { get; set; } = string.Empty;

        public string InputHash { get; set; } = string.Empty;

        public string OutputPath { get; set; } = string.Empty;

        public DateTime OutputModified { get; set; }

        // Every image we ever wrote from this original. Matching any of them means "ours", so an
        // interrupted save can never make us back up a badged poster as if it were the original.
        public HashSet<string> Ours { get; set; } = [];
    }
}
