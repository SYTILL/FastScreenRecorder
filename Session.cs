/// One recording from the user's point of view. Pausing ends the current ffmpeg run;
/// resuming starts a new part; finishing joins the parts losslessly into the final file.
sealed class Session
{
    readonly string source;
    readonly bool cursor, sound, mic;
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "FastScreenRecorder", Guid.NewGuid().ToString("N"));
    readonly List<string> parts = [];
    Recorder? current;
    Action<string>? failed;

    public string OutputFile { get; }
    public bool Paused => current == null;

    Session(string source, bool cursor, bool sound, bool mic, string outputFile)
    {
        this.source = source; this.cursor = cursor; this.sound = sound; this.mic = mic;
        OutputFile = outputFile;
        Directory.CreateDirectory(tempDir);
    }

    public static async Task<Session> Start(string source, bool cursor, bool sound, bool mic, string outputFile)
    {
        var s = new Session(source, cursor, sound, mic, outputFile);
        await s.Resume();
        return s;
    }

    /// Called (on a worker thread) if ffmpeg dies on its own.
    public void OnFailed(Action<string> handler) => failed = handler;

    public async Task Resume()
    {
        var part = Path.Combine(tempDir, $"{parts.Count}.mp4");
        var r = await Recorder.Start(source, cursor, sound, mic, part);
        parts.Add(part);
        current = r;
        r.OnFailed(msg => { current = null; failed?.Invoke(msg); });
    }

    public async Task Pause()
    {
        var r = current;
        current = null;
        if (r != null) await r.Stop();
    }

    /// Stops if needed and writes <see cref="OutputFile"/>. Returns false if nothing usable was recorded.
    public async Task<bool> Finish()
    {
        await Pause();
        var good = parts.Where(p => File.Exists(p) && new FileInfo(p).Length > 0).ToList();
        try
        {
            if (good.Count == 0) return false;
            if (good.Count == 1) { File.Move(good[0], OutputFile, true); return true; }

            var list = Path.Combine(tempDir, "parts.txt");
            File.WriteAllLines(list, good.Select(p => $"file '{p.Replace("'", @"'\''")}'"));
            return await Recorder.RunToEnd($"-f concat -safe 0 -i \"{list}\" -c copy -movflags +faststart \"{OutputFile}\"") == 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
