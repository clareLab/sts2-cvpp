using System.IO.Pipes;
using System.Threading.Channels;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace cvpp;

internal static class WorkerHost
{
    internal static bool Active => OS.GetCmdlineArgs().Contains("--cvpp-worker");
    private static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    internal static void Initialize()
    {
        if (DisplayServer.GetName() != "headless" || !File.Exists(ProjectSettings.GlobalizePath("user://.cvpp-test-sandbox")))
            throw new InvalidOperationException("The solver worker requires an isolated headless profile.");
        new Harmony("clarelab.cvpp.assets").CreateClassProcessor(typeof(HeadlessAssets)).Patch();
        new Harmony("clarelab.cvpp.presentation").CreateClassProcessor(typeof(HeadlessPresentation)).Patch();
        _ = Run();
    }

    private static async Task Run()
    {
        try
        {
            string pipeName = System.Environment.GetEnvironmentVariable("CVPP_PIPE")
                ?? throw new InvalidOperationException("Missing worker pipe.");
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(60_000);
            while (NGame.Instance == null || !SaveManager.Instance.IsProfileInitialized)
                await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            await NGame.Instance.GameStartupComplete;
            while (NGame.Instance.Transition.InTransition) await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            await NAssetLoader.Instance.LoadInTheBackground(PreloadManager.Cache.CreateSession("cvpp-worker", []));
            SaveManager.Instance.SetFtuesEnabled(false);
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            Engine.MaxFps = 10;
            await Wire.Write(pipe, new WorkerMessage("ready", Compatibility: WorkerEnvironment.Capture().Compatibility));
            var incoming = Channel.CreateBounded<WorkerMessage>(1);
            var outgoing = Channel.CreateBounded<WorkerMessage>(new BoundedChannelOptions(16)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
            using var lifetime = new CancellationTokenSource();
            CancellationTokenSource? search = null;
            var gate = new object();
            string? activeId = null;
            string? cancelledId = null;
            var read = Task.Run(async () =>
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var message = await Wire.Read(pipe, lifetime.Token);
                        if (message.Kind == "cancel")
                        {
                            lock (gate)
                            {
                                cancelledId = message.Id;
                                if (activeId == message.Id) search?.Cancel();
                            }
                        }
                        else await incoming.Writer.WriteAsync(message, lifetime.Token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    lock (gate) search?.Cancel();
                    incoming.Writer.TryComplete(error);
                }
            });
            var write = Task.Run(async () =>
            {
                await foreach (var response in outgoing.Reader.ReadAllAsync(lifetime.Token))
                    await Wire.Write(pipe, response, lifetime.Token);
            });
            try
            {
                await foreach (var message in incoming.Reader.ReadAllAsync(lifetime.Token))
                {
                    if (message.Kind != "solve" || message.Request == null) throw new InvalidDataException("Unknown worker request.");
                    using var cancellation = new CancellationTokenSource();
                    Engine.MaxFps = 0;
                    lock (gate)
                    {
                        activeId = message.Id;
                        search = cancellation;
                        if (cancelledId == activeId) cancellation.Cancel();
                    }
                    await using var combat = new NativeCombat { Mode = CombatExecution.Worker };
                    try
                    {
                        var result = await HealthSearch.Run(combat, () => message.Request.Position.Restore(combat), message.Request.Options,
                            progress => outgoing.Writer.TryWrite(new WorkerMessage("progress", message.Id, Progress: progress)), cancellation.Token, message.Request.Incumbent);
                        outgoing.Writer.TryWrite(new WorkerMessage("result", message.Id, Result: result));
                    }
                    catch (Exception error)
                    {
                        GD.PrintErr("[cvpp] Worker: " + error);
                        outgoing.Writer.TryWrite(new WorkerMessage("error", message.Id, Error: error.Message));
                        break;
                    }
                    finally { lock (gate) { search = null; activeId = null; } Engine.MaxFps = 10; }
                }
            }
            finally
            {
                outgoing.Writer.TryComplete();
                try { await write; }
                finally { lifetime.Cancel(); await read; }
            }
        }
        catch (Exception error) { GD.PrintErr("[cvpp] Worker stopped: " + error.Message); }
        Tree.Quit();
    }
}
