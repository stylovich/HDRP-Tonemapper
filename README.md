# HDRP Tonemapper (LPM + AgX)

Two SDR tonemappers for Unity's High Definition Render Pipeline, implemented as custom post processes:

- **FidelityFX LPM**: a port of AMD's Luma Preserving Mapper (LPM 1.20200225), plus an optional highlight desaturation control that isn't part of AMD's LPM.
- **AgX**: Troy Sobotka's AgX display transform in the Rec.2020 form used by Blender 4.x, with the Punchy / Golden looks and a custom ASC CDL look.

Both replace HDRP's own Tonemapping. Use one of them at a time.

## Requirements

- Unity 6000.6 or newer with HDRP 17.6.
- Tested with Unity 6000.6.7f1, HDRP 17.6.0, D3D12. Older Unity 6 / HDRP 17 versions are untested.

## Installation

In **Window > Package Manager**, click **+ > Install package from git URL...** and enter:

```
https://github.com/stylovich/HDRP-Tonemapper.git
```

Or add it to `Packages/manifest.json`:

```json
"com.stylovich.hdrp-tonemapper": "https://github.com/stylovich/HDRP-Tonemapper.git"
```

Append `#<commit or tag>` to the URL to pin a version.

## Setup

1. **Register the effects.** Go to **Project Settings > Graphics > HDRP Global Settings > Custom Post Process Orders**. Add `FidelityFX.Lpm.LpmTonemapping` and/or `AgX.AgxTonemapping` to **After Post Process**. Each component's inspector shows a button that does this for you.
2. **Turn off HDRP's tonemapping.** In your Volume profile, set HDRP's **Tonemapping** mode to **None**. The inspectors warn you when it isn't and offer a fix.
3. **Add the component.** Add **Post-processing > Custom > FidelityFX LPM Tonemapping** or **Post-processing > Custom > AgX Tonemapping** to the profile. Set its **Mode**.

The **Tonemapping Profiles** sample (Package Manager > Samples) has ready-made profiles for both.

## FidelityFX LPM

LPM preserves luma and hue: bright colors keep their saturation instead of going towards white like with ACES.

| Parameter | Default | Description |
|---|---|---|
| Exposure | 0 EV | Exposure applied right before mapping. Because LPM keeps mid-gray at 0.18, about -0.5 to -0.75 EV gets close to ACES brightness. |
| HDR Maximum | 64 | Linear input value that maps to white (LPM `hdrMax`). |
| Curve Exposure | 6 | Stops between HDR Maximum and 18% mid-gray (LPM `exposure`). With 64 / 6, 0.18 maps to 0.18. |
| Contrast | 0.3 | Extra contrast (0 to 1). |
| Shoulder Contrast | 1 | Shoulder shaping. 1 means no change. |
| Saturation | (0, 0, 0) | Per-channel saturation, luma preserving. |
| Crosstalk | (1, 1, 1/32) | Shapes how colors over-expose. AMD's example `{1, 1/2, 1/32}` turns bright skies lavender. |
| Highlight Desaturation / Start | 0 / 0.18 | **Extension, not in AMD's LPM.** Pulls bright colors towards white, keeping their luminance. At 1 it gets close to ACES. |

The inspector includes a tone curve preview, an "Anchor 0.18" helper, and crosstalk presets.

## AgX

| Parameter | Default | Description |
|---|---|---|
| Exposure | 0 EV | AgX maps 0.18 to about 0.5 on screen (sRGB), brighter than ACES. About -1 EV gets close to ACES brightness. |
| Look | None | None (AgX Base), Punchy, Golden, or Custom. |
| Slope / Offset / Power / Saturation | 1 / 0 / 1 / 1 | ASC CDL for the Custom look, applied in AgX's encoded space. |

The inspector shows where 0.18 lands on screen and plots the tone curve with per-channel ramps.

## Notes

- **SDR only.** When HDRP drives an HDR display, both effects pass the image through unchanged.
- **Input range.** Both effects run at **After Post Process**, so HDRP's color grading has already run. With Tonemapping set to None, its grading LUT clamps the input at about 59 (linear). To recover highlights, lower exposure earlier (Exposure, or Color Adjustments > Post Exposure) instead of with the tonemapper's own Exposure.
- **Builds.** The shaders live in `Resources` folders, so they're included in player builds.

## Third-party code

The LPM port is based on AMD FidelityFX LPM (MIT). The AgX implementation uses constants and formulas from Google Filament (Apache-2.0), three.js (MIT), and Benjamin Wrensch's "Minimal AgX implementation" (MIT). See [Third Party Notices.md](Third%20Party%20Notices.md).
