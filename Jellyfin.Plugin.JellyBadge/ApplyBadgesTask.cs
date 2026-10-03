using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyBadge.Detection;
using Jellyfin.Plugin.JellyBadge.Processing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBadge;

/// <summary>
/// Badges the whole library. Items whose inputs did not change are skipped cheaply.
/// </summary>
public class ApplyBadgesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly PosterProcessor _processor;
    private readonly ILogger<ApplyBadgesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplyBadgesTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="processor">Poster processor.</param>
    /// <param name="logger">Logger.</param>
    public ApplyBadgesTask(ILibraryManager libraryManager, PosterProcessor processor, ILogger<ApplyBadgesTask> logger)
    {
        _libraryManager = libraryManager;
        _processor = processor;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Apply poster badges";

    /// <inheritdoc />
    public string Key => "JellyBadgeApply";

    /// <inheritdoc />
    public string Description => "Stamps badges onto movie and series posters, and seasons, episode thumbnails and collections when switched on.";

    /// <inheritdoc />
    public string Category => "JellyBadge";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.Enabled)
        {
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            // Seasons, episodes and collections always, so switching them off restores the ones badged before.
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.BoxSet],
            Recursive = true,
            IsVirtualItem = false
        });

        var done = 0;
        var failed = 0;
        var episodeCache = new ConcurrentDictionary<Guid, List<Badge>>();
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, config.MaxConcurrency), CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                try
                {
                    await _processor.ProcessAsync(item, ct, episodeCache).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    Activity.Log(_logger, LogLevel.Error, ex, "Failed to badge {Item}", item.Name);
                }

                progress.Report(100.0 * Interlocked.Increment(ref done) / items.Count);
            }).ConfigureAwait(false);

        var forgotten = _processor.ForgetRemoved();
        Activity.Info(_logger, "Checked {Count} items, {Failed} failed, {Removed} removed items cleaned up", items.Count, failed, forgotten);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks };
    }
}
