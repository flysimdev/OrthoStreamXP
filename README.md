# OrthoStreamXP

**Fly over real-world aerial imagery in X-Plane 12 — without downloading entire regions in advance.**

OrthoStreamXP streams photo scenery as you fly, loading the areas around your aircraft first and keeping downloaded imagery ready for your next flight.

No scenery preparation before takeoff. No complicated configuration files. Just choose your quality preset, start OrthoStreamXP, and fly.

**Free. Open source. No subscriptions or accounts.**

[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://www.paypal.com/donate/?hosted_button_id=KHUJKNM8PXR7E)

OrthoStreamXP will always be free. If it has given you a flight worth remembering, like a sunrise over the Alps or a final approach into your home airport, please think about supporting its development. Every donation buys more time for sharper textures, smoother loading and more natural colours. Thank you, and blue skies! ✈️

> **Beta software:** OrthoStreamXP is functional and has been used in real flights, but it is still under active development. You may encounter rough edges. Feedback and bug reports are welcome.

## Why OrthoStreamXP?

### Fly more. Prepare less.

Traditional photo scenery often requires downloading and building large areas before you can use them. OrthoStreamXP takes a different approach.

- **Scenery on demand** — download imagery as X-Plane needs it, instead of preparing entire regions in advance.
- **Less disk space wasted** — keep the areas you fly and automatically remove older scenery when your cache reaches its limit.
- **Better scenery ahead** — OrthoStreamXP follows your aircraft and downloads upcoming areas before you reach them.
- **SimBrief flights ready sooner** — optionally, with your SimBrief username, the departure is built while X-Plane loads and the arrival and climb while you sit at the gate.
- **Simple quality settings** — choose Balanced, Sharp, Ultra, or Extra without learning dozens of technical parameters.
- **Settings that adapt to your setup** — test your connection before the first flight to get a recommended preset, and after a sufficiently long flight, get recommendations based on how the scenery performed on your PC and internet connection. You decide whether to apply them.
- **Even colours** — optionally soften the patchwork look where neighbouring photos were taken on different days, so a grey, hazy patch stands out less.
- **Seasonal colours** — optionally give summer imagery a more autumnal or winter appearance, based on the simulated date and location.
- **Clean photos up close** — X-Plane's procedural grass and dirt speckles are kept off the aerial photos by default, or faded to the strength you like, without changing your scenery packages.
- **Towns lit at night** — optionally give the photo scenery street lamps and the light of the houses along X-Plane's roads, so towns glow after dark as on X-Plane's own scenery. You can also skip the detailed photos at night to save downloads, keeping the lights.
- **Exposure and a live preview** — tone down photos that look too bright next to X-Plane's own scenery, and see any colour change on a photo from your cache before you fly.
- **See what happened during your flight** — review download performance, scenery loading, and your flight route.

### Designed for everyday flying

| Instead of… | You get… |
|---|---|
| Spending hours preparing scenery | Start flying with minimal preparation |
| Downloading huge regions you may never visit | Download only the scenery you need |
| Guessing which settings work best | Four straightforward quality presets |
| Manually tuning settings after every problem | Flight-based recommendations you can accept or reject |
| Keeping every downloaded area forever | A cache limit and a visual cache manager |

## What you need

Before installing OrthoStreamXP, make sure you have:

- **Windows 10 or 11 (64-bit)**
- **X-Plane 12**
- **WinFsp**, a free Windows component used to make the streamed scenery available to X-Plane. The setup process checks for it and provides a download link.
- **Scenery packages** for the regions you want to fly over: a region's mesh downloaded from within OrthoStreamXP, or your own.
- **An internet connection** for downloading new scenery.
- **Disk space for the scenery cache** — 25 GB is a reasonable starting point; more space lets you keep more areas available.
- A graphics card with enough memory for your chosen quality preset.

The installer also installs the .NET 10 desktop runtime if it is missing.

**Important:** OrthoStreamXP streams imagery onto scenery packages; it does not create terrain meshes itself. There are two ways to get them:

1. **Download a region from within OrthoStreamXP:** a whole region's mesh (Europe, North America, ...), during setup or later from the **Packages** page.
2. **Use your own packages:** compatible packages can come from tools such as Ortho4XP, OrthoStudio XP, AutoOrtho, and XPME. You can select an existing package folder during setup.

## Installation

1. **Install WinFsp** from [winfsp.dev](https://winfsp.dev/rel/).
2. **Download OrthoStreamXP** from the [Releases page](https://github.com/flysimdev/OrthoStreamXP/releases) and run `OrthoStreamXP-Setup.msi`.
3. **Restart your computer.** WinFsp's driver is ready only after a restart; until then OrthoStreamXP cannot show the scenery to X-Plane.
4. **Complete the first-run setup.** Select your X-Plane installation, your scenery packages, a quality preset, and a cache location.
5. **Click Start and fly.** OrthoStreamXP prepares the scenery connection and starts X-Plane when the scenery is ready.

Always start OrthoStreamXP before X-Plane. If X-Plane is already running, restart it after starting OrthoStreamXP.

For a step-by-step walkthrough of setup, flying, settings, the cache and troubleshooting, see the **[User guide](docs/README.md)**.

### Windows SmartScreen warning

The installer is not currently code-signed, so Windows may display a warning that it does not recognize the publisher. If you downloaded it from the official [Releases page](https://github.com/flysimdev/OrthoStreamXP/releases), choose **More info**, then **Run anyway**.

## Choose your scenery quality

OrthoStreamXP provides four presets:

- **Balanced** — prioritises a balance between visual detail and resource use.
- **Sharp** — the recommended starting point for most systems.
- **Ultra** — for systems with more graphics memory available.
- **Extra** — the highest preset, intended for systems with greater resources.

You can change presets in Settings. OrthoStreamXP also reports graphics memory usage from your flights and warns when the measured scenery requirements exceed your card's available memory.

After flights of at least 10 minutes, OrthoStreamXP may suggest adjustments based on observed performance. Each recommendation includes an explanation, and **nothing changes until you approve it**.

## Keep your scenery under control

The Cache page helps you manage the imagery stored on your computer.

- Set a cache limit from 25 GB up to 1 TB.
- View cached areas on a map.
- Select and delete areas you no longer need.
- Identify areas you have not flown over recently.

When the cache exceeds its limit, OrthoStreamXP removes older areas automatically. Deleted scenery can be downloaded again the next time you visit.

## Make the seasons feel right

Most aerial imagery was captured during warmer months, so a winter flight can otherwise look unexpectedly green.

Enable **Seasons** to adjust the appearance of vegetation and fields according to X-Plane's simulated date and the location you're flying over.

- **Spring:** fresh greens return.
- **Summer:** the imagery as it was photographed.
- **Autumn:** deciduous vegetation takes on yellow, orange, and rust tones.
- **Winter:** deciduous trees appear bare, while fields shift towards brown and olive.

Water, roads, buildings, rock, and sand retain their original colours. Seasonal colouring does not generate snow; snow remains part of X-Plane's weather.

## Known limitations

OrthoStreamXP is currently in beta. Please keep the following in mind:

- **Windows only:** the application depends on WinFsp.
- **Scenery packages are required:** download a region's mesh (6 to 33 GB per region) from the Packages page, or bring your own.
- **Internet required for new areas:** scenery loads from the internet on first use; subsequent flights can reuse cached imagery.
- **Imagery varies by location:** quality, capture date, colours, and transitions between imagery sources may differ.
- **Performance depends on your setup:** connection speed, disk performance, and graphics memory affect the experience.
- **Start order matters:** start OrthoStreamXP before X-Plane.
- **Beta changes:** settings and cache behaviour may evolve between releases.

## Imagery and licensing

OrthoStreamXP is free software released under the **GNU General Public License v3 (GPL-3.0)**.

The aerial imagery is provided by external services, including Bing Maps, Esri World Imagery, and Geoportal for Poland. The imagery is not part of the OrthoStreamXP software and remains subject to each provider's terms of use.

Imagery is intended for personal flight simulation use under the applicable provider terms. Do not redistribute downloaded imagery.

## Updates and support

OrthoStreamXP checks for updates when launched and periodically while running, and downloads a new version in the background. When it is ready, choose **Update and restart** on the Home page; it takes only a moment. Stop scenery streaming before installing an update.

Your settings, cache, and flight history are preserved during normal updates and reinstalls.

To uninstall, use **Windows Settings > Apps**. OrthoStreamXP asks whether to delete your settings and downloaded scenery as well; if you choose No, they stay for a later install.

If you encounter a problem, please open an issue in the project's GitHub repository and include:

- **What happened:** what you saw, and what you expected instead.
- **Steps to reproduce:** what you did before the problem appeared (for example, the area you were flying over and your quality preset).
- **Diagnostic info:** your OrthoStreamXP version, Windows version, and graphics card.
- **The session log:** the log file of the session where the problem occurred, from `%LOCALAPPDATA%\OrthoStreamXP\logs`.

Feedback is especially valuable during beta development.

## For developers and contributors

OrthoStreamXP is open source, and contributions are welcome.

The project uses .NET 10 and WinFsp. To build and test it on Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and WinFsp, then run:

```powershell
dotnet build OrthoStreamXP.slnx
dotnet test --solution OrthoStreamXP.slnx
```

To contribute, fork the repository, branch from `develop`, include tests for your changes, and submit a pull request against `develop`.

---

**Ready to fly?** Download the latest release from the [Releases page](https://github.com/flysimdev/OrthoStreamXP/releases), configure your scenery packages once, and explore the world from above.
