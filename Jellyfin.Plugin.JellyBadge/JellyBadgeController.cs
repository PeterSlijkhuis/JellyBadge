using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyBadge.Configuration;
using Jellyfin.Plugin.JellyBadge.Processing;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyBadge;

/// <summary>
/// Endpoints for the settings page. Admin only.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("JellyBadge")]
public class JellyBadgeController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly PosterProcessor _processor;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyBadgeController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="processor">Poster processor.</param>
    public JellyBadgeController(ILibraryManager libraryManager, PosterProcessor processor)
    {
        _libraryManager = libraryManager;
        _processor = processor;
    }

    /// <summary>
    /// Renders an item's poster with the given (unsaved) settings. Nothing is written.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="config">Settings to preview.</param>
    /// <returns>The badged image.</returns>
    [HttpPost("Preview/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Preview([FromRoute] Guid itemId, [FromBody] PluginConfiguration config)
    {
        var item = _libraryManager.GetItemById(itemId);
        var result = item is null ? null : _processor.Preview(item, config);
        return result is null ? NotFound() : File(result.Value.Image, result.Value.MimeType);
    }

    /// <summary>
    /// The badge font, so the settings page shows the same lettering as the posters.
    /// </summary>
    /// <returns>The font file.</returns>
    [HttpGet("Font")]
    [AllowAnonymous]
    public ActionResult Font()
    {
        var font = typeof(JellyBadgeController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyBadge.Rendering.Fonts.BarlowCondensed-Bold.ttf")!;
        return File(font, "font/ttf");
    }

    /// <summary>
    /// Turns badging off and restores every original poster.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many posters were restored.</returns>
    [HttpPost("RestoreAll")]
    public async Task<ActionResult<int>> RestoreAll(CancellationToken cancellationToken)
        => await _processor.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
}
