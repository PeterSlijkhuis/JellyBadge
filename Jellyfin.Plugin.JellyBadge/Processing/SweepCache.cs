using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyBadge.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyBadge.Processing;

/// <summary>
/// What one sweep shares between items: each episode's quality badges, and each series' and season's episodes.
/// </summary>
public sealed class SweepCache
{
    private readonly ILookup<Guid, BaseItem> _children;

    /// <summary>
    /// Initializes a new instance of the <see cref="SweepCache"/> class.
    /// </summary>
    /// <param name="items">Every item the sweep checks.</param>
    public SweepCache(IEnumerable<BaseItem> items)
    {
        _children = items.OfType<Episode>()
            .SelectMany(e => new[] { (Parent: e.SeriesId, Item: (BaseItem)e), (Parent: e.SeasonId, Item: (BaseItem)e) })
            .Where(p => !p.Parent.Equals(Guid.Empty))
            .ToLookup(p => p.Parent, p => p.Item);
    }

    /// <summary>
    /// Gets the quality badges per episode, so each episode is read once.
    /// </summary>
    public ConcurrentDictionary<Guid, List<Badge>> Episodes { get; } = new();

    /// <summary>
    /// The episodes of a series or season.
    /// </summary>
    /// <param name="parentId">Series or season id.</param>
    /// <returns>Its episodes.</returns>
    public IEnumerable<BaseItem> Children(Guid parentId) => _children[parentId];
}
