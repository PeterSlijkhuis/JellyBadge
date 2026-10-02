using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
    private readonly PosterProcessor _processor;
    private readonly ILogger<BadgeWorker> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BadgeWorker"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="processor">Poster processor.</param>
    /// <param name="logger">Logger.</param>
    public BadgeWorker(ILibraryManager libraryManager, PosterProcessor processor, ILogger<BadgeWorker> logger)
    {
        _libraryManager = libraryManager;
        _processor = processor;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
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
                _logger.LogError(ex, "Failed to badge {Item}", item.Name);
            }
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (Plugin.Instance?.Configuration.Enabled != true)
        {
            return;
        }

        // A new or updated episode can change what is most common for its series.
        // Our own poster save fires ItemUpdated too: drop it here, the hash check catches anything else.
        var id = e.Item switch
        {
            Movie or Series when !_processor.IsOwnWrite(e.Item) => e.Item.Id,
            Episode episode => episode.SeriesId,
            _ => Guid.Empty
        };

        if (id != Guid.Empty && _pending.TryAdd(id, 0))
        {
            _queue.Writer.TryWrite(id);
        }
    }
}
