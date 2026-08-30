using System.Runtime.InteropServices;

namespace MSUIClient.World.Sound;

/// <summary>
/// One process-owned PCM output stream. Logical voices are mixed here before a
/// sample reaches WinMM, so their gain and pan are genuinely independent.
///
/// This boundary is load-bearing. On modern Windows, legacy waveOut streams from
/// one process join the same audio session. <c>waveOutSetVolume</c> therefore
/// changes the application's session volume; it is not a per-handle voice control.
/// Giving every footstep its own handle and volume call made the footstep's
/// distance/pan pump the music and ambience even while every stream consumed bytes
/// on time. The shared output below never calls that API. Per-voice levels are
/// multiplied into samples, then the finished stereo mix is submitted at unity.
/// </summary>
internal sealed class SharedWaveOutMixer : IDisposable
{
    internal const int OutputRate = 48_000;
    private const int OutputChannels = 2;
    private const int OutputBits = 16;
    private const int OutputBlockAlign = OutputChannels * OutputBits / 8;
    private const int BufferFrames = 480; // 10 ms
    private const int BufferCount = 4;    // 40 ms queued against scheduler jitter
    private const int BufferBytes = BufferFrames * OutputBlockAlign;

    internal readonly record struct PcmClip(
        byte[] Bytes,
        int DataOffset,
        int DataLength,
        ushort Channels,
        uint SampleRate,
        ushort BitsPerSample,
        ushort BlockAlign,
        int FrameCount)
    {
        public uint BytesPerSecond => SampleRate * BlockAlign;

        public static bool TryRead(byte[] wav, out PcmClip clip)
        {
            clip = default;
            if (wav.Length < 44 ||
                wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
                wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
                return false;

            ushort channels = 0;
            uint sampleRate = 0;
            ushort bits = 0;
            ushort blockAlign = 0;
            int dataOffset = 0;
            int dataLength = 0;
            int at = 12;
            while (at + 8 <= wav.Length)
            {
                uint unsignedSize = BitConverter.ToUInt32(wav, at + 4);
                if (unsignedSize > int.MaxValue) return false;
                int size = (int)unsignedSize;
                int body = at + 8;
                if (body > wav.Length || size > wav.Length - body) size = wav.Length - body;

                if (wav[at] == 'f' && wav[at + 1] == 'm' &&
                    wav[at + 2] == 't' && wav[at + 3] == ' ')
                {
                    if (size < 16 || body + 16 > wav.Length ||
                        BitConverter.ToUInt16(wav, body) != 1)
                        return false;
                    channels = BitConverter.ToUInt16(wav, body + 2);
                    sampleRate = BitConverter.ToUInt32(wav, body + 4);
                    blockAlign = BitConverter.ToUInt16(wav, body + 12);
                    bits = BitConverter.ToUInt16(wav, body + 14);
                }
                else if (wav[at] == 'd' && wav[at + 1] == 'a' &&
                         wav[at + 2] == 't' && wav[at + 3] == 'a')
                {
                    dataOffset = body;
                    dataLength = Math.Max(0, size);
                }

                if (channels != 0 && dataLength > 0) break;
                long next = (long)body + unsignedSize + (unsignedSize & 1u);
                if (next <= at || next > wav.Length) break;
                at = (int)next;
            }

            ushort expectedAlign = (ushort)(channels * (bits / 8));
            if (channels is 0 or > 2 || sampleRate == 0 || bits is not (8 or 16) ||
                expectedAlign == 0 || blockAlign != expectedAlign || dataLength < expectedAlign)
                return false;

            int frames = dataLength / expectedAlign;
            clip = new PcmClip(wav, dataOffset, frames * expectedAlign, channels,
                sampleRate, bits, expectedAlign, frames);
            return true;
        }

        public float Sample(int frame, int channel)
        {
            if (Channels == 1) channel = 0;
            int at = DataOffset + frame * BlockAlign + channel * (BitsPerSample / 8);
            return BitsPerSample == 16
                ? BitConverter.ToInt16(Bytes, at) / 32768f
                : (Bytes[at] - 128) / 128f;
        }
    }

    /// <summary>A logical source owned by the shared renderer.</summary>
    internal sealed class Voice : IDisposable
    {
        private const int GainRampFrames = OutputRate * 15 / 1000;
        private const int StopRampFrames = OutputRate * 15 / 1000;

        private readonly SharedWaveOutMixer _owner;
        private readonly PcmClip _clip;
        private readonly uint _playbackFrequency;
        private readonly object _targetLock = new();
        private double _sourceFrame;
        private float _targetGain;
        private float _targetPan;
        private float _renderLeft;
        private float _renderRight;
        private float _rampTargetLeft;
        private float _rampTargetRight;
        private int _rampFramesLeft;
        private long _startOutputFrame = -1;
        private long _endOutputFrame = -1;
        private int _stopFramesLeft;
        private int _stopping;
        private int _retired;
        private int _activated;

        public bool Looping { get; }
        public uint BytesPerSecond => _playbackFrequency * _clip.BlockAlign;
        public bool Retired => Volatile.Read(ref _retired) != 0;

        internal Voice(SharedWaveOutMixer owner, in PcmClip clip,
            bool looping, float gain, float pan, uint playbackFrequency, bool activated)
        {
            _owner = owner;
            _clip = clip;
            _playbackFrequency = playbackFrequency == 0
                ? clip.SampleRate : playbackFrequency;
            Looping = looping;
            _targetGain = Math.Clamp(gain, 0f, 1f);
            _targetPan = Math.Clamp(pan, -1f, 1f);
            (_renderLeft, _renderRight) =
                SpatialAudioLaw.StereoLevels(_targetGain, _targetPan);
            _rampTargetLeft = _renderLeft;
            _rampTargetRight = _renderRight;
            _activated = activated ? 1 : 0;
        }

        internal void Activate()
        {
            if (!Retired) Volatile.Write(ref _activated, 1);
        }

        public void SetGain(float gain)
        {
            lock (_targetLock) _targetGain = Math.Clamp(gain, 0f, 1f);
        }

        public void SetMix(float gain, float pan)
        {
            // Gain and pan are one control state. Publishing two volatile floats
            // allowed the render thread to observe old-gain/new-pan for a period.
            lock (_targetLock)
            {
                _targetGain = Math.Clamp(gain, 0f, 1f);
                _targetPan = Math.Clamp(pan, -1f, 1f);
                // OpenPending deliberately creates an inaudible source while the
                // driver is opening. Mix changes received during that window are
                // its initial state, not an audible transition from stale values.
                if (Volatile.Read(ref _activated) == 0 &&
                    Volatile.Read(ref _startOutputFrame) < 0)
                {
                    (_renderLeft, _renderRight) =
                        SpatialAudioLaw.StereoLevels(_targetGain, _targetPan);
                    _rampTargetLeft = _renderLeft;
                    _rampTargetRight = _renderRight;
                    _rampFramesLeft = 0;
                }
            }
        }

        public uint? PlayedBytes()
        {
            long start = Volatile.Read(ref _startOutputFrame);
            if (start < 0) return 0;
            if (!_owner.TryPlayedOutputFrames(out long playedOutputFrames)) return null;
            long outputFrames = Math.Max(0, playedOutputFrames - start);
            long end = Volatile.Read(ref _endOutputFrame);
            if (end >= start) outputFrames = Math.Min(outputFrames, end - start);
            ulong sourceFrames = (ulong)outputFrames * _playbackFrequency / OutputRate;
            return unchecked((uint)(sourceFrames * _clip.BlockAlign));
        }

        public bool Finished
        {
            get
            {
                // A renderer that exhausted its write retries cannot advance a
                // source clock. Report its voices finished so AudioMixer reaps
                // dead logical routes instead of believing silent loops are live.
                if (_owner.Unhealthy) return true;
                long end = Volatile.Read(ref _endOutputFrame);
                return end >= 0 && _owner.CompletedOutputFrames >= end;
            }
        }

        /// <summary>
        /// Mix one output period. Gain changes glide across the period instead of
        /// stepping at its edge; an explicit stop gets the same 15 ms de-click ramp
        /// used by the reference client before this source retires.
        /// </summary>
        internal void Mix(float[] left, float[] right, long bufferStartFrame)
        {
            if (Volatile.Read(ref _activated) == 0 || Retired ||
                Volatile.Read(ref _endOutputFrame) >= 0) return;

            float gain;
            float pan;
            lock (_targetLock)
            {
                gain = _targetGain;
                pan = _targetPan;
            }
            (float targetLeft, float targetRight) =
                SpatialAudioLaw.StereoLevels(gain, pan);
            bool stopping = Volatile.Read(ref _stopping) != 0;
            if (stopping) targetLeft = targetRight = 0f;
            if (targetLeft != _rampTargetLeft || targetRight != _rampTargetRight)
            {
                _rampTargetLeft = targetLeft;
                _rampTargetRight = targetRight;
                _rampFramesLeft = GainRampFrames;
            }
            double sourceStep = _playbackFrequency / (double)OutputRate;

            for (int i = 0; i < BufferFrames; i++)
            {
                if (_sourceFrame >= _clip.FrameCount)
                {
                    // A forced stop still owns a 15 ms de-click envelope. Keep a
                    // looping source continuous across its authored wrap until
                    // that envelope reaches zero; retiring at EOF here would cut
                    // any stop issued in the last 15 ms of the loop.
                    if (Looping)
                        _sourceFrame %= _clip.FrameCount;
                    else
                    {
                        RetireAt(bufferStartFrame + i);
                        break;
                    }
                }

                if (Volatile.Read(ref _startOutputFrame) < 0)
                    Interlocked.CompareExchange(ref _startOutputFrame,
                        bufferStartFrame + i, -1);

                int frame0 = (int)_sourceFrame;
                int frame1 = frame0 + 1;
                if (frame1 >= _clip.FrameCount)
                    frame1 = Looping ? 0 : frame0;
                float fraction = (float)(_sourceFrame - frame0);
                float sampleLeft = Lerp(_clip.Sample(frame0, 0),
                    _clip.Sample(frame1, 0), fraction);
                float sampleRight = _clip.Channels == 1
                    ? sampleLeft
                    : Lerp(_clip.Sample(frame0, 1),
                        _clip.Sample(frame1, 1), fraction);

                if (_rampFramesLeft > 0)
                {
                    _renderLeft += (_rampTargetLeft - _renderLeft) / _rampFramesLeft;
                    _renderRight += (_rampTargetRight - _renderRight) / _rampFramesLeft;
                    _rampFramesLeft--;
                }
                else
                {
                    _renderLeft = _rampTargetLeft;
                    _renderRight = _rampTargetRight;
                }
                left[i] += sampleLeft * _renderLeft;
                right[i] += sampleRight * _renderRight;
                _sourceFrame += sourceStep;

                if (stopping && --_stopFramesLeft <= 0)
                {
                    RetireAt(bufferStartFrame + i + 1);
                    break;
                }
            }
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private void RetireAt(long outputFrame)
        {
            Interlocked.CompareExchange(ref _endOutputFrame, outputFrame, -1);
            Volatile.Write(ref _retired, 1);
        }

        public void Dispose()
            => _owner.Stop(this);

        internal void BeginStop()
        {
            if (Retired) return;
            // A route cancelled before the renderer ever submitted one of its
            // samples must remain silent. Fading from the constructor's non-zero
            // coefficient would create a late 15 ms chirp after the caller stopped.
            if (Volatile.Read(ref _startOutputFrame) < 0)
            {
                long now = _owner.CompletedOutputFrames;
                Interlocked.CompareExchange(ref _startOutputFrame, now, -1);
                RetireAt(now);
                return;
            }
            if (Volatile.Read(ref _stopping) != 0) return;
            _stopFramesLeft = StopRampFrames;
            Volatile.Write(ref _stopping, 1);
        }
    }

    private sealed class OutputBuffer
    {
        public nint Data;
        public nint Header;
        public bool Prepared;
        public bool PendingSubmit;
    }

    private readonly object _voicesLock = new();
    private readonly List<Voice> _voices = [];
    private readonly AutoResetEvent _bufferReady = new(false);
    private readonly OutputBuffer?[] _buffers = new OutputBuffer?[BufferCount];
    private readonly float[] _mixLeft = new float[BufferFrames];
    private readonly float[] _mixRight = new float[BufferFrames];
    private readonly short[] _interleaved = new short[BufferFrames * OutputChannels];
    private readonly object _positionLock = new();
    private readonly object _disposeLock = new();
    private readonly Thread _renderThread;
    private nint _device;
    private volatile bool _shutdown;
    private bool _disposeCompleted;
    private long _submittedFrames;
    private long _completedFrames;
    private uint _lastPositionBytes;
    private long _positionWrapBytes;
    private bool _renderFailureReported;
    private bool _retrySubmit;
    private int _consecutiveRenderFailures;
    private volatile bool _unhealthy;
    private const int MaxRenderFailures = 3;

    public bool Running => _device != 0 && !_shutdown && !_unhealthy;
    public bool Unhealthy => _unhealthy;
    public bool CleanupCompleted
    {
        get { lock (_disposeLock) return _disposeCompleted; }
    }

    private SharedWaveOutMixer(nint device)
    {
        _device = device;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "audio-render",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public static SharedWaveOutMixer? Open()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var mixer = new SharedWaveOutMixer(nint.Zero);
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = OutputChannels,
            SamplesPerSec = OutputRate,
            AvgBytesPerSec = OutputRate * OutputBlockAlign,
            BlockAlign = OutputBlockAlign,
            BitsPerSample = OutputBits,
            Size = 0,
        };

        nint callback = mixer._bufferReady.SafeWaitHandle.DangerousGetHandle();
        int result = waveOutOpen(out nint device, WaveMapper, ref format,
            callback, 0, CallbackEvent);
        if (result != 0)
        {
            mixer.Dispose();
            Console.WriteLine($"[audio] shared waveOut open refused (mmsyserr {result})");
            return null;
        }
        mixer._device = device;

        try
        {
            mixer.PrepareBuffers();
            mixer._renderThread.Start();
            return mixer;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audio] shared output could not start - {ex.Message}");
            mixer.Dispose();
            // A partial native open whose headers could not yet be released must
            // stay rooted. WaveOutVoice publishes this non-running quarantine and
            // retries its cleanup before any replacement output can be opened.
            return mixer.CleanupCompleted ? null : mixer;
        }
    }

    public Voice Add(in PcmClip clip, bool looping, float gain, float pan,
        uint playbackFrequency, bool activated = true)
    {
        var voice = new Voice(this, clip, looping, gain, pan, playbackFrequency, activated);
        lock (_voicesLock) _voices.Add(voice);
        return voice;
    }

    private void Stop(Voice voice)
    {
        // Serialize the never-rendered decision against Mix(). Once Mix has set a
        // start frame, BeginStop arms the ordinary ramp; before that, it can retire
        // the source without ever leaking a sample into a queued buffer.
        lock (_voicesLock) voice.BeginStop();
    }

    private void PrepareBuffers()
    {
        for (int i = 0; i < _buffers.Length; i++)
        {
            var buffer = _buffers[i] = new OutputBuffer
            {
                Data = Marshal.AllocHGlobal(BufferBytes),
                Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>()),
            };
            Marshal.Copy(new byte[BufferBytes], 0, buffer.Data, BufferBytes);
            Marshal.StructureToPtr(new WaveHdr
            {
                Data = buffer.Data,
                BufferLength = BufferBytes,
            }, buffer.Header, false);
            int prepared = waveOutPrepareHeader(_device, buffer.Header,
                (uint)Marshal.SizeOf<WaveHdr>());
            if (prepared != 0)
                throw new InvalidOperationException($"waveOutPrepareHeader returned {prepared}");
            buffer.Prepared = true;
            FillAndSubmit(buffer);
        }
    }

    private void RenderLoop()
    {
        while (!_shutdown)
        {
            // A transient write refusal must retry inside the remaining 30 ms
            // safety ring, not after the ordinary 100 ms lost-event watchdog.
            _bufferReady.WaitOne(_retrySubmit ? 10 : 100);
            _retrySubmit = false;
            if (_shutdown) break;

            try
            {
                bool found;
                do
                {
                    found = false;
                    foreach (OutputBuffer? buffer in _buffers)
                    {
                        if (buffer is null || buffer.Header == 0) continue;
                        WaveHdr header = Marshal.PtrToStructure<WaveHdr>(buffer.Header);
                        if ((header.Flags & WhdrDone) == 0) continue;
                        found = true;
                        // PendingSubmit means this same completion was already
                        // counted and its replacement write failed. Never advance
                        // the device clock again merely because a retry is due.
                        if (!buffer.PendingSubmit)
                            Interlocked.Add(ref _completedFrames, BufferFrames);
                        FillAndSubmit(buffer);
                    }
                } while (found && !_shutdown);
                _consecutiveRenderFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveRenderFailures++;
                if (_consecutiveRenderFailures >= MaxRenderFailures)
                {
                    _unhealthy = true;
                    _shutdown = true;
                    Console.WriteLine("[audio] shared output marked unhealthy after " +
                                      $"{_consecutiveRenderFailures} render failures; " +
                                      "the next route will clean and reopen it");
                    break;
                }
                _retrySubmit = true;
                if (!_renderFailureReported)
                {
                    _renderFailureReported = true;
                    Console.WriteLine($"[audio] shared render failed - {ex.Message}");
                }
            }
        }
    }

    private void FillAndSubmit(OutputBuffer buffer)
    {
        // Preserve a rendered period across a transient write error. Advancing
        // voices again on retry would silently skip audio even though the buffer
        // never entered the device queue.
        if (!buffer.PendingSubmit)
        {
            MixPeriod(_submittedFrames);
            Marshal.Copy(_interleaved, 0, buffer.Data, _interleaved.Length);
            buffer.PendingSubmit = true;
        }

        // The driver owns WHDR_DONE/WHDR_INQUEUE transitions. Leaving DONE intact
        // on failure also makes the render loop retry this buffer on its next wake.
        int written = waveOutWrite(_device, buffer.Header, (uint)Marshal.SizeOf<WaveHdr>());
        if (written != 0)
            throw new InvalidOperationException($"waveOutWrite returned {written}");
        buffer.PendingSubmit = false;
        _submittedFrames += BufferFrames;
    }

    private void MixPeriod(long bufferStartFrame)
    {
        Array.Clear(_mixLeft);
        Array.Clear(_mixRight);
        lock (_voicesLock)
        {
            foreach (Voice voice in _voices)
                voice.Mix(_mixLeft, _mixRight, bufferStartFrame);
            _voices.RemoveAll(static voice => voice.Retired && voice.Finished);
        }

        for (int i = 0; i < BufferFrames; i++)
        {
            _interleaved[i * 2] = ToPcm16(SoftLimit(_mixLeft[i]));
            _interleaved[i * 2 + 1] = ToPcm16(SoftLimit(_mixRight[i]));
        }
    }

    /// <summary>
    /// Deterministic final-mix isolation fixture. Reconstruct the bed from
    /// (bed + a moving cue + 12.5 transient cues/s) minus cues-only. Independent
    /// voice gain/pan keeps the reconstruction within integer rounding; a shared
    /// volume control or accidental bed mutation cannot.
    /// </summary>
    internal static int IsolationFixtureMaxError()
    {
        short[] bed = RenderIsolationScenario(includeBed: true, includeCues: false);
        short[] cues = RenderIsolationScenario(includeBed: false, includeCues: true);
        short[] together = RenderIsolationScenario(includeBed: true, includeCues: true);
        int maxError = 0;
        for (int i = 0; i < together.Length; i++)
            maxError = Math.Max(maxError, Math.Abs((together[i] - cues[i]) - bed[i]));
        return maxError;
    }

    /// <summary>
    /// Offline lifecycle fixture for the seams most likely to become audible:
    /// first-sample gain, an inactive request updated during device open, the
    /// exact 15 ms control glide, and a stop envelope crossing short loop seams.
    /// Each bit names a failed invariant; zero is success.
    /// </summary>
    internal static int LifecycleFixtureFailures()
    {
        if (!PcmClip.TryRead(ConstantWav(12_000, 73), out PcmClip clip))
            return 1 << 30;

        int failures = 0;

        using (var cancelledMixer = new SharedWaveOutMixer(nint.Zero))
        {
            Voice cancelled = cancelledMixer.Add(
                clip, looping: true, .5f, 0f, 0);
            cancelled.Dispose();
            cancelledMixer.MixPeriod(0);
            if (!cancelled.Retired ||
                Array.Exists(cancelledMixer._interleaved, static sample => sample != 0))
                failures |= 1;
        }

        using (var firstMixer = new SharedWaveOutMixer(nint.Zero))
        {
            firstMixer.Add(clip, looping: true, .5f, 0f, 0);
            firstMixer.MixPeriod(0);
            if (firstMixer._interleaved[0] != 6_000 ||
                firstMixer._interleaved[1] != 6_000)
                failures |= 2;
        }

        using (var pendingMixer = new SharedWaveOutMixer(nint.Zero))
        {
            Voice pending = pendingMixer.Add(
                clip, looping: true, 1f, 0f, 0, activated: false);
            pending.SetMix(.25f, 1f);
            pending.Activate();
            pendingMixer.MixPeriod(0);
            if (pendingMixer._interleaved[0] != 0 ||
                pendingMixer._interleaved[1] != 3_000)
                failures |= 4;
        }

        using (var glideMixer = new SharedWaveOutMixer(nint.Zero))
        {
            Voice glide = glideMixer.Add(clip, looping: true, .5f, 0f, 0);
            glideMixer.MixPeriod(0);
            glide.SetMix(.25f, 1f);
            glideMixer.MixPeriod(BufferFrames);
            int endOfFirstGlidePeriod = (BufferFrames - 1) * OutputChannels;
            if (glideMixer._interleaved[endOfFirstGlidePeriod] == 0 ||
                glideMixer._interleaved[endOfFirstGlidePeriod + 1] == 3_000)
                failures |= 8;
            glideMixer.MixPeriod(BufferFrames * 2L);
            int beforeTarget = 238 * OutputChannels;
            int lastRamp = 239 * OutputChannels;
            int firstSteady = 240 * OutputChannels;
            if (glideMixer._interleaved[beforeTarget] == 0 ||
                glideMixer._interleaved[beforeTarget + 1] == 3_000 ||
                glideMixer._interleaved[lastRamp] != 0 ||
                glideMixer._interleaved[lastRamp + 1] != 3_000 ||
                glideMixer._interleaved[firstSteady] != 0 ||
                glideMixer._interleaved[firstSteady + 1] != 3_000)
                failures |= 16;
        }

        using (var stopMixer = new SharedWaveOutMixer(nint.Zero))
        {
            Voice stopping = stopMixer.Add(clip, looping: true, .5f, 0f, 0);
            stopMixer.MixPeriod(0);
            stopping.Dispose();
            stopMixer.MixPeriod(BufferFrames);
            if (stopping.Retired ||
                stopMixer._interleaved[(BufferFrames - 1) * OutputChannels] == 0)
                failures |= 32;
            stopMixer.MixPeriod(BufferFrames * 2L);
            int lastAudible = 238 * OutputChannels;
            int firstSilent = 239 * OutputChannels;
            if (!stopping.Retired ||
                stopMixer._interleaved[lastAudible] == 0 ||
                stopMixer._interleaved[lastAudible + 1] == 0 ||
                stopMixer._interleaved[firstSilent] != 0 ||
                stopMixer._interleaved[firstSilent + 1] != 0 ||
                stopMixer._interleaved[firstSilent + OutputChannels] != 0 ||
                stopMixer._interleaved[firstSilent + OutputChannels + 1] != 0)
                failures |= 64;
        }

        return failures;
    }

    private static short[] RenderIsolationScenario(bool includeBed, bool includeCues)
    {
        const int periods = 100; // one second
        byte[] bedWav = ToneWav(440, 1f);
        byte[] movingWav = ToneWav(997, 1f);
        byte[] transientWav = ToneWav(1_700, .1f);
        PcmClip.TryRead(bedWav, out PcmClip bedClip);
        PcmClip.TryRead(movingWav, out PcmClip movingClip);
        PcmClip.TryRead(transientWav, out PcmClip transientClip);

        var mixer = new SharedWaveOutMixer(nint.Zero);
        try
        {
            if (includeBed) mixer.Add(bedClip, looping: true, .12f, 0f, 0);
            Voice? moving = includeCues
                ? mixer.Add(movingClip, looping: true, .06f, -1f, 0)
                : null;
            var rendered = new short[periods * BufferFrames * OutputChannels];
            for (int period = 0; period < periods; period++)
            {
                if (includeCues && period % 8 == 0)
                {
                    float pan = (period / 8 & 1) == 0 ? -.8f : .8f;
                    float gain = .08f + .01f * (period / 8 % 4);
                    mixer.Add(transientClip, looping: false, gain, pan, 0);
                }
                if (moving is not null && period % 5 == 0)
                {
                    bool right = (period / 5 & 1) != 0;
                    moving.SetMix(right ? .1f : .04f, right ? .9f : -.9f);
                }

                mixer.MixPeriod(mixer._submittedFrames);
                Array.Copy(mixer._interleaved, 0, rendered,
                    period * mixer._interleaved.Length, mixer._interleaved.Length);
                mixer._submittedFrames += BufferFrames;
            }
            return rendered;
        }
        finally
        {
            mixer.Dispose();
        }
    }

    private static byte[] ToneWav(int hz, float seconds)
    {
        int frames = (int)(OutputRate * seconds);
        var pcm = new byte[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            double phase = 2.0 * Math.PI * hz * frame / OutputRate;
            BitConverter.TryWriteBytes(pcm.AsSpan(frame * 2, 2),
                (short)Math.Round(Math.Sin(phase) * 20_000));
        }
        return AudioMixer.BuildWav(pcm, channels: 1, rate: OutputRate, bits: 16);
    }

    private static byte[] ConstantWav(short sample, int frames)
    {
        var pcm = new byte[frames * 2];
        for (int frame = 0; frame < frames; frame++)
            BitConverter.TryWriteBytes(pcm.AsSpan(frame * 2, 2), sample);
        return AudioMixer.BuildWav(pcm, channels: 1, rate: OutputRate, bits: 16);
    }

    /// <summary>A unity-slope soft knee. Ordinary samples are untouched; an
    /// overlapping crowd cannot turn integer clipping into a new source of rattle.</summary>
    internal static float SoftLimit(float sample)
    {
        const float knee = .95f;
        float magnitude = MathF.Abs(sample);
        if (magnitude <= knee) return sample;
        float compressed = knee + (1f - knee) *
            (1f - MathF.Exp(-(magnitude - knee) / (1f - knee)));
        return MathF.CopySign(Math.Min(compressed, .999f), sample);
    }

    private static short ToPcm16(float sample) =>
        (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);

    public long CompletedOutputFrames => Interlocked.Read(ref _completedFrames);

    public bool TryPlayedOutputFrames(out long frames)
    {
        lock (_positionLock)
        {
            frames = 0;
            if (_device == 0) return false;
            var time = new MmTime { Type = TimeBytes };
            if (waveOutGetPosition(_device, ref time, (uint)Marshal.SizeOf<MmTime>()) != 0 ||
                time.Type != TimeBytes)
                return false;
            uint current = time.Value;
            if (current < _lastPositionBytes && _lastPositionBytes - current > int.MaxValue)
                _positionWrapBytes += 1L << 32;
            _lastPositionBytes = current;
            frames = (_positionWrapBytes + current) / OutputBlockAlign;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposeCompleted) return;
            if (!_shutdown)
            {
                _shutdown = true;
                _bufferReady.Set();
            }
            if (_renderThread.IsAlive && !_renderThread.Join(TimeSpan.FromSeconds(2)))
            {
                // Never free a WAVEHDR while the render thread can still submit it.
                // A later Dispose can finish after the thread observes _shutdown.
                Console.WriteLine("[audio] shared render thread did not stop; native output " +
                                  "resources retained until cleanup can be retried");
                return;
            }

            if (_device != 0)
            {
                int reset = waveOutReset(_device);
                if (reset != 0)
                {
                    Console.WriteLine($"[audio] shared waveOut reset failed ({reset}); native " +
                                      "output resources retained until cleanup can be retried");
                    return;
                }
            }
            bool unprepareFailed = false;
            foreach (OutputBuffer? buffer in _buffers)
            {
                if (buffer is null) continue;
                if (_device != 0 && buffer.Prepared && buffer.Header != 0)
                {
                    int unprepared = waveOutUnprepareHeader(_device, buffer.Header,
                        (uint)Marshal.SizeOf<WaveHdr>());
                    if (unprepared != 0)
                    {
                        unprepareFailed = true;
                        Console.WriteLine($"[audio] shared buffer unprepare failed ({unprepared}); " +
                                          "that native buffer was retained");
                        continue;
                    }
                }
                if (buffer.Header != 0) Marshal.FreeHGlobal(buffer.Header);
                if (buffer.Data != 0) Marshal.FreeHGlobal(buffer.Data);
                buffer.Header = buffer.Data = 0;
                buffer.Prepared = false;
            }
            if (unprepareFailed) return;
            if (_device != 0)
            {
                int closed = waveOutClose(_device);
                if (closed != 0)
                {
                    Console.WriteLine($"[audio] shared waveOut close failed ({closed}); event " +
                                      "handle retained until cleanup can be retried");
                    return;
                }
            }
            _device = 0;
            _bufferReady.Dispose();
            _disposeCompleted = true;
        }
    }

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint WhdrDone = 0x0001;
    private const uint TimeBytes = 0x0004;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmTime
    {
        public uint Type;
        public uint Value;
        public uint Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out nint device, uint deviceId,
        ref WaveFormatEx format, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveOutGetPosition(nint device, ref MmTime time, uint size);
}
