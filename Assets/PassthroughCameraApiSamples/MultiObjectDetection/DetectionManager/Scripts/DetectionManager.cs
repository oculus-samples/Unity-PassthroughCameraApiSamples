// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class DetectionManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Controls configuration")]
        [SerializeField] private OVRInput.RawButton m_actionButton = OVRInput.RawButton.A;

        [Header("Placement configuration")]
        [SerializeField] private DetectionSpawnMarkerAnim m_spawnMarker;
        [SerializeField] private AudioSource m_placeSound;

        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [Space(10)]
        public UnityEvent<int> OnObjectsIdentified;

        private readonly List<DetectionSpawnMarkerAnim> m_spawnedEntities = new();
        private bool m_isStarted;
        internal OVRSpatialAnchor m_spatialAnchor;
        private bool m_isHeadsetTracking;

        private void Awake()
        {
            StartCoroutine(UpdateSpatialAnchor());
            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
        }

        private void OnDestroy()
        {
            EraseSpatialAnchor();
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
        }

        private void OnTrackingLost() => m_isHeadsetTracking = false;
        private void OnTrackingAcquired() => m_isHeadsetTracking = true;

        private void Update()
        {
            if (!m_isStarted)
            {
                // Manage the Initial Ui Menu
                if (m_cameraAccess.IsPlaying)
                {
                    m_isStarted = true;
                }
            }
            else
            {
                // Press A button to spawn 3d markers
                if (OVRInput.GetUp(m_actionButton))
                {
                    SpawnCurrentDetectedObjects();
                }
            }

            // Press B button to clean all markers
            if (OVRInput.GetDown(OVRInput.RawButton.B))
            {
                CleanMarkers();
            }
        }

        private IEnumerator UpdateSpatialAnchor()
        {
            while (true)
            {
                yield return null;
                if (m_spatialAnchor == null)
                {
                    yield return CreateSpatialAnchorAndSave();
                    if (m_spatialAnchor == null)
                    {
                        continue;
                    }
                }

                if (!m_spatialAnchor.IsTracked)
                {
                    yield return RestoreSpatialAnchorTracking();
                }
            }

            IEnumerator CreateSpatialAnchorAndSave()
            {
                m_spatialAnchor = m_uiInference.ContentParent.gameObject.AddComponent<OVRSpatialAnchor>();

                // Wait for localization because SaveAnchorAsync() requires the anchor to be localized first.
                while (true)
                {
                    if (m_spatialAnchor == null)
                    {
                        // Spatial Anchor destroys itself when creation fails.
                        yield break;
                    }
                    if (m_spatialAnchor.Localized)
                    {
                        break;
                    }
                    yield return null;
                }

                // Save the anchor.
                var awaiter = m_spatialAnchor.SaveAnchorAsync().GetAwaiter();
                while (!awaiter.IsCompleted)
                {
                    yield return null;
                }
                var saveAnchorResult = awaiter.GetResult();
                if (!saveAnchorResult.Success)
                {
                    LogSpatialAnchor($"SaveAnchorAsync() failed {saveAnchorResult}", LogType.Error);
                    EraseSpatialAnchor();
                    yield break;
                }
                LogSpatialAnchor("created");
            }

            IEnumerator RestoreSpatialAnchorTracking()
            {
                // Try to restore spatial anchor tracking. If restoration fails, erase it.
                const int numRetries = 5;
                for (int i = 0; i < numRetries; i++)
                {
                    if (!m_isHeadsetTracking)
                    {
                        yield break;
                    }

                    LogSpatialAnchor("tracking was lost, restoring...");
                    var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>(1);
                    var awaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[]
                    {
                        m_spatialAnchor.Uuid
                    }, unboundAnchors).GetAwaiter();
                    while (!awaiter.IsCompleted)
                    {
                        yield return null;
                    }
                    var loadResult = awaiter.GetResult();
                    if (!loadResult.Success)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() failed {loadResult.Status}", LogType.Error);
                        EraseSpatialAnchor();
                        yield break;
                    }
                    if (unboundAnchors.Count != 0)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() unexpected count:{unboundAnchors.Count}", LogType.Error);
                        EraseSpatialAnchor();
                        yield break;
                    }
                    yield return null;
                    if (m_spatialAnchor.IsTracked)
                    {
                        LogSpatialAnchor("tracking was restored successfully");
                        yield break;
                    }

                    yield return new WaitForSeconds(1f);
                }

                LogSpatialAnchor("tracking restoration failed", LogType.Warning);
                EraseSpatialAnchor();
            }
        }

        private void EraseSpatialAnchor()
        {
            if (m_spatialAnchor != null)
            {
                LogSpatialAnchor("EraseSpatialAnchor");
                m_spatialAnchor.EraseAnchorAsync();
                DestroyImmediate(m_spatialAnchor);
                m_spatialAnchor = null;

                CleanMarkers();
                m_uiInference.ClearAnnotations();
            }
        }

        private void CleanMarkers()
        {
            foreach (var e in m_spawnedEntities)
            {
                Destroy(e.gameObject);
            }
            m_spawnedEntities.Clear();
            OnObjectsIdentified?.Invoke(-1);
        }

        private static void LogSpatialAnchor(string message, LogType logType = LogType.Log)
        {
            Debug.unityLogger.Log(logType, $"{nameof(OVRSpatialAnchor)}: {message}");
        }

        /// <summary>
        /// Spwan 3d markers for the detected objects
        /// </summary>
        private void SpawnCurrentDetectedObjects()
        {
            var newCount = 0;
            foreach (SentisInferenceUiManager.BoundingBoxData box in m_uiInference.m_boxDrawn)
            {
                if (!HasExistingMarkerInBoundingBox(box))
                {
                    var marker = Instantiate(m_spawnMarker, box.BoxRectTransform.position, box.BoxRectTransform.rotation, m_uiInference.ContentParent);
                    marker.GetComponent<DetectionSpawnMarkerAnim>().SetYoloClassName(box.ClassName);

                    m_spawnedEntities.Add(marker);
                    newCount++;
                }
            }
            if (newCount > 0)
            {
                // Play sound if a new marker is placed.
                m_placeSound.Play();
            }
            OnObjectsIdentified?.Invoke(newCount);

            bool HasExistingMarkerInBoundingBox(SentisInferenceUiManager.BoundingBoxData box)
            {
                foreach (var marker in m_spawnedEntities)
                {
                    if (marker.GetYoloClassName() == box.ClassName)
                    {
                        var markerWorldPos = marker.transform.position;
                        Vector2 localPos = box.BoxRectTransform.InverseTransformPoint(markerWorldPos);
                        var sizeDelta = box.BoxRectTransform.sizeDelta;
                        var currentBox = new Rect(
                            -sizeDelta.x * 0.5f,
                            -sizeDelta.y * 0.5f,
                            sizeDelta.x,
                            sizeDelta.y
                        );

                        if (currentBox.Contains(localPos))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }
    }
}
