using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TinyHand
{
    /// <summary>
    /// Plays the tap sound. Like the macOS app's audio engine, it keeps one output stream open while it's
    /// in use (playing silence between taps) instead of opening the device for every tap: many outputs,
    /// Bluetooth headphones especially, drop the first few hundred milliseconds after waking up, which is
    /// longer than a whole tap sound. Taps are mixed, so quick taps overlap instead of cutting each other off.
    /// The stream closes after 30 seconds without a tap and reopens on the next one.
    /// </summary>
    internal sealed class TapSound
    {
        private string choice;
        public bool Enabled = true;

        public TapSound()
        {
            choice = Prefs.TapSound;
        }

        public void Load(string id)
        {
            choice = id;
        }

        /// <summary>Opens the output stream ahead of the first tap.</summary>
        public void Warm()
        {
            Samples(choice);
            AudioOut.Shared.Start();
        }

        public void Play()
        {
            if (Enabled)
            {
                PlayNow(choice);
            }
        }

        public void Preview(string id)
        {
            PlayNow(id);
        }

        private static void PlayNow(string id)
        {
            short[] s = Samples(id);
            if (s != null)
            {
                AudioOut.Shared.Play(s);
            }
        }

        private static readonly Dictionary<string, short[]> cache = new Dictionary<string, short[]>();

        private static short[] Samples(string id)
        {
            lock (cache)
            {
                short[] s;
                if (cache.TryGetValue(id, out s))
                {
                    return s;
                }
                using (Stream stream = HandAsset.OpenResource("tap_" + id + ".wav"))
                {
                    s = stream == null ? null : Wav.Read(stream, AudioOut.Rate);
                }
                if (s == null)
                {
                    Log.Write("tap sound " + id + " could not be read");
                }
                cache[id] = s;
                return s;
            }
        }
    }

    /// <summary>Reads 16-bit PCM WAV data as mono samples at the given rate.</summary>
    internal static class Wav
    {
        public static short[] Read(Stream stream, int rate)
        {
            BinaryReader r = new BinaryReader(stream);
            if (new string(r.ReadChars(4)) != "RIFF") return null;
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE") return null;
            int channels = 1, srcRate = rate, bits = 16;
            byte[] data = null;
            while (stream.Position + 8 <= stream.Length)
            {
                string id = new string(r.ReadChars(4));
                int size = r.ReadInt32();
                long next = stream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    int format = r.ReadInt16();
                    channels = r.ReadInt16();
                    srcRate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadInt16();
                    bits = r.ReadInt16();
                    if ((format != 1 && format != unchecked((short)0xFFFE)) || bits != 16) return null;
                }
                else if (id == "data")
                {
                    data = r.ReadBytes(size);
                }
                stream.Position = Math.Min(next, stream.Length);
            }
            if (data == null || channels < 1) return null;
            int frames = data.Length / (2 * channels);
            short[] mono = new short[frames];
            for (int i = 0; i < frames; i++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToInt16(data, (i * channels + c) * 2);
                }
                mono[i] = (short)(sum / channels);
            }
            if (srcRate == rate) return mono;
            int outFrames = (int)((long)frames * rate / srcRate);
            short[] resampled = new short[outFrames];
            for (int i = 0; i < outFrames; i++)
            {
                double pos = (double)i * srcRate / rate;
                int a = (int)pos;
                int b = Math.Min(frames - 1, a + 1);
                double t = pos - a;
                resampled[i] = (short)(mono[a] * (1 - t) + mono[b] * t);
            }
            return resampled;
        }
    }

    /// <summary>A small always-running waveOut stream that mixes short sounds into silence.</summary>
    internal sealed class AudioOut
    {
        public static readonly AudioOut Shared = new AudioOut();
        public const int Rate = 44100;
        private const int BufferCount = 3;
        private const int BufferFrames = Rate / 50; // 20 ms
        private const int IdleStopMs = 30000;

        private sealed class Voice
        {
            public short[] Data;
            public int Pos;
        }

        private readonly object gate = new object();
        private readonly List<Voice> voices = new List<Voice>();
        private readonly AutoResetEvent bufferDone = new AutoResetEvent(false);
        private Thread thread;
        private volatile bool running;
        private DateTime lastUse = DateTime.UtcNow;

        public void Start()
        {
            lock (gate)
            {
                lastUse = DateTime.UtcNow;
                if (running) return;
                running = true;
                thread = new Thread(Run);
                thread.Name = "TinyHand audio";
                thread.IsBackground = true;
                thread.Priority = ThreadPriority.AboveNormal;
                thread.Start();
            }
        }

        public void Play(short[] samples)
        {
            lock (gate)
            {
                voices.Add(new Voice { Data = samples });
                if (voices.Count > 6) voices.RemoveAt(0);
            }
            Start();
        }

        private void Mix(short[] output)
        {
            int[] acc = new int[output.Length];
            lock (gate)
            {
                for (int v = voices.Count - 1; v >= 0; v--)
                {
                    Voice voice = voices[v];
                    int n = Math.Min(output.Length, voice.Data.Length - voice.Pos);
                    for (int i = 0; i < n; i++) acc[i] += voice.Data[voice.Pos + i];
                    voice.Pos += n;
                    if (voice.Pos >= voice.Data.Length) voices.RemoveAt(v);
                }
            }
            for (int i = 0; i < output.Length; i++)
            {
                int s = acc[i];
                output[i] = (short)(s > short.MaxValue ? short.MaxValue : s < short.MinValue ? short.MinValue : s);
            }
        }

        private void Run()
        {
            IntPtr wave = IntPtr.Zero;
            IntPtr[] headers = new IntPtr[BufferCount];
            IntPtr[] data = new IntPtr[BufferCount];
            int headerSize = Marshal.SizeOf(typeof(WaveOut.WAVEHDR));
            int flagsOffset = Marshal.OffsetOf(typeof(WaveOut.WAVEHDR), "dwFlags").ToInt32();
            try
            {
                WaveOut.WAVEFORMATEX fmt = new WaveOut.WAVEFORMATEX();
                fmt.wFormatTag = 1;
                fmt.nChannels = 1;
                fmt.nSamplesPerSec = Rate;
                fmt.wBitsPerSample = 16;
                fmt.nBlockAlign = 2;
                fmt.nAvgBytesPerSec = Rate * 2;
                int err = WaveOut.waveOutOpen(out wave, WaveOut.WAVE_MAPPER, ref fmt,
                    bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WaveOut.CALLBACK_EVENT);
                if (err != 0)
                {
                    Log.Write("audio output could not open (waveOutOpen " + err + ")");
                    wave = IntPtr.Zero;
                    return;
                }
                short[] block = new short[BufferFrames];
                for (int i = 0; i < BufferCount; i++)
                {
                    data[i] = Marshal.AllocHGlobal(BufferFrames * 2);
                    WaveOut.WAVEHDR h = new WaveOut.WAVEHDR();
                    h.lpData = data[i];
                    h.dwBufferLength = BufferFrames * 2;
                    headers[i] = Marshal.AllocHGlobal(headerSize);
                    Marshal.StructureToPtr(h, headers[i], false);
                    WaveOut.waveOutPrepareHeader(wave, headers[i], headerSize);
                    Fill(block, data[i], headers[i], flagsOffset);
                    WaveOut.waveOutWrite(wave, headers[i], headerSize);
                }
                while (running)
                {
                    bufferDone.WaitOne(100);
                    for (int i = 0; i < BufferCount; i++)
                    {
                        int flags = Marshal.ReadInt32(headers[i], flagsOffset);
                        if ((flags & WaveOut.WHDR_DONE) != 0)
                        {
                            Fill(block, data[i], headers[i], flagsOffset);
                            WaveOut.waveOutWrite(wave, headers[i], headerSize);
                        }
                    }
                    lock (gate)
                    {
                        if (voices.Count == 0 && (DateTime.UtcNow - lastUse).TotalMilliseconds > IdleStopMs)
                        {
                            running = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("audio output failed: " + e.Message);
            }
            finally
            {
                if (wave != IntPtr.Zero)
                {
                    WaveOut.waveOutReset(wave);
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (headers[i] != IntPtr.Zero) WaveOut.waveOutUnprepareHeader(wave, headers[i], headerSize);
                    }
                    WaveOut.waveOutClose(wave);
                }
                for (int i = 0; i < BufferCount; i++)
                {
                    if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                    if (data[i] != IntPtr.Zero) Marshal.FreeHGlobal(data[i]);
                }
                lock (gate)
                {
                    running = false;
                    voices.Clear();
                }
            }
        }

        private void Fill(short[] block, IntPtr buffer, IntPtr header, int flagsOffset)
        {
            Mix(block);
            Marshal.Copy(block, 0, buffer, block.Length);
            int flags = Marshal.ReadInt32(header, flagsOffset);
            Marshal.WriteInt32(header, flagsOffset, flags & ~WaveOut.WHDR_DONE);
        }
    }

    internal static class WaveOut
    {
        public const uint WAVE_MAPPER = 0xFFFFFFFF;
        public const uint CALLBACK_EVENT = 0x00050000;
        public const int WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr hwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr hwo);
    }
}
