using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const int RenderVersion = 2;

    // How far ahead a season premiere puts SEASON n SOON on its series.
    private const int UpcomingDays = 7;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerApplicationPaths _paths;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<PosterProcessor> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly int _slots;
    private readonly ConcurrentDictionary<Guid, byte> _busy = new();
    private readonly ConcurrentDictionary<Guid, byte> _again = new();
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
    /// Whether this item is a movie, series or (when switched on) season, episode or collection in an included library.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True if it should be badged.</returns>
    public bool IsCandidate(BaseItem item)
    {
        if (item is not (Movie or Series or Season or Episode or BoxSet) || item.IsVirtualItem
            || (item is Episode && !Config.BadgeEpisodes) || (item is Season && !Config.BadgeSeasons) || (item is BoxSet && !Config.BadgeCollections))
        {
            return false;
        }

        // Leaving a series or season alone covers its seasons and episodes too.
        var excluded = Config.ExcludedItems;
        Guid[] ids = item switch
        {
            Episode e => [e.Id, e.SeriesId, e.SeasonId],
            Season se => [se.Id, se.SeriesId],
            _ => [item.Id]
        };
        if (ids.Any(id => excluded.Contains(id.ToString("N"), StringComparer.OrdinalIgnoreCase)))
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
    /// <param name="episodeCache">Quality badges per episode, shared during one sweep so each episode is read once.</param>
    /// <returns>A task.</returns>
    public async Task ProcessAsync(BaseItem item, CancellationToken cancellationToken, ConcurrentDictionary<Guid, List<Badge>>? episodeCache = null)
    {
        if (!Config.Enabled)
        {
            return;
        }

        if (!_busy.TryAdd(item.Id, 0))
        {
            // Busy with this item: run it again when done, so a change that came in meanwhile (a new poster) is not lost.
            _again.TryAdd(item.Id, 0);
            return;
        }

        try
        {
            await ProcessGatedAsync(item, episodeCache, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy.TryRemove(item.Id, out _);
        }

        if (_again.TryRemove(item.Id, out _))
        {
            await ProcessAsync(item, cancellationToken, episodeCache).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the backup and state of an item that left the library.
    /// </summary>
    /// <param name="id">The item id.</param>
    public void Forget(Guid id)
    {
        if (LoadState(id, null) is { } state)
        {
            File.Delete(OriginalFile(id, state));
            File.Delete(StateFile(id));
            _written.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Forgets every item that is no longer in the library, for removals that happened while the server was down.
    /// </summary>
    /// <returns>How many were forgotten.</returns>
    public int ForgetRemoved()
    {
        var stateDir = Path.Combine(DataDir, "state");
        var removed = 0;
        foreach (var file in Directory.Exists(stateDir) ? Directory.GetFiles(stateDir, "*.json") : [])
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id) && _libraryManager.GetItemById(id) is null)
            {
                Forget(id);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// How many posters carry badges right now.
    /// </summary>
    /// <returns>The count.</returns>
    public int CountBadged()
    {
        var stateDir = Path.Combine(DataDir, "state");
        return Directory.Exists(stateDir)
            ? Directory.GetFiles(stateDir, "*.json").Count(f => TryRead(f) is { } s && s.OutputHash.Length > 0 && s.OutputHash != s.OriginalHash)
            : 0;
    }

    /// <summary>
    /// Items whose poster is no longer the one JellyBadge left, found without reading any image.
    /// </summary>
    /// <returns>The items to check again.</returns>
    public List<BaseItem> FindChanged()
    {
        var stateDir = Path.Combine(DataDir, "state");
        var changed = new List<BaseItem>();
        foreach (var file in Directory.Exists(stateDir) ? Directory.GetFiles(stateDir, "*.json") : [])
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id) || _libraryManager.GetItemById(id) is not { } item
                || TryRead(file) is not { } state || state.OutputHash.Length == 0)
            {
                continue;
            }

            var info = item.GetImageInfo(ImageType.Primary, 0);
            if (info is null || info.Path != state.OutputPath || info.DateModified != state.OutputModified
                || !File.Exists(info.Path) || File.GetLastWriteTimeUtc(info.Path) != state.OutputModified)
            {
                changed.Add(item);
            }
        }

        return changed;
    }

    /// <summary>
    /// Points the item back at its badged poster when a scan or refresh swapped in the original. No drawing and no
    /// waiting for other posters, so the badges are back within moments.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the badged poster was put back.</returns>
    public async Task<bool> RepointAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (!Config.Enabled || !_busy.TryAdd(item.Id, 0))
        {
            return false;
        }

        try
        {
            return LoadState(item.Id, item) is { } state && await TryRepointAsync(item, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy.TryRemove(item.Id, out _);
        }
    }

    private async Task<bool> TryRepointAsync(BaseItem item, PosterState state, CancellationToken cancellationToken)
    {
        // Only when our badged file is untouched and the poster now shown is exactly the original we backed up.
        var info = item.GetImageInfo(ImageType.Primary, 0);
        if (!Config.Enabled || info is null || !info.IsLocalFile || state.OutputHash.Length == 0 || state.OutputHash == state.OriginalHash
            || info.Path == state.OutputPath || !File.Exists(state.OutputPath) || File.GetLastWriteTimeUtc(state.OutputPath) != state.OutputModified
            || !File.Exists(info.Path)
            || Hash(await File.ReadAllBytesAsync(info.Path, cancellationToken).ConfigureAwait(false)) != state.OriginalHash)
        {
            return false;
        }

        item.SetImagePath(ImageType.Primary, 0, _fileSystem.GetFileInfo(state.OutputPath));
        RememberWrite(item);
        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        MarkDone(item, state);
        return true;
    }

    // A library filter that cannot tell where the item lives (mid-scan) says nothing: never restore on that.
    private bool LibraryUnknown(BaseItem item)
        => Config.Libraries.Length > 0 && _libraryManager.GetCollectionFolders(item).Count == 0;

    private async Task ProcessGatedAsync(BaseItem item, ConcurrentDictionary<Guid, List<Badge>>? episodeCache, CancellationToken cancellationToken)
    {
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
                await ProcessCoreAsync(item, episodeCache, cancellationToken).ConfigureAwait(false);
            }
            else if (!item.IsVirtualItem && !LibraryUnknown(item) && LoadState(item.Id, item) is { } state)
            {
                // Excluded since we badged it (episodes switched off, library deselected): put its original back.
                Activity.Info(_logger, "{Item} is no longer included, restoring its original poster", item.Name);
                await RestoreAsync(item, state, cancellationToken).ConfigureAwait(false);
                File.Delete(OriginalFile(item.Id, state));
                File.Delete(StateFile(item.Id));
            }
        }
        finally
        {
            _gate.Release();
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

        var state = LoadState(item.Id, item);
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
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(id);
            if (LoadState(id, item) is not { } state)
            {
                continue;
            }

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
                Activity.Log(_logger, LogLevel.Error, ex, "Could not restore poster for {Item}", item?.Name ?? id.ToString());
            }
        }

        Activity.Info(_logger, "Restored {Count} original posters", restored);
        return restored;
    }

    private async Task ProcessCoreAsync(BaseItem item, ConcurrentDictionary<Guid, List<Badge>>? episodeCache, CancellationToken cancellationToken)
    {
        var config = Config;
        var state = LoadState(item.Id, item);

        // Swapped back to the original by a scan or refresh: badged file first, then see whether the badges changed.
        if (state is not null)
        {
            await TryRepointAsync(item, state, cancellationToken).ConfigureAwait(false);
        }

        var badges = GetBadges(item, config, episodeCache, out var mediaInfoMissing);

        // Media info is briefly gone while Jellyfin scans a file again. Drawing now would drop the quality badges,
        // so keep the badged poster that is there; the regular check comes back once the info is in.
        // ponytail: a file that never gets media info keeps its last badges, even when its rating changes.
        if (mediaInfoMissing && state is not null && state.OutputHash.Length > 0 && state.OutputHash != state.OriginalHash
            && item.GetImageInfo(ImageType.Primary, 0)?.Path == state.OutputPath)
        {
            return;
        }

        var (lastOutputPath, lastOutputHash) = (state?.OutputPath, state?.OutputHash);
        var info = item.GetImageInfo(ImageType.Primary, 0);

        // Done before: same image file as we left it and same badges and settings. Skip without reading the image.
        // The file's own timestamp is checked too, because other tools (Radarr, Sonarr, metadata plugins) can rewrite
        // a poster on disk without Jellyfin noticing.
        if (state is not null && info is not null && info.Path == state.OutputPath && info.DateModified == state.OutputModified
            && File.Exists(info.Path) && File.GetLastWriteTimeUtc(info.Path) == state.OutputModified
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
                Activity.Log(_logger, LogLevel.Warning, null, "Backup for {Item} is missing, leaving its poster alone", item.Name);
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
                    Activity.Info(_logger, "Poster of {Item} was replaced, using it as the new original", item.Name);
                }

                state = new PosterState { OriginalHash = currentHash, OriginalExtension = Path.GetExtension(currentPath) };
                Directory.CreateDirectory(Path.Combine(DataDir, "originals"));
                await File.WriteAllBytesAsync(OriginalFile(item.Id, state), original, cancellationToken).ConfigureAwait(false);
            }

            state.OriginalPath = currentPath;
            state.OutputHash = string.Empty;
        }

        var inputHash = InputHash(state, badges, config);

        // A library scan points items back at the poster next to the media. When our badged file is still
        // there and still right, point the item back at it instead of drawing it again.
        if (currentHash == state.OriginalHash && inputHash == state.InputHash && !string.IsNullOrEmpty(lastOutputHash)
            && lastOutputPath != currentPath && File.Exists(lastOutputPath)
            && Hash(await File.ReadAllBytesAsync(lastOutputPath, cancellationToken).ConfigureAwait(false)) == lastOutputHash)
        {
            state.OutputHash = lastOutputHash;
            item.SetImagePath(ImageType.Primary, 0, _fileSystem.GetFileInfo(lastOutputPath));
            RememberWrite(item);
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            MarkDone(item, state);
            return;
        }

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
        if (badges.Count == 0)
        {
            Activity.Log(_logger, LogLevel.Debug, null, "{Item} has no badges to show, original poster put back", item.Name);
        }
        else
        {
            Activity.Log(_logger, LogLevel.Debug, null, "Badged {Item}: {Badges}", item.Name, string.Join(", ", badges.Select(b => b.Text)));
        }
    }

    private static readonly JsonSerializerOptions _hashOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Spots only count when used, and only for the badges this poster has, so earlier hashes stay valid
    // and changing the spot of a badge redraws only the posters that show it.
    private static string InputHash(PosterState state, List<Badge> badges, PluginConfiguration config)
        => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                RenderVersion,
                state.OriginalHash,
                badges,
                config.Position,
                config.Style,
                config.Size,
                Spots = config.SpotPerBadge
                    ? config.Spots.Where(s => badges.Any(b => b.Kind == s.Kind)).OrderBy(s => s.Kind).Select(s => $"{s.Kind}:{s.Position}").ToArray()
                    : null
            },
            _hashOptions)));

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

    private List<Badge> GetBadges(BaseItem item, PluginConfiguration config, ConcurrentDictionary<Guid, List<Badge>>? episodeCache = null)
        => GetBadges(item, config, episodeCache, out _);

    // mediaInfoMissing: the item has media, but none of it has been scanned (no resolution anywhere).
    private List<Badge> GetBadges(BaseItem item, PluginConfiguration config, ConcurrentDictionary<Guid, List<Badge>>? episodeCache, out bool mediaInfoMissing)
    {
        List<Badge> technical;
        IReadOnlyList<BaseItem> episodes = [];
        var hasMedia = true;
        if (item is Series or Season)
        {
            episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [item.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false
            });
            technical = BadgeDetector.MostCommon(episodes
                .Select(e => (IReadOnlyList<Badge>)(episodeCache is null ? Technical(e, config) : episodeCache.GetOrAdd(e.Id, _ => Technical(e, config))))
                .ToList());
            hasMedia = episodes.Count > 0;
        }
        else if (item is BoxSet boxSet)
        {
            // Like a series: the most common quality of the movies in it.
            var movies = boxSet.GetLinkedChildren().OfType<Movie>().ToList();
            technical = BadgeDetector.MostCommon(movies.Select(m => (IReadOnlyList<Badge>)Technical(m, config)).ToList());
            hasMedia = movies.Count > 0;
        }
        else
        {
            technical = item is Episode && episodeCache is not null ? episodeCache.GetOrAdd(item.Id, _ => Technical(item, config)) : Technical(item, config);
        }

        mediaInfoMissing = hasMedia && !technical.Any(b => b.Kind == BadgeKind.Resolution);

        // Status and edition go first: they say the most at a glance.
        var lead = new List<Badge>();
        if (item is Series series
            && BadgeDetector.Status(series.Status, LastAired(episodes), config.ShowStatus ? UpcomingSeason(series) : null, DateTime.UtcNow, config.NewEpisodeDays) is { } status)
        {
            lead.Add(status);
        }

        if (item is Movie && BadgeDetector.Edition(item.Path, item.Name) is { } edition)
        {
            lead.Add(edition);
        }

        // Seasons rarely have their own rating: use the average of their episodes when chosen, otherwise the series rating.
        var (community, critic) = (item.CommunityRating, item.CriticRating);
        // A rating of 0 means none, as with episodes.
        if (item is Season season && !(community > 0) && !(critic > 0))
        {
            (community, critic) = (null, null);
            if (config.SeasonRatingFromEpisodes)
            {
                (community, critic) = (Average(episodes.Select(e => e.CommunityRating)), Average(episodes.Select(e => e.CriticRating)));
            }

            if (community is null && critic is null)
            {
                (community, critic) = (season.Series?.CommunityRating, season.Series?.CriticRating);
            }
        }

        return lead
            .Concat(technical)
            .Concat(BadgeDetector.Ratings(community, critic))
            .Where(b => b.Kind switch
            {
                BadgeKind.Resolution => config.ShowResolution,
                BadgeKind.DynamicRange => config.ShowDynamicRange,
                BadgeKind.AudioFormat => config.ShowAudioFormat,
                BadgeKind.AudioChannels => config.ShowAudioChannels,
                BadgeKind.VideoCodec => config.ShowVideoCodec,
                BadgeKind.Remux => config.ShowRemux,
                BadgeKind.CommunityRating => config.ShowCommunityRating,
                BadgeKind.Edition => config.ShowEdition,
                BadgeKind.Status => config.ShowStatus,
                BadgeKind.Language => config.ShowLanguage,
                _ => config.ShowCriticRating
            } && (!config.PremiumOnly || BadgeDetector.IsPremium(b)))
            .ToList();
    }

    // A season whose first episode airs in the coming days. Unaired episodes only exist when a metadata
    // plugin adds them (TMDb's "unaired episodes" option, for example).
    private int? UpcomingSeason(Series series)
    {
        var now = DateTime.UtcNow;
        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                MinPremiereDate = now,
                MaxPremiereDate = now.AddDays(UpcomingDays)
            })
            .OfType<Episode>()
            .Where(e => e.IndexNumber == 1 && e.ParentIndexNumber > 0)
            .Select(e => e.ParentIndexNumber)
            .Min();
    }

    // The newest air date that has passed, so announced future episodes do not count.
    private static DateTime? LastAired(IEnumerable<BaseItem> episodes)
        => episodes.Select(e => e.PremiereDate).Where(d => d <= DateTime.UtcNow).Max();

    private static float? Average(IEnumerable<float?> values)
    {
        // Unrated episodes often carry 0 instead of nothing: leave both out.
        var rated = values.OfType<float>().Where(v => v > 0).ToList();
        return rated.Count == 0 ? null : rated.Average();
    }

    // Quality of the best version, plus the language badge from all versions.
    private static List<Badge> Technical(BaseItem item, PluginConfiguration config)
    {
        var sources = item.GetMediaSources(false);
        var badges = BadgeDetector.BestVersion(sources.Select(s => ((IReadOnlyList<MediaStream>)s.MediaStreams, (string?)s.Path)));
        if (config.ShowLanguage && BadgeDetector.Language(sources.SelectMany(s => s.MediaStreams), config.LanguageCodes) is { } language)
        {
            badges.Add(language);
        }

        return badges;
    }

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

    private PosterState? LoadState(Guid id, BaseItem? item)
    {
        var file = StateFile(id);
        if (!File.Exists(file))
        {
            return null;
        }

        if (TryRead(file) is { } state)
        {
            return state;
        }

        // Damaged, from a crash in an older version that wrote state in place. Without a backup there is nothing to go on.
        var backup = Directory.Exists(Path.Combine(DataDir, "originals"))
            ? Directory.GetFiles(Path.Combine(DataDir, "originals"), id.ToString("N") + ".*").FirstOrDefault()
            : null;
        if (backup is null)
        {
            Activity.Log(_logger, LogLevel.Warning, null, "State of {Item} was damaged and had no backup, starting over", item?.Name ?? id.ToString());
            File.Delete(file);
            return null;
        }

        // The backup is the original, so whatever is on the poster now is taken to be ours: better than badging a badged poster.
        state = new PosterState { OriginalHash = Hash(File.ReadAllBytes(backup)), OriginalExtension = Path.GetExtension(backup) };
        if (item is not null && ReadPrimary(item) is { } current)
        {
            state.Ours.Add(Hash(current.Bytes));
        }

        Activity.Log(_logger, LogLevel.Warning, null, "State of {Item} was damaged, rebuilt it from the backup", item?.Name ?? id.ToString());
        SaveState(id, state);
        return state;
    }

    private static PosterState? TryRead(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<PosterState>(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Written next to the real file and moved over it, so a crash never leaves half a file.
    private static void SaveState(Guid id, PosterState state)
    {
        Directory.CreateDirectory(Path.Combine(DataDir, "state"));
        var temp = StateFile(id) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, StateFile(id), true);
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
