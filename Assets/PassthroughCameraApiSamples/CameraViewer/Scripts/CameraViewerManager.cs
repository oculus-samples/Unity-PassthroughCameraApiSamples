// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Meta.XR;
using Meta.XR.Samples;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace PassthroughCameraSamples.CameraViewer
{
    [MetaCodeSample("PassthroughCameraApiSamples-CameraViewer")]
    public class CameraViewerManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private Text m_debugText;
        [SerializeField] private RawImage m_image;
        [SerializeField] private CameraViewerObjectDetector m_detector;

        [Header("OpenAI object recognition")]
        [SerializeField] private string m_model = "gpt-4.1-mini";
        [Tooltip("Use a font containing the requested language's glyphs when changing this setting.")]
        [SerializeField] private string m_resultLanguage = "English";
        [SerializeField] private Font m_resultFont;
        [SerializeField, Range(256, 2048)] private int m_maxImageDimension = 1024;
        [SerializeField, Range(50, 95)] private int m_jpegQuality = 80;
        [SerializeField, Range(10, 120)] private int m_requestTimeoutSeconds = 60;
        [Tooltip("Save captured detection metadata and scored results as JSON in the app's persistent data folder.")]
        [SerializeField] private bool m_saveCaptureRecords = true;
        [Header("Attention weights - cooking garlic pork chops")]
        [SerializeField, Min(0)] private float m_salienceWeight = 1;
        [SerializeField, Min(0)] private float m_expectancyWeight = 1;
        [SerializeField, Min(0)] private float m_valueWeight = 1;
        [Tooltip("Receives ranked JSON with dimension scores, normalized weights and locally calculated totals.")]
        [SerializeField] private UnityEvent<string> m_onRecognitionJson = new UnityEvent<string>();

        public bool IsRecognizing { get; private set; }
        public string LastRecognitionJson { get; private set; }
        public string LastDetectionsJson { get; private set; }
        public string LastCaptureRecordPath { get; private set; }
        private CameraDetectionFrame m_scoredFrame;
        private double m_resultReceivedTime;
        private string m_displayedFrameId;
        private Text m_detectionText;
        private RectTransform m_snapshotWinnerBox;

        private string m_apiKey;
        private string m_status = "Waiting for camera...";
        private bool m_showJson;
        private int m_jsonPage;
        private Vector3 m_observedWeights;
        private OpenAIObjectRecognition.AttentionWeights m_requestWeights;
        private readonly GameObject[] m_scoreRows = new GameObject[OpenAIObjectRecognition.DisplayObjectLimit];
        private readonly Text[] m_scoreLabels = new Text[OpenAIObjectRecognition.DisplayObjectLimit];
        private readonly Text[] m_totalLabels = new Text[OpenAIObjectRecognition.DisplayObjectLimit];
        private readonly Text[] m_dimensionLabels = new Text[OpenAIObjectRecognition.DisplayObjectLimit];
        private readonly RectTransform[] m_scoreBars = new RectTransform[OpenAIObjectRecognition.DisplayObjectLimit];
        private Text m_weightsText;
        private bool m_initialized;
        private Text m_resultText;
        private Text m_resultHeading;
        private RawImage m_snapshotImage;
        private Texture2D m_snapshot;
        private RenderTexture m_captureTarget;
        private AsyncGPUReadbackRequest m_readback;
        private bool m_readbackPending;
        private UnityWebRequest m_request;
        private OpenAIObjectRecognition.Result m_result;

        private void Start()
        {
            if (!m_cameraAccess || !m_debugText || !m_image || !m_detector)
            {
                Debug.LogError("CameraViewer requires Camera Access, Debug Text, Image and Detector references.", this);
                enabled = false;
                return;
            }
            CreateRecognitionPanel();
            m_initialized = true;
            m_detector.FrameSpaceInvalidated += OnFrameSpaceInvalidated;
            m_detector.Initialize(m_cameraAccess, m_resultFont ? m_resultFont : m_debugText.font);
            m_status = "Ready. A / index pinch: send a frame to OpenAI. B / middle pinch: cycle JSON pages / ranking.";
        }

        private void Update()
        {
            if (!m_initialized)
                return;
            var weights = new Vector3(m_salienceWeight, m_expectancyWeight, m_valueWeight);
            if (m_result != null && weights != m_observedWeights)
            {
                m_observedWeights = weights;
                try
                {
                    m_result.ApplyWeights(ReadWeights());
                    m_status = "Weights updated. A: capture again. B / middle pinch: cycle JSON pages / ranking.";
                    PublishResult();
                }
                catch (ArgumentException)
                {
                    m_status = "Weights must be nonnegative with at least one positive value.";
                }
            }

            // Refresh after camera restart/resume as the texture can be recreated by MRUK.
            if (m_cameraAccess.IsPlaying)
            {
                m_image.texture = m_cameraAccess.GetTexture();
                var resolution = m_cameraAccess.CurrentResolution;
                if (resolution.x > 0 && resolution.y > 0)
                {
                    var scale = Mathf.Min(740f / resolution.x, 555f / resolution.y);
                    m_image.rectTransform.sizeDelta = new Vector2(resolution.x, resolution.y) * scale;
                }
            }

            if (InputManager.IsButtonADownOrPinchStarted())
                RecognizeCurrentFrame();
            if (InputManager.IsButtonBDownOrMiddleFingerPinchStarted())
                ToggleJson();
#if UNITY_EDITOR
            if (Input.GetKeyDown(KeyCode.Space))
                RecognizeCurrentFrame();
            if (Input.GetKeyDown(KeyCode.J))
                ToggleJson();
#endif
            var cameraStatus = OVRPermissionsRequester.IsPermissionGranted(
                OVRPermissionsRequester.Permission.PassthroughCameraAccess)
                ? (m_cameraAccess.IsPlaying ? "Camera ready" : "Waiting for camera")
                : "Camera permission required";
            if (m_detector.LatestFrame != null && m_detector.LatestFrame.FrameId != m_displayedFrameId)
            {
                var frame = m_detector.LatestFrame;
                m_displayedFrameId = frame.FrameId;
                LastDetectionsJson = frame.ToJson().ToString();
                var lines = new StringBuilder("Live detections (IDs / position in meters)\n");
                foreach (var item in frame.Objects.Take(6))
                {
                    lines.Append(item.Id).Append(" ").Append(item.Name).Append(" | ");
                    if (item.WorldPosition == null) lines.Append("3D unavailable");
                    else lines.AppendFormat(CultureInfo.InvariantCulture, "({0:F2}, {1:F2}, {2:F2})",
                        item.WorldPosition[0], item.WorldPosition[1], item.WorldPosition[2]);
                    lines.Append('\n');
                }
                if (frame.Objects.Length > 6) lines.Append($"+ {frame.Objects.Length - 6} more; all included when scoring.");
                if (frame.Objects.Length == 0) lines.Append("No objects above detection threshold.");
                m_detectionText.text = lines.ToString();
            }
            if (!m_detector.HasFreshFrame) m_detectionText.text = m_detector.Status + "\nWaiting for a fresh detection frame...";
            m_debugText.text = cameraStatus + " | " + m_detector.Status + "\n" + m_status + "\n" + m_detector.HighlightStatus;
        }

        /// <summary>Optional runtime credential injection; never logged or saved.</summary>
        public void SetApiKey(string apiKey) => m_apiKey = apiKey?.Trim();

        /// <summary>Can also be wired to a Unity UI button. One request at a time; no automatic uploads.</summary>
        public void RecognizeCurrentFrame()
        {
            if (!isActiveAndEnabled || !m_initialized || IsRecognizing)
                return;
            if (!m_cameraAccess.IsPlaying)
            {
                m_status = "Wait for camera permission and a camera frame, then press A again.";
                return;
            }
            if (!m_detector.HasFreshFrame || !m_detector.LatestTexture)
            {
                m_status = "Wait for a fresh detector frame, then press A.";
                return;
            }
            if (m_detector.LatestFrame.Objects.Length == 0 || m_detector.LatestFrame.Objects.Length > OpenAIObjectRecognition.MaxObjects)
            {
                m_detector.ClearWinner();
                m_result = null;
                LastRecognitionJson = null;
                ResetResultLayout();
                m_status = m_detector.LatestFrame.Objects.Length == 0 ? "No detected objects to score." :
                    "More than 32 detections. Raise the confidence threshold or narrow the view; nothing uploaded.";
                m_resultText.text = m_status;
                return;
            }
            if (!TryLoadApiKey())
                return;
            if (string.IsNullOrWhiteSpace(m_model))
            {
                m_status = "Set Model in the CameraViewer inspector before capturing.";
                return;
            }
            try { m_requestWeights = ReadWeights(); }
            catch (ArgumentException)
            {
                m_status = "Weights must be nonnegative with at least one positive value.";
                return;
            }
            m_observedWeights = new Vector3(m_salienceWeight, m_expectancyWeight, m_valueWeight);

            IsRecognizing = true;
            m_result = null;
            m_scoredFrame = null;
            LastRecognitionJson = null;
            m_detector.ClearWinner();
            ResetResultLayout();
            m_resultText.text = "Capturing image...";
            m_resultHeading.text = "Analyzing captured frame";
            m_status = "Capturing...";
            StartCoroutine(RunRecognition());
        }

        private bool TryLoadApiKey()
        {
            if (!string.IsNullOrWhiteSpace(m_apiKey))
                return true;
            m_apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim();
            if (!string.IsNullOrWhiteSpace(m_apiKey))
                return true;
            try
            {
                var config = Resources.Load<TextAsset>("OpenAI.local");
                if (config)
                    m_apiKey = ((string)JObject.Parse(config.text)["apiKey"])?.Trim();
            }
            catch (Exception)
            {
                m_status = "Invalid OpenAI.local.json. Expected a JSON object with an apiKey string.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(m_apiKey))
                return true;
            m_status = "Set apiKey in Assets/Resources/OpenAI.local.json, rebuild, then press A.";
            return false;
        }

        // Advance the iterator ourselves so capture/encoding/network exceptions all release resources
        // and unlock the trigger. No exception text or API response body is written to logs.
        private IEnumerator RunRecognition()
        {
            var operation = CaptureAndSend();
            try
            {
                while (true)
                {
                    bool more;
                    try
                    {
                        more = operation.MoveNext();
                    }
                    catch (Exception)
                    {
                        ShowFailure("Could not capture or process the image. Press A to retry.");
                        yield break;
                    }
                    if (!more)
                        break;
                    yield return operation.Current;
                }
            }
            finally
            {
                (operation as IDisposable)?.Dispose();
                ReleaseRequestResources();
                IsRecognizing = false;
            }
        }

        private IEnumerator CaptureAndSend()
        {
            if (!m_detector.HasFreshFrame) { ShowFailure("Detection frame expired. Press A to retry."); yield break; }
            // Copy metadata and blit its published texture before yielding; detector double buffering
            // prevents the uploaded image from being taken from a different detection cycle.
            m_scoredFrame = m_detector.LatestFrame.Copy();
            var source = m_detector.LatestTexture;
            if (!source || source.width <= 0 || source.height <= 0)
            {
                ShowFailure("Camera frame unavailable. Press A to retry.");
                yield break;
            }

            DestroySnapshot();
            var scale = Mathf.Min(1f, Mathf.Clamp(m_maxImageDimension, 256, 2048) /
                                      (float)Mathf.Max(source.width, source.height));
            var width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            m_captureTarget = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            // Freeze the most recent available texture before starting the readback.
            var previousTarget = RenderTexture.active;
            try { Graphics.Blit(source, m_captureTarget); }
            finally { RenderTexture.active = previousTarget; }
            m_snapshot = new Texture2D(width, height, TextureFormat.RGBA32, false);

            if (SystemInfo.supportsAsyncGPUReadback)
            {
                m_readback = AsyncGPUReadback.Request(m_captureTarget, 0, TextureFormat.RGBA32);
                m_readbackPending = true;
                while (!m_readback.done)
                    yield return null;
                m_readbackPending = false;
                if (m_readback.hasError)
                {
                    ShowFailure("Camera readback failed. Press A to retry.");
                    yield break;
                }
                m_snapshot.LoadRawTextureData(m_readback.GetData<byte>());
                m_snapshot.Apply(false);
            }
            else
            {
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = m_captureTarget;
                    m_snapshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    m_snapshot.Apply(false);
                }
                finally { RenderTexture.active = previous; }
            }
            RenderTexture.ReleaseTemporary(m_captureTarget);
            m_captureTarget = null;
            m_snapshotImage.texture = m_snapshot;
            m_snapshotImage.enabled = true;
            var previewScale = Mathf.Min(440f / width, 200f / height);
            m_snapshotImage.rectTransform.sizeDelta = new Vector2(width, height) * previewScale;

            var jpeg = m_snapshot.EncodeToJPG(Mathf.Clamp(m_jpegQuality, 50, 95));
            var payload = OpenAIObjectRecognition.CreateRequest(jpeg, m_model, m_resultLanguage, m_scoredFrame);
            SaveCaptureRecord("captured");
            m_request = new UnityWebRequest(OpenAIObjectRecognition.Endpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Clamp(m_requestTimeoutSeconds, 10, 120),
                redirectLimit = 0
            };
            m_request.SetRequestHeader("Content-Type", "application/json");
            m_request.SetRequestHeader("Authorization", "Bearer " + m_apiKey);
            m_status = "Waiting for OpenAI...";
            m_resultText.text = "Scoring Salience, Expectancy and Value...";
            yield return m_request.SendWebRequest();

            if (m_request.result != UnityWebRequest.Result.Success)
            {
                // Allow a changed local config to be picked up on the next attempt after a bad key.
                if (m_request.responseCode == 401)
                    m_apiKey = null;
                ShowFailure(OpenAIObjectRecognition.HttpError(m_request.responseCode));
                yield break;
            }
            if (m_scoredFrame.SpaceId != m_detector.SpaceId)
            {
                ShowFailure("Tracking origin changed. Capture and score a new frame.");
                yield break;
            }
            try
            {
                m_result = OpenAIObjectRecognition.ParseResponse(m_request.downloadHandler.text, m_scoredFrame, m_requestWeights);
                m_resultReceivedTime = Time.realtimeSinceStartupAsDouble;
            }
            catch (FormatException error)
            {
                ShowFailure(error.Message);
                yield break;
            }
            catch (Exception)
            {
                ShowFailure("OpenAI returned invalid recognition JSON. Press A to retry.");
                yield break;
            }
            m_status = "Done. A / index pinch: capture again. B / middle pinch: cycle JSON pages / ranking.";
            PublishResult();
        }

        private OpenAIObjectRecognition.AttentionWeights ReadWeights() =>
            new OpenAIObjectRecognition.AttentionWeights(m_salienceWeight, m_expectancyWeight, m_valueWeight);

        private void PublishResult()
        {
            LastRecognitionJson = m_result.Json;
            SaveCaptureRecord("scored");
            RefreshResult();
            if (m_result.Objects.Length > 0)
            {
                var winner = m_result.Objects[0];
                m_detector.SetWinner(winner.ObjectId, winner.AttentionScore, m_result.Frame.SpaceId, m_resultReceivedTime);
                ShowSnapshotWinner(winner.Detection.Bbox);
            }
            else m_detector.ClearWinner();
            try
            {
                m_onRecognitionJson.Invoke(LastRecognitionJson);
            }
            catch (Exception)
            {
                Debug.LogError("A CameraViewer recognition event listener failed.", this);
            }
        }

        private void SaveCaptureRecord(string status)
        {
            if (!m_saveCaptureRecords || m_scoredFrame == null) return;
            try
            {
                var directory = Path.Combine(Application.persistentDataPath, "CameraViewerAttention");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, m_scoredFrame.SpaceId + "_" + m_scoredFrame.FrameId + ".json");
                var record = new JObject
                {
                    ["status"] = status,
                    ["detections"] = m_scoredFrame.ToJson(),
                    ["attention"] = m_result == null ? JValue.CreateNull() : (JToken)JObject.Parse(m_result.Json)
                };
                File.WriteAllText(path, record.ToString());
                LastCaptureRecordPath = path;
            }
            catch (IOException) { Debug.LogWarning("CameraViewer could not save the capture record.", this); }
            catch (UnauthorizedAccessException) { Debug.LogWarning("CameraViewer capture record folder is not writable.", this); }
        }

        public void ToggleJson()
        {
            if (m_result == null)
                return;
            if (!m_showJson)
            {
                m_showJson = true;
                m_jsonPage = 0;
            }
            else if (++m_jsonPage > m_result.Objects.Length)
                m_showJson = false;
            RefreshResult();
        }

        private void RefreshResult()
        {
            if (m_result == null)
                return;
            for (var i = 0; i < m_scoreRows.Length; i++)
                m_scoreRows[i].SetActive(!m_showJson && i < m_result.Objects.Length);
            var weights = m_result.Weights;
            m_weightsText.text = string.Format(CultureInfo.InvariantCulture,
                "Weights S {0:P0} / E {1:P0} / V {2:P0}\nDimensions: 0-10 | Total: 0-100", weights.Salience, weights.Expectancy, weights.Value);
            if (m_showJson)
            {
                m_resultHeading.text = $"Attention JSON {m_jsonPage + 1}/{m_result.Objects.Length + 1}\nB / middle pinch: next page";
                var data = JObject.Parse(m_result.Json);
                var objects = (JArray)data["objects"];
                data.Remove("objects");
                m_resultText.text = m_jsonPage == 0 ? data.ToString(Newtonsoft.Json.Formatting.None) : objects[m_jsonPage - 1].ToString(Newtonsoft.Json.Formatting.None);
                SetRect(m_resultText.rectTransform, new Vector2(0, -120), new Vector2(440, 500));
                return;
            }
            m_resultHeading.text = "Attention ranking\nGarlic pork chops";
            SetRect(m_resultText.rectTransform, new Vector2(0, 105), new Vector2(440, 100));
            m_resultText.text = m_result.Summary + $"\n{m_result.Objects.Length} scored | list shows top {Math.Min(m_scoreRows.Length, m_result.Objects.Length)}";
            for (var i = 0; i < Math.Min(m_scoreRows.Length, m_result.Objects.Length); i++)
            {
                var item = m_result.Objects[i];
                var displayName = item.Name.Length > 16 ? item.Name.Substring(0, 15) + "…" : item.Name;
                m_scoreLabels[i].text = $"{i + 1}. {item.ObjectId} {displayName}";
                m_totalLabels[i].text = item.AttentionScore.ToString("F1", CultureInfo.InvariantCulture);
                m_dimensionLabels[i].text = string.Format(CultureInfo.InvariantCulture,
                    "S {0:F1}   E {1:F1}   V {2:F1}", item.Salience, item.Expectancy, item.Value);
                m_scoreBars[i].anchorMax = new Vector2(Mathf.Clamp01((float)(item.AttentionScore / 100)), 1);
            }
        }

        private void ResetResultLayout()
        {
            m_showJson = false;
            m_jsonPage = 0;
            if (m_snapshotWinnerBox) m_snapshotWinnerBox.gameObject.SetActive(false);
            foreach (var row in m_scoreRows)
                if (row) row.SetActive(false);
            if (m_weightsText) m_weightsText.text = "S: Salience | E: Expectancy | V: Value";
            if (m_resultText)
                SetRect(m_resultText.rectTransform, new Vector2(0, -120), new Vector2(440, 500));
        }

        private void ShowFailure(string message)
        {
            m_status = message;
            m_detector.ClearWinner();
            ResetResultLayout();
            m_resultHeading.text = "Recognition unavailable";
            m_resultText.text = message;
        }

        private void OnFrameSpaceInvalidated()
        {
            StopAllCoroutines();
            ReleaseRequestResources();
            DestroySnapshot();
            IsRecognizing = false;
            m_result = null; m_scoredFrame = null;
            LastRecognitionJson = null; LastDetectionsJson = null; m_displayedFrameId = null;
            ResetResultLayout();
            m_resultText.text = "Camera / tracking reset. Press A when detection is ready.";
        }

        private void OnDestroy()
        {
            if (m_detector) m_detector.FrameSpaceInvalidated -= OnFrameSpaceInvalidated;
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            ReleaseRequestResources();
            DestroySnapshot();
            if (m_detector) m_detector.ClearWinner();
            IsRecognizing = false;
            m_result = null;
            LastRecognitionJson = null;
            ResetResultLayout();
            if (m_resultText)
            {
                m_resultHeading.text = "Attention ranking\nGarlic pork chops";
                m_resultText.text = "Press A / index pinch to analyze a frame.";
            }
            m_status = "A / index pinch: send a frame to OpenAI. B / middle pinch: cycle JSON pages / ranking.";
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && IsRecognizing)
            {
                StopAllCoroutines();
                ReleaseRequestResources();
                IsRecognizing = false;
                ShowFailure("Recognition canceled while app was paused. Press A to retry.");
            }
        }

        private void ReleaseRequestResources()
        {
            if (m_request != null)
            {
                if (!m_request.isDone)
                    m_request.Abort();
                m_request.Dispose();
                m_request = null;
            }
            // The GPU must finish using the render target before it returns to the pool.
            if (m_readbackPending)
            {
                m_readback.WaitForCompletion();
                m_readbackPending = false;
            }
            if (m_captureTarget)
            {
                RenderTexture.ReleaseTemporary(m_captureTarget);
                m_captureTarget = null;
            }
        }

        private void DestroySnapshot()
        {
            if (m_snapshotImage)
            {
                m_snapshotImage.texture = null;
                m_snapshotImage.enabled = false;
            }
            if (m_snapshot)
                Destroy(m_snapshot);
            m_snapshot = null;
        }

        private void CreateRecognitionPanel()
        {
            var parent = m_image.rectTransform.parent;
            SetRect(m_image.rectTransform, new Vector2(-270, 100), new Vector2(740, 555));
            CreateText("LiveCameraTitle", parent, new Vector2(-270, 430), new Vector2(740, 60), "Task: Cooking garlic pork chops", 32);
            CreateText("UploadHint", parent, new Vector2(-270, -220), new Vector2(740, 90),
                "A / index pinch: score all detected instances\nB / middle pinch: cycle JSON pages / ranking", 26);
            m_detectionText = CreateText("DetectedInstances", parent, new Vector2(-270, -365),
                new Vector2(740, 190), "Starting detector...", 20);
            m_detectionText.alignment = TextAnchor.UpperLeft;

            var panel = new GameObject("OpenAIRecognitionPanel", typeof(RectTransform), typeof(Image));
            panel.layer = gameObject.layer;
            panel.transform.SetParent(parent, false);
            SetRect((RectTransform)panel.transform, new Vector2(390, 0), new Vector2(500, 960));
            var background = panel.GetComponent<Image>();
            background.color = new Color(0.025f, 0.04f, 0.065f, 0.96f);
            background.raycastTarget = false;
            m_resultHeading = CreateText("Heading", panel.transform, new Vector2(0, 425),
                new Vector2(440, 75), "Attention ranking\nGarlic pork chops", 28);
            var snapshotGo = new GameObject("CapturedFrame", typeof(RectTransform), typeof(RawImage));
            snapshotGo.layer = gameObject.layer;
            snapshotGo.transform.SetParent(panel.transform, false);
            m_snapshotImage = snapshotGo.GetComponent<RawImage>();
            SetRect(m_snapshotImage.rectTransform, new Vector2(0, 270), new Vector2(440, 200));
            m_snapshotImage.raycastTarget = false;
            m_snapshotImage.enabled = false;
            m_resultText = CreateText("RecognitionResult", panel.transform, new Vector2(0, -185),
                new Vector2(440, 500), "Press A / index pinch to analyze a frame.", 26);
            m_resultText.alignment = TextAnchor.UpperLeft;
            m_resultText.resizeTextForBestFit = true;
            m_resultText.resizeTextMinSize = 14;
            m_resultText.resizeTextMaxSize = 26;
            m_weightsText = CreateText("AttentionWeights", panel.transform, new Vector2(0, -425),
                new Vector2(440, 65), "S: Salience | E: Expectancy | V: Value", 20);
            for (var i = 0; i < m_scoreRows.Length; i++)
            {
                var row = new GameObject("AttentionRank" + (i + 1), typeof(RectTransform));
                row.layer = gameObject.layer;
                row.transform.SetParent(panel.transform, false);
                SetRect((RectTransform)row.transform, new Vector2(0, 8 - i * 80), new Vector2(440, 74));
                m_scoreRows[i] = row;
                m_scoreLabels[i] = CreateText("ObjectName", row.transform, new Vector2(-55, 22), new Vector2(330, 28), "", 23);
                m_scoreLabels[i].alignment = TextAnchor.MiddleLeft;
                m_scoreLabels[i].resizeTextForBestFit = true;
                m_scoreLabels[i].resizeTextMinSize = 14;
                m_scoreLabels[i].resizeTextMaxSize = 23;
                m_totalLabels[i] = CreateText("AttentionScore", row.transform, new Vector2(175, 22), new Vector2(90, 28), "", 23);
                m_totalLabels[i].alignment = TextAnchor.MiddleRight;
                m_dimensionLabels[i] = CreateText("Dimensions", row.transform, new Vector2(0, -23), new Vector2(440, 24), "", 19);
                m_dimensionLabels[i].alignment = TextAnchor.MiddleLeft;
                var track = new GameObject("ScoreTrack", typeof(RectTransform), typeof(Image));
                track.layer = gameObject.layer;
                track.transform.SetParent(row.transform, false);
                SetRect((RectTransform)track.transform, Vector2.zero, new Vector2(440, 10));
                track.GetComponent<Image>().color = new Color(0.18f, 0.23f, 0.3f);
                track.GetComponent<Image>().raycastTarget = false;
                var bar = new GameObject("WeightedScore", typeof(RectTransform), typeof(Image));
                bar.layer = gameObject.layer;
                bar.transform.SetParent(track.transform, false);
                var rect = (RectTransform)bar.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                bar.GetComponent<Image>().color = new Color(0.2f, 0.55f, 1f);
                bar.GetComponent<Image>().raycastTarget = false;
                m_scoreBars[i] = rect;
                row.SetActive(false);
            }
            m_debugText.fontSize = 21;
            m_debugText.supportRichText = false;
        }

        private void ShowSnapshotWinner(double[] bbox)
        {
            if (!m_snapshotWinnerBox)
            {
                var go = new GameObject("HighestAttentionSnapshotBox", typeof(RectTransform));
                go.layer = gameObject.layer; go.transform.SetParent(m_snapshotImage.transform, false);
                m_snapshotWinnerBox = (RectTransform)go.transform;
                for (var i = 0; i < 4; i++)
                {
                    var edge = new GameObject("Edge" + i, typeof(RectTransform), typeof(Image));
                    edge.layer = gameObject.layer; edge.transform.SetParent(go.transform, false);
                    var image = edge.GetComponent<Image>(); image.color = Color.yellow; image.raycastTarget = false;
                    var rect = image.rectTransform;
                    rect.anchorMin = i == 0 || i == 2 ? Vector2.zero : i == 1 ? Vector2.up : Vector2.right;
                    rect.anchorMax = i < 2 ? new Vector2(1, rect.anchorMin.y) : new Vector2(rect.anchorMin.x, 1);
                    rect.sizeDelta = i < 2 ? new Vector2(0, 2) : new Vector2(2, 0);
                    rect.anchoredPosition = Vector2.zero;
                }
            }
            m_snapshotWinnerBox.anchorMin = new Vector2((float)bbox[0], 1 - (float)bbox[3]);
            m_snapshotWinnerBox.anchorMax = new Vector2((float)bbox[2], 1 - (float)bbox[1]);
            m_snapshotWinnerBox.offsetMin = m_snapshotWinnerBox.offsetMax = Vector2.zero;
            m_snapshotWinnerBox.gameObject.SetActive(true);
        }

        private Text CreateText(string objectName, Transform parent, Vector2 position, Vector2 size,
            string value, int fontSize)
        {
            var go = new GameObject(objectName, typeof(RectTransform), typeof(Text));
            go.layer = gameObject.layer;
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            SetRect(text.rectTransform, position, size);
            text.font = m_resultFont ? m_resultFont : m_debugText.font;
            text.fontSize = fontSize;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}
