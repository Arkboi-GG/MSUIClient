using System.Runtime.InteropServices;

namespace MSUIClient.World.Sound;

/// <summary>
/// A PCM voice played straight out of MEMORY through winmm's waveOut, with no MCI,
/// no COM apartment, no message queue and no file on disk anywhere in the path.
///
/// WHY THIS EXISTS. Every mechanism the MCI path made us fight is absent here by
/// construction, and each one was a real defect we worked around rather than
/// removed:
///   * The clip is handed to the driver ONCE, as one buffer. Nothing streams it
///     from a temp file, so nothing downstream can be starved reading it.
///   * Looping is WHDR_BEGINLOOP/WHDR_ENDLOOP with an infinite count - the driver
///     wraps it sample-exactly. No seek, no re-play, no seam to time, and none of
///     the machinery in MaintainLoops applies.
///   * Volume is waveOutSetVolume on this stream alone: no shared device state,
///     and no command queue behind a blocking open.
///   * waveOutOpen costs a millisecond or two, against the 50-300 ms that
///     mciSendString("open") costs.
///
/// It handles PCM only, which is every .wav vanilla ships. Zone music is .mp3 and
/// still goes through MCI; deciding whether that path needs the same treatment is
/// exactly what shipping this half answers, since the two now share nothing.
/// </summary>
internal sealed class WaveOutVoice : IDisposable
{
    private nint _device;
    private nint _data;
    private nint _header;
    private bool _prepared;
    private float _gain;
    private float _pan;

    public bool Looping { get; private init; }

    private WaveOutVoice() { }

    /// <summary>
    /// Hand a RIFF/WAVE buffer to the wave mapper. Returns null when the bytes are
    /// not PCM we can describe or the driver refuses the format - the caller falls
    /// back to the MCI path rather than going silent.
    /// </summary>
    public static WaveOutVoice? Open(byte[] wav, bool looping, float gain, float pan = 0f)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!TryDescribe(wav, out WaveFormatEx format, out int dataOffset, out int dataLength))
            return null;

        var voice = new WaveOutVoice { Looping = looping };
        try
        {
            if (waveOutOpen(out voice._device, WaveMapper, ref format, 0, 0, CallbackNull) != 0)
                return null;

            voice._data = Marshal.AllocHGlobal(dataLength);
            Marshal.Copy(wav, dataOffset, voice._data, dataLength);

            var header = new WaveHdr
            {
                Data = voice._data,
                BufferLength = (uint)dataLength,
                // waveOutPrepareHeader requires caller-owned flags to be clear.
                // Loop markers are installed on the prepared native header below.
                Flags = 0u,
                Loops = 0u,
            };
            voice._header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
            Marshal.StructureToPtr(header, voice._header, false);

            if (waveOutPrepareHeader(voice._device, voice._header, (uint)Marshal.SizeOf<WaveHdr>()) != 0)
            { voice.Dispose(); return null; }
            voice._prepared = true;

            if (looping)
            {
                // Preserve WHDR_PREPARED and any native-owned fields written by
                // prepare. WinMM permits changing dwFlags/dwLoops after prepare
                // and before write; one block may carry both loop markers.
                header = Marshal.PtrToStructure<WaveHdr>(voice._header);
                header.Flags |= WhdrBeginLoop | WhdrEndLoop;
                header.Loops = uint.MaxValue;
                Marshal.StructureToPtr(header, voice._header, false);
            }

            voice.SetMix(gain, pan);

            if (waveOutWrite(voice._device, voice._header, (uint)Marshal.SizeOf<WaveHdr>()) != 0)
            { voice.Dispose(); return null; }

            return voice;
        }
        catch
        {
            voice.Dispose();
            return null;
        }
    }

    /// <summary>Absolute output gain, 0..1, on this stream only.</summary>
    public void SetGain(float gain)
    {
        SetMix(gain, _pan);
    }

    /// <summary>Absolute gain plus stereo balance on this stream only.</summary>
    public void SetMix(float gain, float pan)
    {
        _gain = Math.Clamp(gain, 0f, 1f);
        _pan = Math.Clamp(pan, -1f, 1f);
        if (_device == 0) return;
        (float left, float right) = SpatialAudioLaw.StereoLevels(_gain, _pan);
        uint leftLevel = (uint)Math.Clamp(left * 0xFFFF, 0f, 0xFFFF);
        uint rightLevel = (uint)Math.Clamp(right * 0xFFFF, 0f, 0xFFFF);
        waveOutSetVolume(_device, (rightLevel << 16) | leftLevel);
    }

    /// <summary>True once the driver is done with the buffer. Always false while
    /// looping, which never finishes on its own.</summary>
    public bool Finished
    {
        get
        {
            if (_device == 0 || _header == 0 || Looping) return false;
            var header = Marshal.PtrToStructure<WaveHdr>(_header);
            return (header.Flags & WhdrDone) != 0;
        }
    }

    public void Dispose()
    {
        if (_device != 0)
        {
            // Reset before unprepare: a header still in the queue cannot be
            // unprepared, and unpreparing it anyway leaks the whole allocation.
            waveOutReset(_device);
            if (_prepared && _header != 0)
                waveOutUnprepareHeader(_device, _header, (uint)Marshal.SizeOf<WaveHdr>());
            waveOutClose(_device);
            _device = 0;
        }
        _prepared = false;
        if (_header != 0) { Marshal.FreeHGlobal(_header); _header = 0; }
        if (_data != 0) { Marshal.FreeHGlobal(_data); _data = 0; }
    }

    /// <summary>
    /// Walk the RIFF chunks for the format and the sample data. The header repair
    /// AudioMixer applies has already run on these bytes, so the fields here are
    /// the corrected ones.
    /// </summary>
    private static bool TryDescribe(byte[] wav, out WaveFormatEx format,
        out int dataOffset, out int dataLength)
    {
        format = default;
        dataOffset = dataLength = 0;
        if (wav.Length < 44 ||
            wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
            return false;

        bool haveFormat = false;
        int at = 12;
        while (at + 8 <= wav.Length)
        {
            uint size = BitConverter.ToUInt32(wav, at + 4);
            int body = at + 8;
            if (wav[at] == 'f' && wav[at + 1] == 'm' && wav[at + 2] == 't' && wav[at + 3] == ' ')
            {
                if (body + 16 > wav.Length) return false;
                if (BitConverter.ToUInt16(wav, body) != 1) return false;   // PCM only
                format = new WaveFormatEx
                {
                    FormatTag = 1,
                    Channels = BitConverter.ToUInt16(wav, body + 2),
                    SamplesPerSec = BitConverter.ToUInt32(wav, body + 4),
                    AvgBytesPerSec = BitConverter.ToUInt32(wav, body + 8),
                    BlockAlign = BitConverter.ToUInt16(wav, body + 12),
                    BitsPerSample = BitConverter.ToUInt16(wav, body + 14),
                    Size = 0,
                };
                if (format.Channels is 0 or > 2 || format.SamplesPerSec == 0 ||
                    format.BitsPerSample is not (8 or 16)) return false;
                haveFormat = true;
            }
            else if (wav[at] == 'd' && wav[at + 1] == 'a' && wav[at + 2] == 't' && wav[at + 3] == 'a')
            {
                dataOffset = body;
                dataLength = (int)Math.Min(size, (uint)(wav.Length - body));
            }
            if (haveFormat && dataLength > 0) return true;
            at = body + (int)size + ((int)size & 1);
        }
        return false;
    }

    // ── winmm ────────────────────────────────────────────────────────────────

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackNull = 0x0000;
    private const uint WhdrDone = 0x0001;
    private const uint WhdrBeginLoop = 0x0004;
    private const uint WhdrEndLoop = 0x0008;

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
    private static extern int waveOutSetVolume(nint device, uint volume);
}
