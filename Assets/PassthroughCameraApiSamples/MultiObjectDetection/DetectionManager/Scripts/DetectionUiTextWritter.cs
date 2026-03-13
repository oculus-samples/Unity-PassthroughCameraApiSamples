// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class DetectionUiTextWritter : MonoBehaviour
    {
        [SerializeField] private Text m_labelInfo;
        [SerializeField] private float m_writtingSpeed = 0.00015f;
        [SerializeField] private float m_writtingInfoPause = 0.005f;

        public UnityEvent OnStartWritting;
        public UnityEvent OnFinishWritting;

        private float m_writtingTime = 0;
        private bool m_isWritting = false;
        private string m_currentInfo = "";
        private int m_currentInfoIndex = 0;

        private void Start()
        {
            SetWrittingConfig();
        }

        private void OnEnable()
        {
            SetWrittingConfig();
        }

        private void OnDisable()
        {
            m_isWritting = false;
            m_writtingTime = 0;
            m_currentInfoIndex = 0;
            m_labelInfo.text = m_currentInfo;
        }

        private void LateUpdate()
        {
            if (m_isWritting)
            {
                if (m_writtingTime <= 0)
                {
                    m_writtingTime = m_writtingSpeed;

                    var nextChar = m_currentInfo.Substring(m_currentInfoIndex, 1);
                    m_labelInfo.text += nextChar;

                    if (nextChar == ":")
                    {
                        m_writtingTime += m_writtingInfoPause;
                    }

                    m_currentInfoIndex++;
                    if (m_currentInfoIndex >= m_currentInfo.Length)
                    {
                        m_isWritting = false;
                        OnFinishWritting?.Invoke();
                    }
                }
                else
                {
                    m_writtingTime -= Time.deltaTime;
                }
            }
        }

        private void SetWrittingConfig()
        {
            if (!m_isWritting)
            {
                m_isWritting = true;
                m_currentInfo = m_labelInfo.text;
                m_labelInfo.text = "";
                OnStartWritting?.Invoke();
            }
        }
    }
}
