using System;
using System.IO;
using UnityEngine;
using UnityCtl.Protocol;

#if UNITYCTL_RECORDER
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
#endif

namespace UnityCtl.Editor
{
    /// <summary>
    /// Manages video recording via Unity Recorder package.
    /// Guarded by UNITYCTL_RECORDER define (auto-set via Version Defines when com.unity.recorder is installed).
    /// </summary>
    public class RecordingManager
    {
        private static RecordingManager _instance;
        public static RecordingManager Instance => _instance ??= new RecordingManager();

#if UNITYCTL_RECORDER
        private RecorderController _controller;
        private RecorderControllerSettings _controllerSettings;
#endif

        private string _recordingId;
        private string _outputPath;
        private DateTime _startTime;
        private int _fps;
        private bool _isRecording;
        private Action<RecordFinishedPayload> _onFinished;

        public bool IsRecording => _isRecording;
        public string RecordingId => _recordingId;
        public string OutputPath => _outputPath;

        public double Elapsed => _isRecording ? (DateTime.UtcNow - _startTime).TotalSeconds : 0;
        public int FrameCount => _isRecording ? (int)(Elapsed * _fps) : 0;

        public RecordStartResult Start(string outputName, double? duration, int? width, int? height, int fps, Action<RecordFinishedPayload> onFinished)
        {
#if !UNITYCTL_RECORDER
            throw new InvalidOperationException(
                "Unity Recorder package not found. Install com.unity.recorder via Package Manager: " +
                "Window > Package Manager > + > Add package by name > com.unity.recorder");
#else
            if (_isRecording)
            {
                throw new InvalidOperationException("Recording already in progress. Stop the current recording first.");
            }

            _recordingId = Guid.NewGuid().ToString();
            _fps = fps;
            _onFinished = onFinished;

            // Build output path
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

            // Unity Recorder appends frame range to filename, so we set it up properly
            // The output file path for Recorder is relative to the project
            _outputPath = $"Recordings/{outputName}";

            // Configure RecorderControllerSettings
            _controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            _controllerSettings.FrameRate = fps;
            _controllerSettings.CapFrameRate = true;

            if (duration.HasValue)
            {
                // Frame-based recording: calculate exact frame count
                var totalFrames = (int)(fps * duration.Value);
                _controllerSettings.SetRecordModeToFrameInterval(0, totalFrames);
            }
            else
            {
                _controllerSettings.SetRecordModeToManual();
            }

            // Configure MovieRecorderSettings
            var movieRecorder = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            movieRecorder.name = "UnityCtl Video Recorder";
            movieRecorder.Enabled = true;
            movieRecorder.OutputFile = _outputPath;
            movieRecorder.OutputFormat = MovieRecorderSettings.VideoRecorderOutputFormat.MP4;
            movieRecorder.VideoBitRateMode = VideoBitrateMode.High;

            // Configure input (game view)
            var inputSettings = new GameViewInputSettings();
            if (width.HasValue && height.HasValue)
            {
                inputSettings.OutputWidth = width.Value;
                inputSettings.OutputHeight = height.Value;
            }
            movieRecorder.ImageInputSettings = inputSettings;

            _controllerSettings.AddRecorderSettings(movieRecorder);

            // Create controller and start
            _controller = new RecorderController(_controllerSettings);
            _controller.PrepareRecording();
            _controller.StartRecording();

            _isRecording = true;
            _startTime = DateTime.UtcNow;

            Debug.Log($"[UnityCtl] Recording started: {_outputPath}.mp4 (fps: {fps}, duration: {(duration.HasValue ? $"{duration}s" : "manual")})");

            return new RecordStartResult
            {
                RecordingId = _recordingId,
                OutputPath = _outputPath + ".mp4",
                State = "recording"
            };
#endif
        }

        public RecordStopResult Stop()
        {
#if !UNITYCTL_RECORDER
            throw new InvalidOperationException(
                "Unity Recorder package not found. Install com.unity.recorder via Package Manager.");
#else
            if (!_isRecording)
            {
                throw new InvalidOperationException("No recording in progress.");
            }

            _controller.StopRecording();
            var duration = (DateTime.UtcNow - _startTime).TotalSeconds;
            var frameCount = (int)(duration * _fps);

            var result = new RecordStopResult
            {
                OutputPath = _outputPath + ".mp4",
                Duration = duration,
                FrameCount = frameCount
            };

            Debug.Log($"[UnityCtl] Recording stopped: {_outputPath}.mp4 ({duration:F1}s, {frameCount} frames)");

            Cleanup();
            return result;
#endif
        }

        /// <summary>
        /// Called from EditorApplication.update to check if a duration-based recording has finished.
        /// </summary>
        public void Update()
        {
#if UNITYCTL_RECORDER
            if (!_isRecording || _controller == null)
                return;

            // Check if the recorder has stopped (duration-based recording completed)
            if (!_controller.IsRecording())
            {
                var duration = (DateTime.UtcNow - _startTime).TotalSeconds;
                var frameCount = (int)(duration * _fps);

                Debug.Log($"[UnityCtl] Recording finished: {_outputPath}.mp4 ({duration:F1}s, {frameCount} frames)");

                var payload = new RecordFinishedPayload
                {
                    RecordingId = _recordingId,
                    OutputPath = _outputPath + ".mp4",
                    Duration = duration,
                    FrameCount = frameCount
                };

                var onFinished = _onFinished;
                Cleanup();
                onFinished?.Invoke(payload);
            }
#endif
        }

        public RecordStatusResult GetStatus()
        {
            return new RecordStatusResult
            {
                IsRecording = _isRecording,
                RecordingId = _isRecording ? _recordingId : null,
                OutputPath = _isRecording ? _outputPath + ".mp4" : null,
                Elapsed = _isRecording ? Elapsed : null,
                FrameCount = _isRecording ? FrameCount : null
            };
        }

        private void Cleanup()
        {
            _isRecording = false;
            _onFinished = null;
#if UNITYCTL_RECORDER
            if (_controllerSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_controllerSettings);
                _controllerSettings = null;
            }
            _controller = null;
#endif
            _recordingId = null;
            _outputPath = null;
        }

        /// <summary>
        /// Stop recording if active (called when play mode exits unexpectedly).
        /// </summary>
        public void StopIfActive()
        {
            if (!_isRecording) return;

#if UNITYCTL_RECORDER
            try
            {
                if (_controller != null && _controller.IsRecording())
                {
                    _controller.StopRecording();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCtl] Error stopping recording: {ex.Message}");
            }
#endif

            var duration = (DateTime.UtcNow - _startTime).TotalSeconds;
            var frameCount = (int)(duration * _fps);

            Debug.Log($"[UnityCtl] Recording stopped (play mode exited): {_outputPath}.mp4 ({duration:F1}s, {frameCount} frames)");

            var payload = new RecordFinishedPayload
            {
                RecordingId = _recordingId,
                OutputPath = _outputPath + ".mp4",
                Duration = duration,
                FrameCount = frameCount
            };

            var onFinished = _onFinished;
            Cleanup();
            onFinished?.Invoke(payload);
        }
    }
}
