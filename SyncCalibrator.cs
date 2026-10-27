using System.Diagnostics;
using System.Numerics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiSpeaker;

/// <summary>
/// Measures with the microphone how far each extra speaker is ahead of or behind the source speaker,
/// and how much louder or quieter it is. A short chirp is played on the source device and reaches the
/// extra speakers through the normal loopback path, so the measurement covers the real chain,
/// including each speaker's Bluetooth latency and its volume.
/// </summary>
/// <param name="changeVolumeDb">Changes a speaker's volume by the given dB; returns false if it hit its limit.</param>
sealed class SyncCalibrator(
    MMDevice source,
    IReadOnlyList<SpeakerOutput> speakers,
    Func<SpeakerOutput, double, bool> changeVolumeDb,
    IProgress<string> progress)
{
    /// <param name="OffsetMs">&gt; 0: the speaker sounds later than the source. Null: it couldn't be heard.</param>
    /// <param name="LevelDb">Loudness compared with the source after any volume correction (0 = equal).</param>
    /// <param name="VolumeLimited">The speaker's volume hit 0 % or 100 % before it matched.</param>
    public sealed record Result(SpeakerOutput Speaker, int? OffsetMs, double? LevelDb, bool VolumeLimited);

    const int Repeats = 3;
    const int LeadInMs = 100;        // start listening a little before the chirp is due
    const int SourceWindowMs = 1000;
    const int SeparationMs = 400;    // extra delay while measuring, so the two chirps never overlap
    const int MaskMs = 12;           // around the source's peak: its own room echoes, not the other speaker
    const float MinSignalToNoise = 8;
    const float MinPeakRatio = 0.1f; // the other speaker's peak must be at least this strong (-20 dB)
    const double VolumeToleranceDb = 1.5;
    const int VolumeRounds = 3;

    sealed record Measurement(double OffsetMs, double LevelDb);

    CancellationToken cancel;

    /// <param name="matchVolume">Also adjust each speaker's volume to the source's; otherwise only measure.</param>
    /// <param name="cancel">Stops the measurement (e.g. the user pressed Stop); throws OperationCanceledException.</param>
    public async Task<IReadOnlyList<Result>> RunAsync(bool matchVolume, CancellationToken cancel)
    {
        this.cancel = cancel;
        MMDevice micDevice;
        try { micDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
        catch (System.Runtime.InteropServices.COMException) { throw new InvalidOperationException("No microphone found. Auto sync needs your laptop's microphone."); }

        using var mic = new MicRecorder(micDevice);
        using var player = new ChirpPlayer(source);
        var template = Chirp.Create(mic.SampleRate);
        await Task.Delay(500, cancel);  // let both streams settle

        try
        {
            foreach (var speaker in speakers)
                speaker.Muted = true;

            progress.Report("Auto sync: listening to the source speaker…");
            var sourcePeaks = new List<double>();
            for (int i = 0; i < Repeats; i++)
            {
                var corr = await MeasureAsync(mic, player, template, SourceWindowMs, cancel);
                int peak = ArgMax(corr, 0, corr.Length);
                if (corr[peak] >= MinSignalToNoise * Median(corr))
                    sourcePeaks.Add(peak);
            }
            if (sourcePeaks.Count < 2)
                throw new InvalidOperationException("Couldn't hear the source speaker. Turn it up, pause other sound, and keep the laptop near the speakers.");
            int sourceIndex = (int)Median(sourcePeaks);

            var results = new List<Result>();
            foreach (var speaker in speakers)
            {
                progress.Report($"Auto sync: listening to {speaker.Name}…");
                speaker.Muted = false;
                var first = await MeasureSpeakerAsync(mic, player, template, speaker, sourceIndex);
                double? level = first?.LevelDb;
                bool limited = false;

                // Bring its loudness to the source's. Speakers don't all follow the dB they're given,
                // so re-measure after each step.
                for (int round = 0; matchVolume && round < VolumeRounds && level is double db && Math.Abs(db) > VolumeToleranceDb && !limited; round++)
                {
                    progress.Report($"Auto sync: matching the volume of {speaker.Name}…");
                    limited = !changeVolumeDb(speaker, -db);
                    await Task.Delay(300, cancel);  // let the speaker apply it
                    level = (await MeasureSpeakerAsync(mic, player, template, speaker, sourceIndex))?.LevelDb ?? level;
                }

                speaker.Muted = true;
                results.Add(new Result(speaker, first is null ? null : (int)Math.Round(first.OffsetMs), level, limited));
            }
            return results;
        }
        finally
        {
            foreach (var speaker in speakers)
                speaker.Muted = false;
        }
    }

    /// <summary>
    /// Plays chirps with only this extra speaker audible besides the source; returns its timing and loudness
    /// relative to the source, or null if it couldn't be heard reliably.
    /// </summary>
    async Task<Measurement?> MeasureSpeakerAsync(MicRecorder mic, ChirpPlayer player, float[] template, SpeakerOutput speaker, int sourceIndex)
    {
        int originalDelay = speaker.DelayMs;
        int separation = originalDelay + SeparationMs <= SpeakerOutput.MaxDelayMs ? SeparationMs : -SeparationMs;
        speaker.DelayMs = originalDelay + separation;
        try
        {
            await Task.Delay(200, cancel);  // let the new delay settle
            int sourceMs = sourceIndex * 1000 / mic.SampleRate;
            int windowMs = sourceMs + originalDelay + SeparationMs + 1000;
            int mask = MaskMs * mic.SampleRate / 1000;
            var offsets = new List<double>();
            var levels = new List<double>();
            for (int i = 0; i < Repeats; i++)
            {
                var corr = await MeasureAsync(mic, player, template, windowMs, cancel);
                float noise = Median(corr);
                int lo = Math.Max(0, sourceIndex - mask), hi = Math.Min(corr.Length, sourceIndex + mask);
                float sourcePeak = corr[ArgMax(corr, lo, hi)];
                int before = ArgMax(corr, 0, lo), after = ArgMax(corr, hi, corr.Length);
                int other = corr[before] > corr[after] ? before : after;

                if (sourcePeak >= MinSignalToNoise * noise && corr[other] >= MinSignalToNoise * noise && corr[other] >= MinPeakRatio * sourcePeak)
                {
                    offsets.Add((other - sourceIndex) * 1000.0 / mic.SampleRate - separation);
                    levels.Add(20 * Math.Log10(corr[other] / sourcePeak));
                }
            }
            return offsets.Count >= 2 ? new Measurement(Median(offsets), Median(levels)) : null;
        }
        finally
        {
            speaker.DelayMs = originalDelay;
        }
    }

    /// <summary>Plays one chirp and returns the correlation of the recording against it, indexed from the lead-in.</summary>
    static async Task<float[]> MeasureAsync(MicRecorder mic, ChirpPlayer player, float[] template, int windowMs, CancellationToken cancel)
    {
        long playedAt = await player.PlayAsync().WaitAsync(cancel);
        long start = Math.Max(0, mic.FrameAt(playedAt) - LeadInMs * mic.SampleRate / 1000);
        int length = (LeadInMs + windowMs) * mic.SampleRate / 1000 + template.Length;
        var signal = await mic.ReadAsync(start, length, cancel);
        return await Task.Run(() => Correlate(signal, template), cancel);
    }

    static float[] Correlate(float[] signal, float[] template)
    {
        var result = new float[Math.Max(0, signal.Length - template.Length)];
        Parallel.For(0, result.Length, i => result[i] = Math.Abs(Dot(signal.AsSpan(i, template.Length), template)));
        return result;
    }

    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        int i = 0;
        for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        float total = Vector.Dot(sum, Vector<float>.One);
        for (; i < a.Length; i++)
            total += a[i] * b[i];
        return total;
    }

    static int ArgMax(float[] values, int from, int to)
    {
        int best = Math.Min(from, values.Length - 1);
        for (int i = from; i < to; i++)
            if (values[i] > values[best])
                best = i;
        return best;
    }

    static float Median(float[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    static class Chirp
    {
        /// <summary>80 ms sweep from 800 Hz to 5 kHz: sharp correlation peak, survives Bluetooth codecs and laptop mics.</summary>
        public static float[] Create(int sampleRate)
        {
            const double f0 = 800, f1 = 5000, seconds = 0.08;
            int n = (int)(sampleRate * seconds);
            var samples = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / sampleRate;
                double phase = 2 * Math.PI * (f0 * t + (f1 - f0) * t * t / (2 * seconds));
                double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
                samples[i] = (float)(0.6 * window * Math.Sin(phase));
            }
            return samples;
        }
    }

    /// <summary>Plays chirps on a device on demand and reports when each one was handed to the device.</summary>
    sealed class ChirpPlayer : IWaveProvider, IDisposable
    {
        readonly WasapiOut player;
        readonly float[] chirp;
        readonly int channels;
        float[] scratch = [];
        int position = -1;
        TaskCompletionSource<long>? pending;

        public WaveFormat WaveFormat { get; }

        public ChirpPlayer(MMDevice device)
        {
            var mix = device.AudioClient.MixFormat;
            channels = mix.Channels;
            WaveFormat = new WaveFormatExtensible(mix.SampleRate, 32, channels);
            chirp = Chirp.Create(mix.SampleRate);
            player = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            player.Init(this);
            player.Play();
        }

        public Task<long> PlayAsync()
        {
            var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pending, tcs);
            return tcs.Task;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / (channels * 4);
            int samples = frames * channels;
            if (scratch.Length < samples)
                scratch = new float[samples];
            Array.Clear(scratch, 0, samples);

            if (position < 0 && Interlocked.Exchange(ref pending, null) is { } tcs)
            {
                position = 0;
                tcs.SetResult(Stopwatch.GetTimestamp());
            }
            for (int f = 0; f < frames && position >= 0; f++)
            {
                for (int c = 0; c < channels; c++)
                    scratch[f * channels + c] = chirp[position];
                if (++position == chirp.Length)
                    position = -1;
            }

            Buffer.BlockCopy(scratch, 0, buffer, offset, samples * 4);
            return samples * 4;
        }

        public void Dispose()
        {
            player.Stop();
            player.Dispose();
        }
    }

    /// <summary>Records the microphone as mono and maps clock times to positions in the recording.</summary>
    sealed class MicRecorder : IDisposable
    {
        readonly WasapiCapture capture;
        readonly List<float> samples = [];
        readonly int channels;
        readonly bool isFloat;
        long firstTicks = -1;
        long firstFrames;
        Exception? error;

        public int SampleRate { get; }

        public MicRecorder(MMDevice device)
        {
            capture = new WasapiCapture(device, true, 20);
            var format = capture.WaveFormat;
            SampleRate = format.SampleRate;
            channels = format.Channels;
            isFloat = format.BitsPerSample == 32;
            if (format.BitsPerSample is not (32 or 16))
                throw new NotSupportedException($"Unsupported microphone format: {format}");
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) => error = e.Exception ?? new InvalidOperationException("The microphone stopped.");
            capture.StartRecording();
        }

        void OnData(object? sender, WaveInEventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            int bytesPerFrame = channels * (isFloat ? 4 : 2);
            int frames = e.BytesRecorded / bytesPerFrame;
            lock (samples)
            {
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int at = f * bytesPerFrame + c * (isFloat ? 4 : 2);
                        sum += isFloat ? BitConverter.ToSingle(e.Buffer, at) : BitConverter.ToInt16(e.Buffer, at) / 32768f;
                    }
                    samples.Add(sum / channels);
                }
                if (firstTicks < 0)
                {
                    firstTicks = now;
                    firstFrames = samples.Count;
                }
            }
        }

        /// <summary>Recording position at a Stopwatch time. Any fixed offset cancels out between measurements.</summary>
        public long FrameAt(long ticks)
        {
            lock (samples)
                return firstFrames + (long)((ticks - firstTicks) * (double)SampleRate / Stopwatch.Frequency);
        }

        public async Task<float[]> ReadAsync(long start, int count, CancellationToken cancel)
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (error != null)
                    throw error;
                lock (samples)
                {
                    if (samples.Count >= start + count)
                        return samples.GetRange((int)start, count).ToArray();
                }
                if (deadline.ElapsedMilliseconds > count * 2000L / SampleRate + 2000)
                    throw new InvalidOperationException("The microphone isn't delivering sound.");
                await Task.Delay(50, cancel);
            }
        }

        public void Dispose()
        {
            capture.StopRecording();
            capture.Dispose();
        }
    }
}
