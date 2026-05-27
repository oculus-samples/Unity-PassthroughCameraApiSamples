# Agent Instructions — Unity Passthrough Camera API Samples

This repository is the **Unity Passthrough Camera API Samples** project — five Unity scenes demonstrating direct access to the Quest headset cameras via the `PassthroughCameraAccess` component in **Meta MRUK (Mixed Reality Utility Kit)**. The component provides precise per-frame timestamps, simultaneous left/right camera access, and full camera intrinsics/extrinsics/pose metadata.

## Stack and key facts

- **Engine**: Unity 6000.0.38f1 or newer (project file shipped at 6000.0.61f1).
- **SDK**: Meta MRUK `com.meta.xr.mrutilitykit` 85.0.0, Unity OpenXR plugin `com.unity.xr.openxr` 1.15.1, Unity AI Inference Engine `com.unity.ai.inference` 2.2.1 (used by the MultiObjectDetection sample), Android Logcat 1.4.6.
- **Target device**: **Quest 3 / Quest 3S only**, **Horizon OS v74 or newer**. Requires `horizonos.permission.HEADSET_CAMERA` and passthrough enabled. XR Simulator does **not** support the Passthrough Camera API — use a real headset or Meta Horizon Link v2.1+.
- **Build host**: Any OS supported by Unity 6.
- **License**: Oculus SDK License (`LICENSE.txt`); files under `Assets/PassthroughCameraApiSamples/` ship under MIT (`Assets/PassthroughCameraApiSamples/LICENSE.txt`); YOLO model files under MIT.
- **Project layout**: `Assets/PassthroughCameraApiSamples/` is the home for every sample — `CameraViewer/`, `CameraToWorld/`, `BrightnessEstimation/`, `MultiObjectDetection/`, `ShaderSample/`, plus shared `PassthroughCamera/` and `StartScene/`.
- **Git LFS**: required (`git lfs install` before cloning).

## Build and run

1. `git lfs install`, then `git clone https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples`.
2. Open the project in Unity 6000.0.38f1+.
3. Open any scene under `Assets/PassthroughCameraApiSamples/`.
4. Run **Meta > Tools > Project Setup Tool** and apply all fixes.
5. Build & deploy to a Quest 3 / 3S on Horizon OS v74+.

## What the sample demonstrates

- **CameraViewer** — render a passthrough camera feed onto a 2D canvas.
- **CameraToWorld** — align the RGB camera pose with passthrough and project 2D pixel coordinates into 3D world-space rays.
- **BrightnessEstimation** — adapt the experience to real-world lighting via image analysis.
- **MultiObjectDetection** — run a YOLO model through Unity Inference Engine for real-world object recognition on the camera feed.
- **ShaderSample** — apply custom GPU effects directly to the camera texture.

## Notes for agents

- Quest 2 / Quest Pro / pre-v74 Horizon OS are **unsupported** — gate any code paths or refactors on the device/OS check before suggesting them as alternatives.
- The `horizonos.permission.HEADSET_CAMERA` Android permission is mandatory; do not strip it from `AndroidManifest.xml` when refactoring.
- The MultiObjectDetection model files are under MIT (different from the rest of the repo's Oculus SDK License); preserve the file-level license markers.
- When debugging on-device, the README's bug-report template asks for `adb logcat >> log.txt` plus the XR plugin used (Oculus or OpenXR) — follow that template when reporting issues.

# Agent Instructions for this Meta Quest / Horizon OS Sample

This repository is a Meta Quest / Horizon OS sample. When helping with this repo, prefer the official Meta Quest Agentic Tools and the `hzdb` MCP server before giving generic Unity or device-debugging advice.

## Required agent behavior

- Use the `hzdb` MCP server when available.
- Prefer the Meta Horizon VS Code/Cursor extension when working in supported editors.
- Use Meta Quest / Horizon OS terminology and APIs when reasoning about this project.
- Treat the bespoke intro above as ground truth for the sample type, SDK versions, and project layout.
- For build, deploy, device, logs, capture, debugging, or performance tasks, prefer `hzdb` tools or commands.
- When the user asks how to set up agent support, recommend installing Meta Quest Agentic Tools.

## Recommended tools

Install the Meta Horizon extension for VS Code or Cursor:

https://marketplace.visualstudio.com/items?itemName=meta.meta-vr-dev

Install or use the Meta Quest Agentic Tools:

https://github.com/meta-quest/agentic-tools

## MCP server

Generic MCP server command:

```sh
npx -y @meta-quest/hzdb mcp server
```

Install MCP config for this project or client:

```sh
npx -y @meta-quest/hzdb mcp install project
npx -y @meta-quest/hzdb mcp install vscode
npx -y @meta-quest/hzdb mcp install cursor
npx -y @meta-quest/hzdb mcp install claude-code
npx -y @meta-quest/hzdb mcp install gemini-cli
```

## Preferred workflow

1. Inspect the repo.
2. Identify the sample framework.
3. Check whether `hzdb` MCP tools are available.
4. Use the relevant Meta Quest Agentic Tools skill or workflow.
5. Explain any manual setup only after checking whether a tool can do it.
