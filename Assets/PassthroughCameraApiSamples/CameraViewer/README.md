# CameraViewer: detected instances and attention highlighting

CameraViewer continuously runs the **same YOLOv9 model and class labels as MultiObjectDetection**. It assigns short-lived instance IDs, estimates 3D surface positions using MRUK environment raycasts, and shows an ID/name/position list beneath the live camera. Press **A / index pinch** to send the latest completed detection frame and its metadata to OpenAI. The task remains **cooking garlic pork chops**.

OpenAI rates every submitted instance on Salience, Expectancy and Value. Unity validates the IDs, computes weighted totals, and highlights **only the highest-scoring instance**. The right panel shows the frozen uploaded image, its winner box, the top five scores, and paginated JSON. The world-space box follows the current local detection with the winning ID. No unscored boxes are drawn.

## Setup and controls

1. Keep your API key in the existing Git-ignored `Assets/Resources/OpenAI.local.json`:

   ```json
   { "apiKey": "YOUR_OPENAI_API_KEY" }
   ```

   The Unity process can alternatively inherit `OPENAI_API_KEY`, or code can call `SetApiKey()`. A Mac environment variable does not automatically reach the Quest app. Do not put keys into tracked scripts or prefabs.
2. Open `CameraViewer.unity`. Its existing `CameraViewerManagerPrefab` now includes a wired `CameraViewerObjectDetector` with references to the sample model and labels. No manual prefab installation is required. MRUK's environment raycaster is created at runtime if supported; camera and Scene permissions are requested even when this is the only build scene. Keep both permissions and internet access enabled.
3. Rebuild and install using the repository's [setup instructions](../../../README.md). Allow camera and spatial-data permissions, and wait for a fresh detection frame. No cloud requests occur automatically.
4. **A / index pinch:** score all instances in one captured detection frame. **B / middle pinch:** cycle through JSON metadata, each ranked instance, then return to the ranking. Editor shortcuts remain **Space / J** when a supported camera connection provides frames.

The model's supported categories are in [SentisYoloClasses.txt](../MultiObjectDetection/SentisInference/Model/SentisYoloClasses.txt). They include cups, bottles, knives and bowls, but **not garlic, pork chops or frying pans**. This change does not train a cooking-specific detector. Use supported classes for the initial integration test, or supply a compatible model and label set later. The detector expects the sample converted model's three outputs: pixel corner boxes `[N,4]`, class IDs `[N]`, and confidences `[N]`.

The default UI and model descriptions use English. To request Chinese descriptions, set **Result Language** to `Simplified Chinese` and assign a font with Chinese glyphs to **Result Font**. Object names remain the detector's class labels.

## Detection, positions and frame consistency

- Inference starts from a frozen render target after rendering. Double buffering publishes the finished detection list together with that exact source image. Uploads copy the published image and metadata before yielding, then resize without cropping (default longest edge 1024) and encode as JPEG. They never combine a detection list with a newer unrelated camera frame.
- Every frame has `frame_id`, `space_id`, capture timestamp, source dimensions and capture camera pose. Every instance has `object_id`, class/name, confidence, normalized `[left, top, right, bottom]` bounds with a **top-left** origin, and `world_position_m`.
- Position is a **surface hit estimate**, not the object's volume center or a full 3D bounding box. Depth is sampled after inference, with a separately recorded `depth_sample_realtime_s`; it is not exactly synchronized RGB/depth. If inference takes over **Max Depth Delay** (default 1 second), depth is unavailable, or raycasting fails, position is `null`. No fixed-distance replacement is fabricated.
- Instance association is class-aware and one-to-one: nearby 3D hits are matched, or overlapping image boxes are matched only when the viewpoint changed little. This is a conservative prototype tracker, not persistent identity recognition. IDs are not reused after expiration/reset. Crossing identical objects, fast movement and occlusion can still break association; capture again if an identity changes.
- The detector continues while OpenAI runs. Cloud results must match the submitted frame ID and contain **exactly one score for every supplied instance ID**, with no invented, duplicated or missing IDs. Model output cannot overwrite detector labels or coordinates.
- Up to **32 instances** may be submitted per request. Above that limit the app asks you to raise the confidence threshold or narrow the view; it does not silently truncate. The on-screen detection list shows six entries and the ranking shows five; JSON retains all instances. No detections means no API request.

## Scoring and winner display

Each dimension is rated 0–10:

- **Salience:** how distinctly the source stands out visually, independent of task importance.
- **Expectancy:** anticipated information change based on visible state and cooking context. With one image this is an estimate, not measured motion.
- **Value:** importance to cooking garlic pork chops and consequences of missing the source's information.

The three Inspector weights default to **1, 1, 1**. Unity calculates:

```text
Attention score = 10 × (wS × S + wE × E + wV × V) / (wS + wE + wV)
```

Totals range from 0–100 and are sorted before rounding; exact ties use the instance ID to select a single winner deterministically. Valid weight edits during Play re-score the existing response without another upload. Negative/all-zero weights are rejected.

The world-space rectangle uses the winner's latest depth estimate and cached camera pose. It is a flat rectangle at the estimated surface depth, not a reconstructed 3D object. It is hidden immediately if the winning ID is absent from the latest detections, depth is missing, the frame is stale, tracking is lost, or the score expires (default **Score Lifetime: 30 seconds**). A lower-ranked or same-class object is never substituted. The old snapshot remains visible as the record of what was scored. Press A to obtain a new assessment. Pause, recenter, camera restart and disabling the detector invalidate pending associations/results.

The detector defaults to GPUCompute, with CPU fallback if compute shaders are unavailable. **Detection Interval** is a minimum gap after inference, not a guaranteed frame rate. **Max Frame Age** defaults to 1.5 seconds. Actual speed, depth quality, pose alignment and thermals must be measured on the target headset; CPU can be selected in the detector Inspector for comparison.

## JSON and saved records

OpenAI returns only the frame ID, summary and instance scores:

```json
{
  "frame_id": "frame_00000042",
  "summary": "A cup and knife are visible on the counter.",
  "objects": [
    { "object_id": "obj_000007", "description": "A static cup with little immediate task relevance.", "salience": 6, "expectancy": 1, "value": 2 },
    { "object_id": "obj_000008", "description": "A knife relevant to ingredient preparation.", "salience": 5, "expectancy": 3, "value": 8 }
  ]
}
```

Unity merges scores with the trusted capture metadata. Each enriched object contains `rank`, `object_id`, `name`, `class_id`, `detection_confidence`, `bbox`, `world_position_m` (nullable), `depth_sample_realtime_s` (nullable), `description`, `salience`, `expectancy`, `value`, and the locally calculated `attention_score`. Top-level data includes capture frame/space IDs, camera pose, weights and score scales.

- `LastDetectionsJson`: latest local detection frame, updated without an API request.
- `LastRecognitionJson`: validated, sorted, enriched attention result for the submitted snapshot.
- **On Recognition Json**: emits that result after success and valid reweighting.
- **Save Capture Records** (default enabled): writes `<space_id>_<frame_id>.json` under `Application.persistentDataPath/CameraViewerAttention/`. It first records captured detections, then updates the same record with scored results. `LastCaptureRecordPath` exposes the latest saved path. Images and keys are not written to these records. Disable this option to keep results only in memory. Records are not auto-deleted.

## Verification and limitations

Run the offline protocol/tracking tests from the repository root:

```sh
python3 Tests/CameraViewerVision/run_tests.py
```

They use Unity's bundled Mono compiler/runtime and the cached Newtonsoft.Json package, and make no API requests. `--unity-contents /path/to/Unity.app/Contents` selects another installation. Tests cover frame/ID matching, metadata preservation, all-instance scoring, rejected responses, weighted winner selection, class-aware NMS, one-to-one ID matching, expired/recentered tracks and suppression of stale/unlocated winner boxes.

On Quest, verify two same-class objects receive different IDs; head motion keeps a stationary object's world box aligned; the uploaded screenshot and normalized boxes match; the response scores every ID; only the winner gets a world box; moving/removing it does not highlight a different object; denied spatial permission keeps positions null; pause/recenter/camera interruption cancels stale work; and capture records contain the corresponding frame and scores. These runtime and live-API checks require hardware and credentials and are not replaced by compilation or offline tests.

This direct OpenAI integration is for local development: a Resources key is included in the APK even though Git ignores it. Use an authenticated backend before distributing builds. Requests set `store: false`, without claiming zero provider retention. The sample YOLO model remains at its original path with the repository's [MIT license marker](../../../README.md#license).

References: [OpenAI image inputs](https://developers.openai.com/api/docs/guides/images-vision), [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses), [Meta camera-to-world mapping](https://developers.meta.com/vr/documentation/unity/unity-pca-documentation/).
