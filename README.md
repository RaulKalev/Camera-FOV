# Camera FOV for Revit

Draw what your security cameras can see, straight onto your Revit floor plans.

Camera FOV turns a camera family into coverage areas for the four DORI levels (Detection, Observation, Recognition, Identification). Walls, columns, doors and windows cut the view where they block it. When the design changes, the plugin tells you which cameras need redrawing.

<p align="center">
  <img src="docs/images/main-window.png" alt="The Camera FOV window" width="340">
</p>

## What you can do

- **Draw coverage.** Pick a camera, tick the DORI levels you need and press **Draw coverage**. Each level becomes its own coloured area, and the field-of-view angle is dimensioned for you.
- **Let walls block the view.** **Trace walls** turns the walls, columns, doors and windows at the plan's cut height into boundary lines, including those in linked models. You can also draw boundary lines yourself.
- **Keep drawings up to date.** Move or rotate a camera and the plugin shows that its coverage is out of date. Press **Update** to redraw it.
- **Check a spot.** Click a point in the plan to see which cameras cover it, how many pixels per metre each gives, and why the others don't.
- **Audit a whole floor.** See gaps and overlaps of all cameras at once, per DORI level, against the rooms on that floor.

<p align="center">
  <img src="docs/images/coverage-audit.png" alt="Coverage audit showing the best DORI level across a floor" width="720">
</p>

## Install

1. Download **CameraFOV_Installer.exe** from the [Releases](https://github.com/RaulKalev/Camera-FOV/releases) page.
2. Run it. It finds Revit 2024 and 2026 by itself.
3. Start Revit. The plugin is on the **RK Tools** tab.

## Getting started

1. Open a floor plan.
2. Open **Settings** (the gear icon) and create the **Boundary** line style and the **DORI region types**. You only need to do this once per project.
3. Press **Trace walls** in Settings to turn the walls into boundary lines.
4. In the main window, press **Select** and click a camera in the plan.
5. Check the field of view, resolution and rotation, tick the DORI levels, and press **Draw coverage**.

Scroll the mouse wheel to zoom in the audit, hold it down to pan, and double-click it to zoom out.

## Your camera family

The plugin works with families in the **Security Devices** category. It reads these parameters when they exist. You can rename them in Settings to match your own families.

| Parameter | What it's for |
| :--- | :--- |
| **Vaatenurk** | The camera's field of view angle |
| **Horisontaalne Resolutsioon** | Horizontal resolution in pixels, e.g. `3840` |
| **Pööra Kaamerat** | Rotation of the camera (optional) |
| **Kaamera nurk** | A field of view that overrides the standard one (optional) |

If a family has none of these, type the field of view and pick the resolution in the window instead.

## More screenshots

<p align="center">
  <img src="docs/images/point-check.png" alt="Point check result" width="380">
  <img src="docs/images/settings.png" alt="Settings window" width="260">
</p>

## Good to know

- Everything is calculated on the floor plan. Camera height and tilt aren't taken into account yet.
- The plugin only changes what it drew itself. Filled regions and boundary lines you drew by hand are kept.
- DORI distances follow a formula checked against Axis Site Designer.
- Works in **Revit 2024** and **Revit 2026**.

## License

MIT. See [LICENSE](LICENSE).
