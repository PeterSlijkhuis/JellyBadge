using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBadge.Processing;

/// <summary>
/// What JellyBadge did and why, for the Activity section of the settings page.
/// Every line also goes to the Jellyfin log. Kept in a small file so it survives restarts.
/// </summary>
public static partial class Activity
{
    private const int MaxLines = 1000;
    private static readonly object _lock = new();

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "activity.log");

    /// <summary>
    /// Writes a message to the Jellyfin log and the activity file.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="level">Level in the Jellyfin log.</param>
    /// <param name="exception">Optional exception.</param>
    /// <param name="template">Message template with {Placeholders}.</param>
    /// <param name="args">Values for the placeholders, in order.</param>
    public static void Log(ILogger logger, LogLevel level, Exception? exception, string template, params object?[] args)
    {
#pragma warning disable CA2254 // The template is passed through from the callers' constants.
        logger.Log(level, exception, template, args);
#pragma warning restore CA2254

        var i = 0;
        var text = Placeholder().Replace(template, _ => i < args.Length ? args[i++]?.ToString() ?? string.Empty : string.Empty);
        if (exception is not null)
        {
            text += ": " + exception.Message;
        }

        var kind = level >= LogLevel.Error ? "Error" : level == LogLevel.Warning ? "Warning" : "Info";
        Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{kind}\t{text.ReplaceLineEndings(" ")}");
    }

    /// <summary>
    /// Writes an informational message.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="template">Message template with {Placeholders}.</param>
    /// <param name="args">Values for the placeholders, in order.</param>
    public static void Info(ILogger logger, string template, params object?[] args) => Log(logger, LogLevel.Information, null, template, args);

    /// <summary>
    /// Returns the most recent lines, newest first.
    /// </summary>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> Read()
    {
        lock (_lock)
        {
            return File.Exists(FilePath) ? File.ReadAllLines(FilePath).Reverse().ToArray() : [];
        }
    }

    /// <summary>
    /// Empties the activity file.
    /// </summary>
    public static void Clear()
    {
        lock (_lock)
        {
            File.Delete(FilePath);
        }
    }

    private static void Append(string line)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllLines(FilePath, [line]);

                // Trim back to the newest lines once the file passes about 300 KB.
                if (new FileInfo(FilePath).Length > 300_000)
                {
                    File.WriteAllLines(FilePath, File.ReadAllLines(FilePath)[^MaxLines..]);
                }
            }
        }
        catch (IOException)
        {
            // The activity view is a convenience; never let it break badging.
        }
    }

    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex Placeholder();
}
