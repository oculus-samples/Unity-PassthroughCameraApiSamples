// Copyright (c) Meta Platforms, Inc. and affiliates.
// Original Source code from Oculus Starter Samples (https://github.com/oculus-samples/Unity-StarterSamples)

using System;
using System.Collections.Generic;
using System.IO;
using Meta.XR.Samples;
using PassthroughCameraSamples.MultiObjectDetection;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PassthroughCameraSamples.StartScene
{
    // Create menu of all scenes included in the build.
    [MetaCodeSample("PassthroughCameraApiSamples-StartScene")]
    public class StartMenu : MonoBehaviour
    {
        public OVROverlay Overlay;
        public OVROverlay Text;
        public OVRCameraRig VrRig;
        [SerializeField] private ModelAsset m_objectDetectionModel;

        private void Awake() => SentisInferenceRunManager.PreloadModel(m_objectDetectionModel);

        private void Start()
        {
            var generalScenes = new List<Tuple<int, string>>();
            var passthroughScenes = new List<Tuple<int, string>>();
            var proControllerScenes = new List<Tuple<int, string>>();

            var n = UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings;
            for (var sceneIndex = 1; sceneIndex < n; ++sceneIndex)
            {
                var path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(sceneIndex);

                if (path.Contains("Passthrough"))
                {
                    passthroughScenes.Add(new Tuple<int, string>(sceneIndex, path));
                }
                else if (path.Contains("TouchPro"))
                {
                    proControllerScenes.Add(new Tuple<int, string>(sceneIndex, path));
                }
                else
                {
                    generalScenes.Add(new Tuple<int, string>(sceneIndex, path));
                }
            }

            var uiBuilder = DebugUIBuilder.Instance;
            if (passthroughScenes.Count > 0)
            {
                _ = uiBuilder.AddLabel("Passthrough Sample Scenes", DebugUIBuilder.DEBUG_PANE_LEFT);
                foreach (var scene in passthroughScenes)
                {
                    _ = uiBuilder.AddButton(Path.GetFileNameWithoutExtension(scene.Item2), () => LoadScene(scene.Item1), -1, DebugUIBuilder.DEBUG_PANE_LEFT);
                }
            }

            if (proControllerScenes.Count > 0)
            {
                _ = uiBuilder.AddLabel("Pro Controller Sample Scenes", DebugUIBuilder.DEBUG_PANE_RIGHT);
                foreach (var scene in proControllerScenes)
                {
                    _ = uiBuilder.AddButton(Path.GetFileNameWithoutExtension(scene.Item2), () => LoadScene(scene.Item1), -1, DebugUIBuilder.DEBUG_PANE_RIGHT);
                }
            }

            _ = uiBuilder.AddLabel("Press ☰ at any time to return to scene selection", DebugUIBuilder.DEBUG_PANE_CENTER);
            if (generalScenes.Count > 0)
            {
                _ = uiBuilder.AddDivider(DebugUIBuilder.DEBUG_PANE_CENTER);
                _ = uiBuilder.AddLabel("Sample Scenes", DebugUIBuilder.DEBUG_PANE_CENTER);
                foreach (var scene in generalScenes)
                {
                    _ = uiBuilder.AddButton(Path.GetFileNameWithoutExtension(scene.Item2), () => LoadScene(scene.Item1), -1, DebugUIBuilder.DEBUG_PANE_CENTER);
                }
            }

            uiBuilder.Show();
        }

        private static void LoadScene(int idx)
        {
            DebugUIBuilder.Instance.Hide();
            Debug.Log("Load scene: " + idx);
            SceneManager.LoadScene(idx);
        }
    }
}
