using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
    public string Description => "Stamps badges onto movie and series posters.";

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
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false
        });

        var done = 0;
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, config.MaxConcurrency), CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                try
                {
                    await _processor.ProcessAsync(item, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to badge {Item}", item.Name);
                }

                progress.Report(100.0 * Interlocked.Increment(ref done) / items.Count);
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks };
    }
}
