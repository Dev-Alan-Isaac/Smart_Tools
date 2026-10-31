#pragma warning disable CS0618 // WasapiOut is marked obsolete in NAudio 3 but is tested here; WasapiPlayer would add low-latency modes this tool does not need
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Smart_Tools.WPF.Models;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The sound-card half of the Audio Output Tester: WASAPI devices and a shared-mode stream.</summary>
    public static partial class AudioOutputTestManager
    {
        /// <summary>The active output devices (speakers, headphones, USB DACs, Bluetooth...). The Windows default is flagged.</summary>
        public static List<AudioOutputDevice> ListDevices()
        {
            var result = new List<AudioOutputDevice>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                string? defaultId = null;
                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = def.ID;
                }
                catch (COMException)
                {
                    // no default output at all
                }

                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                    {
                        try
                        {
                            using var client = device.CreateAudioClient();
                            var mix = client.MixFormat;
                            uint mask = mix is WaveFormatExtensible ext ? (uint)ext.ChannelMask : 0;
                            result.Add(new AudioOutputDevice(device.ID, device.FriendlyName, mix.Channels, mix.SampleRate, mask, device.ID == defaultId));
                        }
                        catch (Exception ex) when (ex is COMException or InvalidOperationException)
                        {
                            // a device that can't be queried right now is left out
                        }
                    }
                }
            }
            catch (COMException)
            {
                // the audio service isn't running
            }

            return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Opens the device in shared mode at its own mix format (so no resampling), with a silent stream ready for <see cref="AudioOutputSession.Generator"/>.</summary>
        public static AudioOutputSession Open(AudioOutputDevice device) => new(device);
    }

    /// <summary>A running output stream to one device. The generator decides what is heard; <c>Generator.IsPlaying</c> is the start/stop.</summary>
    public sealed class AudioOutputSession : IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly MMDevice _mmDevice;
        private readonly WasapiOut _output;

        public AudioOutputDevice Device { get; }
        public AudioSignalGenerator Generator { get; }

        public AudioOutputSession(AudioOutputDevice device)
        {
            Device = device;
            _mmDevice = _enumerator.GetDevice(device.Id);
            using var client = _mmDevice.CreateAudioClient();
            var mix = client.MixFormat;
            Generator = new AudioSignalGenerator(mix.SampleRate, mix.Channels);

            _output = new WasapiOut(_mmDevice, AudioClientShareMode.Shared, true, 60);
            _output.Init(new GeneratorProvider(Generator, StreamFormat(mix)));
            _output.Play();
        }

        public void Dispose()
        {
            Generator.IsPlaying = false;
            try { _output.Stop(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* device already gone */ }
            _output.Dispose();
            _mmDevice.Dispose();
            _enumerator.Dispose();
        }

        private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

        /// <summary>
        /// The format we write in. For 5.1 / 7.1 devices WASAPI rejects a plain float format with no speaker layout
        /// (E_INVALIDARG, 0x80070057), so the device's own mix format - which carries the channel mask - is used when it is 32-bit float
        /// (shared mode always is on Windows). Anything else falls back to plain float.
        /// </summary>
        private static WaveFormat StreamFormat(WaveFormat mix) =>
            mix is WaveFormatExtensible { BitsPerSample: 32 } ext && ext.SubFormat == IeeeFloatSubFormat
                ? mix
                : WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);

        private sealed class GeneratorProvider : IWaveProvider
        {
            private readonly AudioSignalGenerator _generator;

            public GeneratorProvider(AudioSignalGenerator generator, WaveFormat format)
            {
                _generator = generator;
                WaveFormat = format;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(Span<byte> buffer)
            {
                var samples = MemoryMarshal.Cast<byte, float>(buffer);
                _generator.Fill(samples);
                return samples.Length * sizeof(float);
            }
        }
    }
}
