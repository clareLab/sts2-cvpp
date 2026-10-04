using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace cvpp;

internal sealed class WorkerClient(string executable, string package, string cache) : IAsyncDisposable
{
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private string? _directory;
    private Task? _stdout;
    private Task? _stderr;
    private readonly SemaphoreSlim _write = new(1);
    private int _busy;

    private async Task Start(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The solver worker currently requires Linux.");
        if (_process is { HasExited: false } && _pipe is { IsConnected: true }) return;
        await Stop();
        string name = "cvpp-" + Guid.NewGuid().ToString("N");
        _directory = Path.Combine(cache, name);
        string game = Path.Combine(_directory, "game");
        string userdata = Path.Combine(_directory, "userdata");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(game);
            string original = Path.GetDirectoryName(executable)!;
            foreach (string entry in Directory.EnumerateFileSystemEntries(original))
            {
                token.ThrowIfCancellationRequested();
                string file = Path.GetFileName(entry);
                if (file is "mods" or "mods_STEAMTEST" or "steam_appid.txt") continue;
                string target = Path.Combine(game, file);
                if (Path.GetFullPath(entry) == Path.GetFullPath(executable)) File.Copy(entry, target);
                else if (Directory.Exists(entry)) Directory.CreateSymbolicLink(target, entry);
                else File.CreateSymbolicLink(target, entry);
            }
            string mod = Path.Combine(game, "mods", "cvpp");
            Directory.CreateDirectory(mod);
            foreach (string file in Directory.EnumerateFiles(package)) File.Copy(file, Path.Combine(mod, Path.GetFileName(file)));
            string profile = Path.Combine(userdata, "SlayTheSpire2");
            string settings = Path.Combine(profile, "default", "1", "settings.save");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, JsonSerializer.Serialize(new
            {
                schema_version = 8,
                mod_settings = new { mods_enabled = true, mod_list = Array.Empty<object>() },
                volume_master = 0,
                skip_intro_logo = true,
                seen_ea_disclaimer = true,
                fullscreen = false,
                fps_limit = 0,
                language = "eng"
            }));
            File.WriteAllText(Path.Combine(profile, ".cvpp-test-sandbox"), "");
        }, token);
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(game, Path.GetFileName(executable)))
        {
            WorkingDirectory = game,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "--headless", "--audio-driver", "Dummy", "--force-steam=off", "--cvpp-worker" })
            start.ArgumentList.Add(argument);
        start.Environment["XDG_DATA_HOME"] = userdata;
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(_directory, "config");
        start.Environment["APPDATA"] = userdata;
        start.Environment["LP_NUM_THREADS"] = "1";
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        start.Environment["CVPP_PIPE"] = name;
        _process = Process.Start(start) ?? throw new IOException("Could not start the solver worker.");
        var output = _process.StandardOutput;
        var errors = _process.StandardError;
        string directory = _directory;
        _stdout = Task.Run(() => Drain(output, Path.Combine(directory, "stdout.log")));
        _stderr = Task.Run(() => Drain(errors, Path.Combine(directory, "stderr.log")));
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(75));
        var connected = _pipe.WaitForConnectionAsync(startup.Token);
        var exited = _process.WaitForExitAsync(startup.Token);
        if (await Task.WhenAny(connected, exited) == exited)
        {
            await exited;
            throw new IOException("The solver worker exited during startup. See cvpp-workers/last-stderr.log.");
        }
        await connected;
        var ready = await Wire.Read(_pipe, startup.Token);
        if (ready.Kind != "ready") throw new InvalidDataException("The solver worker did not become ready.");
    }

    internal async Task<SolveResult> Solve(SolveRequest request, Action<SolveProgress>? progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("A solve is already running.");
        try
        {
            request.Options.Validate();
            await Start(token);
            string id = Guid.NewGuid().ToString("N");
            await Send(new WorkerMessage("solve", id, request), token);
            using var deadline = new CancellationTokenSource();
            deadline.CancelAfter(TimeSpan.FromSeconds(request.Options.Seconds + 45));
            Task cancel = Task.CompletedTask;
            using var registration = token.Register(() => cancel = Cancel(id, deadline));
            while (true)
            {
                var message = await Wire.Read(_pipe!, deadline.Token);
                if (message.Id != id) throw new InvalidDataException("Stale worker reply.");
                if (message.Kind == "progress" && message.Progress != null) progress?.Invoke(message.Progress);
                else if (message.Kind == "result" && message.Result != null)
                {
                    await cancel;
                    return message.Result;
                }
                else if (message.Kind == "error") throw new InvalidOperationException(message.Error);
                else throw new InvalidDataException("Unexpected worker reply.");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await Stop(preserveLogs: true);
            throw new TimeoutException("The solver worker timed out. Try again.");
        }
        catch { await Stop(preserveLogs: true); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private async Task Cancel(string id, CancellationTokenSource deadline)
    {
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await Send(new WorkerMessage("cancel", id), deadline.Token); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { deadline.Cancel(); }
    }

    private async Task Send(WorkerMessage message, CancellationToken token)
    {
        await _write.WaitAsync(token);
        try { await Wire.Write(_pipe!, message, token); }
        finally { _write.Release(); }
    }

    private static async Task Drain(StreamReader reader, string path)
    {
        await using var file = new StreamWriter(path);
        int written = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (written > 1_048_576) { await file.FlushAsync(); file.BaseStream.SetLength(0); file.BaseStream.Position = 0; written = 0; }
            await file.WriteLineAsync(line);
            written += line.Length;
        }
    }

    private async Task Stop(bool preserveLogs = false)
    {
        _pipe?.Dispose();
        _pipe = null;
        if (_process != null)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            _process.Dispose();
            _process = null;
        }
        if (_stdout != null) await _stdout;
        if (_stderr != null) await _stderr;
        _stdout = _stderr = null;
        if (_directory != null && Directory.Exists(_directory))
        {
            if (preserveLogs)
                foreach (string name in new[] { "stdout.log", "stderr.log" })
                    if (File.Exists(Path.Combine(_directory, name))) File.Copy(Path.Combine(_directory, name), Path.Combine(cache, "last-" + name), overwrite: true);
            Directory.Delete(_directory, recursive: true);
        }
        _directory = null;
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
        _write.Dispose();
    }
}
