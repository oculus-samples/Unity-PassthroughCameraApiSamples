// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UI;

namespace PassthroughCameraSamples.CameraToWorld
{
    [MetaCodeSample("PassthroughCameraApiSamples-CameraToWorld")]
    public class CameraToWorldManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private GameObject m_centerEyeAnchor;
        [SerializeField] private GameObject m_headMarker;
        [SerializeField] private GameObject m_cameraMarker;
        [SerializeField] private GameObject m_rayMarker;

        [SerializeField] private CameraToWorldCameraCanvas m_cameraCanvas;
        [SerializeField] private float m_canvasDistance = 1f;

        [SerializeField] private Vector3 m_headSpaceDebugShift = new(0, -.15f, .4f);
        private GameObject m_rayGo1, m_rayGo2, m_rayGo3, m_rayGo4;

        private bool m_isDebugOn;
        private bool m_snapshotTaken;
        private OVRPose m_snapshotHeadPose;

        private void OnEnable() => OVRManager.display.RecenteredPose += RecenterCallBack;

        private void OnDisable() => OVRManager.display.RecenteredPose -= RecenterCallBack;

        private IEnumerator Start()
        {
            m_rayGo1 = m_rayMarker;
            m_rayGo2 = Instantiate(m_rayMarker);
            m_rayGo3 = Instantiate(m_rayMarker);
            m_rayGo4 = Instantiate(m_rayMarker);

            if (m_cameraAccess == null)
            {
                Debug.LogError($"PCA: {nameof(m_cameraAccess)} field is required "
                            + $"for the component {nameof(CameraToWorldManager)} to operate properly");
                enabled = false;
                yield break;
            }

            Assert.IsTrue(m_cameraAccess.enabled, "m_cameraAccess.enabled");
            while (!m_cameraAccess.IsPlaying)
            {
                yield return null;
            }

            ScaleCameraCanvas();
            UpdateRaysRendering();
        }

        private void Update()
        {
            if (InputManager.IsButtonADownOrPinchStarted())
            {
                m_snapshotTaken = !m_snapshotTaken;
                if (m_snapshotTaken)
                {
                    // Asking the canvas to make a snapshot before stopping the camera access
                    m_cameraCanvas.MakeCameraSnapshot();
                    m_snapshotHeadPose = m_centerEyeAnchor.transform.ToOVRPose();
                    UpdateMarkerPoses();
                    m_cameraAccess.enabled = false;
                }
                else
                {
                    m_cameraAccess.enabled = true;
                    m_cameraCanvas.ResumeStreamingFromCamera();
                }

                UpdateRaysRendering();
            }

            if (InputManager.IsButtonBDownOrMiddleFingerPinchStarted())
            {
                m_isDebugOn = !m_isDebugOn;
                Debug.Log($"PCA: SpatialSnapshotManager: DEBUG mode is {(m_isDebugOn ? "ON" : "OFF")}");
                UpdateRaysRendering();
            }

            if (!m_snapshotTaken)
            {
                UpdateMarkerPoses();
            }
        }

        /// <summary>
        /// Calculate the dimensions of the canvas based on the distance from the camera origin and the camera resolution
        /// </summary>
        private void ScaleCameraCanvas()
        {
            var cameraCanvasRectTransform = m_cameraCanvas.GetComponentInChildren<RectTransform>();
            var leftSidePointInCamera = m_cameraAccess.ViewportPointToRay(new Vector2(0f, 0.5f));
            var rightSidePointInCamera = m_cameraAccess.ViewportPointToRay(new Vector2(1f, 0.5f));
            var horizontalFoVDegrees = Vector3.Angle(leftSidePointInCamera.direction, rightSidePointInCamera.direction);
            var horizontalFoVRadians = horizontalFoVDegrees / 180 * Math.PI;
            var newCanvasWidthInMeters = 2 * m_canvasDistance * Math.Tan(horizontalFoVRadians / 2);
            var localScale = (float)(newCanvasWidthInMeters / cameraCanvasRectTransform.sizeDelta.x);
            cameraCanvasRectTransform.localScale = new Vector3(localScale, localScale, localScale);
        }

        private void UpdateRaysRendering()
        {
            // Hide rays' middle segments and rendering only their tips
            // when rays' origins are too close to the headset. Otherwise, it looks ugly
            foreach (var rayGo in new[] { m_rayGo1, m_rayGo2, m_rayGo3, m_rayGo4 })
            {
                var rayRenderer = rayGo.GetComponent<CameraToWorldRayRenderer>();
                foreach (var debugSegment in rayRenderer.m_debugSegments)
                {
                    debugSegment.SetActive(m_snapshotTaken || m_isDebugOn);
                }
            }
        }

        private void UpdateMarkerPoses()
        {
            if (!m_cameraAccess.IsPlaying)
            {
                return;
            }
            var headPose = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head).Pose.ToOVRPose();
            m_headMarker.transform.position = headPose.position;
            m_headMarker.transform.rotation = headPose.orientation;

            var cameraPose = m_cameraAccess.GetCameraPose();
            m_cameraMarker.transform.position = cameraPose.position;
            m_cameraMarker.transform.rotation = cameraPose.rotation;

            // Position the canvas in front of the camera
            m_cameraCanvas.transform.position = cameraPose.position + cameraPose.rotation * Vector3.forward * m_canvasDistance;
            m_cameraCanvas.transform.rotation = cameraPose.rotation;

            // Position the rays pointing to 4 corners of the canvas / image
            var rays = new[]
            {
                new { rayGo = m_rayGo1, u = 0f, v = 0f },
                new { rayGo = m_rayGo2, u = 0f, v = 1f },
                new { rayGo = m_rayGo3, u = 1f, v = 1f },
                new { rayGo = m_rayGo4, u = 1f, v = 0f }
            };

            foreach (var item in rays)
            {
                var rayInWorld = m_cameraAccess.ViewportPointToRay(new Vector2(item.u, item.v));
                item.rayGo.transform.position = rayInWorld.origin;
                item.rayGo.transform.LookAt(rayInWorld.origin + rayInWorld.direction);

                var angleWithCameraForwardDegree =
                    Vector3.Angle(item.rayGo.transform.forward, cameraPose.rotation * Vector3.forward);
                // The original size of the ray GameObject along z axis is 0.5f. Hardcoding it here for simplicity
                var zScale = (float)(m_canvasDistance / Math.Cos(angleWithCameraForwardDegree / 180 * Math.PI) / 0.5);
                item.rayGo.transform.localScale = new Vector3(item.rayGo.transform.localScale.x, item.rayGo.transform.localScale.y, zScale);

                var label = item.rayGo.GetComponentInChildren<Text>();
                label.text = $"({item.u:F0}, {item.v:F0})";
            }

            // Move the updated markers forward to better see them
            m_headMarker.SetActive(m_isDebugOn || m_snapshotTaken);
            m_cameraMarker.SetActive(m_isDebugOn || m_snapshotTaken);
            var gameObjects = new[]
            {
                m_headMarker, m_cameraMarker, m_cameraCanvas.gameObject, m_rayGo1, m_rayGo2, m_rayGo3, m_rayGo4
            };

            var direction = m_snapshotTaken ? m_snapshotHeadPose.orientation : m_centerEyeAnchor.transform.rotation;

            foreach (var go in gameObjects)
            {
                go.transform.position += direction * m_headSpaceDebugShift * (m_isDebugOn ? 1 : 0);
            }
        }

        private void RecenterCallBack()
        {
            if (m_snapshotTaken)
            {
                m_snapshotTaken = false;
                m_cameraAccess.enabled = true;
                m_cameraCanvas.ResumeStreamingFromCamera();
                UpdateRaysRendering();
            }
        }
    }
}
