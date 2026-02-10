using System;
using UnityEngine;
using UnityCtl.Protocol;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Manages video recording state. Delegates to IRecordingBackend when Unity Recorder is available.
    /// The backend is registered by RecorderBackend (in the Recorder subfolder) when com.unity.recorder is installed.
    /// </summary>
    public class RecordingManager
    {
        private static RecordingManager _instance;
        public static RecordingManager Instance => _instance ??= new RecordingManager();

        private IRecordingBackend _backend;
        private string _recordingId;
        private string _outputPath;
        private DateTime _startTime;
        private int _fps;
        private bool _isRecording;
        private bool _hasDuration;
        private Action<RecordFinishedPayload> _onFinished;

        public bool IsRecording => _isRecording;

        public double Elapsed => _isRecording ? (DateTime.UtcNow - _startTime).TotalSeconds : 0;
        public int FrameCount => _isRecording ? (int)(Elapsed * _fps) : 0;

        /// <summary>
        /// Called by the Recorder assembly to register itself as the recording backend.
        /// </summary>
        public void RegisterBackend(IRecordingBackend backend)
        {
            _backend = backend;
            Debug.Log("[UnityCtl] Recording backend registered (Unity Recorder available)");
        }

        public RecordStartResult Start(string outputName, double? duration, int? width, int? height, int fps, Action<RecordFinishedPayload> onFinished)
        {
            if (_backend == null)
            {
                throw new InvalidOperationException(
                    "Unity Recorder package not found. Install com.unity.recorder via Package Manager: " +
                    "Window > Package Manager > + > Add package by name > com.unity.recorder");
            }

            if (_isRecording)
            {
                throw new InvalidOperationException("Recording already in progress. Stop the current recording first.");
            }

            _recordingId = Guid.NewGuid().ToString();
            _fps = fps;
            _hasDuration = duration.HasValue;
            _onFinished = onFinished;
            _outputPath = _backend.StartRecording(outputName, duration, width, height, fps);

            _isRecording = true;
            _startTime = DateTime.UtcNow;

            Debug.Log($"[UnityCtl] Recording started: {_outputPath}.mp4 (fps: {fps}, duration: {(duration.HasValue ? $"{duration}s" : "manual")})");

            return new RecordStartResult
            {
                RecordingId = _recordingId,
                OutputPath = _outputPath + ".mp4",
                State = "recording"
            };
        }

        public RecordStopResult Stop()
        {
            if (!_isRecording)
            {
                throw new InvalidOperationException("No recording in progress.");
            }

            _backend?.StopRecording();
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
        }

        /// <summary>
        /// Called from EditorApplication.update to check if a duration-based recording has finished.
        /// </summary>
        public void Update()
        {
            if (!_isRecording || !_hasDuration || _backend == null)
                return;

            if (!_backend.IsRecording())
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
            _hasDuration = false;
            _onFinished = null;
            _backend?.Cleanup();
            _recordingId = null;
            _outputPath = null;
        }

        /// <summary>
        /// Stop recording if active (called when play mode exits unexpectedly).
        /// </summary>
        public void StopIfActive()
        {
            if (!_isRecording) return;

            try
            {
                _backend?.StopRecording();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCtl] Error stopping recording: {ex.Message}");
            }

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

    /// <summary>
    /// Interface for recording backends. Implemented by RecorderBackend when com.unity.recorder is installed.
    /// </summary>
    public interface IRecordingBackend
    {
        /// <summary>
        /// Start recording. Returns the output path (without extension).
        /// </summary>
        string StartRecording(string outputName, double? duration, int? width, int? height, int fps);

        /// <summary>
        /// Stop recording.
        /// </summary>
        void StopRecording();

        /// <summary>
        /// Check if the recorder is currently recording (for detecting duration-based completion).
        /// </summary>
        bool IsRecording();

        /// <summary>
        /// Clean up recording resources.
        /// </summary>
        void Cleanup();
    }
}
