#!/usr/bin/env python3
"""Compile/run the real protocol code offline using Unity's bundled Mono tools."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--unity-contents", type=Path, default=Path(
    "/Applications/Unity/Hub/Editor/6000.0.61f1/Unity.app/Contents"))
args = parser.parse_args()
mono_root = args.unity_contents / "MonoBleedingEdge"
newtonsoft = next((ROOT / "Library/PackageCache").glob(
    "com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll"), None)
if newtonsoft is None:
    parser.error("Open the project in Unity once to restore its Newtonsoft.Json package.")
with tempfile.TemporaryDirectory(prefix="camera-viewer-tests-") as directory:
    work = Path(directory)
    shutil.copy2(newtonsoft, work / "Newtonsoft.Json.dll")
    target = work / "ProtocolTests.exe"
    subprocess.run([
        str(mono_root / "bin/mono"), str(mono_root / "lib/mono/4.5/mcs.exe"),
        "-langversion:latest", "-out:" + str(target),
        "-r:" + str(newtonsoft),
        "-r:" + str(mono_root / "lib/mono/4.5/Facades/netstandard.dll"),
        str(ROOT / "Assets/PassthroughCameraApiSamples/CameraViewer/Scripts/OpenAIObjectRecognition.cs"),
        str(ROOT / "Assets/PassthroughCameraApiSamples/CameraViewer/Scripts/CameraObjectTracking.cs"),
        str(Path(__file__).with_name("ProtocolTests.cs")),
    ], check=True)
    subprocess.run([str(mono_root / "bin/mono"), str(target)], check=True,
                   env={**os.environ, "MONO_PATH": str(work)})
