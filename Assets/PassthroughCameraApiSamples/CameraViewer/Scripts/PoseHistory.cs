using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.InputSystem.Controls;

public class PoseHistory
{
    public int historyLength;
    public Queue<TimestampPose> prevPose = new Queue<TimestampPose>();

    private readonly object historyLock = new object ();

    public PoseHistory(int length=10)
	{
        this.historyLength = length;
    }

    public void addPose(ulong timestamp, Vector3 position, Quaternion rotation)
    {
        lock (historyLock)
        {
            if (prevPose.Count >= historyLength)
            {
                prevPose.Dequeue();
            }
            prevPose.Enqueue(new TimestampPose { timestamp = timestamp, position = position, rotation = rotation });
        }
    }

    public TimestampPose searchHistory(ulong timestamp)
    {
        lock (historyLock)
        {
            TimestampPose closest = null;
            ulong min = ulong.MaxValue;
            foreach (var pose in prevPose)
            {
                ulong delta = (ulong)Math.Abs((long)pose.timestamp - (long)timestamp);

                if (delta < min)
                {
                    closest = pose;
                    min = delta;
                }
            }

            return closest;
        }
    }
}
