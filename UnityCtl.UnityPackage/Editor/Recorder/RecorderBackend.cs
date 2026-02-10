using System;
using System.IO;
using UnityEngine;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;

namespace UnityCtl.Editor.Recorder
{
    /// <summary>
    /// Unity Recorder backend implementation. Only compiled when com.unity.recorder is installed
    /// (UNITYCTL_RECORDER define constraint on the asmdef).
    /// Discovered lazily by RecordingManager via reflection when a record command arrives.
    /// </summary>
    public class RecorderBackend : IRecordingBackend
    {
        private RecorderController _controller;
        private RecorderControllerSettings _controllerSettings;
        private MovieRecorderSettings _movieRecorder;

        public string StartRecording(string outputName, double? duration, int? width, int? height, int fps)
        {
            if (string.IsNullOrEmpty(outputName))
            {
                outputName = $"recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            }

            // Ensure Recordings directory exists
            var recordingsDir = Path.Combine(Application.dataPath, "..", "Recordings");
            recordingsDir = Path.GetFullPath(recordingsDir);
            if (!Directory.Exists(recordingsDir))
            {
                Directory.CreateDirectory(recordingsDir);
            }

            var outputPath = $"Recordings/{outputName}";

            // Configure RecorderControllerSettings
            _controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            _controllerSettings.FrameRate = fps;
            _controllerSettings.CapFrameRate = true;

            if (duration.HasValue)
            {
                var totalFrames = (int)(fps * duration.Value);
                _controllerSettings.SetRecordModeToFrameInterval(0, totalFrames);
            }
            else
            {
                _controllerSettings.SetRecordModeToManual();
            }

            // Configure MovieRecorderSettings
            _movieRecorder = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            _movieRecorder.name = "UnityCtl Video Recorder";
            _movieRecorder.Enabled = true;
            _movieRecorder.OutputFile = outputPath;
            _movieRecorder.OutputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4;
            _movieRecorder.VideoBitRateMode = VideoBitrateMode.High;

            // Configure input (game view)
            var inputSettings = new GameViewInputSettings();
            if (width.HasValue && height.HasValue)
            {
                // MP4 requires even dimensions
                inputSettings.OutputWidth = EnsureEven(width.Value);
                inputSettings.OutputHeight = EnsureEven(height.Value);
            }
            else
            {
                // Default game view size may have odd dimensions — ensure even for MP4
                inputSettings.OutputWidth = EnsureEven(inputSettings.OutputWidth);
                inputSettings.OutputHeight = EnsureEven(inputSettings.OutputHeight);
            }
            _movieRecorder.ImageInputSettings = inputSettings;

            _controllerSettings.AddRecorderSettings(_movieRecorder);

            // Create controller and start
            _controller = new RecorderController(_controllerSettings);
            _controller.PrepareRecording();
            _controller.StartRecording();

            return outputPath;
        }

        public void StopRecording()
        {
            _controller?.StopRecording();
        }

        public bool IsRecording()
        {
            return _controller != null && _controller.IsRecording();
        }

        private static int EnsureEven(int value)
        {
            return value % 2 == 0 ? value : value + 1;
        }

        public void Cleanup()
        {
            if (_movieRecorder != null)
            {
                UnityEngine.Object.DestroyImmediate(_movieRecorder);
                _movieRecorder = null;
            }
            if (_controllerSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_controllerSettings);
                _controllerSettings = null;
            }
            _controller = null;
        }
    }
}
