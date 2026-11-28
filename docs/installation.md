# Installation

[User guide](README.md) › Installation

## What you need

- **Windows 10 or 11 (64-bit).**
- **X-Plane 12.**
- **Scenery packages** for the regions you fly: the terrain (the mesh) that OrthoStreamXP adds the photos to. Three ways to get them:
    1. **Build your own in OrthoStreamXP** from X-Plane's Global Scenery, during the setup or later on the **Packages** page: pick the areas on a map, and each tile is built in a few seconds. See [Packages › Build own packages](packages.md#build-own-packages).
    2. **Download a region in OrthoStreamXP** on the **Packages** page (Europe, North America...); see [Packages › Download kubilus1 packages](packages.md#download-kubilus1-packages).
    3. **Use your own.** Packages made by Ortho4XP, OrthoStudio XP, AutoOrtho or XPME work. Each package is a folder that contains an `Earth nav data` folder. Put all of them in **one parent folder**, for example:

        ```
        D:\OrthoPackages\
            Europe\
                Earth nav data\
                terrain\
                ...
            Asia\
                Earth nav data\
                ...
        ```

        **In this example you have to specify the "OrthoPackages" folder as a Scenery packages folder in the application. Keep in mind the "OrthoPackages" here is just for example it can be whatever folder you have. Don't put your scenery packages to the "X-Plane 12/Custom Scenery" - it has to be out of it somewhere on you PC**

- **WinFsp**, a free Windows driver that OrthoStreamXP uses to show the scenery to X-Plane.
- **An internet connection** for areas you have not flown yet.
- **Disk space for the cache.** Each new 1° × 1° area you fly can take up to about 2 GB at Balanced, 8 GB at Sharp and 30 GB at Ultra or Extra. 25 GB is a reasonable minimum; a fast SSD is best.
- **A graphics card** with enough memory for the quality you choose (see [Settings › Quality](settings.md#quality)).

## 1. Install WinFsp

Download the latest WinFsp installer from [winfsp.dev/rel](https://winfsp.dev/rel/) and run it with the default options.

If you skip this step, the first setup shows a **Get WinFsp** button. Install WinFsp, restart your computer, then open OrthoStreamXP again.

## 2. Install OrthoStreamXP

1. Download `OrthoStreamXP-Setup.msi` from the [Releases page](https://github.com/flysimdev/OrthoStreamXP/releases).
2. Run it. The installer also installs the .NET 10 desktop runtime if your PC does not have it yet.

### Windows SmartScreen warning

The installer is not code-signed yet, so Windows may say it does not recognise the publisher. If you downloaded the file from the official Releases page, click **More info**, then **Run anyway**.

## 3. Restart your computer

Restart Windows after installing WinFsp and OrthoStreamXP. WinFsp's driver is ready only after a restart: until then OrthoStreamXP can fail to start the scenery, even though WinFsp shows as installed.

## 4. Open OrthoStreamXP

Start OrthoStreamXP from the Start menu. The first time, it opens the [first setup](first-setup.md).

Next: [First setup](first-setup.md)
