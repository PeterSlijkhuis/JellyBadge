using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.JellyBadge.Configuration;
using Jellyfin.Plugin.JellyBadge.Processing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyBadge;

/// <summary>
/// The JellyBadge plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "JellyBadge";

    /// <inheritdoc />
    public override string Description => "Quality and rating badges on your posters, for every client.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("9d421997-eadd-4434-a275-a4e85fb3771c");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        // Put the originals back before the plugin goes, so no badged poster is left behind.
        PosterProcessor.Current?.RestoreAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "new_releases"
            },
            new PluginPageInfo
            {
                Name = "JellyBadgeActivity",
                DisplayName = "JellyBadge activity",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.activityPage.html"
            }
        ];
    }
}

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PosterProcessor>();
        serviceCollection.AddHostedService<BadgeWorker>();
    }
}
