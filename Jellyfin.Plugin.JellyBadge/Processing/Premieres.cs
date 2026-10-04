using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBadge.Processing;

/// <summary>
/// Season premieres looked up on TVmaze, so SEASON n SOON does not depend on a metadata plugin adding upcoming
/// episodes to the library. Only the show's TVDb or IMDb id is sent. Answers are kept for a day.
/// </summary>
public sealed class Premieres
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Premieres> _logger;
    private readonly Lock _lock = new();
    private Dictionary<Guid, Premiere>? _known;

    /// <summary>
    /// Initializes a new instance of the <see cref="Premieres"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public Premieres(IHttpClientFactory httpClientFactory, ILogger<Premieres> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "premieres.json");

    /// <summary>
    /// The season whose first episode airs between now and the end of the window, as last looked up.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="now">The current time.</param>
    /// <param name="window">How far ahead counts as soon.</param>
    /// <returns>The season number, or null.</returns>
    public int? Season(Guid seriesId, DateTime now, TimeSpan window)
    {
        lock (_lock)
        {
            return Known().TryGetValue(seriesId, out var p) && p.Season > 0 && p.Airs >= now && p.Airs <= now + window ? p.Season : null;
        }
    }

    /// <summary>
    /// Whether the series was not looked up in the last day.
    /// </summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="now">The current time.</param>
    /// <returns>True if it should be looked up.</returns>
    public bool IsStale(Guid seriesId, DateTime now)
    {
        lock (_lock)
        {
            return !Known().TryGetValue(seriesId, out var p) || now - p.Checked > MaxAge;
        }
    }

    /// <summary>
    /// Looks up the next episode of a series. Network trouble leaves the last answer in place.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the premiere it knows of changed.</returns>
    public async Task<bool> RefreshAsync(Series series, CancellationToken cancellationToken)
    {
        Premiere? last;
        lock (_lock)
        {
            Known().TryGetValue(series.Id, out last);
        }

        var next = new Premiere { TvMazeId = last?.TvMazeId, Checked = DateTime.UtcNow };
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            if (next.TvMazeId is null)
            {
                var lookup = series.GetProviderId(MetadataProvider.Tvdb) is { Length: > 0 } tvdb ? "thetvdb=" + Uri.EscapeDataString(tvdb)
                    : series.GetProviderId(MetadataProvider.Imdb) is { Length: > 0 } imdb ? "imdb=" + Uri.EscapeDataString(imdb)
                    : null;
                if (lookup is not null)
                {
                    using var found = await client.GetAsync("https://api.tvmaze.com/lookup/shows?" + lookup, cancellationToken).ConfigureAwait(false);
                    if (found.StatusCode != HttpStatusCode.NotFound)
                    {
                        found.EnsureSuccessStatusCode();
                        using var show = JsonDocument.Parse(await found.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                        next.TvMazeId = show.RootElement.GetProperty("id").GetInt32();
                    }
                }
            }

            if (next.TvMazeId is { } id)
            {
                var json = await client.GetStringAsync(string.Create(CultureInfo.InvariantCulture, $"https://api.tvmaze.com/shows/{id}?embed=nextepisode"), cancellationToken).ConfigureAwait(false);
                (next.Season, next.Airs) = NextPremiere(json);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not look up the next season of {Series} on TVmaze", series.Name);
            return false;
        }

        lock (_lock)
        {
            Known()[series.Id] = next;
        }

        return next.Season != last?.Season || next.Airs != last?.Airs;
    }

    /// <summary>
    /// Saves what was looked up, so a restart does not ask again.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            if (_known is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_known));
            File.Move(temp, FilePath, true);
        }
    }

    // The next episode only counts when it opens a season.
    private static (int? Season, DateTime? Airs) NextPremiere(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("_embedded", out var embedded) || !embedded.TryGetProperty("nextepisode", out var episode)
            || episode.ValueKind != JsonValueKind.Object
            || !episode.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number || number.GetInt32() != 1
            || !episode.TryGetProperty("season", out var season) || season.ValueKind != JsonValueKind.Number
            || !episode.TryGetProperty("airstamp", out var airstamp) || airstamp.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(airstamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var airs))
        {
            return (null, null);
        }

        return (season.GetInt32(), airs.UtcDateTime);
    }

    private Dictionary<Guid, Premiere> Known()
    {
        if (_known is null)
        {
            try
            {
                _known = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<Guid, Premiere>>(File.ReadAllText(FilePath)) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Could not read the remembered season premieres, looking them up again");
            }

            _known ??= [];
        }

        return _known;
    }

    /// <summary>
    /// What TVmaze said about a series.
    /// </summary>
    public sealed class Premiere
    {
        /// <summary>Gets or sets the show's TVmaze id, so the lookup by TVDb or IMDb id happens once.</summary>
        public int? TvMazeId { get; set; }

        /// <summary>Gets or sets the season that premieres next.</summary>
        public int? Season { get; set; }

        /// <summary>Gets or sets when that season premieres, in UTC.</summary>
        public DateTime? Airs { get; set; }

        /// <summary>Gets or sets when this was looked up, in UTC.</summary>
        public DateTime Checked { get; set; }
    }
}
