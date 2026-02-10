using System;
using UnityEngine;
using UnityEditor;
using UnityCtl.Protocol;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Manages video recording state. Delegates to IRecordingBackend when Unity Recorder is available.
    /// The backend is discovered lazily via reflection when a record command first arrives.
    /// </summary>
    public class RecordingManager
    {
        private static RecordingManager _instance;
        public static RecordingManager Instance => _instance ??= new RecordingManager();

        private IRecordingBackend _backend;
        private bool _backendResolved;
        private string _recordingId;
        private string _outputPath;
        private DateTime _startTime;
        private bool _isRecording;
        private bool _hasDuration;
        private Action<RecordFinishedPayload> _onFinished;

        public bool IsRecording => _isRecording;

        public double Elapsed => _isRecording ? (DateTime.UtcNow - _startTime).TotalSeconds : 0;
        public int FrameCount => _isRecording ? _backend.GetRecordedFrameCount() : 0;

        private void EnsureBackend()
        {
            if (_backendResolved) return;
            _backendResolved = true;

            var backendType = Type.GetType("UnityCtl.Editor.Recorder.RecorderBackend, UnityCtl.Recorder");
            if (backendType != null)
            {
                _backend = (IRecordingBackend)Activator.CreateInstance(backendType);
            }
        }

        public RecordStartResult Start(string outputName, double? duration, int? width, int? height, int fps, Action<RecordFinishedPayload> onFinished)
        {
            EnsureBackend();

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
            _hasDuration = duration.HasValue;
            _onFinished = onFinished;
            _outputPath = _backend.StartRecording(outputName, duration, width, height, fps);

            _isRecording = true;
            _startTime = DateTime.UtcNow;

            if (_hasDuration)
            {
                EditorApplication.update += PollRecordingFinished;
            }

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

            var frameCount = _backend.GetRecordedFrameCount();
            _backend.StopRecording();
            var duration = (DateTime.UtcNow - _startTime).TotalSeconds;

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

        private void PollRecordingFinished()
        {
            if (!_isRecording || !_hasDuration || _backend == null)
            {
                EditorApplication.update -= PollRecordingFinished;
                return;
            }

            if (!_backend.IsRecording())
            {
                EditorApplication.update -= PollRecordingFinished;

                var frameCount = _backend.GetRecordedFrameCount();
                var duration = (DateTime.UtcNow - _startTime).TotalSeconds;

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
            EditorApplication.update -= PollRecordingFinished;
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

            var frameCount = _backend.GetRecordedFrameCount();

            try
            {
                _backend?.StopRecording();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCtl] Error stopping recording: {ex.Message}");
            }

            var duration = (DateTime.UtcNow - _startTime).TotalSeconds;

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
        /// Get the actual number of frames recorded (using Time.frameCount with CapFrameRate).
        /// Valid while recording and after StopRecording() but before Cleanup().
        /// </summary>
        int GetRecordedFrameCount();

        /// <summary>
        /// Clean up recording resources.
        /// </summary>
        void Cleanup();
    }
}
