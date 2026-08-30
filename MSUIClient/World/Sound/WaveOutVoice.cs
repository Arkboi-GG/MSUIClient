namespace MSUIClient.World.Sound;

/// <summary>
/// A logical PCM voice inside the process's one <see cref="SharedWaveOutMixer"/>.
///
/// The old implementation opened one WinMM stream per sound and called
/// waveOutSetVolume on each handle. Modern Windows maps those legacy streams into
/// one process audio session, so the supposedly per-voice call actually changed
/// every sound in the application. Rapid positional cues therefore chopped and
/// panned the music/ambience without dropping a single byte. This wrapper keeps
/// the existing voice lifecycle API while the shared renderer applies gain/pan to
/// each source's samples before summing them.
/// </summary>
internal sealed class WaveOutVoice : IDisposable
{
    private static readonly object SharedLock = new();
    private static SharedWaveOutMixer? _shared;
    private static long _devicesOpened;
    private static long _sharedRoutes;

    private SharedWaveOutMixer.Voice? _voice;
    private int _disposed;

    public bool Looping => _voice?.Looping == true;
    public uint BytesPerSecond => _voice?.BytesPerSecond ?? 0;
    public bool Finished => _voice?.Finished != false;

    private WaveOutVoice(SharedWaveOutMixer.Voice voice) => _voice = voice;

    /// <summary>Parse a PCM RIFF/WAVE source and attach it to the one output stream.</summary>
    public static WaveOutVoice? Open(byte[] wav, bool looping, float gain, float pan = 0f,
        uint playbackFrequency = 0)
        => OpenCore(wav, looping, gain, pan, playbackFrequency, activated: true);

    /// <summary>Create a source that cannot render until AudioMixer wins its
    /// request-state gate and explicitly activates it.</summary>
    internal static WaveOutVoice? OpenPending(byte[] wav, bool looping, float gain,
        float pan = 0f, uint playbackFrequency = 0)
        => OpenCore(wav, looping, gain, pan, playbackFrequency, activated: false);

    private static WaveOutVoice? OpenCore(byte[] wav, bool looping, float gain, float pan,
        uint playbackFrequency, bool activated)
    {
        if (!OperatingSystem.IsWindows() ||
            !SharedWaveOutMixer.PcmClip.TryRead(wav, out var clip))
            return null;

        lock (SharedLock)
        {
            bool reused = _shared?.Running == true;
            if (!reused)
            {
                if (_shared is not null)
                {
                    _shared.Dispose();
                    // A renderer that retained native headers after a failed
                    // reset/unprepare must remain reachable for a later cleanup;
                    // never overwrite it with a second physical output.
                    if (!_shared.CleanupCompleted) return null;
                }
                _shared = SharedWaveOutMixer.Open();
                if (_shared?.Running != true) return null;
                Interlocked.Increment(ref _devicesOpened);
            }
            else
            {
                Interlocked.Increment(ref _sharedRoutes);
            }

            return new WaveOutVoice(_shared!.Add(
                clip, looping, gain, pan, playbackFrequency, activated));
        }
    }

    internal void Activate() => _voice?.Activate();

    public void SetGain(float gain) => _voice?.SetGain(gain);

    public void SetMix(float gain, float pan) => _voice?.SetMix(gain, pan);

    /// <summary>Source-format bytes whose corresponding mixed output has actually
    /// passed the device clock. Kept for the existing playback-progress probe.</summary>
    public uint? PlayedBytes() => _voice?.PlayedBytes();

    /// <summary>Physical outputs versus logical voices routed through an existing
    /// output. Kept under the old name so diagnostics can compare the topology.</summary>
    public static (long Opened, long Reused) PoolCounters() =>
        (Interlocked.Read(ref _devicesOpened), Interlocked.Read(ref _sharedRoutes));

    /// <summary>Close the process output after every logical voice has stopped.</summary>
    public static bool DrainPool()
    {
        lock (SharedLock)
        {
            if (_shared is null) return true;
            _shared.Dispose();
            if (!_shared.CleanupCompleted) return false;
            _shared = null;
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _voice?.Dispose();
        _voice = null;
    }
}
