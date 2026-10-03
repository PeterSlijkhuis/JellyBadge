<p align="center">
  <img src="docs/banner.png" alt="JellyBadge" width="100%" />
</p>

<p align="center">
  <a href="https://github.com/PeterSlijkhuis/JellyBadge/releases/latest"><img src="https://img.shields.io/github/v/release/PeterSlijkhuis/JellyBadge?style=flat-square&label=release&color=aa5cc3" alt="Latest release" /></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.1%2B-00a4dc?style=flat-square" alt="Jellyfin 12.1 or newer" />
  <a href="https://github.com/PeterSlijkhuis/JellyBadge/actions/workflows/build.yaml"><img src="https://img.shields.io/github/actions/workflow/status/PeterSlijkhuis/JellyBadge/build.yaml?style=flat-square&label=build" alt="Build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-555?style=flat-square" alt="GPL-3.0" /></a>
</p>

<p align="center"><b>Quality and rating badges on your posters, stamped on the server, visible in every Jellyfin app.</b></p>

JellyBadge adds clean badges like **4K**, **Dolby Vision**, **Atmos** and **★ 8.4** to your movie and series posters. The badges are drawn into the poster image itself, so they show up everywhere: the web app, phones, tablets and native TV apps such as Wholphin on Android TV. No themes, no CSS, no JavaScript, nothing to install on your devices.

**Know Kometa overlays from Plex?** JellyBadge brings the same idea to Jellyfin as a regular plugin: resolution, HDR, codec, audio and rating overlays on your posters, set up from the dashboard with a live preview. No scripts, no config files, no cron jobs.

<table>
  <tr>
    <th>Before</th>
    <th>After</th>
  </tr>
  <tr>
    <td><img src="docs/before.jpg" alt="Poster without badges" width="300" /></td>
    <td><img src="docs/after.jpg" alt="The same poster with badges" width="300" /></td>
  </tr>
</table>

## Contents

- [What you get](#what-you-get)
- [Install](#install)
- [Getting started](#getting-started)
- [What it can and cannot do](#what-it-can-and-cannot-do)
- [Removing badges](#removing-badges)
- [FAQ](#faq)
- [For developers](#for-developers)
- [Credits](#credits)
- [License](#license)

## What you get

- **Works in every client.** Badges are part of the poster, so every Jellyfin app shows them, including ones that never load custom styles.
- **Smart detection.** Resolution, HDR format, audio format and channels come from your actual files. For movies with several versions, the best one wins. For series, the most common quality across the episodes is used.
- **Ratings at a glance.** Community score and critic score from your existing metadata.
- **Your originals are safe.** Every original poster is backed up before the first change, and one button puts them all back.
- **Hands off.** New and updated items are badged automatically in the background, and a daily task keeps the whole library in sync. Library scans never wait on it.
- **Keeps up with changes.** When a metadata refresh or an upload replaces a poster, JellyBadge treats the new one as the original and badges it again.
- **Episodes too, if you like.** Switch on episode thumbnails and every episode gets its own quality and rating badges.
- **Live preview.** See exactly how a poster will look before anything is written.

### Badges

| Group | Badges | Comes from |
|---|---|---|
| Resolution | `4K` `1080p` `720p` `SD` | the video stream size |
| Dynamic range | `DOLBY VISION` `HDR10+` `HDR10` `HLG` | the video range type |
| Video codec | `AV1` `HEVC` `VP9` `H.264` `VC-1` `MPEG-2` | the video codec, off until you switch it on |
| Remux | `REMUX` | the file or folder name, off until you switch it on |
| Audio format | `ATMOS` `DTS:X` `TRUEHD` `DTS-HD MA` `FLAC` `PCM` `DTS` `DD+` `DD` `AAC` | the audio codec and profile |
| Audio channels | `7.1` `5.1` `2.1` `2.0` `MONO` and others | the audio channel layout |
| Edition | `DIRECTOR'S CUT` `EXTENDED` `IMAX` and others | Radarr's `{edition-...}` tag or the file name, movies only, off until you switch it on |
| Show status | `NEW EPISODE` `SEASON 3 SOON` `RETURNING` `ENDED` | an episode that aired in the last 7 days, a season starting in the next 7 days (needs unaired episodes, for example from the TMDb plugin), else the series status, off until you switch it on |
| Language | `NL` or `NL SUBS` | audio or subtitles in the language you pick, off until you switch it on |
| Community rating | `★ 8.4` | the item's community rating |
| Critic rating | `✓ 93%` | the item's critic rating |

Each group can be switched on or off. A badge only appears when the item actually has it. Switch on **Only premium badges** to leave out everyday quality (`720p`, `SD`, stereo, mono, lossy audio like `DD+` and `AAC`, and older codecs like `H.264`), so badges only appear when something stands out.

### Styles and placement

Pick a corner or a strip along the top or bottom, one of three styles and three sizes. Badges fill their spot: with only a few they grow (up to 2 times the chosen size), and with many they move to two rows or two columns so they stay readable on a phone. They also scale with the poster, so they stay readable on a big TV and on a small phone.

<table>
  <tr>
    <td align="center"><img src="docs/after.jpg" width="180" alt="Top left, pill" /><br /><sub>Top left, pill</sub></td>
    <td align="center"><img src="docs/example-square.jpg" width="180" alt="Top right, square" /><br /><sub>Top right, square</sub></td>
    <td align="center"><img src="docs/example-top-strip.jpg" width="180" alt="Top strip, pill" /><br /><sub>Top strip, pill</sub></td>
    <td align="center"><img src="docs/example-minimal-strip.jpg" width="180" alt="Bottom strip, minimal" /><br /><sub>Bottom strip, minimal</sub></td>
    <td align="center"><img src="docs/example-large.jpg" width="180" alt="Bottom right, large" /><br /><sub>Bottom right, large</sub></td>
  </tr>
</table>

## Install

JellyBadge needs **Jellyfin 12.1 or newer**.

### Option 1: plugin repository (recommended)

1. Open **Dashboard > Plugins** and click **Manage Repositories**.
2. Click **+**, give it a name such as `JellyBadge` and paste this URL:
   ```
   https://github.com/PeterSlijkhuis/JellyBadge/releases/latest/download/manifest.json
   ```
3. Go back to **Plugins**, open **Available**, find **JellyBadge** and click **Install**.
4. Restart Jellyfin.

Updates show up in the same place and can install automatically.

### Option 2: manual install

1. Download `jellybadge_x.y.z.0.zip` from the [latest release](https://github.com/PeterSlijkhuis/JellyBadge/releases/latest).
2. Unzip it into a folder called `JellyBadge` inside your Jellyfin `plugins` folder. With Docker this is usually `/config/plugins/JellyBadge`.
3. Restart Jellyfin.

## Getting started

Open **Dashboard > Plugins > JellyBadge**.

<p align="center"><img src="docs/settings.png" alt="JellyBadge settings page" width="900" /></p>

1. **Pick your badges.** Click a tile to switch a badge group on or off.
2. **Choose the placement.** Click a spot on the little poster.
3. **Choose a style and size.** The live preview updates as you go. Search any movie or series to try it on, and hold **Hold to compare** to see the original.
4. **Switch it on.** Flip the switch at the top to **Active**, then click **Save and apply now**.

JellyBadge is off after installing, so nothing changes until you switch it on. Progress of the first run shows under **Dashboard > Scheduled Tasks > Apply poster badges**.

The **Activity** section on the same page lists what JellyBadge did recently and why: posters it badged, posters that were replaced, restores and errors.

## What it can and cannot do

**It can**

- Badge movie and series posters in the libraries you choose.
- Badge season posters with the most common quality of their episodes, when you switch that on under **Libraries**. The rating is the series rating, or the average of the episode ratings if you choose that. Home screen rows often show the season poster for a new episode.
- Use the best version of a movie that has several files.
- Show the most common quality of a series or season, based on its episodes.
- Badge episode thumbnails with the episode's own quality and rating, when you switch that on under **Libraries**. Switch it off again and the next run puts the original thumbnails back.
- Badge collection posters with the most common quality of the movies in them, when you switch that on under **Libraries**.
- Keep running by itself: new items, updated items and a daily check of everything.
- Put every original poster back with one click.

**It cannot**

- Badge season posters, backdrops or logos. Only the main poster of movies, series and collections, and optionally the episode thumbnail, is changed.
- Tell a remux from an encode by the file itself. The Remux badge only shows when the file or its folder has "Remux" in the name.
- Add custom badges, colors, logos of rating sites, or seasonal and decorative overlays.
- Detect what Jellyfin does not know. Badges are based on the media info Jellyfin reads from your files, so if Jellyfin does not report Atmos or DTS:X for a file, there is no badge for it.
- Say which site a rating came from. Jellyfin stores one community rating and one critic rating without a source, so the badges show a neutral star and check mark.
- Run on Jellyfin 10.x.

## Removing badges

Switch JellyBadge **Off** at the top of its settings page and save, or click **Restore originals**. Both:

1. switches JellyBadge off, so nothing gets badged again,
2. puts every original poster back,
3. deletes all backups and stored data of the plugin.

If you replaced a poster after it was badged, your newer poster is kept.

Uninstalling JellyBadge does the same before it goes, so no badged poster is left behind.

## FAQ

<details>
<summary><b>How is this different from Kometa or Plex Meta Manager overlays?</b></summary>

Kometa is a separate script you run on a schedule, built around Plex. JellyBadge is a Jellyfin plugin: you install it from the plugin catalog, set it up on its settings page with a live preview, and it badges new items by itself as they arrive. One button puts all original posters back.
</details>

<details>
<summary><b>My badges disappeared after the nightly tasks. What happened?</b></summary>

Some scheduled tasks and outside tools (metadata plugins, Sonarr, Radarr) can put the original poster back. JellyBadge checks all posters again after every other scheduled task finishes and badges them again, which takes seconds when nothing changed. The **Activity** section shows which task ran and what was badged afterwards.
</details>

<details>
<summary><b>Does JellyBadge change files in my media folders?</b></summary>

No. Badged posters are saved in Jellyfin's own metadata folder. Your `poster.jpg` files and other artwork next to your media are never written or deleted, so tools like Kodi, Plex, Sonarr and Radarr never see a badged poster.
</details>

<details>
<summary><b>I switched it on, but my app still shows posters without badges.</b></summary>

Most apps keep posters in a cache. Give it a moment, pull to refresh, or clear the app's cache. Also check that the **Apply poster badges** task has finished under **Dashboard > Scheduled Tasks**.
</details>

<details>
<summary><b>A badge is missing or wrong for one item.</b></summary>

JellyBadge reads the media info Jellyfin collected for the file. Open the item, choose **Refresh metadata**, and the poster is updated with what Jellyfin finds. If Jellyfin itself does not show the format under the item's media info, JellyBadge cannot show it either.
</details>

<details>
<summary><b>I changed a poster myself. Will JellyBadge overwrite it?</b></summary>

It treats your new poster as the original, backs it up and adds badges to it. If you do not want badges on it, use **Restore originals** or limit JellyBadge to certain libraries.
</details>

<details>
<summary><b>Will it slow down my server or library scans?</b></summary>

No. Library events only add the item to a queue. The work happens in the background, two posters at a time by default, and items whose poster, settings and media did not change are skipped without even opening the image. New movies, shows and episodes are badged as soon as Jellyfin has their artwork, without waiting for the daily task.
</details>

<details>
<summary><b>Where are the original posters kept?</b></summary>

In the plugin's data folder inside your Jellyfin data folder: `plugins/Jellyfin.Plugin.JellyBadge/originals`.
</details>

<details>
<summary><b>Do episodes have ratings?</b></summary>

Most do. Metadata providers such as TMDb and TheTVDB give each episode its own community score, so episode thumbnails get a star badge just like posters. Critic scores for single episodes are rare, so that badge usually stays away on episodes. Episodes that Jellyfin has no rating for simply get no rating badge.
</details>

<details>
<summary><b>Do I need to install anything on my TV or phone?</b></summary>

No. Everything happens on the server. Your apps just load the poster like they always do.
</details>

## For developers

```bash
dotnet test JellyBadge.slnx
dotnet publish Jellyfin.Plugin.JellyBadge -c Release -o out
```

- Built on the official [jellyfin-plugin-template](https://github.com/jellyfin/jellyfin-plugin-template), targeting .NET 10 and Jellyfin 12.1.
- Badge detection is plain logic in `Detection/BadgeDetector.cs`, covered by unit tests with stream fixtures in `Jellyfin.Plugin.JellyBadge.Tests/Fixtures`.
- **Releasing.** Open **Actions > Release > Run workflow**, fill in the version and release notes, and run it. It builds, tests and publishes a release with the plugin zip and `manifest.json`. Leave the version empty to count the last number up from the latest release (1.0.0, 1.0.1, 1.0.2). The notes default to "Bug fixes and improvements." and show up in Jellyfin's plugin catalog.

## Credits

- [Jellyfin](https://jellyfin.org) and its [plugin template](https://github.com/jellyfin/jellyfin-plugin-template).
- [SkiaSharp](https://github.com/mono/SkiaSharp) for drawing, using the copy that ships with Jellyfin.
- [Barlow Condensed](https://github.com/jpt/barlow) by Jeremy Tribby, used for the badge lettering under the SIL Open Font License.
- The posters in this README are made up for illustration.

## License

JellyBadge is licensed under the [GNU General Public License v3.0](LICENSE). The bundled Barlow Condensed font is licensed under the [SIL Open Font License 1.1](Jellyfin.Plugin.JellyBadge/Rendering/Fonts/OFL.txt).
