# SimBrief

[User guide](README.md) › SimBrief

If you plan your flights with [SimBrief](https://www.simbrief.com/), OrthoStreamXP can build your flight's scenery before X-Plane needs it. It is optional and off until you enter your SimBrief username.

## What it does

When streaming starts and while you sit at the gate, OrthoStreamXP builds the scenery of your latest SimBrief flight plan ahead of time:

1. **The departure airport, when streaming starts.** While X-Plane is still loading, OrthoStreamXP builds the photos of the scenery tiles X-Plane loads around your departure airport, each as sharp as X-Plane will want it from the gate. The view at the gate is sharp sooner.
2. **The arrival and the climb, while the aircraft stands at the gate.** About 30 seconds after the aircraft has come to a stop at the departure airport, OrthoStreamXP starts on the arrival airport, then the climb from the departure to the top of climb. This uses the quiet time before pushback, at the lowest priority: what X-Plane needs now always comes first.
3. **The alternate airport, if you switch it on.** It is rarely flown, so it is off unless you set `simbrief-alternate = on` (see [Settings in the ini file](#settings-in-the-ini-file)).

When the aircraft rolls for takeoff (faster than 40 kt), whatever has not started yet is dropped and the normal scenery loading takes over. Photos already in the cache are never downloaded again.

The gate and the takeoff roll are told by the aircraft's own ground speed and X-Plane's "on the ground" flag, so moving or lifting the camera in an outside view does not count as a takeoff.

**With mip streaming off** only the departure is built, whole textures within `simbrief-radius-km` of the airport, and only as many as the background build queue takes at once (4 × `dds-build-workers`). These builds do not count against the download limit, are not in the per-phase summary in the log, and are not dropped on the takeoff roll. The arrival, the climb and the alternate need mip streaming.

## Setting it up

Enter your SimBrief username, or your Pilot ID (the number in your SimBrief account settings):

- in the **SimBrief** step of the [first setup](first-setup.md#6-simbrief), or
- later in **Settings › SimBrief**.

Click **Test SimBrief connection** (in the setup or in Settings) to check that OrthoStreamXP finds your latest flight plan. Both places also have the **download limit per session** and **Also build the alternate airport**.

## Why there is a download limit

The photos come from free map services (Bing, Esri). They are made for looking at maps, not for bulk downloads, and a program that downloads too much can be slowed down or blocked. That would hit every OrthoStreamXP user, not only you. Building a whole flight ahead means thousands of photos before you need any of them, so the SimBrief prefetch gets a budget per streaming session. It also protects a metered or slow connection.

When the limit is reached nothing breaks: the rest loads as usual while you fly. Your own flying never counts against it.

| Your situation | Limit |
|---|---|
| Metered or mobile connection | 300 MB |
| Slow connection | 500 MB |
| A typical flight at High or Ultra | 1000 MB (default) |
| Extra quality (about four times the downloads near airports), or the alternate airport on | 3000 MB |

If the log says *the download budget ... is used up* on your flights, raise the limit a step. **No limit** (`0`) is not recommended.

## On the Home page

While **Build my SimBrief flight's scenery ahead** is on, the line below the buttons says what OrthoStreamXP knows about your flight: before the start that the plan is read then, after it the flight found (for example *Flight plan: EDDM → EGLL, generated 12 min ago*) or why there is none, and after a reload what the reload found.

- **Build my SimBrief flight's scenery ahead.** Turn this off to skip SimBrief for one session. It can be changed while streaming is stopped and is on again the next time OrthoStreamXP opens. With **Start when OrthoStreamXP opens** on, streaming starts before you can turn it off.
- **Reload SimBrief flight plan.** OrthoStreamXP reads your flight plan once, when streaming starts. If you generated it afterwards, or planned another flight, press this button: OrthoStreamXP asks SimBrief for your latest flight plan and builds its scenery. The text next to the button says what it found, for example *Loaded EDDM → EGLL, generated 12 min ago*. SimBrief is asked only when you press it, never on its own.

## When the flight plan is skipped

- **An old flight plan.** A plan generated more than 6 hours ago is taken for an earlier flight: its departure is not built. Generate a new one, then press **Reload SimBrief flight plan**.
- **The aircraft is somewhere else.** If X-Plane places your aircraft more than 50 km from the plan's departure airport, the plan is not this flight's: the departure textures still waiting are dropped.
- **The download limit.** The SimBrief prefetch downloads at most 1000 MB per streaming session (`simbrief-download-limit`). After that, the rest is left to the normal scenery loading. Your own flying never counts against the limit.

The log says each time why something was skipped.

## Settings in the ini file

These are in `orthostreamxp.ini` (see [Settings](settings.md)):

| Setting | What it does | Default |
|---|---|---|
| `simbrief-user` | Your SimBrief username or Pilot ID. Empty: off. | empty |
| `simbrief-alternate` | `on`: build the alternate airport too, after the arrival. | `off` |
| `simbrief-download-limit` | The most the SimBrief prefetch may download per streaming session, in MB. `0`: no limit. | `1000` |
| `simbrief-radius-km` | With mip streaming off: how far around the departure airport textures are built at full detail, 1-50 km. | `10` |

## Privacy

OrthoStreamXP sends your SimBrief username (or Pilot ID) to simbrief.com over HTTPS, once when streaming starts and each time you press **Reload SimBrief flight plan**. Nothing else is sent. The username is never written to the log, the flight reports or the setup summary.

It is stored in `orthostreamxp.ini` (`simbrief-user`). If you attach that file to a bug report, delete that line first.
