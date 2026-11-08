using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Concentus.Enums;
using Concentus.Structs;

namespace Buzzcord
{
    // Mic and speakers for a call, through the classic Windows audio API (winmm), plus Opus.
    // Capture: 48 kHz mono, 20 ms frames, voice activity or push-to-talk, encoded as stereo Opus
    // (Discord's format). Playback: each speaker gets a jitter buffer and a decoder; a mixer
    // feeds the output every 20 ms.
    public sealed class AudioEngine : IDisposable
    {
        public const int Rate = 48000, FrameSamples = 960;   // 20 ms
        public static readonly byte[] SilenceFrame = { 0xF8, 0xFF, 0xFE };

        // Settings, changeable while running.
        public volatile bool Muted, Deafened, UsePushToTalk;
        public volatile int PushToTalkKey = 0x14;          // virtual-key code; Caps Lock by default
        public float SensitivityDb = -50f;                 // voice activity threshold
        public float InputGain = 1f;

        public event Action<byte[]> FrameReady;             // Opus frame to send (capture thread)
        public event Action<bool> TransmittingChanged;      // start/stop talking (capture thread)
        public event Action<float> InputLevel;              // dBFS of each frame, for a meter

        readonly OpusEncoder encoder;
        readonly Dictionary<ulong, PlaybackStream> streams = new Dictionary<ulong, PlaybackStream>();
        WaveDevice input, output;
        bool transmitting;
        DateTime hangUntil;
        int silenceToSend;

        public AudioEngine()
        {
            encoder = OpusEncoder.Create(Rate, 2, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 64000;
            encoder.Complexity = 5;
            encoder.UseInbandFEC = true;
            encoder.PacketLossPercent = 5;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        }

        public void Start(int inputDevice = -1, int outputDevice = -1)
        {
            input = WaveDevice.OpenInput(inputDevice, OnCaptured);
            output = WaveDevice.OpenOutput(outputDevice, Mix);
        }

        // Just the mic, for the settings window's level meter.
        public void StartInput(int inputDevice) { input = WaveDevice.OpenInput(inputDevice, OnCaptured); }

        // Saved device names back to winmm indexes; -1 is the Windows default.
        public static int DeviceIndex(bool inputs, string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            var names = inputs ? InputDevices() : OutputDevices();
            return names.IndexOf(name);
        }

        public void Dispose()
        {
            if (input != null) input.Dispose();
            if (output != null) output.Dispose();
            input = output = null;
        }

        public static List<string> InputDevices() { return WaveDevice.Names(true); }
        public static List<string> OutputDevices() { return WaveDevice.Names(false); }

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        // Just reads the key's state; no keyboard hook, so it's safe next to anti-cheat.
        public bool PushToTalkDown { get { return (GetAsyncKeyState(PushToTalkKey) & 0x8000) != 0; } }

        // ---- capture ----

        void OnCaptured(short[] mono) { ProcessCaptured(mono, DateTime.UtcNow); }

        // One 20 ms mono frame from the mic; public so the self-test can drive it.
        public void ProcessCaptured(short[] mono, DateTime now)
        {
            double sum = 0;
            for (int i = 0; i < mono.Length; i++) { double s = mono[i] * InputGain; sum += s * s; }
            float db = (float)(10 * Math.Log10(sum / mono.Length / (32768.0 * 32768.0) + 1e-12));
            if (InputLevel != null) InputLevel(db);

            bool want;
            if (Muted) want = false;
            else if (UsePushToTalk) want = PushToTalkDown;
            else
            {
                if (db > SensitivityDb) hangUntil = now.AddMilliseconds(300);
                want = now < hangUntil;
            }

            if (want)
            {
                if (!transmitting) { transmitting = true; if (TransmittingChanged != null) TransmittingChanged(true); }
                silenceToSend = 5;
                var stereo = new short[mono.Length * 2];
                for (int i = 0; i < mono.Length; i++)
                {
                    int s = (int)(mono[i] * InputGain);
                    short c = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s));
                    stereo[2 * i] = c;
                    stereo[2 * i + 1] = c;
                }
                var packet = new byte[1275];
                int n = encoder.Encode(stereo, 0, FrameSamples, packet, 0, packet.Length);
                var frame = new byte[n];
                Buffer.BlockCopy(packet, 0, frame, 0, n);
                if (FrameReady != null) FrameReady(frame);
            }
            else if (transmitting)
            {
                // Discord expects five silence frames when someone stops talking.
                if (silenceToSend-- > 0) { if (FrameReady != null) FrameReady(SilenceFrame); return; }
                transmitting = false;
                if (TransmittingChanged != null) TransmittingChanged(false);
            }
        }

        // ---- playback ----

        public void Receive(ulong user, ushort seq, byte[] opus)
        {
            PlaybackStream s;
            lock (streams)
                if (!streams.TryGetValue(user, out s)) streams[user] = s = new PlaybackStream();
            s.Push(seq, opus);
        }

        public void SetVolume(ulong user, float volume)
        {
            lock (streams)
            {
                PlaybackStream s;
                if (!streams.TryGetValue(user, out s)) streams[user] = s = new PlaybackStream();
                s.Volume = volume;
            }
        }

        public void RemoveUser(ulong user) { lock (streams) streams.Remove(user); }

        // Who is audible right now, for the speaking indicators.
        public bool IsPlaying(ulong user)
        {
            lock (streams) { PlaybackStream s; return streams.TryGetValue(user, out s) && s.Playing; }
        }

        // 20 ms of stereo output; public so the self-test can drive it.
        public short[] Mix()
        {
            var mix = new int[FrameSamples * 2];
            List<PlaybackStream> list;
            lock (streams) list = new List<PlaybackStream>(streams.Values);
            foreach (var s in list)
            {
                var pcm = s.Next();
                if (pcm == null || Deafened) continue;
                float v = s.Volume;
                for (int i = 0; i < mix.Length; i++) mix[i] += (int)(pcm[i] * v);
            }
            var output = new short[mix.Length];
            for (int i = 0; i < mix.Length; i++) output[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, mix[i]));
            return output;
        }
    }

    // One speaker's incoming audio: reorders packets by RTP sequence, waits for 60 ms before
    // starting, conceals lost packets, and stops after 100 ms of nothing.
    public sealed class PlaybackStream
    {
        const int StartDepth = 3, MaxMisses = 5, MaxDepth = 25;
        readonly OpusDecoder decoder = OpusDecoder.Create(AudioEngine.Rate, 2);
        readonly Dictionary<ushort, byte[]> buffer = new Dictionary<ushort, byte[]>();
        ushort next;
        int misses;
        public volatile bool Playing;
        public float Volume = 1f;

        public void Push(ushort seq, byte[] opus)
        {
            lock (buffer)
            {
                if (Playing && (short)(seq - next) < 0) return;   // too late
                buffer[seq] = opus;
                if (buffer.Count > MaxDepth) { buffer.Clear(); Playing = false; }   // fell far behind; start over
            }
        }

        public short[] Next()
        {
            lock (buffer)
            {
                if (!Playing)
                {
                    if (buffer.Count < StartDepth) return null;
                    bool first = true;
                    foreach (var s in buffer.Keys) if (first || (short)(s - next) < 0) { next = s; first = false; }
                    Playing = true;
                    misses = 0;
                }
                var pcm = new short[AudioEngine.FrameSamples * 2];
                byte[] opus;
                if (buffer.TryGetValue(next, out opus))
                {
                    buffer.Remove(next);
                    misses = 0;
                    try { decoder.Decode(opus, 0, opus.Length, pcm, 0, AudioEngine.FrameSamples, false); }
                    catch (Exception) { Array.Clear(pcm, 0, pcm.Length); }
                }
                else
                {
                    if (buffer.Count == 0 && ++misses > MaxMisses) { Playing = false; return null; }
                    // Lost or late: let Opus fill the gap.
                    try { decoder.Decode(null, 0, 0, pcm, 0, AudioEngine.FrameSamples, false); }
                    catch (Exception) { Array.Clear(pcm, 0, pcm.Length); }
                }
                next++;
                return pcm;
            }
        }
    }

    // A winmm wave-in or wave-out device running 20 ms buffers on its own thread.
    sealed class WaveDevice : IDisposable
    {
        const int Buffers = 4;
        const int CallbackEvent = 0x50000, WhdrDone = 1, WaveMapper = -1;

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public short Tag, Channels; public int SamplesPerSec, AvgBytesPerSec; public short BlockAlign, BitsPerSample, Size; }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader { public IntPtr Data; public int BufferLength, BytesRecorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveCaps { public short Mid, Pid; public int DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public int Formats; public short Channels, Reserved; }

        // WAVEOUTCAPSW has one more field than WAVEINCAPSW; a size mismatch makes winmm refuse.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveOutCaps { public short Mid, Pid; public int DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public int Formats; public short Channels, Reserved; public int Support; }

        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int device, ref WaveFormat f, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveInGetDevCapsW(IntPtr id, ref WaveCaps caps, int size);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int device, ref WaveFormat f, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCapsW(IntPtr id, ref WaveOutCaps caps, int size);

        readonly bool isInput;
        readonly IntPtr handle;
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly IntPtr[] headers = new IntPtr[Buffers];
        readonly int bytes;
        readonly Thread thread;
        volatile bool stopping;
        readonly Action<short[]> onCaptured;
        readonly Func<short[]> fill;
        static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveHeader));

        WaveDevice(bool isInput, int device, Action<short[]> onCaptured, Func<short[]> fill)
        {
            this.isInput = isInput;
            this.onCaptured = onCaptured;
            this.fill = fill;
            short channels = (short)(isInput ? 1 : 2);
            var f = new WaveFormat { Tag = 1, Channels = channels, SamplesPerSec = AudioEngine.Rate, BitsPerSample = 16, BlockAlign = (short)(channels * 2) };
            f.AvgBytesPerSec = f.SamplesPerSec * f.BlockAlign;
            bytes = AudioEngine.FrameSamples * f.BlockAlign;
            int r = isInput
                ? waveInOpen(out handle, device, ref f, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent)
                : waveOutOpen(out handle, device, ref f, signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
            if (r != 0) throw new InvalidOperationException((isInput ? "microphone" : "speakers") + " couldn't be opened (winmm error " + r + ")");
            for (int i = 0; i < Buffers; i++)
            {
                headers[i] = Marshal.AllocHGlobal(HeaderSize);
                var h = new WaveHeader { Data = Marshal.AllocHGlobal(bytes), BufferLength = bytes };
                Marshal.StructureToPtr(h, headers[i], false);
                if (isInput) { waveInPrepareHeader(handle, headers[i], HeaderSize); waveInAddBuffer(handle, headers[i], HeaderSize); }
                else
                {
                    waveOutPrepareHeader(handle, headers[i], HeaderSize);
                    WriteOut(headers[i], new short[AudioEngine.FrameSamples * 2]);   // prime with silence
                }
            }
            if (isInput) waveInStart(handle);
            thread = new Thread(Run) { IsBackground = true, Name = isInput ? "Buzzcord mic" : "Buzzcord speakers", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public static WaveDevice OpenInput(int device, Action<short[]> onCaptured) { return new WaveDevice(true, device < 0 ? WaveMapper : device, onCaptured, null); }
        public static WaveDevice OpenOutput(int device, Func<short[]> fill) { return new WaveDevice(false, device < 0 ? WaveMapper : device, null, fill); }

        public static List<string> Names(bool inputs)
        {
            var list = new List<string>();
            int n = inputs ? waveInGetNumDevs() : waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                string name = null;
                if (inputs)
                {
                    var caps = new WaveCaps();
                    if (waveInGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf(caps)) == 0) name = caps.Name;
                }
                else
                {
                    var caps = new WaveOutCaps();
                    if (waveOutGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf(caps)) == 0) name = caps.Name;
                }
                list.Add(string.IsNullOrEmpty(name) ? "Device " + i : name);
            }
            return list;
        }

        void WriteOut(IntPtr hdr, short[] pcm)
        {
            var h = (WaveHeader)Marshal.PtrToStructure(hdr, typeof(WaveHeader));
            Marshal.Copy(pcm, 0, h.Data, Math.Min(pcm.Length, bytes / 2));
            h.Flags &= ~WhdrDone;
            Marshal.StructureToPtr(h, hdr, false);
            waveOutWrite(handle, hdr, HeaderSize);
        }

        void Run()
        {
            while (!stopping)
            {
                signal.WaitOne(100);
                foreach (var hdr in headers)
                {
                    if (stopping) return;
                    var h = (WaveHeader)Marshal.PtrToStructure(hdr, typeof(WaveHeader));
                    if ((h.Flags & WhdrDone) == 0) continue;
                    try
                    {
                        if (isInput)
                        {
                            var pcm = new short[h.BytesRecorded / 2];
                            Marshal.Copy(h.Data, pcm, 0, pcm.Length);
                            h.Flags &= ~WhdrDone;
                            h.BytesRecorded = 0;
                            Marshal.StructureToPtr(h, hdr, false);
                            waveInAddBuffer(handle, hdr, HeaderSize);
                            if (pcm.Length == AudioEngine.FrameSamples) onCaptured(pcm);
                        }
                        else WriteOut(hdr, fill());
                    }
                    catch (Exception ex) { Program.Log("audio: " + ex.Message); }
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            if (isInput) waveInReset(handle); else waveOutReset(handle);
            if (thread != null) thread.Join(500);
            foreach (var hdr in headers)
            {
                if (hdr == IntPtr.Zero) continue;
                if (isInput) waveInUnprepareHeader(handle, hdr, HeaderSize); else waveOutUnprepareHeader(handle, hdr, HeaderSize);
                var h = (WaveHeader)Marshal.PtrToStructure(hdr, typeof(WaveHeader));
                Marshal.FreeHGlobal(h.Data);
                Marshal.FreeHGlobal(hdr);
            }
            if (isInput) waveInClose(handle); else waveOutClose(handle);
        }
    }
}
