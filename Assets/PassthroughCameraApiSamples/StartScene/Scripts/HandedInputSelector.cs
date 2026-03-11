// Copyright (c) Meta Platforms, Inc. and affiliates.
// Original Source code from Oculus Starter Samples (https://github.com/oculus-samples/Unity-StarterSamples)

using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PassthroughCameraSamples.StartScene
{
    [MetaCodeSample("PassthroughCameraApiSamples-StartScene")]
    public class HandedInputSelector : MonoBehaviour
    {
        private OVRCameraRig m_cameraRig;
        private OVRInputModule m_inputModule;

        private void Start()
        {
            m_cameraRig = FindFirstObjectByType<OVRCameraRig>();
            m_inputModule = FindFirstObjectByType<OVRInputModule>();
        }

        private void Update()
        {
            var rayTransform = OVRInput.GetActiveController() is OVRInput.Controller.LTouch or OVRInput.Controller.LHand ? m_cameraRig.leftHandAnchor : m_cameraRig.rightHandAnchor;
            m_inputModule.rayTransform = rayTransform;
        }
    }
}
