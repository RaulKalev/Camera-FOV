# Camera FOV for Autodesk Revit

A comprehensive Revit plugin designed to calculate and visualize Security Camera Field of View (FOV) based on DORI (Detection, Observation, Recognition, Identification) standards.

## Supported Revit Versions

- **Revit 2024**
- **Revit 2026**

*Note: The plugin has been tested in these versions but may work in other versions if the Revit API supports the required features.*

## Features

- **FOV Visualization**: Generates precise 2D filled regions representing the camera's coverage area directly in your Revit views.
- **DORI Standards**: Built-in presets for DORI zones:
  - **Detection** (25px/m)
  - **Observation** (63px/m)
  - **Recognition** (125px/m)
  - **Identification** (250px/m)
- **Manual Boundary Definition**: Uses detail lines to allow users to manually define visual boundaries for the FOV.
- **Smart Rotation**: Auto-detects camera orientation from Revit families and supports manual rotation offsets.
- **Customizable**: Adjustable max distance, FOV angle, and resolution settings.
- **Modern UI**: Features a sleek interface with Dark/Light theme support.

## Installation

1. Build the solution using Visual Studio 2022.
2. Load the resulting assembly into Revit using the Add-in Manager or by creating a `.addin` manifest file.

## Usage

1. Open a Floor Plan view in Revit.
2. Go to the **RK Tools** tab.
3. Click **Run Camera FOV plugin**.
4. Select a security camera element.
5. Adjust DORI settings, Resolution, and Angle as needed.
6. Click **"Draw"** to generate the FOV visualization.

## Requirements

### Revit Families
The plugin works with elements in the **Security Devices** category (`OST_SecurityDevices`).
For full functionality, the families should contain the following parameters (Parameter names are configurable in Settings):

| Parameter Name | Scope | Data Type | Description |
| :--- | :--- | :--- | :--- |
| **Vaatenurk** | Instance/Type | Angle | The camera's Field of View angle (in degrees). |
| **Horisontaalne Resolutsioon** | Instance/Type | Text or Integer | The sensor's horizontal resolution (e.g. "1920" or "1920 px"). |
| **Pööra Kaamerat** | Instance | Angle | (Optional) Manual rotation offset for the camera (in degrees). |
| **Kaamera nurk** | Instance | Angle | (Optional) Manual Field of view angle override. |

## Technical Overview

### How it Works
The FOV generation uses an intelligent algorithm to simulate the camera's line of sight. 
1. **Ray Emission**: The system casts rays across the specified Field of View angle.
2. **Boundary Definition**: It detects Detail Lines to limit the FOV, allowing users to manually define obstacles or view limits.
3. **Geometry Synthesis**: Intersection points are connected to form a closed loop, generating a `FilledRegion` that accurately represents the visible area.

### DORI Calculation
The maximum effective range is dynamically calculated based on the **DORI standard** (Pixels Per Meter), ensuring the visualization meets specific security requirements (Detection, Observation, Recognition, Identification). 
The formula derives the maximum distance from the camera's **Resolution** and **Field of View**, adhering to the mathematical relationship between pixel density and arc length.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
