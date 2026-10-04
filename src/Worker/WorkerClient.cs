using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace cvpp;

internal sealed class WorkerClient(string executable, string package, string cache, WorkerSetup setup) : IAsyncDisposable
{
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private string? _directory;
    private FileStream? _lease;
    private Task? _stdout;
    private Task? _stderr;
    private readonly SemaphoreSlim _write = new(1);
    private readonly SemaphoreSlim _operation = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _dispose;
    private volatile bool _disposed;
    private volatile int _processId;
    private volatile int _supervisorId;
    private volatile string? _requestId;
    private volatile bool _paused;
    internal int? ProcessId { get { int id = _processId; return id == 0 ? null : id; } }

    private async Task Start(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The solver worker currently requires Linux.");
        if (_process is { HasExited: false } && _pipe is { IsConnected: true }) return;
        await Stop().ConfigureAwait(false);
        string name = "cvpp-" + Guid.NewGuid().ToString("N");
        _directory = Path.Combine(cache, name);
        string game = Path.Combine(_directory, "game");
        string userdata = Path.Combine(_directory, "userdata");
        await Task.Run(() =>
        {
            Reap(cache);
            Directory.CreateDirectory(game);
            _lease = new FileStream(Path.Combine(_directory, ".cvpp-worker"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
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
            for (int index = 0; index < setup.Mods.Length; index++)
            {
                var source = setup.Mods[index];
                if (source.Id == "cvpp") continue;
                string destination = Path.Combine(game, "mods", "environment", index.ToString("D3"));
                foreach (string file in Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    string target = Path.Combine(destination, Path.GetRelativePath(source.Path, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (Path.GetExtension(file).Equals(".pck", StringComparison.OrdinalIgnoreCase)) File.CreateSymbolicLink(target, file);
                    else File.Copy(file, target);
                }
            }
            string profile = Path.Combine(userdata, "SlayTheSpire2");
            string settings = Path.Combine(profile, "default", "1", "settings.save");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, JsonSerializer.Serialize(new
            {
                schema_version = 8,
                mod_settings = new { mods_enabled = true, mod_list = setup.Mods.Select(mod => new { id = mod.Id, is_enabled = true, source = "mods_directory" }).ToArray() },
                volume_master = 0,
                skip_intro_logo = true,
                seen_ea_disclaimer = true,
                fullscreen = false,
                fps_limit = 0,
                language = "eng"
            }));
            File.WriteAllText(Path.Combine(profile, ".cvpp-test-sandbox"), "");
        }, token).ConfigureAwait(false);
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(game, "mods", "cvpp", "cvpp-worker"))
        {
            WorkingDirectory = game,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(_directory);
        start.ArgumentList.Add(Path.Combine(game, Path.GetFileName(executable)));
        foreach (string argument in new[] { "--headless", "--audio-driver", "Dummy", "--force-steam=off", "--cvpp-worker" })
            start.ArgumentList.Add(argument);
        start.Environment["XDG_DATA_HOME"] = userdata;
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(_directory, "config");
        start.Environment["APPDATA"] = userdata;
        start.Environment["LP_NUM_THREADS"] = "1";
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        start.Environment["CVPP_PIPE"] = name;
        token.ThrowIfCancellationRequested();
        _process = Process.Start(start) ?? throw new IOException("Could not start the solver worker.");
        _processId = _supervisorId = _process.Id;
        var output = _process.StandardOutput;
        var errors = _process.StandardError;
        string directory = _directory;
        _stdout = Task.Run(() => Drain(output, Path.Combine(directory, "stdout.log")));
        _stderr = Task.Run(() => Drain(errors, Path.Combine(directory, "stderr.log")));
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(75));
        var connected = _pipe.WaitForConnectionAsync(startup.Token);
        var exited = _process.WaitForExitAsync(startup.Token);
        if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
        {
            await exited.ConfigureAwait(false);
            throw new IOException("The solver worker exited during startup. See cvpp-workers/last-stderr.log.");
        }
        await connected.ConfigureAwait(false);
        var ready = await Wire.Read(_pipe, startup.Token).ConfigureAwait(false);
        if (ready.Kind != "ready" || ready.ProcessId is not > 0) throw new InvalidDataException("The solver worker did not become ready.");
        _processId = ready.ProcessId.Value;
        if (ready.Compatibility != setup.Compatibility)
            throw new NotSupportedException("The worker's loaded mods or serialization schema differ from the game. Restart after updating mods.");
    }

    internal async Task<SolveResult> Solve(SolveRequest request, IProgress<SolveProgress>? progress, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        request.Options.Validate();
        if (!await _operation.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("A solve is already running.");
        if (_disposed) { _operation.Release(); throw new ObjectDisposedException(nameof(WorkerClient)); }
        using var memoryLimit = new CancellationTokenSource();
        using var monitorStop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, memoryLimit.Token);
        var originalToken = token;
        token = linked.Token;
        long memory = 0;
        SolveProgress? latest = null;
        var clock = Stopwatch.StartNew();
        var monitor = MonitorMemory(request.Options.MemoryMiB, value => Interlocked.Exchange(ref memory, value), memoryLimit, monitorStop.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Start(token).ConfigureAwait(false);
            string id = Guid.NewGuid().ToString("N");
            await _write.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _requestId = id;
                await Wire.Write(_pipe!, new WorkerMessage("solve", id, request), token).ConfigureAwait(false);
                if (_paused) await Wire.Write(_pipe!, new WorkerMessage("pause", id), token).ConfigureAwait(false);
            }
            finally { _write.Release(); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, memoryLimit.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            Task cancel = Task.CompletedTask;
            using var registration = token.Register(() => cancel = Cancel(id, deadline));
            try
            {
                while (true)
                {
                    var message = await Wire.Read(_pipe!, deadline.Token).ConfigureAwait(false);
                    if (message.Id != id) throw new InvalidDataException("Stale worker reply.");
                    if (message.Kind == "progress" && message.Progress != null)
                    {
                        if (!token.IsCancellationRequested) deadline.CancelAfter(TimeSpan.FromSeconds(45));
                        latest = message.Progress with { MemoryBytes = Interlocked.Read(ref memory) };
                        progress?.Report(latest);
                    }
                    else if (message.Kind == "result" && message.Result != null) return message.Result;
                    else if (message.Kind == "error") throw new InvalidOperationException(message.Error);
                    else throw new InvalidDataException("Unexpected worker reply.");
                }
            }
            finally { await registration.DisposeAsync().ConfigureAwait(false); await cancel.ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (memoryLimit.IsCancellationRequested && !originalToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            await Stop().ConfigureAwait(false);
            progress?.Report((latest ?? new SolveProgress(0, 0, null, 0)) with { MemoryBytes = Interlocked.Read(ref memory) });
            return new SolveResult(latest?.Plan, default, latest?.ElapsedMs ?? clock.Elapsed.TotalMilliseconds, "memory_limit", 0, 0);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await Stop(preserveLogs: true).ConfigureAwait(false);
            throw new TimeoutException("Worker unresponsive.");
        }
        catch (OperationCanceledException) { await Stop().ConfigureAwait(false); throw; }
        catch { await Stop(preserveLogs: true).ConfigureAwait(false); throw; }
        finally
        {
            _requestId = null;
            try { monitorStop.Cancel(); await monitor.ConfigureAwait(false); }
            finally { _operation.Release(); }
        }
    }

    internal async Task SetPaused(bool paused)
    {
        _paused = paused;
        await _write.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_requestId is { } id)
                await Wire.Write(_pipe!, new WorkerMessage(_paused ? "pause" : "resume", id), _lifetime.Token).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    private async Task MonitorMemory(int limit, Action<long> update, CancellationTokenSource exceeded, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        Process? measured = null;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    int id = _processId;
                    if (id == 0) continue;
                    if (id == _supervisorId)
                    {
                        string children = await File.ReadAllTextAsync($"/proc/{id}/task/{id}/children", token).ConfigureAwait(false);
                        if (!int.TryParse(children.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out id)) continue;
                    }
                    if (measured?.Id != id) { measured?.Dispose(); measured = Process.GetProcessById(id); }
                    measured.Refresh();
                    long bytes = measured.WorkingSet64;
                    update(bytes);
                    if (limit > 0 && bytes >= (long)limit * 1024 * 1024) { exceeded.Cancel(); return; }
                }
                catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { measured?.Dispose(); }
    }

    private async Task Cancel(string id, CancellationTokenSource deadline)
    {
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await Send(new WorkerMessage("cancel", id), deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { deadline.Cancel(); }
    }

    private async Task Send(WorkerMessage message, CancellationToken token)
    {
        await _write.WaitAsync(token).ConfigureAwait(false);
        try { await Wire.Write(_pipe!, message, token).ConfigureAwait(false); }
        finally { _write.Release(); }
    }

    private static async Task Drain(StreamReader reader, string path)
    {
        await using var file = new StreamWriter(path);
        int written = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (written > 1_048_576) { await file.FlushAsync().ConfigureAwait(false); file.BaseStream.SetLength(0); file.BaseStream.Position = 0; written = 0; }
            await file.WriteLineAsync(line).ConfigureAwait(false);
            written += line.Length;
        }
    }

    private async Task Stop(bool preserveLogs = false)
    {
        _pipe?.Dispose();
        _pipe = null;
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.BaseStream.WriteAsync(new byte[] { 0 }).ConfigureAwait(false);
                    await _process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (_process.HasExited) { }
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            _process.Dispose();
            _process = null;
            _processId = 0;
        }
        try { await Task.WhenAll(_stdout ?? Task.CompletedTask, _stderr ?? Task.CompletedTask).ConfigureAwait(false); }
        finally
        {
            _stdout = _stderr = null;
            _lease?.Dispose();
            _lease = null;
            if (_directory != null && Directory.Exists(_directory))
            {
                try
                {
                    if (preserveLogs)
                        foreach (string name in new[] { "stdout.log", "stderr.log" })
                            if (File.Exists(Path.Combine(_directory, name))) File.Copy(Path.Combine(_directory, name), Path.Combine(cache, "last-" + name), overwrite: true);
                }
                finally { Directory.Delete(_directory, recursive: true); }
            }
            _directory = null;
        }
    }

    private static void Reap(string cache)
    {
        if (!Directory.Exists(cache)) return;
        foreach (string directory in Directory.EnumerateDirectories(cache, "cvpp-*"))
        {
            string marker = Path.Combine(directory, ".cvpp-worker");
            if (!File.Exists(marker)) continue;
            try
            {
                using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    internal void Abort() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_operation) return new ValueTask(_dispose ??= Dispose());
    }

    private async Task Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        await _operation.WaitAsync().ConfigureAwait(false);
        try { await Stop().ConfigureAwait(false); }
        finally { _lifetime.Dispose(); _write.Dispose(); _operation.Release(); }
    }
}
