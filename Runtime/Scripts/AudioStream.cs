using System;
using UnityEngine;
using LiveKit.Internal;
using LiveKit.Proto;
using System.Runtime.InteropServices;

namespace LiveKit
{
    // from https://github.com/Unity-Technologies/com.unity.webrtc
    internal class AudioFilter : MonoBehaviour
    {
        public delegate void OnAudioDelegate(float[] data, int channels, int sampleRate);
        // Event is called from the Unity audio thread
        public event OnAudioDelegate AudioRead;
        private int _sampleRate;

        void OnEnable()
        {
            OnAudioConfigurationChanged(false);
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
        }

        void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            _sampleRate = AudioSettings.outputSampleRate;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            // Called by Unity on the Audio thread
            AudioRead?.Invoke(data, channels, _sampleRate);
        }
    }

    public class AudioStream
    {
        internal readonly FfiHandle Handle;
        private AudioSource _audioSource;
        private AudioFilter _audioFilter;
        private RingBuffer _buffer;
        private short[] _tempBuffer;
        private uint _numChannels;
        private uint _sampleRate;
        private object _lock = new object();

        public AudioStream(IAudioTrack audioTrack, AudioSource source)
        {
            if (!audioTrack.Room.TryGetTarget(out var room))
                throw new InvalidOperationException("audiotrack's room is invalid");

            if (!audioTrack.Participant.TryGetTarget(out var participant))
                throw new InvalidOperationException("audiotrack's participant is invalid");

            var newAudioStream = new NewAudioStreamRequest();
            newAudioStream.RoomHandle = new FFIHandleId { Id = (ulong)room.Handle.DangerousGetHandle() };
            newAudioStream.ParticipantSid = participant.Sid;
            newAudioStream.TrackSid = audioTrack.Sid;
            newAudioStream.Type = AudioStreamType.AudioStreamNative;

            var request = new FFIRequest();
            request.NewAudioStream = newAudioStream;

            var resp = FfiClient.SendRequest(request);
            var streamInfo = resp.NewAudioStream.Stream;

            Handle = new FfiHandle((IntPtr)streamInfo.Handle.Id);
            FfiClient.Instance.AudioStreamEventReceived += OnAudioStreamEvent;

            UpdateSource(source);
        }

        private void UpdateSource(AudioSource source)
        {
            _audioSource = source;
            _audioFilter = source.gameObject.AddComponent<AudioFilter>();
            //_audioFilter.hideFlags = HideFlags.HideInInspector;
            _audioFilter.AudioRead += OnAudioRead;
            source.Play();
        }

        // Called on Unity audio thread
        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            lock (_lock)
            {
                if (_buffer == null || channels != _numChannels || sampleRate != _sampleRate || data.Length != _tempBuffer.Length)
                {
                    int size = (int)(channels * sampleRate * 0.2);
                    _buffer = new RingBuffer(size * sizeof(short));
                    _tempBuffer = new short[data.Length];
                    _numChannels = (uint)channels;
                    _sampleRate = (uint)sampleRate;
                }


                static float S16ToFloat(short v)
                {
                    return v / 32768f;
                }

                // "Send" the data to Unity
                var temp = MemoryMarshal.Cast<short, byte>(_tempBuffer.AsSpan());
                int read = _buffer.Read(temp);

                Array.Clear(data, 0, data.Length);

                for (int i = 0; i < read / sizeof(short); i++)
                    data[i] = S16ToFloat(_tempBuffer[i]);
            }
        }

        // Called on the MainThread (See FfiClient)
        private void OnAudioStreamEvent(AudioStreamEvent e)
        {
            if (e.Handle.Id != (ulong)Handle.DangerousGetHandle())
                return;

            if (e.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived)
                return;

            var info = e.FrameReceived.Frame;
            var handle = new FfiHandle((IntPtr)info.Handle.Id);

            lock (_lock)
            {
                unsafe
                {
                    uint len = info.SamplesPerChannel * info.NumChannels;
                    var data = new Span<byte>(((IntPtr)info.DataPtr).ToPointer(), (int)len);
                    _buffer?.Write(data);
                }
            }
        }
    }
}
