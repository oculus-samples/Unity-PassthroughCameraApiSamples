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
    [SerializeField] private EnvironmentRaycastManager environmentRaycastManager;
    [SerializeField] private GameObject InteractiveCube;
    [SerializeField] private Text debugText;
    [SerializeField] private LayerMask environmentMask;

    [Header("Tuning Sensitivity")]
    public float sensitivity = 0.5f;

    // One euro filter parameters
    private float minCutoffPosition = 0.70f;
    private float betaPosition = 0.67f;

    private float minCutoffRotation = 0.16f;
    private float betaRotation = 0.25f;

    OneEuroVector3 positionFilter;
    OneEuroQuaternion rotationFilter;

    private bool handshakeCompleted = false;

    public class CornerData
    {
        public string id;

        public float[] corner0;
        public float[] corner1;
        public float[] corner2;
        public float[] corner3;
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

                string intrinsicsMessage = $"{fx},{fy},{cx},{cy}";
                byte[] messageBytes = Encoding.UTF8.GetBytes(intrinsicsMessage);

                stream.Write(messageBytes, 0, messageBytes.Length);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to send message: " + e.Message);
        }
    }

    private void Update()
    {
        if (handshakeCompleted && m_cameraAccess.IsPlaying)
        {
            //// Color32[] pixels = new Color32[m_cameraAccess.CurrentResolution.x * m_cameraAccess.CurrentResolution.y];
            //Color32[] pixels = ((Texture2D)m_cameraAccess.GetTexture()).GetPixels32();

            //int dataLength = pixels.Length * 4;
            //byte[] byteData = MemoryMarshal.Cast<Color32, byte>(pixels).ToArray();

            var texture = (Texture2D)m_cameraAccess.GetTexture();

            //byte[] byteData = texture.GetRawTextureData();
            byte[] byteData = texture.EncodeToJPG(60); // Compress to JPEG to prep for send

            SendMessageToServer(byteData);
        }

        // Debug send test message
        //SendMessageToServer();
        HandleControllerTuning();
    }

    private void HandleControllerTuning()
    {
        // 1. Read Right Thumbstick (Position Tuning)
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        if (rightStick.magnitude > 0.1f)
        {
            minCutoffPosition += rightStick.y * sensitivity * Time.deltaTime;
            betaPosition += rightStick.x * sensitivity * Time.deltaTime;

            // Clamping to prevent negative values
            minCutoffPosition = Mathf.Max(0.01f, minCutoffPosition);
            betaPosition = Mathf.Max(0.0f, betaPosition);

            positionFilter.UpdateParams(minCutoffPosition, betaPosition);
            Debug.Log($"POS TUNING: MinCutoff: {minCutoffPosition:F3} | Beta: {betaPosition:F3}");
        }

        // 2. Read Left Thumbstick (Rotation Tuning)
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        if (leftStick.magnitude > 0.1f)
        {
            minCutoffRotation += leftStick.y * sensitivity * Time.deltaTime;
            betaRotation += leftStick.x * sensitivity * Time.deltaTime;

            minCutoffRotation = Mathf.Max(0.01f, minCutoffRotation);
            betaRotation = Mathf.Max(0.0f, betaRotation);

            rotationFilter.UpdateParams(minCutoffRotation, betaRotation);
            Debug.Log($"ROT TUNING: MinCutoff: {minCutoffRotation:F3} | Beta: {betaRotation:F3}");
        }

        debugText.text = "POS mincutoff: " + minCutoffPosition.ToString("F2") + "  |  " + "POS beta: " + betaPosition.ToString("F2")
                            + "\n" +
                         "ROT mincutoff: " + minCutoffRotation.ToString("F2") + "  |  " + "ROT beta: " + betaRotation.ToString("F2");
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

            // This function modifies a GameObject and MUST run on the main thread (LateUpdate)
            (Vector3, Quaternion) cubePos = GetCubePosition(dataToProcess);

            if (cubePos.Item1 != Vector3.zero)
            {
                float currentTime = Time.time; // Seconds

                InteractiveCube.transform.SetPositionAndRotation(
                    positionFilter.Filter(cubePos.Item1, currentTime),
                    rotationFilter.Filter(cubePos.Item2, currentTime)
                );
            }
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

    private (Vector3, Quaternion) GetCubePosition(CornerData tag)
    {
        // Use three corners of tag for computations
        var viewportPoint0 = new Vector2(tag.corner0[0] / m_cameraAccess.CurrentResolution.x, 1.0f - (tag.corner0[1] / m_cameraAccess.CurrentResolution.y));
        var viewportPoint1 = new Vector2(tag.corner1[0] / m_cameraAccess.CurrentResolution.x, 1.0f - (tag.corner1[1] / m_cameraAccess.CurrentResolution.y));
        var viewportPoint3 = new Vector2(tag.corner3[0] / m_cameraAccess.CurrentResolution.x, 1.0f - (tag.corner3[1] / m_cameraAccess.CurrentResolution.y));

        // Cast rays to three corners
        var ray0 = m_cameraAccess.ViewportPointToRay(viewportPoint0);
        var ray1 = m_cameraAccess.ViewportPointToRay(viewportPoint1);
        var ray3 = m_cameraAccess.ViewportPointToRay(viewportPoint3);

        // Instantiate 3D world points to zero
        var worldPoint0 = Vector3.zero;
        var worldPoint1 = Vector3.zero;
        var worldPoint3 = Vector3.zero;

        // Convert 2D viewport points to 3D world with raycasts
        if (environmentRaycastManager.Raycast(ray0, out EnvironmentRaycastHit hitInfo0, environmentMask))
        {
            worldPoint0 = hitInfo0.point;
            //// Place a GameObject at the place of intersection
            //InteractiveCube.transform.SetPositionAndRotation(
            //    hitInfo.point,
            //    Quaternion.LookRotation(hitInfo.normal, Vector3.up));
        }

        if (environmentRaycastManager.Raycast(ray1, out EnvironmentRaycastHit hitInfo1, environmentMask))
        {
            worldPoint1 = hitInfo1.point;
        }

        if (environmentRaycastManager.Raycast(ray3, out EnvironmentRaycastHit hitInfo3, environmentMask))
        {
            worldPoint3 = hitInfo3.point;
        }

        // Check if points valid
        if (worldPoint0 != Vector3.zero && worldPoint1 != Vector3.zero && worldPoint3 != Vector3.zero)
        {
            var normal = Vector3.Cross(worldPoint1 - worldPoint0, worldPoint3 - worldPoint0).normalized;

            // Offset of tag corner from real cube corner
            var offset = (worldPoint1 - worldPoint0) / 2.0f + (worldPoint3 - worldPoint0) / 2.0f - (normal * 0.02f);

            //// Place cube at tag position and rotation
            //InteractiveCube.transform.SetPositionAndRotation(
            //    worldPoint0 + offset,
            //    Quaternion.LookRotation((worldPoint1 - worldPoint0).normalized, normal));

            Vector3 position = worldPoint0 + offset;
            //Quaternion rotation = Quaternion.LookRotation((worldPoint1 - worldPoint0).normalized, normal);
            Quaternion rotation = Quaternion.LookRotation(Vector3.Cross(normal, (worldPoint1 - worldPoint0).normalized), normal);

            return (position, rotation);
        }

        return (Vector3.zero, Quaternion.identity);
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