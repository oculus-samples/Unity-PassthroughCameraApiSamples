// Copyright (c) Meta Platforms, Inc. and affiliates.
// Original Source code from Oculus Starter Samples (https://github.com/oculus-samples/Unity-StarterSamples)

using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PassthroughCameraSamples.StartScene
{
    [MetaCodeSample("PassthroughCameraApiSamples-StartScene")]
    public class ReturnToStartScene : MonoBehaviour
    {
        [SerializeField] private TextMesh _tooltipText;
        [SerializeField, Multiline] private string _controllerTooltip;
        [SerializeField, Multiline] private string _handTooltip;
        [SerializeField] private Vector3 _controllerOffset = new Vector3(0f, 0f, -0.07f);
        [SerializeField] private Vector3 _controllerRotation = new Vector3(45f, 0f, 0f);
        [SerializeField] private Vector3 _handOffset = new Vector3(0f, 0f, 0.04f);
        [SerializeField] private Vector3 _handRotation = new Vector3(0f, 180f, 90f);
        private bool? _isHandTracking;

        private void Update()
        {
            if (OVRInput.GetUp(OVRInput.Button.Start))
            {
                SceneManager.LoadScene(0);
            }

            bool isHandTracking = (OVRInput.GetActiveController() & OVRInput.Controller.Hands) != 0;
            if (isHandTracking != _isHandTracking)
            {
                _isHandTracking = isHandTracking;
                _tooltipText.text = isHandTracking ? _handTooltip : _controllerTooltip;
                var pos = isHandTracking ? _handOffset : _controllerOffset;
                var rot = isHandTracking ? _handRotation : _controllerRotation;
                transform.SetLocalPositionAndRotation(pos, Quaternion.Euler(rot));
            }
        }
    }
}
