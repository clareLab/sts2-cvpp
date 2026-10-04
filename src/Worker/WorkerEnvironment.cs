using System.Security.Cryptography;
using System.Text;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace cvpp;

internal static class WorkerEnvironment
{
    internal static WorkerSetup Capture()
    {
        var loaded = ModManager.GetLoadedMods().ToArray();
        var selected = loaded.Where(mod => mod.assemblies.Count != 0).ToHashSet();
        void Dependencies(Mod mod)
        {
            foreach (var dependency in mod.manifest!.dependencies ?? [])
            {
                var required = loaded.Single(m => m.manifest!.id == dependency.id);
                if (selected.Add(required)) Dependencies(required);
            }
        }
        foreach (var mod in selected.ToArray()) Dependencies(mod);
        var mods = loaded.Where(selected.Contains).Select(mod => new WorkerMod(mod.manifest!.id!, mod.path)).ToArray();
        var text = new StringBuilder(ModelIdSerializationCache.Dump());
        text.Append(typeof(ModelIdSerializationCache).Assembly.ManifestModule.ModuleVersionId).AppendLine();
        foreach (var mod in loaded.Where(selected.Contains))
        {
            text.Append(mod.manifest!.id).Append(':').Append(mod.manifest.version).AppendLine();
            foreach (var assembly in mod.assemblies.Where(assembly => !assembly.IsDynamic))
                text.Append(assembly.ManifestModule.ModuleVersionId).AppendLine();
        }
        return new WorkerSetup(mods, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
    }
}
