using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618 // NAudio 3 marks the classic WASAPI classes obsolete; they still work and are simpler here.

/// One recording = one ffmpeg process. Video comes from Windows.Graphics.Capture (gfxcapture)
/// and stays on the GPU; audio is captured with WASAPI and streamed in over named pipes.
sealed class Recorder
{
    record Encoder(string Hw, string Chain, string Codec);

    // Tried in order; the first one that can encode a frame on this machine wins.
    // QSV: vpp_qsv copies each frame into the encoder's own NV12 surface. Feeding the mapped capture texture
    // straight to h264_qsv produced solid-blue stretches in the output (seen on Intel Arc while scrolling).
    static readonly Encoder[] Encoders =
    [
        new("-init_hw_device d3d11va=d3d -filter_hw_device d3d", "", "-c:v h264_nvenc -preset p1 -cq 23"),
        new("-init_hw_device d3d11va=d3d -init_hw_device qsv=qs@d3d -filter_hw_device d3d", ",hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12","-c:v h264_qsv -preset veryfast -global_quality 23"),
        new("-init_hw_device d3d11va=d3d -filter_hw_device d3d", "", "-c:v h264_amf -quality speed -rc cqp -qp_i 22 -qp_p 22"),
        new("-init_hw_device d3d11va=d3d -filter_hw_device d3d", ",hwdownload,format=bgra", "-c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p"),
    ];

    static readonly string Ffmpeg = File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") : "ffmpeg";

    static Task<Encoder>? probe;
    public static Task Warmup() => probe ??= Task.Run(Probe);

    static Encoder Probe()
    {
        foreach (var e in Encoders)
        {
            using var p = Run($"{e.Hw} -filter_complex gfxcapture=monitor_idx=0:width=-2:height=-2{e.Chain} -frames:v 1 {e.Codec} -f null -", out _);
            if (p.WaitForExit(8000) && p.ExitCode == 0) return e;
            try { p.Kill(); } catch { }
        }
        throw new Exception("Couldn't start ffmpeg. Make sure ffmpeg.exe is in the same folder as FastScreenRecorder.exe.");
    }

    readonly Process ff;
    readonly List<AudioPipe> audio;
    readonly List<string> log;
    public string OutputFile { get; }

    volatile bool stopping;
    int reported;
    Action<string>? failed;

    Recorder(Process ff, List<AudioPipe> audio, List<string> log, string file)
    {
        this.ff = ff; this.audio = audio; this.log = log; OutputFile = file;
        ff.EnableRaisingEvents = true;
        ff.Exited += (_, _) => ReportFailure();
    }

    /// Called (on a worker thread) if ffmpeg dies on its own, with its last error lines.
    public void OnFailed(Action<string> handler)
    {
        failed = handler;
        if (ff.HasExited) ReportFailure();
    }

    void ReportFailure()
    {
        if (stopping || failed == null || Interlocked.Exchange(ref reported, 1) == 1) return;
        foreach (var a in audio) a.Stop();
        ff.WaitForExit(); // flush stderr
        lock (log) failed("Recording stopped unexpectedly.\n\n" + string.Join("\n", log.TakeLast(6)));
    }

    /// <param name="source">gfxcapture options selecting what to capture, e.g. "hmonitor=123".</param>
    public static async Task<Recorder> Start(string source, bool cursor, bool sound, bool mic, string file)
    {
        var enc = await (probe ??= Task.Run(Probe));

        // Shared time zero. Video frames are stamped with the wall clock relative to it (WGC's own timestamps
        // drift: a static screen's first frame carries the time it was last drawn), and each audio stream is
        // padded with silence so its first sample also lands at the moment it was actually heard.
        long epoch = WallClockUs();

        var audio = new List<AudioPipe>();
        if (sound) audio.Add(new AudioPipe(loopback: true, epoch));
        if (mic) audio.Add(new AudioPipe(loopback: false, epoch));
        try
        {
            var inputs = string.Join(" ", await Task.WhenAll(audio.Select(a => a.Input)));
            var graph = $"gfxcapture={source}:capture_cursor={(cursor ? 1 : 0)}:max_framerate=60:width=-2:height=-2,setpts=PTS-STARTPTS+(RTCSTART-{epoch})/(TB*1000000),fps=60:start_time=0{enc.Chain}[v]";
            var map = "-map [v]";
            if (audio.Count == 1) map += " -map 0:a";
            if (audio.Count == 2) { graph += ";[0:a][1:a]amix=inputs=2:duration=longest:normalize=0[a]"; map += " -map [a]"; }
            var aud = audio.Count > 0 ? "-c:a aac -b:a 192k -ac 2" : "";

            var p = Run($"{enc.Hw} {inputs} -filter_complex \"{graph}\" {map} {enc.Codec} {aud} -movflags +faststart \"{file}\"", out var log);
            return new Recorder(p, audio, log, file);
        }
        catch
        {
            foreach (var a in audio) a.Stop();
            throw;
        }
    }

    public async Task Stop()
    {
        stopping = true;
        // Stop ffmpeg first so video and audio end on the same instant, then release the devices.
        try { await ff.StandardInput.WriteAsync('q'); await ff.StandardInput.FlushAsync(); } catch { }
        if (!await Task.Run(() => ff.WaitForExit(15000))) ff.Kill();
        foreach (var a in audio) a.Stop();
    }

    /// Runs a one-off ffmpeg job to completion; returns its exit code.
    public static Task<int> RunToEnd(string args) => Task.Run(() =>
    {
        using var p = Run(args, out _);
        p.WaitForExit();
        return p.ExitCode;
    });

    /// Microseconds since the Unix epoch: the same clock ffmpeg's RTCTIME uses.
    public static long WallClockUs() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;

    static Process Run(string args, out List<string> log)
    {
        var psi = new ProcessStartInfo(Ffmpeg, $"-hide_banner -loglevel error -nostats -y {args}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi)!;
        var lines = log = [];
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); }; // drain so ffmpeg never blocks
        p.BeginErrorReadLine();
        return p;
    }
}

/// Captures one WASAPI endpoint and serves the raw samples on a named pipe for ffmpeg to read.
/// All WASAPI objects live on one private thread (they don't like crossing COM apartments).
sealed class AudioPipe
{
    static int count;
    readonly string name = $"fsr_{Environment.ProcessId}_{Interlocked.Increment(ref count)}";
    readonly bool loopback;
    readonly NamedPipeServerStream pipe;
    readonly BlockingCollection<byte[]> queue = new();
    readonly TaskCompletionSource<string> input = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool stopping, connected;

    /// The ffmpeg "-i" arguments for this stream, available once the device's format is known.
    public Task<string> Input => input.Task;

    readonly long epoch;
    bool started;

    public AudioPipe(bool loopback, long epoch)
    {
        this.loopback = loopback;
        this.epoch = epoch;
        pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 20);
        new Thread(Pump) { IsBackground = true }.Start();
    }

    void Pump()
    {
        IWaveIn? cap = null;
        WasapiOut? keepAlive = null;
        try
        {
            cap = loopback ? new WasapiLoopbackCapture() : new WasapiCapture();
            if (loopback)
            {
                // Loopback goes quiet when nothing plays, which would desync audio. Playing silence keeps it ticking.
                keepAlive = new WasapiOut(AudioClientShareMode.Shared, 100);
                keepAlive.Init(new SilenceProvider(cap.WaveFormat));
            }
            var f = cap.WaveFormat;
            cap.DataAvailable += (_, e) =>
            {
                if (stopping || e.BytesRecorded == 0) return;
                if (!started)
                {
                    // Fill the gap between time zero and the first sample we got, so audio lines up with video.
                    started = true;
                    long firstSampleUs = Recorder.WallClockUs() - (long)e.BytesRecorded * 1_000_000 / f.AverageBytesPerSecond;
                    long gap = Math.Max(0, firstSampleUs - epoch) * f.SampleRate / 1_000_000 * f.BlockAlign;
                    if (gap > 0) queue.Add(new byte[gap]);
                }
                try { queue.Add(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray()); } catch (InvalidOperationException) { }
            };
            input.SetResult($"-f {SampleFormat(f)} -ar {f.SampleRate} -ac {f.Channels} -thread_queue_size 4096 -i \\\\.\\pipe\\{name}");

            pipe.WaitForConnection();
            connected = true;
            keepAlive?.Play();
            cap.StartRecording();
            foreach (var b in queue.GetConsumingEnumerable()) pipe.Write(b);
        }
        catch (Exception ex) { input.TrySetException(new Exception((loopback ? "System sound" : "Microphone") + ": " + ex.Message)); }
        finally
        {
            try { cap?.StopRecording(); } catch { }
            cap?.Dispose();
            keepAlive?.Dispose();
            try { pipe.Dispose(); } catch { }
        }
    }

    public void Stop()
    {
        stopping = true;
        queue.CompleteAdding();
        if (!connected) try { pipe.Dispose(); } catch { } // unblocks WaitForConnection
    }

    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    static string SampleFormat(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
            || (f is WaveFormatExtensible x && x.SubFormat == IeeeFloat);
        if (isFloat) return f.BitsPerSample == 64 ? "f64le" : "f32le";
        return f.BitsPerSample switch { 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => "u8" };
    }
}
