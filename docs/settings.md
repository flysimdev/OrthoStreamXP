# Settings

[User guide](README.md) › Settings

The Settings page has seven tabs. Changes are saved straight away, and a short **Saved** note confirms each one.

Settings can be changed only while OrthoStreamXP is stopped. While the scenery is streaming, the page shows the settings in use but you cannot edit them: a banner says *Streaming is running. Stop it to change settings.* Click **Stop streaming** in the banner to stop it. Changes apply from the next start.

At the bottom of every tab:

- **Show the file** shows the settings file (`%LOCALAPPDATA%\OrthoStreamXP\orthostreamxp.ini`) in Explorer.
- **Run the setup again** reopens the [first setup](first-setup.md).

## Quality

![Settings: Quality](images/settings-quality.png)

Choose how sharp the ground is. Each preset comes with settings tested for it and keeps its own textures in the cache, so you can switch at any time without losing anything.

| Preset | Textures | Imagery zoom | Resolution | Best for |
|---|---|---|---|---|
| **Balanced** | 2048 px, 1024 near airports | 15, 16 near airports | ~3.4 m per pixel | High flying, a slower connection or little graphics memory |
| **Sharp** | 4096 px, 2048 near airports | 16, 17 near airports | ~1.7 m per pixel | Everyday flying, approaches included |
| **Ultra** | 8192 px, 4096 near airports | 17, 18 near airports | ~0.8 m per pixel | Low flight and GA, with plenty of graphics memory |
| **Extra** | 8192 px | 17, 19 near airports | ~0.8 m per pixel | Low and slow flight and screenshots, on a fast connection |
| **Dynamic** | its base's near the ground (Extra, Ultra or Sharp), less higher up | as its base below 5,000 ft | follows the height | Climbs, cruise and approaches in one flight |

At **Full** resolution (the default), each texture keeps all the detail of the imagery it is made from: nothing is shrunk. The sizes above are Full's; see [Resolution](#resolution) for less. The resolution is for the wide areas away from airports, at mid latitudes; near airports the textures cover a quarter of the width, so the same size is four times as sharp there. Each new texture is also sharpened a little as it is built, so roofs, field edges and treetops look crisper. This is always on and has no setting.

**Extra** is Ultra with the finest imagery (zoom 19) near airports, as 8192 px textures, twice as sharp as Ultra's there and often without its clouds and other artefacts. Elsewhere it is the same as Ultra. Near airports it downloads four times as much as Ultra, and a new area takes a little longer to load: about 4 minutes, then sharp next to the aircraft.

### Dynamic

**Dynamic** makes the detail follow your height above the ground:

- **Below 5,000 ft** it is its **Base** preset: Extra unless you choose another.
- **From 5,000 ft** the photos are downloaded at Sharp's detail.
- **From 25,000 ft** they are downloaded at Balanced's detail.

High up, X-Plane still asks for the photos in full detail, though from there the detail cannot be seen. Dynamic downloads about half as much in a climb and cruise, which suits airliners and other high flying.

The **Base** box on Dynamic's card sets the preset it is near the ground:

| Base | Near the ground | Higher up | Best for |
|---|---|---|---|
| **Extra** (the default) | Extra | Sharp from 5,000 ft, Balanced from 25,000 ft | a fast connection |
| **Ultra** | Ultra: zoom 18 near airports instead of 19, a quarter of Extra's downloads there | Sharp from 5,000 ft, Balanced from 25,000 ft | a good connection |
| **Sharp** | Sharp, through the climb too | Balanced from 25,000 ft | long flights at airliner heights, on an everyday connection |

The card's text follows the base you choose, and Home names it, for example *Dynamic from Ultra*. Dynamic uses its base's textures in the cache and its base's tuning.

On a descent, Dynamic looks four minutes ahead and goes back to full detail early, so the ground is sharp again before you come close to it. X-Plane does not load a photo again just because you came lower: one it loaded with less detail high up stays that way while X-Plane keeps it. That is why the switch back comes early.

Dynamic shares its base's textures in the cache (Extra's by default), so you can switch between the two without downloading anything again, and photos already in the cache are shown as they are at any height. It needs X-Plane to send the aircraft's position, which it does on the same PC by itself. The heights can be changed in the settings file (**Show the file**): `dynamic-sharp-above` and `dynamic-balanced-above`, in feet.

### Resolution

The **Resolution** box at the top right of each card shrinks that preset's textures, to save graphics memory:

- **Full** (the default): the textures keep all the detail of their imagery.
- **Medium**: halves the textures of the wide areas and keeps the airports' textures sharp.
- **Low**: halves them all, a quarter of Full's graphics memory.

The imagery is the same at every resolution: it is downloaded at the same zoom, so a lower resolution does not download less. It shows the same picture with less detail, and takes less graphics memory, RAM and disk. Below a resolution other than Full, the card gives the sizes it makes, for example *At Medium resolution: 4096 px textures in most areas, 4096 px near airports, from the same imagery.*

Each preset keeps its own resolution, so you can have Ultra at Medium and Extra at Full; the one of the preset you choose is used. Textures of each resolution are kept in their own cache folder: [Cache › By quality](cache.md#by-quality) lists them as, for example, *Ultra, Medium resolution*. Like every setting, a change applies from the next start.

Try a lower resolution when a preset's card warns that its textures would not fit your graphics card, rather than a coarser preset: Medium keeps the airports at full detail.

### The cards

Each card shows the imagery zoom, the metres per pixel, roughly how long a new area takes to load, and how much graphics memory the photo textures take. Until you have flown, that memory figure comes from a test flight; after your first long flight, it is measured on your PC.

- The **Recommended** badge marks the preset that suits your connection and graphics card, with the reason below it. When it fits only at a lower resolution, the badge says so, for example **Recommended at Medium**.
- The other cards say in a line under the title whether they would suit your PC too: *Also suits your PC.* in green (or *Also suits your PC at Medium resolution*, when only a lower resolution fits your graphics card), or in amber why not, such as *Needs about 100 Mbit/s of downloads* or *Too large for your 4 GB graphics card, even at Low resolution*.
- If a preset's photo textures plus X-Plane's own needs (about 5 GB) would be more than your graphics card has, its card shows a warning. X-Plane may then stutter or lower its texture detail; a lower resolution or a smaller preset is safer.

### Ground detail

Up close, X-Plane can sprinkle its own procedural grass, shrubs and dirt over the ground. This helps X-Plane's blurry default textures, but on sharp aerial photos it looks like noise: dark speckles and pale stony patches that the real imagery does not have. The photo scenery packages ask for it in every photo tile, so you see it with any scenery packages.

The **Ground detail** slider sets how strong this effect is:

- **30 %** (the lowest, and the default): a faint version of X-Plane's effect; the photos stay clean and sharp.
- **40–90 %**: a stronger version of X-Plane's effect, for more texture when you are low.
- **100 %**: X-Plane's full effect, as the packages define it.

Your scenery packages are not changed: OrthoStreamXP adjusts the terrain files as X-Plane reads them. An older settings file with a lower value (such as the former default, 0) is read as 30 %. The strengths below 100 % are made from X-Plane's own decal files, so they need the **X-Plane folder** to be set. Like every setting, a change applies from the next start.

## Folders

![Settings: Folders](images/settings-folders.png)

- **X-Plane folder:** the X-Plane installation where the packages appear (in its `Custom Scenery` folder).
- **Scenery packages folder:** the parent folder of your packages. The line below it lists the packages found. Keep the packages of each source (packages built or regions downloaded in OrthoStreamXP, Ortho4XP, OrthoStudio XP...) in a folder of their own, and choose here the one you fly with: see [Packages › One folder per source](packages.md#one-folder-per-source).
- **Cache folder:** where the textures are kept. The line below it shows the free space and how much a new area takes at the current texture size.

## Pictures and download

![Settings: Pictures and download](images/settings-download.png)

- **Where the pictures come from:** the imagery source.
  - *Bing, then Esri where Bing has no picture* (recommended): the sharpest pictures in most places.
  - *Esri (ArcGIS), then Bing where Esri has no picture*: in some regions Esri's photos are newer or clearer than Bing's.
  - *Bing only*.
  - *Geoportal in Poland (national orthophoto), Bing and Esri elsewhere*.
- **Downloads at once:** how many pictures download in parallel, from 16 to 256. Your internet connection sets the real speed; beyond about 160, more downloads only wait longer. Lower this if other programs need the connection while you fly.
- **Test my connection:** about 30 seconds and 30 MB of real picture tiles from your chosen source. It suggests a value for *Downloads at once* and the quality your connection can keep up with. When its suggestion differs from your setting, a **Use N at once** button applies it. The test cannot run while the scenery is streaming. Speeds change during the day, so test a few times at different hours and choose a number in the middle of the results.

## Colours

![Settings: Colours](images/settings-colours.png)

### Preview

The top of the tab shows a photo from your cache twice: **Before** (as downloaded) and **After** (as X-Plane will get it with the settings below). It updates as you change them, and **Another photo** picks a different one. The preview needs a cache folder with at least one flown area in it. It works while the scenery is streaming too. When you scroll down to the settings, the preview stays at the top of the tab, so you can see each change as you make it.

The line under the pictures says where the photo is and which changes the **After** picture shows, for example *Exposure 80 % · Contrast 60 % (always on)*.

The **After** picture always has a little more contrast than **Before**, even with every setting below off: OrthoStreamXP gives all photos a touch of contrast as X-Plane reads them, because aerial photos are often hazy. That contrast has no setting.

### Seasons

Most aerial photos are taken in summer.

- **Colour the scenery for the season** changes the vegetation to match X-Plane's date and your location (southern hemisphere included):
  - **Spring:** fresh green.
  - **Autumn:** leaves turn yellow and rust.
  - **Winter:** deciduous trees look bare and fields turn brown.
  
  Conifers, roofs, roads and water keep their colours, and snow is left to X-Plane's weather.
- **Season:** *Automatic: X-Plane's date and the place* follows the date of your flight; a new date takes effect from the next flight. You can also force one season everywhere: *Early spring*, *Spring*, *Summer (the pictures as they are)*, *Early autumn*, *Autumn*, *Late autumn* or *Winter, without snow*.
- **Season: Satellite: grass and fields as they really were** also follows X-Plane's date. Trees keep the calendar's colours and times, but **grass and fields** look as green as NASA's satellites saw the place in that part of the last year, compared with its greenest month. So they stay green through a mild Atlantic or Mediterranean winter, dry out in a dry summer, and turn straw-coloured in the tropics' dry season, where the calendar shows nothing or the opposite. A snowy or ploughed winter rests fully, as with the calendar.

  The satellites see the country around a place at about 4 km, so a whole area changes together, not each field on its own. Towns, rock and sand, which are never very green, keep the calendar. OrthoStreamXP downloads about 2 MB once a year for each area of about 500 km, the first time you fly there, and keeps it with the low-zoom base. Where it has no satellite data (offline, before the first download), it uses the calendar. The colour preview uses only what is already downloaded: for a place you have not flown with Satellite yet, the line under the pictures says *No satellite record of this place yet* and shows the calendar's season.
- **Strength:** how far the colours move towards the season. 100 % is the full change.

### Exposure

**Exposure** darkens photos that look too bright next to X-Plane's own scenery, like a camera's exposure. 100 % shows them as downloaded; 50 % is one stop darker.

All the colour options only change how the photos look. The cache keeps the photos as downloaded, so changing them never downloads anything again.

## Night

![Settings: Night](images/settings-night.png)

How the photo scenery looks after dark. Both switches are **Off** by default and work independently: you can have the lights without skipping the photos, skip the photos without the lights, or both.

### Night lights

X-Plane's own scenery glows at night where there are towns. Photo scenery replaces that ground, so without night lights a town on the photos is dark except for X-Plane's 3D roads, cars and buildings.

Turn on **Light the towns at night** to give every photo tile its own lights:

- street lamps along X-Plane's roads: a lamp every 28–45 m depending on the road, mostly warm sodium orange with some white LEDs, a few of them out;
- a soft glow around the lamps, which stand beside the road so that X-Plane's 3D roads (or an overlay's, like simHeaven's) do not cover their light;
- a faint, even light from the houses along town streets.

How many lamps are lit and how bright the houses are depends on how built up the place is: a town centre is fully lit, a village keeps all its lamps, and in the open country only a few lamps along the roads shine. Between the lights the photo stays dark; X-Plane fades the lights in at dusk by itself.

The roads come from the overlay package built or installed with your packages (**OrthoGlobal_Overlays_XP** or **OrthoStream_Overlays**), or from another overlay package in X-Plane's Custom Scenery. Where no overlay package covers an area, its photo tiles stay dark at night.

X-Plane loads the lights with the scenery, by day too, so they are there when night falls during a flight. The lights get the same detail as the photos under them at every distance, up to about 3 m per pixel beside the aircraft. Each tile's lights are made the first time X-Plane loads that tile, and again with more detail as you come closer; only the detail X-Plane reads is kept in the cache with the area's photos, compressed (about 50 KB per tile; a 20-minute flight around Zurich kept 160 MB), so the first flight over a new area uses a little more processor time and later flights none. Your scenery packages are not changed. **Off** is the default; a change applies from the next start.

### Photos at night

At night the ground is dark and you see little of the photos, so downloading them in full detail mostly costs bandwidth and processor time. Turn on **Skip the photos at night** to save that on night flights:

- **While it is dark in X-Plane** (the sun more than 9° below the horizon), OrthoStreamXP downloads no detailed photos. The ground you fly over is a blurry version of the photos, made from the small overview pictures it already keeps for far-away scenery.
- **The night lights still work.** With **Light the towns at night** on, the street lamps and houses shine on the blurry ground as usual, as sharp as ever.
- **Places you have flown over before stay sharp**: photos already in the cache are shown as they are.
- **Around sunset and sunrise** the photos download as usual, while there is still light to see them by: from sunset until well after the sky starts to darken, and again from early dawn.

OrthoStreamXP reads the time of day from X-Plane. When a flight starts, X-Plane first loads the area around the aircraft, and only then, once the flight is running, says what time it is. So the area where a flight starts still gets its photos, even at night, and the saving starts from there: everything X-Plane loads as you fly on.

If you fly on into the day, ground that loaded during the night stays blurry after sunrise until X-Plane loads it again, which happens as you fly on into new areas. Leave this off for flights that end in daylight. Like every setting, a change applies from the next start.

How dark it must be is set in the settings file (**Show the file**), not on the page: `skip-photos-sun-below = 9` is the sun's height below the horizon in degrees, from 0 to 18. A higher number keeps the photos longer at dusk and brings them back sooner at dawn; a lower one skips more of the twilight.

## Tuning

![Settings: Tuning](images/settings-tuning.png)

After flights of 10 minutes or more, Home may suggest settings for your preset (see [Flying › Tuning suggestions](flying.md#tuning-suggestions)). The tab lists each setting the tuning changed, with the preset's own value beside it. **Reset to defaults** puts every setting back to the preset's tested values and forgets what was tuned; it asks first. The folders, the picture source and *Downloads at once* stay as they are.

## Other

![Settings: Other](images/settings-other.png)

- **Keep the photo scenery below airports and overlays:** at every start, moves the packages to the end of X-Plane's `scenery_packs.ini`, so airports and overlays draw on top of the photos. Leave it on unless you order that list yourself. To see and change the list, use the [X-Plane Custom Scenery](custom-scenery.md) page.
- **X-Plane address:** needed only when X-Plane runs on another PC.
- **X-Plane start options:** command-line options added when **Start and fly** starts X-Plane, for example `--allow_reshade`. They take effect the next time X-Plane starts.

Next: [Cache](cache.md)
