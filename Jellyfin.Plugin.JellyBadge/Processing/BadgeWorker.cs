using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Jellyfin.Plugin.JellyBadge.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBadge.Processing;

/// <summary>
/// Badges single items in the background when the library adds or updates them.
/// Event handlers only queue an id, so library scans never wait on us.
/// </summary>
public sealed class BadgeWorker : BackgroundService
{
    // Jellyfin's own tasks that never touch posters or the inputs of a badge. Any other task, including
    // every plugin task, is followed by a check.
    private static readonly HashSet<string> _harmlessTasks = new(StringComparer.Ordinal)
    {
        "CleanActivityLog", "DeleteTranscodeFiles", "PluginUpdates", "OptimizeDatabaseTask", "CleanLogFiles", "DeleteCacheFiles",
        "MoveTrickplayImages", "AudioNormalization", "RefreshChapterImages", "TaskExtractMediaSegments", "RefreshTrickplayImages",
        "DownloadLyrics", "KeyframeExtraction", "RefreshInternetChannels", "RefreshGuide", "RefreshPeople"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IPluginManager _pluginManager;
    private readonly PosterProcessor _processor;
    private readonly ILogger<BadgeWorker> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private bool _wasEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="BadgeWorker"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="pluginManager">Plugin manager.</param>
    /// <param name="processor">Poster processor.</param>
    /// <param name="logger">Logger.</param>
    public BadgeWorker(ILibraryManager libraryManager, ITaskManager taskManager, IPluginManager pluginManager, PosterProcessor processor, ILogger<BadgeWorker> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _pluginManager = pluginManager;
        _processor = processor;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _taskManager.TaskCompleted += OnTaskCompleted;
        Plugin.Instance!.ConfigurationChanged += OnConfigurationChanged;
        _wasEnabled = Plugin.Instance.Configuration.Enabled;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Disabling in the plugin list asks for a restart: if that comes before the next check, restore now.
        await RestoreIfDisabledAsync(cancellationToken).ConfigureAwait(false);

        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _taskManager.TaskCompleted -= OnTaskCompleted;
        Plugin.Instance!.ConfigurationChanged -= OnConfigurationChanged;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = WatchPluginListAsync(stoppingToken);

        // ponytail: one consumer, events trickle in one at a time; the scheduled task does the bulk work in parallel.
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            _pending.TryRemove(id, out _);
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            try
            {
                await _processor.ProcessAsync(item, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Activity.Log(_logger, LogLevel.Error, ex, "Failed to badge {Item}", item.Name);
            }
        }
    }

    // Jellyfin tells a plugin nothing when it is disabled in the plugin list, and after the restart the plugin
    // no longer runs. So look every few seconds and put the originals back while we still can.
    private async Task WatchPluginListAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RestoreIfDisabledAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RestoreIfDisabledAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Enabled != true
            || _pluginManager.GetPlugin(Plugin.Instance.Id)?.Manifest.Status != PluginStatus.Disabled)
        {
            return;
        }

        Activity.Info(_logger, "JellyBadge was disabled in the plugin list, restoring original posters");
        try
        {
            _wasEnabled = false;
            await _processor.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Log(_logger, LogLevel.Error, ex, "Failed to restore original posters");
        }
    }

    // Saving the settings redraws what changed; switching JellyBadge off puts the originals back, once.
    private void OnConfigurationChanged(object? sender, BasePluginConfiguration config)
    {
        var wasEnabled = _wasEnabled;
        _wasEnabled = config is PluginConfiguration { Enabled: true };
        if (_wasEnabled)
        {
            Activity.Info(_logger, "Settings saved, checking all posters");
            _taskManager.QueueScheduledTask<ApplyBadgesTask>();
        }
        else if (wasEnabled)
        {
            Activity.Info(_logger, "JellyBadge was switched off, restoring original posters");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _processor.RestoreAllAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Activity.Log(_logger, LogLevel.Error, ex, "Failed to restore original posters");
                }
            });
        }
    }

    // Scans and metadata plugins can swap posters without an update event we can use. A sweep after
    // each of them puts the badges back; items that are still fine are skipped without reading the image.
    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (Plugin.Instance?.Configuration.Enabled == true && e.Task.ScheduledTask is not ApplyBadgesTask && !_harmlessTasks.Contains(e.Task.ScheduledTask.Key))
        {
            Activity.Info(_logger, "{Task} finished, checking all posters", e.Task.Name);
            _taskManager.QueueScheduledTask<ApplyBadgesTask>();
        }
    }

    // Its backup and state are of no use once the item is gone (moved files come back with a new id).
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            _processor.Forget(e.Item.Id);
        }
        catch (IOException ex)
        {
            Activity.Log(_logger, LogLevel.Warning, ex, "Could not clean up after {Item}", e.Item.Name);
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (Plugin.Instance?.Configuration.Enabled != true)
        {
            return;
        }

        // Our own poster save fires ItemUpdated too: drop it here, the hash check catches anything else.
        if (e.Item is Movie or Series or Season or Episode or BoxSet && !_processor.IsOwnWrite(e.Item))
        {
            Enqueue(e.Item.Id);
        }

        // A new or updated episode can change what is most common for its series and season.
        if (e.Item is Episode episode)
        {
            Enqueue(episode.SeriesId);
            Enqueue(episode.SeasonId);
        }
    }

    private void Enqueue(Guid id)
    {
        if (id != Guid.Empty && _pending.TryAdd(id, 0))
        {
            _queue.Writer.TryWrite(id);
        }
    }
}
