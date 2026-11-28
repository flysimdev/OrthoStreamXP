# Questions and answers

[User guide](README.md) › Questions and answers

Answers to the questions people ask most often about OrthoStreamXP.

## What it is

### What does OrthoStreamXP do?

It streams aerial photos onto your X-Plane 12 scenery while you fly. It downloads the pictures for the area around your aircraft from Bing, Esri or Geoportal (Poland), turns them into X-Plane textures, and keeps them in a cache for your next flight.

It is free and open source, and it goes up to imagery zoom level 19 (the **Extra** preset, near airports).

### Does it make the terrain (the mesh) too?

It can. The mesh, the water masks and the coastlines come from scenery packages, and OrthoStreamXP can build them for you from X-Plane's own Global Scenery: pick the areas on the **Packages** page's **Build own packages** tab (see [Packages › Build own packages](packages.md#build-own-packages)). You can also download a whole region's mesh there, or use packages you already have, made by Ortho4XP, OrthoStudio XP, AutoOrtho or XPME. See [Installation › What you need](installation.md#what-you-need).

### Can it use the mesh from X-Plane's default scenery?

Yes, through the packages it builds. Each scenery file (`.dsf`) says exactly which texture goes on which part of the ground, and X-Plane's default scenery uses generic land textures, not photos, so OrthoStreamXP cannot stream onto it as it is. **Build own packages** makes a copy of each tile you pick, with X-Plane's own relief, water and coastlines, whose ground uses photo textures instead. X-Plane's Global Scenery itself is only read, never changed.

### What is the difference from AutoOrtho or XPME?

All three put photos on scenery packages, so the base packages are the same. OrthoStreamXP was written to avoid the problems of the others:

- **Speed.** A new area loads in a few minutes (each preset card shows an estimate), and areas you have flown before load from the cache at once.
- **Quality for free.** There is no paid tier. Textures go up to 8192 px and the imagery up to zoom 19, with no limit on the zoom level or the picture size.
- **Settings tuned for you.** **Test my connection** chooses the quality and the number of downloads, and after each flight OrthoStreamXP suggests changes from what it measured (see [Flying](flying.md#tuning-suggestions)).

### I already have ZL17/ZL18 photo scenery on disk. What do I gain?

If you already have photo scenery for every region you fly and you are happy with it, not much. Your setup works and there is no reason to replace it.

OrthoStreamXP suits a different way of flying:

- **No waiting before a flight.** You do not build or download a region first: the photos arrive as you fly.
- **Flying somewhere new** without planning your coverage in advance.
- **Keeping only what you use.** The cache fills only with the areas you fly over, not whole regions built ahead of time. You can set a size limit on the [Cache page](cache.md).

### How does it handle the transition from water to land?

It does not change it. The water, the coastlines and the sea and inland water come from your scenery packages, with the settings they were built with. OrthoStreamXP only supplies the photos. If you are happy with the water in your Ortho4XP or OrthoStudio XP packages, it looks the same with OrthoStreamXP.

### Does it work with SimHeaven (X-World)?

Yes. Install SimHeaven the way its instructions say, and OrthoStreamXP as this guide describes. OrthoStreamXP puts its packages at the end of `scenery_packs.ini`, below SimHeaven, airports and other overlays, so they stay on top of the photos.

### An airport sits on a hill or in a dip. Can I fix it?

Yes, for X-Plane's own airports. The ground comes from your scenery packages, and it is more detailed than X-Plane's, so a few airports no longer sit level on it. Find the airport on the [Airports](airports.md) page and click **Flatten**: X-Plane levels the ground under it from its next start. **Undo** puts it back. Custom airport scenery should not be flattened this way: it loses its buildings.

### Is there a Mac or Linux version?

No, OrthoStreamXP runs on Windows 10 and 11 only. There are no plans for a Mac version at the moment; that may change if enough people ask for it.

## Setup

### Which folder do I choose as the scenery packages folder?

The **parent folder** whose subfolders are your packages. Each package is a folder with an `Earth nav data` folder directly inside it. For example:

```
D:\OrthoPackages\          <- choose this one
    Europe\
        Earth nav data\
        terrain\
        ...
    Asia\
        Earth nav data\
        ...
```

Do not choose a package itself (`Europe`) or a folder further down (`Earth nav data`). Below the folder, OrthoStreamXP lists the packages it found; if it finds none, the hint turns red.

For XPME, this is usually XPME's top folder. For AutoOrtho, it is usually the `scenery` folder in AutoOrtho's folder (for example `z_autoortho\scenery`), after you move it out of `Custom Scenery` (see the next question). Check where the `Earth nav data` folders are: the folder you choose is the one two levels above them.

Have packages from more than one tool? Keep each tool's packages, and the regions downloaded in OrthoStreamXP, in a folder of their own, and choose the one you fly with. See [Packages › One folder per source](packages.md#one-folder-per-source).

### Can my packages stay in X-Plane's Custom Scenery folder?

No. Move them anywhere else on your PC, for example `D:\OrthoPackages`. OrthoStreamXP shows them in `Custom Scenery` itself while it runs; if they were already there, X-Plane would see each package twice and OrthoStreamXP refuses to start.

### I used AutoOrtho before: how do I switch?

1. Close AutoOrtho.
2. Move your AutoOrtho scenery packages out of `X-Plane 12\Custom Scenery` to any folder on your PC.
3. Open `X-Plane 12\Custom Scenery\scenery_packs.ini` and remove the lines for the AutoOrtho packages. If you would rather not edit the file, switch them off on OrthoStreamXP's [X-Plane Custom Scenery](custom-scenery.md) page instead.
4. Open OrthoStreamXP **without** starting the scenery. In **Settings › Folders**, set the **Scenery packages folder** to the new place.
5. Click **Start and fly** (or **Start**, then start X-Plane).

### What happens when I click Start?

1. Your packages appear in `X-Plane 12\Custom Scenery`, with the photos added. The packages on disk stay unchanged.
2. OrthoStreamXP adds them to the end of `scenery_packs.ini`, so airports and overlays stay above them. The previous file is kept as `scenery_packs.ini.bak`.

When you stop OrthoStreamXP, the packages disappear from `Custom Scenery` again, and the lines it added to `scenery_packs.ini` at the start are taken out.

Packages switched off under **Scenery packages** on the Home page are left out (see [Flying › Leaving a package out](flying.md#leaving-a-package-out)).

### It downloaded a few pictures and then nothing, and the ground looks like X-Plane's own scenery.

Usually X-Plane is not reading the packages from OrthoStreamXP:

- The packages are still in `Custom Scenery`, or another tool (such as AutoOrtho) is still running. Follow the steps in *I used AutoOrtho before* above.
- X-Plane was started before OrthoStreamXP. Quit X-Plane, click **Start** in OrthoStreamXP, then start X-Plane again.
- The packages do not cover the area you fly. OrthoStreamXP can only add photos where you have packages.

See also [Troubleshooting](troubleshooting.md).

### OrthoStreamXP says it cannot start.

Check the notice on Home: it names the problem and usually has a button for the fix. The most common causes are a wrong scenery packages folder (see above), WinFsp installed without restarting Windows, and a folder in `Custom Scenery` with the same name as one of your packages. See [Troubleshooting › Common problems](troubleshooting.md#common-problems).
