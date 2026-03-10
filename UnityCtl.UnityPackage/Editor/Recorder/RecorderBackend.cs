using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
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
        private int _lastFrameCount;

        // Cached reflection info (resolved once)
        private static FieldInfo s_sessionsField;
        private static FieldInfo s_recorderField;
        private static PropertyInfo s_countProp;
        private static bool s_reflectionResolved;

        public string StartRecording(string outputName, double? duration, int? frames, int? width, int? height, int fps)
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

            if (frames.HasValue)
            {
                // SetRecordModeToFrameInterval end is exclusive (stops when count > end - start),
                // so (0, N-1) records exactly N frames.
                _controllerSettings.SetRecordModeToFrameInterval(0, frames.Value - 1);
            }
            else if (duration.HasValue)
            {
                var totalFrames = (int)(fps * duration.Value);
                _controllerSettings.SetRecordModeToFrameInterval(0, totalFrames - 1);
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
            _movieRecorder.EncoderSettings = new CoreEncoderSettings
            {
                Codec = CoreEncoderSettings.OutputCodec.MP4,
                EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High
            };

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
            _lastFrameCount = 0;
            _controller.StartRecording();

            return outputPath;
        }

        public void StopRecording()
        {
            // Capture final count before stopping (StopRecording nulls out sessions)
            _lastFrameCount = ReadRecordedFramesCount();
            _controller?.StopRecording();
        }

        public bool IsRecording()
        {
            if (_controller == null || !_controller.IsRecording())
            {
                // Recording just stopped — capture the final count before sessions get cleaned up
                if (_lastFrameCount == 0)
                    _lastFrameCount = ReadRecordedFramesCount();
                return false;
            }
            return true;
        }

        public int GetRecordedFrameCount()
        {
            // Try live count first, fall back to cached value
            var live = ReadRecordedFramesCount();
            if (live > 0)
                _lastFrameCount = live;
            return _lastFrameCount;
        }

        /// <summary>
        /// Read RecordedFramesCount from the recorder via reflection.
        /// Returns 0 if sessions are not available.
        /// </summary>
        private int ReadRecordedFramesCount()
        {
            if (_controller == null) return 0;

            EnsureReflection();
            if (s_sessionsField == null) return 0;

            if (s_sessionsField.GetValue(_controller) is System.Collections.IList sessions && sessions.Count > 0)
            {
                var session = sessions[0];
                var recorder = s_recorderField?.GetValue(session);
                if (recorder != null && s_countProp != null)
                    return (int)s_countProp.GetValue(recorder);
            }
            return 0;
        }

        private static void EnsureReflection()
        {
            if (s_reflectionResolved) return;
            s_reflectionResolved = true;

            s_sessionsField = typeof(RecorderController).GetField(
                "m_RecordingSessions", BindingFlags.NonPublic | BindingFlags.Instance);

            var sessionType = typeof(RecordingSession);
            s_recorderField = sessionType.GetField("recorder");

            // RecordedFramesCount is protected internal on Recorder
            var recorderType = typeof(UnityEditor.Recorder.Recorder);
            s_countProp = recorderType.GetProperty(
                "RecordedFramesCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
            _lastFrameCount = 0;
        }
    }
}
