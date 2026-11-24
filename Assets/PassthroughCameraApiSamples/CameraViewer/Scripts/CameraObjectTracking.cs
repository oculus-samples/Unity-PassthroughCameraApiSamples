// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PassthroughCameraSamples.CameraViewer
{
    // Plain data: usable by the protocol and offline tests without loading Unity.
    public sealed class CameraDetectedObject
    {
        public string Id;
        public string Name;
        public int ClassId;
        public double Confidence;
        // Normalized [left, top, right, bottom] relative to the frozen camera image.
        public double[] Bbox;
        // Surface hit in Unity world space, meters. Null means no valid depth, never an invented distance.
        public double[] WorldPosition;
        public double? DepthSampleRealtime;

        public CameraDetectedObject Copy() => new CameraDetectedObject
        {
            Id = Id, Name = Name, ClassId = ClassId, Confidence = Confidence,
            Bbox = (double[])Bbox.Clone(), WorldPosition = (double[])WorldPosition?.Clone(),
            DepthSampleRealtime = DepthSampleRealtime
        };

        public JObject ToJson() => new JObject
        {
            ["object_id"] = Id, ["name"] = Name, ["class_id"] = ClassId,
            ["detection_confidence"] = Confidence, ["bbox"] = new JArray(Bbox),
            ["world_position_m"] = WorldPosition == null ? JValue.CreateNull() : (JToken)new JArray(WorldPosition),
            ["depth_sample_realtime_s"] = DepthSampleRealtime.HasValue ? new JValue(DepthSampleRealtime.Value) : JValue.CreateNull()
        };
    }

    public sealed class CameraDetectionFrame
    {
        public string FrameId;
        public string SpaceId;
        public string CapturedAtUtc;
        public double CaptureRealtime;
        public int Width;
        public int Height;
        public double[] CameraPosition;
        public double[] CameraRotation;
        public CameraDetectedObject[] Objects;

        public JObject ToJson() => new JObject
        {
            ["frame_id"] = FrameId, ["space_id"] = SpaceId,
            ["captured_at_utc"] = CapturedAtUtc, ["capture_realtime_s"] = CaptureRealtime,
            ["source_width"] = Width, ["source_height"] = Height,
            ["bbox_convention"] = "normalized left,top,right,bottom; origin top-left; image resized without crop",
            ["world_coordinates"] = "Unity world space, meters; surface estimates, not object centers; null means depth unavailable",
            ["camera_position_m"] = new JArray(CameraPosition),
            ["camera_rotation_xyzw"] = new JArray(CameraRotation),
            ["objects"] = new JArray(Objects.Select(item => item.ToJson()))
        };

        public CameraDetectionFrame Copy() => new CameraDetectionFrame
        {
            FrameId = FrameId, SpaceId = SpaceId, CapturedAtUtc = CapturedAtUtc,
            CaptureRealtime = CaptureRealtime, Width = Width, Height = Height,
            CameraPosition = (double[])CameraPosition.Clone(), CameraRotation = (double[])CameraRotation.Clone(),
            Objects = Objects.Select(item => item.Copy()).ToArray()
        };
    }

    /// <summary>Conservative short-lived instance association, not semantic re-identification.</summary>
    public sealed class CameraObjectTracker
    {
        private sealed class Track { public CameraDetectedObject Object; public double Time; }
        private readonly Dictionary<string, Track> tracks = new Dictionary<string, Track>();
        private long nextId;
        public double TrackLifetime = 1.5;
        public double MaxWorldDistance = 0.3;

        public void Clear() => tracks.Clear(); // IDs are never reused after loss/recenter.

        public void Update(IList<CameraDetectedObject> detections, double now, bool allowImageMatching)
        {
            foreach (var id in tracks.Where(t => now - t.Value.Time > TrackLifetime).Select(t => t.Key).ToArray())
                tracks.Remove(id);
            var candidates = new List<(int index, string id, double cost)>();
            for (var i = 0; i < detections.Count; i++)
            {
                foreach (var pair in tracks)
                {
                    var previous = pair.Value.Object;
                    var current = detections[i];
                    if (previous.ClassId != current.ClassId) continue;
                    if (previous.WorldPosition != null && current.WorldPosition != null)
                    {
                        var distance = Distance(previous.WorldPosition, current.WorldPosition);
                        if (distance <= MaxWorldDistance)
                            candidates.Add((i, pair.Key, distance / MaxWorldDistance));
                    }
                    else if (allowImageMatching)
                    {
                        var overlap = IoU(previous.Bbox, current.Bbox);
                        if (overlap >= 0.35) candidates.Add((i, pair.Key, 2 - overlap));
                    }
                }
            }
            var usedIds = new HashSet<string>();
            var usedDetections = new HashSet<int>();
            foreach (var candidate in candidates.OrderBy(c => c.cost).ThenBy(c => c.id, StringComparer.Ordinal).ThenBy(c => c.index))
            {
                if (usedIds.Contains(candidate.id) || usedDetections.Contains(candidate.index)) continue;
                detections[candidate.index].Id = candidate.id;
                usedIds.Add(candidate.id);
                usedDetections.Add(candidate.index);
            }
            for (var i = 0; i < detections.Count; i++)
            {
                var item = detections[i];
                if (!usedDetections.Contains(i)) item.Id = "obj_" + (++nextId).ToString("D6");
                tracks[item.Id] = new Track { Object = item.Copy(), Time = now };
            }
        }

        public static List<CameraDetectedObject> SuppressOverlaps(IEnumerable<CameraDetectedObject> input, double threshold)
        {
            var kept = new List<CameraDetectedObject>();
            foreach (var item in input.OrderByDescending(d => d.Confidence))
            {
                // Class-aware NMS preserves overlapping objects of different types (e.g. knife and plate).
                if (!kept.Any(other => other.ClassId == item.ClassId && IoU(other.Bbox, item.Bbox) > threshold))
                    kept.Add(item);
            }
            return kept;
        }

        public static double IoU(double[] a, double[] b)
        {
            var intersection = Math.Max(0, Math.Min(a[2], b[2]) - Math.Max(a[0], b[0])) *
                               Math.Max(0, Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]));
            var union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - intersection;
            return union > 0 ? intersection / union : 0;
        }

        private static double Distance(double[] a, double[] b) => Math.Sqrt(
            (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
    }

    public static class CameraHighlightPolicy
    {
        public static CameraDetectedObject FindWinner(CameraDetectionFrame frame, string winnerId, string spaceId,
            double now, double scoreTime, double maxFrameAge, double maxScoreAge)
        {
            if (frame == null || frame.SpaceId != spaceId || string.IsNullOrEmpty(winnerId) ||
                now - frame.CaptureRealtime > maxFrameAge || now < frame.CaptureRealtime ||
                now - scoreTime > maxScoreAge || now < scoreTime) return null;
            // Do not fall back to a lower-ranked instance or a same-class substitute.
            return frame.Objects.FirstOrDefault(item => item.Id == winnerId && item.WorldPosition != null);
        }
    }
}
