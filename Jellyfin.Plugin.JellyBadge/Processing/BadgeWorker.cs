using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Jellyfin.Plugin.JellyBadge.Configuration;
using MediaBrowser.Controller.Library;
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
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly PosterProcessor _processor;
    private readonly ILogger<BadgeWorker> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BadgeWorker"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="processor">Poster processor.</param>
    /// <param name="logger">Logger.</param>
    public BadgeWorker(ILibraryManager libraryManager, ITaskManager taskManager, PosterProcessor processor, ILogger<BadgeWorker> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _processor = processor;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _taskManager.TaskCompleted += OnTaskCompleted;
        Plugin.Instance!.ConfigurationChanged += OnConfigurationChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _taskManager.TaskCompleted -= OnTaskCompleted;
        Plugin.Instance!.ConfigurationChanged -= OnConfigurationChanged;
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    // Switching JellyBadge off in the settings puts the originals back.
    private void OnConfigurationChanged(object? sender, BasePluginConfiguration config)
    {
        if (config is PluginConfiguration { Enabled: false })
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
        if (Plugin.Instance?.Configuration.Enabled == true && e.Task.ScheduledTask is not ApplyBadgesTask)
        {
            Activity.Info(_logger, "{Task} finished, checking all posters", e.Task.Name);
            _taskManager.QueueScheduledTask<ApplyBadgesTask>();
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (Plugin.Instance?.Configuration.Enabled != true)
        {
            return;
        }

        // Our own poster save fires ItemUpdated too: drop it here, the hash check catches anything else.
        if (e.Item is Movie or Series or Episode or BoxSet && !_processor.IsOwnWrite(e.Item))
        {
            Enqueue(e.Item.Id);
        }

        // A new or updated episode can change what is most common for its series.
        if (e.Item is Episode episode)
        {
            Enqueue(episode.SeriesId);
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
