// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using UnityEngine;

public class InputManager : MonoBehaviour
{
    private const int _numHands = 2;
    private static InputManager Instance { get; set; }
    private readonly OVRPlugin.HandState[] _prevState = new OVRPlugin.HandState[_numHands];
    private readonly OVRPlugin.HandState[] _currentState = new OVRPlugin.HandState[_numHands];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AfterSceneLoad()
    {
        var go = new GameObject(nameof(InputManager));
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<InputManager>();
    }

    private void Update()
    {
        for (int i = 0; i < _numHands; i++)
        {
            _prevState[i] = _currentState[i];
            OVRPlugin.GetHandState(OVRPlugin.Step.Render, (OVRPlugin.Hand)i, ref _currentState[i]);
        }
    }

    public static bool IsButtonADownOrPinchStarted() => OVRInput.GetDown(OVRInput.RawButton.A) || Instance.GetPinchStarted(OVRPlugin.HandFingerPinch.Index);
    public static bool IsButtonBDownOrMiddleFingerPinchStarted() => OVRInput.GetDown(OVRInput.RawButton.B) || Instance.GetPinchStarted(OVRPlugin.HandFingerPinch.Middle);

    private bool GetPinchStarted(OVRPlugin.HandFingerPinch finger) => GetPinchStarted(OVRPlugin.Hand.HandLeft, finger) || GetPinchStarted(OVRPlugin.Hand.HandRight, finger);

    private bool GetPinchStarted(OVRPlugin.Hand hand, OVRPlugin.HandFingerPinch finger)
    {
        if (hand == OVRPlugin.Hand.None)
        {
            throw new Exception("hand parameter is None");
        }
        int handIndex = (int)hand;
        bool prevPinched = (_prevState[handIndex].Pinches & finger) != 0;
        bool curPinched = (_currentState[handIndex].Pinches & finger) != 0;
        return !prevPinched && curPinched;
    }
}
