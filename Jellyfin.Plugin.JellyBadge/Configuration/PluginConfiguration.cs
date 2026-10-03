using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyBadge.Configuration;

/// <summary>
/// Where the badges go on the poster.
/// </summary>
public enum BadgePosition
{
    /// <summary>Stacked in the top left corner.</summary>
    TopLeft,

    /// <summary>Stacked in the top right corner.</summary>
    TopRight,

    /// <summary>Stacked in the bottom left corner.</summary>
    BottomLeft,

    /// <summary>Stacked in the bottom right corner.</summary>
    BottomRight,

    /// <summary>One row across the top.</summary>
    TopStrip,

    /// <summary>One row across the bottom.</summary>
    BottomStrip
}

/// <summary>
/// Badge look.
/// </summary>
public enum BadgeStyle
{
    /// <summary>Fully rounded.</summary>
    Pill,

    /// <summary>Slightly rounded corners.</summary>
    Square,

    /// <summary>Text with shadow, no background.</summary>
    Minimal
}

/// <summary>
/// Badge size relative to the poster width.
/// </summary>
public enum BadgeSize
{
    /// <summary>Small.</summary>
    Small,

    /// <summary>Medium.</summary>
    Medium,

    /// <summary>Large.</summary>
    Large
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether posters get badged. Off until the user turns it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether to show the resolution badge.</summary>
    public bool ShowResolution { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to show the HDR badge.</summary>
    public bool ShowDynamicRange { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to show the audio format badge.</summary>
    public bool ShowAudioFormat { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to show the audio channels badge.</summary>
    public bool ShowAudioChannels { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to show the video codec badge. Off by default, so updating changes no poster.</summary>
    public bool ShowVideoCodec { get; set; }

    /// <summary>Gets or sets a value indicating whether to show the remux badge. Off by default.</summary>
    public bool ShowRemux { get; set; }

    /// <summary>Gets or sets a value indicating whether to show the community rating badge.</summary>
    public bool ShowCommunityRating { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether to show the critic rating badge.</summary>
    public bool ShowCriticRating { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether episode thumbnails get badged too.</summary>
    public bool BadgeEpisodes { get; set; }

    /// <summary>Gets or sets a value indicating whether season posters get badged too.</summary>
    public bool BadgeSeasons { get; set; }

    /// <summary>Gets or sets a value indicating whether a season without its own rating shows the average of its episode ratings instead of the series rating.</summary>
    public bool SeasonRatingFromEpisodes { get; set; }

    /// <summary>Gets or sets a value indicating whether collection posters get badged too.</summary>
    public bool BadgeCollections { get; set; }

    /// <summary>Gets or sets the badge position.</summary>
    public BadgePosition Position { get; set; } = BadgePosition.TopLeft;

    /// <summary>Gets or sets the badge style.</summary>
    public BadgeStyle Style { get; set; } = BadgeStyle.Pill;

    /// <summary>Gets or sets the badge size.</summary>
    public BadgeSize Size { get; set; } = BadgeSize.Medium;

    /// <summary>Gets or sets the included library ids. Empty means all libraries.</summary>
    public string[] Libraries { get; set; } = [];

    /// <summary>Gets or sets how many posters are processed at the same time.</summary>
    public int MaxConcurrency { get; set; } = 2;
}
