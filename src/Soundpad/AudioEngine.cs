using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VioletSoundpad;

internal sealed class AudioDevice
{
    public AudioDevice(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsCable =>
        (Name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0 &&
         (Name.IndexOf("Cable", StringComparison.OrdinalIgnoreCase) >= 0 ||
          Name.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0)) ||
        Name.IndexOf("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase) >= 0 ||
        Name.IndexOf("VoiceMeeter", StringComparison.OrdinalIgnoreCase) >= 0;
}

internal sealed class AudioEngine : ISampleProvider, IDisposable
{
    private sealed class Output : IDisposable
    {
        public Output(MMDevice device, IWaveProvider source, Action<Exception> onError)
        {
            Device = device;
            Player = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
            Player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    onError(e.Exception);
            };
            try
            {
                Player.Init(source);
                Player.Play();
            }
            catch
            {
                Player.Dispose();
                throw;
            }
        }

        public MMDevice Device { get; }
        public WasapiOut Player { get; }

        public void Dispose()
        {
            Player.Dispose();
            Device.Dispose();
        }
    }

    private readonly MMDeviceEnumerator devices = new();
    private readonly object gate = new();
    private readonly BufferedWaveProvider monitorBuffer;
    private Output? cable;
    private Output? monitor;
    private WasapiCapture? capture;
    private MMDevice? microphone;
    private BufferedWaveProvider? microphoneBuffer;
    private ISampleProvider? voice;
    private WaveStream? file;
    private ISampleProvider? samples;
    private bool paused;
    private bool voiceEnabled;
    private TimeSpan position;
    private TimeSpan duration;
    private float volume = .8f;
    private float[] voiceSamples = Array.Empty<float>();
    private byte[] monitorBytes = Array.Empty<byte>();

    public AudioEngine()
    {
        monitorBuffer = CreateBuffer(WaveFormat);
    }

    public event Action<Exception, bool>? DeviceError;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public bool HasClip { get { lock (gate) return file is not null; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    public TimeSpan Position { get { lock (gate) return position; } }
    public TimeSpan Duration { get { lock (gate) return duration; } }
    public float MicrophonePeak => microphone?.AudioMeterInformation.MasterPeakValue ?? 0;
    public float Volume { set { lock (gate) volume = value; } }

    public List<AudioDevice> ListDevices(DataFlow direction)
    {
        var list = new List<AudioDevice>();
        foreach (MMDevice device in devices.EnumerateAudioEndPoints(direction, DeviceState.Active))
        {
            using (device)
                list.Add(new AudioDevice(device.ID, device.FriendlyName));
        }
        return list.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string? DefaultDevice(DataFlow direction)
    {
        if (!devices.HasDefaultAudioEndpoint(direction, Role.Multimedia))
            return null;
        using MMDevice device = devices.GetDefaultAudioEndpoint(direction, Role.Multimedia);
        return device.ID;
    }

    public void Route(string cableId, string? monitorId)
    {
        if (cableId == monitorId)
            throw new InvalidOperationException("Выберите отдельное устройство для наушников.");

        if (cable?.Device.ID != cableId)
        {
            ReleaseOutputs();
            cable = OpenOutput(cableId, this.ToWaveProvider());
        }

        if (monitor?.Device.ID != monitorId)
        {
            var previous = monitor;
            lock (gate)
            {
                monitor = null;
                monitorBuffer.ClearBuffer();
            }
            previous?.Dispose();
            if (monitorId is not null)
            {
                var next = OpenOutput(monitorId, monitorBuffer);
                lock (gate)
                    monitor = next;
            }
        }
    }

    private Output OpenOutput(string id, IWaveProvider source)
    {
        MMDevice device = devices.GetDevice(id);
        try { return new Output(device, source, error => DeviceError?.Invoke(error, false)); }
        catch { device.Dispose(); throw; }
    }

    public static WaveStream OpenFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".aif" or ".aiff"
            ? new AiffFileReader(path)
            : new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
    }

    public void Play(string path)
    {
        WaveStream nextFile = OpenFile(path);
        try
        {
            var nextSamples = ToStereo(nextFile.ToSampleProvider());
            lock (gate)
            {
                ResetClip();
                file = nextFile;
                samples = nextSamples;
                duration = file.TotalTime;
            }
        }
        catch
        {
            nextFile.Dispose();
            throw;
        }
    }

    public void TogglePause()
    {
        lock (gate)
        {
            if (file is null)
                return;
            paused = !paused;
            if (paused)
                monitorBuffer.ClearBuffer();
        }
    }

    public void Stop()
    {
        lock (gate)
            ResetClip();
    }

    private void ResetClip()
    {
        file?.Dispose();
        file = null;
        samples = null;
        paused = false;
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        monitorBuffer.ClearBuffer();
    }

    public void SetMicrophone(string? id, bool sendVoice)
    {
        if (microphone?.ID != id)
        {
            StopCapture();
            if (id is not null)
                StartCapture(id);
        }

        lock (gate)
        {
            if (voiceEnabled != sendVoice)
                microphoneBuffer?.ClearBuffer();
            voiceEnabled = sendVoice && microphone is not null;
        }
    }

    private void StartCapture(string id)
    {
        var device = devices.GetDevice(id);
        WasapiCapture? input = null;
        try
        {
            input = new WasapiCapture(device, true, 40);
            var buffer = CreateBuffer(input.WaveFormat);
            var provider = ToStereo(buffer.ToSampleProvider());
            input.DataAvailable += (_, e) =>
            {
                lock (gate)
                {
                    if (voiceEnabled && ReferenceEquals(buffer, microphoneBuffer))
                        Queue(buffer, e.Buffer, e.BytesRecorded);
                }
            };
            input.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    DeviceError?.Invoke(e.Exception, true);
            };
            input.StartRecording();
            microphone = device;
            capture = input;
            lock (gate)
            {
                microphoneBuffer = buffer;
                voice = provider;
            }
        }
        catch
        {
            input?.Dispose();
            device.Dispose();
            throw;
        }
    }

    private void StopCapture()
    {
        lock (gate)
        {
            voiceEnabled = false;
            voice = null;
            microphoneBuffer = null;
        }
        capture?.Dispose();
        capture = null;
        microphone?.Dispose();
        microphone = null;
    }

    private ISampleProvider ToStereo(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2)
        {
            var stereo = new MultiplexingSampleProvider(new[] { source }, 2);
            stereo.ConnectInputToOutput(0, 0);
            stereo.ConnectInputToOutput(1, 1);
            source = stereo;
        }

        return source.WaveFormat.SampleRate == WaveFormat.SampleRate
            ? source
            : new WdlResamplingSampleProvider(source, WaveFormat.SampleRate);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            int available = 0;
            if (samples is not null && file is not null && !paused)
            {
                available = samples.Read(buffer, offset, count);
                position = file.CurrentTime;
                if (available == 0)
                {
                    file.Dispose();
                    file = null;
                    samples = null;
                    position = duration;
                }
            }
            Array.Clear(buffer, offset + available, count - available);

            for (int i = offset; i < offset + available; i++)
                buffer[i] = Math.Max(-1, Math.Min(1, buffer[i] * volume));

            if (monitor is not null)
            {
                int length = count * sizeof(float);
                if (monitorBytes.Length < length)
                    monitorBytes = new byte[length];
                Buffer.BlockCopy(buffer, offset * sizeof(float), monitorBytes, 0, length);
                Queue(monitorBuffer, monitorBytes, length);
            }

            if (voiceEnabled && voice is not null)
            {
                if (voiceSamples.Length < count)
                    voiceSamples = new float[count];
                int recorded = voice.Read(voiceSamples, 0, count);
                for (int i = 0; i < recorded; i++)
                    buffer[offset + i] = Math.Max(-1, Math.Min(1, buffer[offset + i] + voiceSamples[i]));
            }
            return count;
        }
    }

    private static BufferedWaveProvider CreateBuffer(WaveFormat format) => new(format)
    {
        BufferDuration = TimeSpan.FromMilliseconds(250),
        DiscardOnBufferOverflow = true
    };

    private static void Queue(BufferedWaveProvider queue, byte[] data, int length)
    {
        if (queue.BufferedDuration.TotalMilliseconds > 120)
            queue.ClearBuffer();
        queue.AddSamples(data, 0, length);
    }

    public void ReleaseOutputs()
    {
        Output? previousMonitor;
        lock (gate)
        {
            previousMonitor = monitor;
            monitor = null;
            monitorBuffer.ClearBuffer();
        }
        previousMonitor?.Dispose();
        cable?.Dispose();
        cable = null;
    }

    public void Dispose()
    {
        StopCapture();
        ReleaseOutputs();
        Stop();
        devices.Dispose();
    }
}
