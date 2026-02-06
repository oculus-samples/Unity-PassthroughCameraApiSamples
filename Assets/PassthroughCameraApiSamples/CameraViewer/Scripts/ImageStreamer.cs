// TCP client code adapted from https://medium.com/@rabeeqiblawi/implementing-a-basic-tcp-server-in-unity-a-step-by-step-guide-449d8504d1c5

// System imports
using System;
using System.Text;
using System.Net;

// TCP socket imports
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;

// UnityEngine imports
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Assertions;

// For passthrough camera
using System.Runtime.InteropServices;
using System.Collections;

using Meta.XR;
using Meta.XR.Samples;
using Meta.XR.EnvironmentDepth;

using PassthroughCameraSamples;
using System.Collections.Generic;
using UnityEngine.UI;

public class ImageStreamer : MonoBehaviour
{
    // Server connection parameters
    public string serverIP = "127.0.0.1";
    public int serverPort = 65432;

    private TcpClient client;
    private NetworkStream stream;
    private Thread clientReceiveThread;

    private readonly Queue<CornerData> receivedDataQueue = new Queue<CornerData>(); // Buffer for incoming data

    // Parameters
    [SerializeField] private RawImage m_image;
    [SerializeField] private PassthroughCameraAccess m_cameraAccess;
    [SerializeField] private Transform leftEyeCamera;
    [SerializeField] private EnvironmentRaycastManager environmentRaycastManager;
    [SerializeField] private GameObject InteractiveCube;
    [SerializeField] private Text debugText;
    [SerializeField] private LayerMask environmentMask;

    [Header("Tuning Sensitivity")]
    public float sensitivity = 0.00001f;

    // One euro filter parameters
    private float minCutoffPosition = 0.70f;
    private float betaPosition = 0.67f;

    private float minCutoffRotation = 0.16f;
    private float betaRotation = 0.25f;

    OneEuroVector3 positionFilter;
    OneEuroQuaternion rotationFilter;

    private bool handshakeCompleted = false;

    private Vector3 adjustmentOffset = new Vector3(0.0f, 0.0f, 0.0f);
    bool euroAdjustment = false;

    public class CornerData
    {
        public string id;

        public float[] tvec;
        public float[] rvec;
    }

    private IEnumerator Start()
    {
        var supportedResolutions = PassthroughCameraAccess.GetSupportedResolutions(PassthroughCameraAccess.CameraPositionType.Left);
        Assert.IsNotNull(supportedResolutions, nameof(supportedResolutions));
        Debug.Log($"PassthroughCameraAccess.GetSupportedResolutions(): {string.Join(", ", supportedResolutions)}");

        while (!m_cameraAccess.IsPlaying)
        {
            yield return null;
        }
        // Set texture to the RawImage Ui element
        m_image.texture = m_cameraAccess.GetTexture();

        // Setup One Euro Filter
        positionFilter = new OneEuroVector3(minCutoffPosition, betaPosition);
        rotationFilter = new OneEuroQuaternion(minCutoffRotation, betaRotation);

        ConnectToServer();

        // Pass intrinsics
        try
        {
            if (stream != null && client != null && client.Connected)
            {
                float fx = m_cameraAccess.Intrinsics.FocalLength.x;
                float fy = m_cameraAccess.Intrinsics.FocalLength.y;
                float cx = m_cameraAccess.Intrinsics.PrincipalPoint.x;
                float cy = m_cameraAccess.Intrinsics.PrincipalPoint.y;

                string intrinsicsMessage = $"{fx},{fy},{cx},{cy},{m_cameraAccess.CurrentResolution.x},{m_cameraAccess.CurrentResolution.y}";
                byte[] messageBytes = Encoding.UTF8.GetBytes(intrinsicsMessage);

                stream.Write(messageBytes, 0, messageBytes.Length);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to send message: " + e.Message);
        }
    }

    public float targetFPS = 30f; // Send 15 images per second instead of 72/90
    private float m_lastSendTime = 0f;
    private Texture2D m_cpuTexture;
    private RenderTexture m_smallDescriptor;
    private int targetWidth = 1280;
    private int targetHeight = 1280;

    private void Update()
    {
        if (handshakeCompleted && m_cameraAccess.IsPlaying)
        {
            Texture rawTexture = m_cameraAccess.GetTexture();
            if (rawTexture == null) return;

            // 1. Initialize the small GPU buffer (RenderTexture)
            if (m_smallDescriptor == null)
            {
                m_smallDescriptor = new RenderTexture(targetWidth, targetHeight, 0);
            }

            // 2. Initialize the CPU buffer (Texture2D) to match the small size
            if (m_cpuTexture == null || m_cpuTexture.width != targetWidth)
            {
                m_cpuTexture = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            }

            // 3. Downscale on the GPU
            // This takes the 1280x1280 and shrinks it to 640x640 instantly
            Graphics.Blit(rawTexture, m_smallDescriptor);

            // 4. Download the SMALLER image to the CPU
            RenderTexture currentRT = RenderTexture.active;
            RenderTexture.active = m_smallDescriptor;

            m_cpuTexture.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            m_cpuTexture.Apply();

            RenderTexture.active = currentRT;

            // 5. Encode the much smaller dataset
            byte[] byteData = m_cpuTexture.EncodeToJPG(90);
            SendMessageToServer(byteData);
        }

        HandleControllerTuning();
    }

    private void HandleControllerTuning()
    {
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            euroAdjustment = !euroAdjustment;
        }

        // A button
        // 1. Read Right Thumbstick (Position Tuning)
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        if (rightStick.magnitude > 0.1f)
        {
            if (euroAdjustment)
            {
                minCutoffPosition += rightStick.y * sensitivity * Time.deltaTime;
                betaPosition += rightStick.x * sensitivity * Time.deltaTime;

                // Clamping to prevent negative values
                minCutoffPosition = Mathf.Max(0.01f, minCutoffPosition);
                betaPosition = Mathf.Max(0.0f, betaPosition);

                positionFilter.UpdateParams(minCutoffPosition, betaPosition);
                Debug.Log($"POS TUNING: MinCutoff: {minCutoffPosition:F3} | Beta: {betaPosition:F3}");
            }
            else
            {
                adjustmentOffset.y += rightStick.y * sensitivity * Time.deltaTime;
                adjustmentOffset.z += rightStick.x * sensitivity * Time.deltaTime;
            }
        }

        // 2. Read Left Thumbstick (Rotation Tuning)
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        if (leftStick.magnitude > 0.1f)
        {
            if (euroAdjustment)
            {
                minCutoffRotation += leftStick.y * sensitivity * Time.deltaTime;
                betaRotation += leftStick.x * sensitivity * Time.deltaTime;

                minCutoffRotation = Mathf.Max(0.01f, minCutoffRotation);
                betaRotation = Mathf.Max(0.0f, betaRotation);

                rotationFilter.UpdateParams(minCutoffRotation, betaRotation);
                Debug.Log($"ROT TUNING: MinCutoff: {minCutoffRotation:F3} | Beta: {betaRotation:F3}");
            }
            else
            {
                adjustmentOffset.x += leftStick.x * sensitivity * Time.deltaTime;
            }
        }

        if (euroAdjustment)
        {
            debugText.text = "POS mincutoff: " + minCutoffPosition.ToString("F2") + "  |  " + "POS beta: " + betaPosition.ToString("F2")
                            + "\n" +
                         "ROT mincutoff: " + minCutoffRotation.ToString("F2") + "  |  " + "ROT beta: " + betaRotation.ToString("F2");
        }
        else
        {
            debugText.text = "X offset: " + adjustmentOffset.x.ToString("F5") + "  |  " + "Y offset: " + adjustmentOffset.y.ToString("F5") + "  |  " + "Z offset: " + adjustmentOffset.z.ToString("F5");
        }
    }

    private void ConnectToServer()
    {
        try
        {
            client = new TcpClient(serverIP, serverPort);
            client.NoDelay = true; // Disable Nagle's algorithm for lower latency
            stream = client.GetStream();

            Debug.Log("Connected to server at " + serverIP + ":" + serverPort);

            clientReceiveThread = new Thread(new ThreadStart(ListenForData));
            clientReceiveThread.IsBackground = true;
            clientReceiveThread.Start();
        }
        catch (SocketException e)
        {
            Debug.Log("SocketException: " + e.ToString());
        }
    }

    void OnApplicationQuit()
    {
        if (stream != null)
            stream.Close();
        if (client != null)
            client.Close();
        if (clientReceiveThread != null)
            clientReceiveThread.Abort();
    }

    private void LateUpdate()
    {
        // Check for new data in the thread-safe queue every frame
        CornerData dataToProcess = null;

        lock (receivedDataQueue)
        {
            // Dequeue oldest element in queue for processing
            if (receivedDataQueue.Count > 0)
            {
                dataToProcess = receivedDataQueue.Dequeue();
            }
        }

        if (dataToProcess != null)
        {
            Debug.Log("Server message received: " + JsonConvert.SerializeObject(dataToProcess));

            // 1. Convert OpenCV (RHS) to Unity (LHS)
            Vector3 localPos = new Vector3(dataToProcess.tvec[0], -dataToProcess.tvec[1], dataToProcess.tvec[2]);

            Vector3 rotAxis = new Vector3(dataToProcess.rvec[0], dataToProcess.rvec[1], dataToProcess.rvec[2]);
            float angle = rotAxis.magnitude;
            Vector3 axis = rotAxis.normalized;
            Quaternion localRot = Quaternion.AngleAxis(-angle * Mathf.Rad2Deg, new Vector3(axis.x, -axis.y, axis.z));

            // 2. Transform relative to Camera
            Transform camTrans = leftEyeCamera;

            Vector3 worldPos = m_cameraAccess.GetCameraPose().position + (m_cameraAccess.GetCameraPose().rotation * (localPos + adjustmentOffset));
            Quaternion worldRot = camTrans.rotation * localRot;

            // 3. Filter and Apply
            InteractiveCube.transform.position = positionFilter.Filter(worldPos, Time.time);
            InteractiveCube.transform.rotation = rotationFilter.Filter(worldRot, Time.time);
        }
    }

    public void ListenForData()
    {
        // Use a StringBuilder to buffer incoming TCP bytes
        System.Text.StringBuilder jsonBuffer = new System.Text.StringBuilder();

        try
        {
            byte[] bytes = new byte[1024];
            while (true)
            {
                // Use Thread.Sleep to prevent 100% CPU usage when idle
                if (!stream.DataAvailable)
                {
                    Thread.Sleep(5);
                    continue;
                }

                int length = stream.Read(bytes, 0, bytes.Length);

                // Check for connection closure
                if (length == 0) break;

                // 1. Append the new incoming chunk to the buffer
                string incomingChunk = Encoding.UTF8.GetString(bytes, 0, length);
                jsonBuffer.Append(incomingChunk);

                // 2. Process complete messages from the buffer
                while (true)
                {
                    string bufferContent = jsonBuffer.ToString();

                    // Find the index of the newline delimiter ('\n')
                    int newlineIndex = bufferContent.IndexOf('\n');

                    // If complete message found
                    if (newlineIndex >= 0)
                    {
                        // A complete message found! Extract it.
                        string completeMessage = bufferContent.Substring(0, newlineIndex).Trim();

                        // Remove the processed message AND the delimiter from the buffer
                        jsonBuffer.Remove(0, newlineIndex + 1);

                        if (!string.IsNullOrEmpty(completeMessage))
                        {
                            try
                            {
                                if (completeMessage == "HANDSHAKE_OK")
                                {
                                    handshakeCompleted = true;
                                    Debug.Log("Handshake with server completed.");
                                    continue;
                                }

                                // 3. Deserialize the single, complete message (SAFE in background thread)
                                CornerData tag = JsonConvert.DeserializeObject<CornerData>(completeMessage);

                                // 4. Enqueue the data for the main thread to handle (THREAD-SAFE)
                                lock (receivedDataQueue)
                                {
                                    receivedDataQueue.Enqueue(tag);
                                }
                            }
                            catch (JsonReaderException)
                            {
                                // Silently ignore corrupted data or log a warning if necessary
                            }
                        }
                    }
                    else
                    {
                        // No more complete messages in the buffer. Wait for more data.
                        break;
                    }
                }
            }
        }
        catch (SocketException e)
        {
            // Must use Debug.Log on the Main Thread for safety, but for minimal change,
            // we leave this risky line here. A crash could occur if this is called during cleanup.
            Debug.Log("SocketException: " + e);
        }
        catch (Exception e)
        {
            Debug.Log("ListenForData thread error: " + e);
        }
    }

    public void SendMessageToServer(byte[] image)
    {
        try
        {
            if (stream != null && client != null && client.Connected)
            {
                // 1. Send the length of the JPEG data (as a 4-byte integer)
                int dataLength = image.Length;
                byte[] lengthPrefix = BitConverter.GetBytes(dataLength);

                // Ensure consistent endianness (e.g., use Big-Endian across client/server)
                if (BitConverter.IsLittleEndian)
                    System.Array.Reverse(lengthPrefix);

                stream.Write(lengthPrefix, 0, lengthPrefix.Length);

                // 2. Send the actual JPEG data bytes
                stream.Write(image, 0, dataLength);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to send message: " + e.Message);
        }
    }

    // Send debug message
    //public void SendMessageToServer()
    //{
    //    try
    //    {
    //        if (stream != null && client != null && client.Connected)
    //        {
    //            string message = "hello";
    //            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
    //            stream.Write(messageBytes, 0, messageBytes.Length);
    //        }
    //    }
    //    catch (Exception e)
    //    {
    //        Debug.LogError("Failed to send message: " + e.Message);
    //    }
    //}
}