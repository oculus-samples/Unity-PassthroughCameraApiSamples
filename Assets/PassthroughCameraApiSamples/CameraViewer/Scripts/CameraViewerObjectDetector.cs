// Copyright (c) Meta Platforms, Inc. and affiliates.
// Uses the repository's YOLOv9 sample model (MIT; see MultiObjectDetection/SentisInference/Model).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;

namespace PassthroughCameraSamples.CameraViewer
{
    public sealed class CameraViewerObjectDetector : MonoBehaviour
    {
        [SerializeField] private ModelAsset m_sentisModel;
        [SerializeField] private TextAsset m_labelsAsset;
        [SerializeField] private BackendType m_backend = BackendType.GPUCompute;
        [SerializeField, Range(0, 1)] private float m_confidenceThreshold = 0.3f;
        [SerializeField, Range(0, 1)] private float m_iouThreshold = 0.4f;
        [SerializeField, Min(0.05f)] private float m_detectionInterval = 0.2f;
        [SerializeField, Min(0.1f)] private float m_maxFrameAge = 1.5f;
        [SerializeField, Min(0.1f)] private float m_maxDepthDelay = 1f;
        [SerializeField, Min(1)] private float m_scoreLifetime = 30f;

        public CameraDetectionFrame LatestFrame { get; private set; }
        public Texture LatestTexture => m_publishedTexture;
        public string Status { get; private set; } = "Starting detector...";
        public string HighlightStatus { get; private set; } = "Press A to score detected objects.";
        public string SpaceId { get; private set; } = Guid.NewGuid().ToString("N");
        public bool HasFreshFrame => LatestFrame != null &&
            Time.realtimeSinceStartupAsDouble - LatestFrame.CaptureRealtime <= m_maxFrameAge;
        public event Action FrameSpaceInvalidated;

        private PassthroughCameraAccess m_camera;
        private Worker m_worker;
        private Tensor<float> m_input;
        private int m_inputWidth, m_inputHeight;
        private string[] m_labels;
        private EnvironmentRaycastManager m_raycast;
        private RenderTexture m_workTexture, m_publishedTexture;
        private readonly CameraObjectTracker m_tracker = new CameraObjectTracker();
        private Pose m_previousPose;
        private Pose m_publishedPose;
        private bool m_hasPreviousPose;
        private bool m_running;
        private bool m_trackingLost;
        private bool m_suspended;
        private long m_nextFrame;
        private string m_winnerId, m_winnerSpace;
        private double m_winnerScore, m_scoreTime;
        private RectTransform m_worldBox;
        private Text m_worldLabel;
        private Font m_font;
        private bool m_eventsRegistered;

        public void Initialize(PassthroughCameraAccess cameraAccess, Font font)
        {
            m_camera = cameraAccess;
            m_font = font;
            if (!m_eventsRegistered)
            {
                OVRManager.TrackingLost += TrackingLost;
                OVRManager.TrackingAcquired += TrackingAcquired;
                OVRManager.display.RecenteredPose += InvalidateSpace;
                m_eventsRegistered = true;
            }
            // Also works when CameraViewer is the only build scene (no StartScene permission flow).
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene,
                OVRPermissionsRequester.Permission.PassthroughCameraAccess });
            if (!m_running && isActiveAndEnabled) StartCoroutine(DetectionLoop());
        }

        private void OnEnable()
        {
            if (m_camera && !m_running) StartCoroutine(DetectionLoop());
        }

        private void TrackingLost() { m_trackingLost = true; InvalidateSpace(); }
        private void TrackingAcquired() { m_trackingLost = false; }

        private void InvalidateSpace()
        {
            SpaceId = Guid.NewGuid().ToString("N");
            LatestFrame = null;
            m_hasPreviousPose = false;
            m_tracker.Clear();
            ClearWinner();
            FrameSpaceInvalidated?.Invoke();
        }

        private IEnumerator DetectionLoop()
        {
            m_running = true;
            var operation = DetectContinuously();
            try
            {
                while (true)
                {
                    bool more;
                    try { more = operation.MoveNext(); }
                    catch (Exception error)
                    {
                        Status = "Detector failed. Check model/backend in Inspector; re-enable to retry.";
                        Debug.LogError("CameraViewer detector: " + error.GetType().Name, this);
                        InvalidateSpace();
                        yield break;
                    }
                    if (!more) break;
                    yield return operation.Current;
                }
            }
            finally
            {
                (operation as IDisposable)?.Dispose();
                ReleaseInference();
                m_running = false;
            }
        }

        private IEnumerator DetectContinuously()
        {
            if (!m_sentisModel || !m_labelsAsset)
                throw new InvalidOperationException("Assign the sample detector model and labels.");
            var model = ModelLoader.Load(m_sentisModel);
            m_inputHeight = model.inputs[0].shape.Get(2);
            m_inputWidth = model.inputs[0].shape.Get(3);
            var backend = m_backend == BackendType.GPUCompute && !SystemInfo.supportsComputeShaders
                ? BackendType.CPU : m_backend;
            m_worker = new Worker(model, backend);
            m_input = new Tensor<float>(new TensorShape(1, 3, m_inputHeight, m_inputWidth));
            m_labels = m_labelsAsset.text.Split('\n').Select(label => label.Trim()).ToArray();
            double nextCapture = 0;
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                if (m_suspended || m_trackingLost || !m_camera.IsPlaying)
                {
                    if (LatestFrame != null) InvalidateSpace();
                    Status = "Waiting for camera / headset tracking...";
                    yield return null;
                    continue;
                }
                if (Time.realtimeSinceStartupAsDouble < nextCapture) { yield return null; continue; }
                // MRUK writes the camera texture in the render thread; freeze after rendering.
                yield return endOfFrame;
                if (m_suspended || m_trackingLost || !m_camera.IsPlaying) continue;
                var source = m_camera.GetTexture();
                if (!source) continue;
                EnsureTextures(source.width, source.height);
                var pose = m_camera.GetCameraPose();
                if (!IsFinite(pose.position) || !float.IsFinite(pose.rotation.x) || !float.IsFinite(pose.rotation.y) ||
                    !float.IsFinite(pose.rotation.z) || !float.IsFinite(pose.rotation.w) ||
                    Quaternion.Dot(pose.rotation, pose.rotation) < 0.9f)
                {
                    Status = "Waiting for a valid capture pose...";
                    continue;
                }
                var stamp = m_camera.Timestamp;
                var captureTime = Time.realtimeSinceStartupAsDouble;
                var space = SpaceId;
                var previousTarget = RenderTexture.active;
                try { Graphics.Blit(source, m_workTexture); }
                finally { RenderTexture.active = previousTarget; }
                var frame = new CameraDetectionFrame
                {
                    FrameId = "frame_" + (++m_nextFrame).ToString("D8"), SpaceId = space,
                    CapturedAtUtc = stamp.ToUniversalTime().ToString("O"), CaptureRealtime = captureTime,
                    Width = source.width, Height = source.height,
                    CameraPosition = Coordinates(pose.position),
                    CameraRotation = new double[] { pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w }
                };
                TextureConverter.ToTensor(m_workTexture, m_input, new TextureTransform().SetCoordOrigin(CoordOrigin.TopLeft));
                m_worker.Schedule(m_input);
                var outputBoxes = m_worker.PeekOutput(0) as Tensor<float>;
                var outputClasses = m_worker.PeekOutput(1) as Tensor<int>;
                var outputScores = m_worker.PeekOutput(2) as Tensor<float>;
                if (outputBoxes == null || outputClasses == null || outputScores == null)
                    throw new InvalidOperationException("Expected sample model's boxes/classes/scores outputs.");
                outputBoxes.ReadbackRequest(); outputClasses.ReadbackRequest(); outputScores.ReadbackRequest();
                while ((outputBoxes.count > 0 && !outputBoxes.IsReadbackRequestDone()) ||
                       (outputClasses.count > 0 && !outputClasses.IsReadbackRequestDone()) ||
                       (outputScores.count > 0 && !outputScores.IsReadbackRequestDone()))
                {
                    if (Time.realtimeSinceStartupAsDouble - captureTime > 15)
                        throw new TimeoutException("Detector readback timed out.");
                    yield return null;
                }
                using var boxes = outputBoxes.ReadbackAndClone();
                using var classes = outputClasses.ReadbackAndClone();
                using var scores = outputScores.ReadbackAndClone();
                if (space != SpaceId || m_suspended || m_trackingLost || !m_camera.IsPlaying) continue;
                var candidates = new List<CameraDetectedObject>();
                var count = Math.Min(boxes.shape[0], Math.Min(classes.count, scores.count));
                for (var i = 0; i < count; i++)
                {
                    var score = scores[i];
                    var classId = classes[i];
                    if (!float.IsFinite(score) || score < m_confidenceThreshold || score > 1 || classId < 0 || classId >= m_labels.Length)
                        continue;
                    var raw = new[] { boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3] };
                    if (raw.Any(value => !float.IsFinite(value))) continue;
                    var bounds = new double[] { Mathf.Clamp01(raw[0] / m_inputWidth), Mathf.Clamp01(raw[1] / m_inputHeight),
                        Mathf.Clamp01(raw[2] / m_inputWidth), Mathf.Clamp01(raw[3] / m_inputHeight) };
                    if (bounds[2] <= bounds[0] || bounds[3] <= bounds[1] || string.IsNullOrEmpty(m_labels[classId])) continue;
                    candidates.Add(new CameraDetectedObject { Name = m_labels[classId], ClassId = classId,
                        Confidence = score, Bbox = bounds });
                }
                var detections = CameraObjectTracker.SuppressOverlaps(candidates, m_iouThreshold);
                EnsureRaycaster();
                // Depth API is sampled after inference, not at exactly the RGB timestamp. Record this
                // separately, reject slow frames and don't fabricate positions when depth is unavailable.
                if (Time.realtimeSinceStartupAsDouble - captureTime <= m_maxDepthDelay && m_raycast &&
                    m_camera.CurrentResolution.x == frame.Width && m_camera.CurrentResolution.y == frame.Height)
                {
                    foreach (var item in detections)
                    {
                        var ray = m_camera.ViewportPointToRay(CenterViewport(item), pose);
                        if (m_raycast.Raycast(ray, out var hit, 10f) && IsFinite(hit.point))
                        {
                            item.WorldPosition = Coordinates(hit.point);
                            item.DepthSampleRealtime = Time.realtimeSinceStartupAsDouble;
                        }
                    }
                }
                var stableView = m_hasPreviousPose && Vector3.Distance(pose.position, m_previousPose.position) < 0.05f &&
                    Quaternion.Angle(pose.rotation, m_previousPose.rotation) < 8f;
                m_tracker.Update(detections, captureTime, stableView);
                m_previousPose = pose;
                m_hasPreviousPose = true;
                frame.Objects = detections.ToArray();
                // Only publish a complete frame. The next inference uses the other render target.
                var swap = m_publishedTexture;
                m_publishedTexture = m_workTexture;
                m_workTexture = swap;
                m_publishedPose = pose;
                LatestFrame = frame;
                var located = detections.Count(item => item.WorldPosition != null);
                Status = $"Detected {detections.Count} | 3D located {located}/{detections.Count}";
                nextCapture = Time.realtimeSinceStartupAsDouble + m_detectionInterval;
            }
        }

        private void EnsureRaycaster()
        {
            if (m_raycast || !OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene) ||
                !EnvironmentRaycastManager.IsSupported) return;
            m_raycast = FindAnyObjectByType<EnvironmentRaycastManager>();
            if (!m_raycast)
            {
                var go = new GameObject("CameraViewerEnvironmentRaycast");
                go.transform.SetParent(transform, false);
                m_raycast = go.AddComponent<EnvironmentRaycastManager>();
            }
        }

        private void EnsureTextures(int width, int height)
        {
            if (m_workTexture && m_workTexture.width == width && m_workTexture.height == height) return;
            ReleaseTextures();
            InvalidateSpace();
            m_workTexture = NewTexture(width, height);
            m_publishedTexture = NewTexture(width, height);
        }

        private static RenderTexture NewTexture(int width, int height)
        {
            var texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            texture.Create();
            return texture;
        }

        public void SetWinner(string objectId, double score, string spaceId, double assessedAt)
        {
            // Reweighting may change the winner but must not renew an old assessment.
            m_scoreTime = assessedAt;
            m_winnerId = objectId; m_winnerScore = score; m_winnerSpace = spaceId;
        }

        public void ClearWinner()
        {
            m_winnerId = null;
            if (m_worldBox) m_worldBox.gameObject.SetActive(false);
            HighlightStatus = "Press A to score detected objects.";
        }

        private void LateUpdate()
        {
            if (m_worldBox) m_worldBox.gameObject.SetActive(false);
            if (string.IsNullOrEmpty(m_winnerId)) return;
            HighlightStatus = "Highest-score object not currently located; box hidden.";
            if (!HasFreshFrame || m_suspended || m_trackingLost || !m_camera.IsPlaying || m_winnerSpace != SpaceId) return;
            if (Time.realtimeSinceStartupAsDouble - m_scoreTime > m_scoreLifetime)
            {
                HighlightStatus = "Attention score expired. Press A to rescore.";
                return;
            }
            var item = CameraHighlightPolicy.FindWinner(LatestFrame, m_winnerId, m_winnerSpace,
                Time.realtimeSinceStartupAsDouble, m_scoreTime, m_maxFrameAge, m_scoreLifetime);
            if (item?.WorldPosition == null) return; // Never substitute a lower-ranked object.
            var center = new Vector3((float)item.WorldPosition[0], (float)item.WorldPosition[1], (float)item.WorldPosition[2]);
            if (Vector3.Dot(center - m_publishedPose.position, m_publishedPose.forward) <= 0) return;
            var plane = new Plane(m_publishedPose.forward, center);
            var a = m_camera.ViewportPointToRay(new Vector2((float)item.Bbox[0], 1 - (float)item.Bbox[3]), m_publishedPose);
            var b = m_camera.ViewportPointToRay(new Vector2((float)item.Bbox[2], 1 - (float)item.Bbox[1]), m_publishedPose);
            if (!plane.Raycast(a, out var da) || !plane.Raycast(b, out var db)) return;
            var bottomLeft = a.GetPoint(da); var topRight = b.GetPoint(db);
            var size = Quaternion.Inverse(m_publishedPose.rotation) * (topRight - bottomLeft);
            if (!IsFinite(size) || size.x <= 0 || size.y <= 0) return;
            if (!m_worldBox) CreateWorldBox();
            m_worldBox.SetPositionAndRotation((bottomLeft + topRight) * 0.5f, m_publishedPose.rotation);
            m_worldBox.sizeDelta = new Vector2(size.x, size.y) * 1000;
            m_worldLabel.text = $"{item.Id} | {item.Name} | {m_winnerScore:F1}";
            m_worldBox.gameObject.SetActive(true);
            HighlightStatus = $"Highlight: {item.Id} {item.Name} ({m_winnerScore:F1})";
        }

        private void CreateWorldBox()
        {
            var go = new GameObject("HighestAttentionWorldBox", typeof(RectTransform), typeof(Canvas));
            go.layer = 5;
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            m_worldBox = (RectTransform)go.transform;
            m_worldBox.localScale = Vector3.one * 0.001f;
            for (var i = 0; i < 4; i++)
            {
                var edge = new GameObject("Edge" + i, typeof(RectTransform), typeof(Image));
                edge.layer = 5; edge.transform.SetParent(go.transform, false);
                var image = edge.GetComponent<Image>(); image.color = Color.yellow; image.raycastTarget = false;
                var rect = (RectTransform)edge.transform;
                rect.anchorMin = i == 0 || i == 2 ? Vector2.zero : i == 1 ? Vector2.up : Vector2.right;
                rect.anchorMax = i < 2 ? new Vector2(1, rect.anchorMin.y) : new Vector2(rect.anchorMin.x, 1);
                rect.sizeDelta = i < 2 ? new Vector2(0, 3) : new Vector2(3, 0);
                rect.anchoredPosition = Vector2.zero;
            }
            var labelGo = new GameObject("WinnerLabel", typeof(RectTransform), typeof(Text));
            labelGo.layer = 5; labelGo.transform.SetParent(go.transform, false);
            m_worldLabel = labelGo.GetComponent<Text>(); m_worldLabel.font = m_font;
            m_worldLabel.fontSize = 22; m_worldLabel.resizeTextForBestFit = true;
            m_worldLabel.resizeTextMinSize = 10; m_worldLabel.resizeTextMaxSize = 22;
            m_worldLabel.color = Color.yellow; m_worldLabel.supportRichText = false; m_worldLabel.raycastTarget = false;
            m_worldLabel.alignment = TextAnchor.MiddleCenter;
            var labelRect = m_worldLabel.rectTransform;
            labelRect.anchorMin = new Vector2(0, 1); labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(0.5f, 0); labelRect.sizeDelta = new Vector2(0, 45);
            labelRect.anchoredPosition = new Vector2(0, 5);
        }

        private void OnApplicationPause(bool paused)
        {
            m_suspended = paused;
            if (paused) InvalidateSpace();
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            ReleaseInference();
            ReleaseTextures();
            m_running = false;
            InvalidateSpace();
        }

        private void OnDestroy()
        {
            if (m_eventsRegistered)
            {
                OVRManager.TrackingLost -= TrackingLost; OVRManager.TrackingAcquired -= TrackingAcquired;
                OVRManager.display.RecenteredPose -= InvalidateSpace;
            }
            if (m_worldBox) Destroy(m_worldBox.gameObject);
        }

        private void ReleaseInference()
        {
            if (m_worker != null)
            {
                for (var i = 0; i < 3; i++) m_worker.PeekOutput(i)?.CompleteAllPendingOperations();
                m_worker.Dispose(); m_worker = null;
            }
            m_input?.Dispose(); m_input = null;
        }

        private void ReleaseTextures()
        {
            if (m_workTexture) { m_workTexture.Release(); Destroy(m_workTexture); }
            if (m_publishedTexture) { m_publishedTexture.Release(); Destroy(m_publishedTexture); }
            m_workTexture = m_publishedTexture = null;
            LatestFrame = null;
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static double[] Coordinates(Vector3 v) => new double[] { v.x, v.y, v.z };
        private static Vector2 CenterViewport(CameraDetectedObject item) =>
            new Vector2((float)((item.Bbox[0] + item.Bbox[2]) / 2), 1 - (float)((item.Bbox[1] + item.Bbox[3]) / 2));
    }
}
