# Camera FOV for Autodesk Revit

A comprehensive Revit plugin designed to calculate and visualize Security Camera Field of View (FOV) based on DORI (Detection, Observation, Recognition, Identification) standards.

## Features

- **FOV Visualization**: Generates precise 2D filled regions representing the camera's coverage area directly in your Revit views.
- **DORI Standards**: Built-in presets for DORI zones:
  - **Detection** (25px/m)
  - **Observation** (63px/m)
  - **Recognition** (125px/m)
  - **Identification** (250px/m)
- **Automatic Geometry**: Automatically handles obstacles like walls and columns to create accurate visible areas.
- **Smart Rotation**: Auto-detects camera orientation from Revit families and supports manual "Pööra Kaamerat" offsets.
- **Customizable**: Adjustable max distance, FOV angle, and resolution settings.
- **Modern UI**: Features a sleek interface with Dark/Light theme support.

## Installation

1. Build the solution using Visual Studio 2022.
2. Load the resulting assembly into Revit using the Add-in Manager or by creating a `.addin` manifest file.

## Usage

1. Open a Floor Plan view in Revit.
2. Run the `Show Camera FOV` command.
3. Select a security camera element.
4. Adjust DORI settings, Resolution, and Angle as needed.
5. Click **"Draw"** to generate the FOV visualization.

## Requirements

### Revit Families
The plugin works with elements in the **Security Devices** category (`OST_SecurityDevices`).
For full functionality, the families should contain the following parameters:

| Parameter Name | Type | Description |
| :--- | :--- | :--- |
| **Vaatenurk** | Instance | The camera's Field of View angle (in degrees). |
| **Horisontaalne Resolutsioon** | Type/Instance | The sensor's horizontal resolution (in pixels). |
| **Pööra Kaamerat** | Instance | (Optional) Manual rotation offset for the camera (in degrees). |
| **Kaamera nurk** | Instance | (Optional) Legacy fallback for rotation offset. |

## Technical Overview

### How it Works
The FOV generation uses an intelligent **ray-casting algorithm** to simulate the camera's line of sight. 
1. **Ray Emission**: The system casts rays across the specified Field of View angle.
2. **Obstruction Detection**: It utilizes Revit's `ReferenceIntersector` to detect collisions with model elements like walls, columns, and other obstacles.
3. **Geometry Synthesis**: Intersection points are connected to form a closed loop, generating a `FilledRegion` that accurately represents the visible area, respecting physical occlusions.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
