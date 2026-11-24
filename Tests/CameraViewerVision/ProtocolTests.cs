using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PassthroughCameraSamples.CameraViewer;

internal static class ProtocolTests
{
    private static int passed;
    private static void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (FormatException) { return; }
        catch (JsonException) { return; }
        catch (ArgumentException) { return; }
        throw new Exception("Invalid input was accepted");
    }
    private static CameraDetectedObject Detection(string id = "obj_000001", double x = 0, bool depth = true, int category = 41)
    {
        return new CameraDetectedObject { Id = id, Name = "cup", ClassId = category, Confidence = 0.9,
            Bbox = new[] { 0.1 + x, 0.2, 0.3 + x, 0.5 },
            WorldPosition = depth ? new[] { x, 0d, 2d } : null, DepthSampleRealtime = depth ? (double?)10.1 : null };
    }
    private static CameraDetectionFrame Frame(int count = 1)
    {
        return new CameraDetectionFrame { FrameId = "frame_1", SpaceId = "space_1", CapturedAtUtc = "2026-11-22T12:00:00Z",
            CaptureRealtime = 10, Width = 1280, Height = 960, CameraPosition = new double[] { 0, 0, 0 },
            CameraRotation = new double[] { 0, 0, 0, 1 },
            Objects = Enumerable.Range(1, count).Select(i => Detection("obj_" + i.ToString("D6"))).ToArray() };
    }
    private static JObject Scores(CameraDetectionFrame frame)
    {
        return new JObject { ["frame_id"] = frame.FrameId, ["summary"] = "Visible objects on the counter.",
            ["objects"] = new JArray(frame.Objects.Select(item => new JObject {
                ["object_id"] = item.Id, ["description"] = "Distinct, static and useful to the task.",
                ["salience"] = 6, ["expectancy"] = 1, ["value"] = 2 })) };
    }
    private static JObject Envelope(JObject data)
    {
        return new JObject { ["status"] = "completed", ["output"] = new JArray(
            new JObject { ["type"] = "reasoning" },
            new JObject { ["type"] = "message", ["content"] = new JArray(
                new JObject { ["type"] = "output_text", ["text"] = data.ToString(Formatting.None) }) }) };
    }
    private static OpenAIObjectRecognition.Result Parse(JObject data, CameraDetectionFrame frame,
        OpenAIObjectRecognition.AttentionWeights weights = null) =>
        OpenAIObjectRecognition.ParseResponse(Envelope(data).ToString(), frame, weights);

    public static int Main()
    {
        Check("Request includes image and matching IDs, names, boxes, positions and frame metadata", () => {
            var frame = Frame(2);
            var request = JObject.Parse(OpenAIObjectRecognition.CreateRequest(new byte[] {255,216,255}, "gpt-4.1-mini", "English", frame));
            Assert((string)request["input"][0]["content"][1]["image_url"] == "data:image/jpeg;base64,/9j/");
            var text = (string)request["input"][0]["content"][0]["text"];
            var metadata = JObject.Parse(text.Substring(text.IndexOf('{')));
            Assert((string)metadata["frame_id"] == frame.FrameId);
            Assert((string)metadata["objects"][1]["object_id"] == frame.Objects[1].Id);
            Assert((string)metadata["objects"][0]["name"] == "cup");
            Assert((double)metadata["objects"][0]["world_position_m"][2] == 2);
            Assert((double)metadata["objects"][0]["bbox"][0] == 0.1);
            Assert(!(bool)request["store"] && request["apiKey"] == null);
        });
        Check("Schema restricts response to exactly the supplied instances and frame", () => {
            var frame = Frame(2);
            var request = JObject.Parse(OpenAIObjectRecognition.CreateRequest(new byte[] {1}, "test", "English", frame));
            var format = request["text"]["format"];
            Assert((bool)format["strict"] && (string)format["type"] == "json_schema");
            var schema = format["schema"];
            Assert((string)schema["properties"]["frame_id"]["enum"][0] == frame.FrameId);
            var objects = schema["properties"]["objects"];
            Assert((int)objects["minItems"] == 2 && (int)objects["maxItems"] == 2);
            Assert(((JArray)objects["items"]["properties"]["object_id"]["enum"]).Count == 2);
            Assert(objects["items"]["properties"]["name"] == null && objects["items"]["properties"]["attention_score"] == null);
            foreach (var key in new[] {"salience", "expectancy", "value"}) {
                Assert((int)objects["items"]["properties"][key]["minimum"] == 0);
                Assert((int)objects["items"]["properties"][key]["maximum"] == 10);
            }
        });
        Check("No silent top-five truncation: all eight detections are scored", () => {
            var frame = Frame(8);
            var request = JObject.Parse(OpenAIObjectRecognition.CreateRequest(new byte[] {1}, "test", "English", frame));
            Assert((int)request["text"]["format"]["schema"]["properties"]["objects"]["maxItems"] == 8);
            Assert(Parse(Scores(frame), frame).Objects.Length == 8);
        });
        Check("Zero and overflowing detection counts do not produce upload requests", () => {
            Reject(() => OpenAIObjectRecognition.CreateRequest(new byte[] {1}, "test", "English", Frame(0)));
            Reject(() => OpenAIObjectRecognition.CreateRequest(new byte[] {1}, "test", "English", Frame(33)));
        });
        Check("Invalid image, model, duplicate IDs and malformed geometry rejected", () => {
            Reject(() => OpenAIObjectRecognition.CreateRequest(new byte[0], "test", "English", Frame()));
            Reject(() => OpenAIObjectRecognition.CreateRequest(new byte[] {1}, " ", "English", Frame()));
            var duplicate = Frame(2); duplicate.Objects[1].Id = duplicate.Objects[0].Id;
            Reject(() => OpenAIObjectRecognition.ValidateFrame(duplicate));
            foreach (var bounds in new[] {new[] {-0.1,0d,1d,1d}, new[] {0.8,0d,0.3,1d}, new[] {0d,0d,double.NaN,1d}}) {
                var frame = Frame(); frame.Objects[0].Bbox = bounds;
                Reject(() => OpenAIObjectRecognition.ValidateFrame(frame));
            }
            var badPosition = Frame(); badPosition.Objects[0].WorldPosition[1] = double.PositiveInfinity;
            Reject(() => OpenAIObjectRecognition.ValidateFrame(badPosition));
        });
        Check("Depth unavailable is explicitly null, never a fake position", () => {
            var frame = Frame(); frame.Objects[0] = Detection(depth:false);
            var result = Parse(Scores(frame), frame);
            Assert(JObject.Parse(result.Json)["objects"][0]["world_position_m"].Type == JTokenType.Null);
        });
        Check("Unicode and escaping survive request and result serialization", () => {
            var frame = Frame(); frame.Objects[0].Name = "杯子\"\n";
            var request = JObject.Parse(OpenAIObjectRecognition.CreateRequest(new byte[] {1}, "model\"\\", "中文\"", frame));
            Assert((string)request["model"] == "model\"\\");
            var result = Parse(Scores(frame), frame);
            Assert(result.Objects[0].Name == frame.Objects[0].Name);
        });
        Check("Prompt preserves independent cooking attention dimensions and one-frame uncertainty", () => {
            var request = JObject.Parse(OpenAIObjectRecognition.CreateRequest(new byte[] {1}, "test", "English", Frame()));
            var prompt = (string)request["instructions"];
            Assert(prompt.Contains("Cooking garlic pork chops") && prompt.Contains("ONE still image"));
            Assert(prompt.Contains("Salience:") && prompt.Contains("Expectancy:") && prompt.Contains("Value:"));
            Assert(prompt.Contains("EVERY supplied detection") && prompt.Contains("object_id"));
        });
        Check("Response envelope extraction and local metadata retention", () => {
            var frame = Frame(); var result = Parse(Scores(frame), frame);
            Assert(result.Objects[0].AttentionScore == 30 && result.Objects[0].ObjectId == frame.Objects[0].Id);
            var json = JObject.Parse(result.Json);
            Assert((string)json["frame_id"] == frame.FrameId && (string)json["space_id"] == frame.SpaceId);
            Assert((double)json["objects"][0]["attention_score"] == 30);
            Assert((double)json["objects"][0]["world_position_m"][2] == 2);
        });
        Check("Wrong frame, unknown IDs and missing IDs rejected", () => {
            var frame = Frame(); var data = Scores(frame); data["frame_id"] = "old_frame";
            Reject(() => Parse(data, frame));
            data = Scores(frame); data["objects"][0]["object_id"] = "invented";
            Reject(() => Parse(data, frame));
            data = Scores(frame); ((JObject)data["objects"][0]).Remove("object_id");
            Reject(() => Parse(data, frame));
        });
        Check("Duplicate, omitted and extra instance scores rejected", () => {
            var frame = Frame(2); var data = Scores(frame);
            data["objects"][1]["object_id"] = frame.Objects[0].Id;
            Reject(() => Parse(data, frame));
            data = Scores(frame); ((JArray)data["objects"]).RemoveAt(1);
            Reject(() => Parse(data, frame));
            data = Scores(frame); ((JArray)data["objects"]).Add(data["objects"][0].DeepClone());
            Reject(() => Parse(data, frame));
        });
        Check("Provider cannot overwrite local labels, positions or calculated totals", () => {
            foreach (var key in new[] {"name", "world_position_m", "attention_score"}) {
                var frame = Frame(); var data = Scores(frame); data["objects"][0][key] = "forged";
                Reject(() => Parse(data, frame));
            }
        });
        Check("Weighted ranking preserves instance association when response order changes", () => {
            var frame = Frame(2); frame.Objects[1].Name = "bottle";
            var data = Scores(frame);
            data["objects"][0]["salience"] = 10; data["objects"][0]["expectancy"] = 0; data["objects"][0]["value"] = 0;
            data["objects"][1]["salience"] = 2; data["objects"][1]["expectancy"] = 8; data["objects"][1]["value"] = 10;
            data["objects"] = new JArray(((JArray)data["objects"]).Reverse());
            var result = Parse(data, frame, new OpenAIObjectRecognition.AttentionWeights(2,3,5));
            Assert(result.Objects[0].ObjectId == frame.Objects[1].Id && Math.Abs(result.Objects[0].AttentionScore - 78) < 0.00001);
            Assert(result.Objects[0].Name == "bottle");
            result.ApplyWeights(new OpenAIObjectRecognition.AttentionWeights(1,0,0));
            Assert(result.Objects[0].ObjectId == frame.Objects[0].Id && result.Objects[0].AttentionScore == 100);
            Assert((string)JObject.Parse(result.Json)["objects"][0]["object_id"] == frame.Objects[0].Id);
        });
        Check("Tied scores choose exactly one winner deterministically by ID", () => {
            var frame = Frame(2); var data = Scores(frame); data["objects"] = new JArray(((JArray)data["objects"]).Reverse());
            var result = Parse(data, frame);
            Assert(result.Objects[0].ObjectId == frame.Objects[0].Id);
        });
        Check("Invalid scores and weights rejected; fractional boundaries accepted", () => {
            foreach (var key in new[] {"salience", "expectancy", "value"})
                foreach (var value in new JToken[] {-1, 10.1, "8", true, JValue.CreateNull(), new JValue(double.NaN)}) {
                    var frame = Frame(); var data = Scores(frame); data["objects"][0][key] = value;
                    Reject(() => Parse(data, frame));
                }
            Reject(() => new OpenAIObjectRecognition.AttentionWeights(0,0,0));
            Reject(() => new OpenAIObjectRecognition.AttentionWeights(-1,1,1));
            Reject(() => new OpenAIObjectRecognition.AttentionWeights(1,double.PositiveInfinity,1));
            var valid = Frame(); var scores = Scores(valid);
            scores["objects"][0]["salience"] = 0; scores["objects"][0]["expectancy"] = 5.5; scores["objects"][0]["value"] = 10;
            Assert(Math.Abs(Parse(scores, valid).Objects[0].AttentionScore - 155.0/3) < 0.00001);
        });
        Check("Refused, incomplete, empty and malformed output rejected", () => {
            var frame = Frame(); var response = Envelope(Scores(frame)); response["status"] = "incomplete";
            Reject(() => OpenAIObjectRecognition.ParseResponse(response.ToString(), frame));
            response = Envelope(Scores(frame)); response["output"][1]["content"] = new JArray(new JObject { ["type"] = "refusal" });
            Reject(() => OpenAIObjectRecognition.ParseResponse(response.ToString(), frame));
            Reject(() => OpenAIObjectRecognition.ParseResponse("not json", frame));
            Reject(() => OpenAIObjectRecognition.ParseResponse("{\"status\":\"completed\",\"output\":[]}", frame));
            response = Envelope(Scores(frame)); response["output"][1]["content"][0]["text"] = "```json\n{}\n```";
            Reject(() => OpenAIObjectRecognition.ParseResponse(response.ToString(), frame));
        });
        Check("Result and frame snapshots are isolated from later detector mutations", () => {
            var frame = Frame(); var copy = frame.Copy(); var result = Parse(Scores(frame), frame);
            frame.Objects[0].WorldPosition[2] = 999; frame.Objects[0].Bbox[0] = 0.9; frame.Objects[0].Name = "changed";
            Assert(copy.Objects[0].WorldPosition[2] == 2 && result.Objects[0].Detection.WorldPosition[2] == 2);
            Assert(result.Objects[0].Name == "cup");
            result.ApplyWeights(new OpenAIObjectRecognition.AttentionWeights());
            Assert((double)JObject.Parse(result.Json)["objects"][0]["world_position_m"][2] == 2);
        });
        Check("Same-class instances get unique stable IDs despite reordered detections", () => {
            var tracker = new CameraObjectTracker();
            var first = new List<CameraDetectedObject> {Detection(x:0), Detection(x:0.5)};
            tracker.Update(first, 0, false);
            Assert(first[0].Id != first[1].Id);
            var second = new List<CameraDetectedObject> {Detection(x:0.51), Detection(x:0.01)};
            tracker.Update(second, 0.2, false);
            Assert(second[0].Id == first[1].Id && second[1].Id == first[0].Id);
        });
        Check("Association is one-to-one and does not reuse IDs across classes", () => {
            var tracker = new CameraObjectTracker(); var first = new List<CameraDetectedObject> {Detection()}; tracker.Update(first,0,false);
            var next = new List<CameraDetectedObject> {Detection(x:0.01), Detection(x:0.02), Detection(category:42)};
            tracker.Update(next,0.2,false);
            Assert(next.Select(i=>i.Id).Distinct().Count() == 3);
            Assert(next[0].Id == first[0].Id && next[2].Id != first[0].Id);
        });
        Check("Depthless image association only allowed for a stable viewpoint", () => {
            var tracker = new CameraObjectTracker(); var first = new List<CameraDetectedObject> {Detection(depth:false)}; tracker.Update(first,0,false);
            var next = new List<CameraDetectedObject> {Detection(depth:false)}; tracker.Update(next,0.2,true);
            Assert(next[0].Id == first[0].Id);
            var movedView = new List<CameraDetectedObject> {Detection(depth:false)}; tracker.Update(movedView,0.4,false);
            Assert(movedView[0].Id != next[0].Id);
        });
        Check("Expired, recentered and distant objects do not inherit old scores via ID reuse", () => {
            var tracker = new CameraObjectTracker(); var first = new List<CameraDetectedObject> {Detection()}; tracker.Update(first,0,false);
            var distant = new List<CameraDetectedObject> {Detection(x:0.5)}; tracker.Update(distant,0.2,true);
            Assert(distant[0].Id != first[0].Id);
            var expired = new List<CameraDetectedObject> {Detection()}; tracker.Update(expired,3,true);
            Assert(expired[0].Id != first[0].Id);
            tracker.Clear(); var recentered = new List<CameraDetectedObject> {Detection()}; tracker.Update(recentered,3.1,true);
            Assert(recentered[0].Id != expired[0].Id);
        });
        Check("Class-aware NMS suppresses duplicates but keeps overlapping different classes", () => {
            var a = Detection(); var duplicate = Detection(); duplicate.Confidence = 0.5;
            var otherClass = Detection(category:42);
            var kept = CameraObjectTracker.SuppressOverlaps(new[] {a,duplicate,otherClass},0.4);
            Assert(kept.Count == 2 && kept.Contains(a) && kept.Contains(otherClass));
            Assert(CameraObjectTracker.IoU(a.Bbox, a.Bbox) == 1);
        });
        Check("Highlight follows the exact current winner only", () => {
            var frame = Frame(2);
            Assert(CameraHighlightPolicy.FindWinner(frame,frame.Objects[1].Id,frame.SpaceId,10.3,10.2,1.5,30) == frame.Objects[1]);
            Assert(CameraHighlightPolicy.FindWinner(frame,"lost_id",frame.SpaceId,10.3,10.2,1.5,30) == null);
            frame.Objects[1].WorldPosition = null;
            Assert(CameraHighlightPolicy.FindWinner(frame,frame.Objects[1].Id,frame.SpaceId,10.3,10.2,1.5,30) == null);
        });
        Check("Highlight hidden on stale frame, recenter or expired score", () => {
            var frame = Frame(); var id = frame.Objects[0].Id;
            Assert(CameraHighlightPolicy.FindWinner(frame,id,"different_space",10.3,10.2,1.5,30) == null);
            Assert(CameraHighlightPolicy.FindWinner(frame,id,frame.SpaceId,12,10.2,1.5,30) == null);
            frame.CaptureRealtime = 42;
            Assert(CameraHighlightPolicy.FindWinner(frame,id,frame.SpaceId,42.2,10,1.5,30) == null);
        });
        Check("Actionable HTTP errors retained", () => {
            Assert(OpenAIObjectRecognition.HttpError(401).Contains("key"));
            Assert(OpenAIObjectRecognition.HttpError(429).Contains("quota"));
            Assert(OpenAIObjectRecognition.HttpError(0).Contains("timed out"));
        });
        Console.WriteLine(passed + " tests passed; no API requests made.");
        return 0;
    }
}
