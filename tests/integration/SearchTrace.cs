using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace cvpp;

internal sealed class SearchTrace : IDisposable
{
    private static SearchTrace? _active;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    internal SearchTrace()
    {
        if (_active != null) throw new InvalidOperationException("A search trace is already active.");
        _active = this;
    }

    internal static void Observe(ReadOnlySpan<uint> path, int selectedDepth, int hp, bool terminal, ReadOnlySpan<uint> actions)
    {
        if (_active == null) return;
        ReadOnlySpan<int> header = [path.Length, selectedDepth, hp, terminal ? 1 : 0, actions.Length];
        _active._hash.AppendData(MemoryMarshal.AsBytes(header));
        _active._hash.AppendData(MemoryMarshal.AsBytes(path));
        _active._hash.AppendData(MemoryMarshal.AsBytes(actions));
    }

    internal string Finish() => Convert.ToHexString(_hash.GetHashAndReset());

    public void Dispose()
    {
        _active = null;
        _hash.Dispose();
    }
}
