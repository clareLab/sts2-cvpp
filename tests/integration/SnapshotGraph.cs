using System.Reflection;
using System.Runtime.InteropServices;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace cvpp;

internal sealed class SnapshotGraph : IDisposable
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<Type, FieldInfo[]> Layouts = [];
    private static readonly Dictionary<Type, Action<object, ulong[], int, object?[]>> Decoders = [];
    private static readonly Dictionary<Type, Action<SnapshotGraph, object>> Encoders = [];
    private static readonly HashSet<string> RuntimeFields = [];
    private static readonly HashSet<string> KnownRuntimeFields =
    [
        "MegaCrit.Sts2.Core.Combat.CombatManager._turnLoopTask",
        "MegaCrit.Sts2.Core.Combat.CombatManager._turnLoopWaitingForPreviousSource",
        "MegaCrit.Sts2.Core.Combat.CombatStateTracker._combatStateChangedDeferredTask",
        "MegaCrit.Sts2.Core.Combat.CombatTurnState._cts",
        "MegaCrit.Sts2.Core.Combat.CombatTurnState.<BeginEnemyTurnSignalSource>k__BackingField",
        "MegaCrit.Sts2.Core.Combat.CombatTurnState.<EndTurnSignalSource>k__BackingField",
        "MegaCrit.Sts2.Core.Combat.CombatTurnState.<ReadyLock>k__BackingField",
        "MegaCrit.Sts2.Core.GameActions.ActionExecutor._actionCancelToken",
        "MegaCrit.Sts2.Core.GameActions.ActionExecutor._queueTaskCompletionSource",
        "MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet._queuesEmptyCompletionSource",
        "MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer+ReceivedChoice.completionSource",
        "MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer._hoverMessageTask"
    ];
    private readonly List<object?> _references = [null];
    private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _pending = [];
    private readonly List<Entry> _entries = [];
    private readonly List<ulong> _words = [];
    private readonly HashSet<string> _boundaries = [];
    private readonly Dictionary<object, (object? Parent, string Member)> _origins = new(ReferenceEqualityComparer.Instance);
    private object? _visiting;
    private string _member = "root";
    private readonly SnapshotNative _native;
    private readonly ulong[] _buffer;
    private object?[] _referenceTable;
    private bool _disposed;

    internal int Objects => _entries.Count;
    internal int References => _references.Count;
    internal int Bytes => _buffer.Length * sizeof(ulong);
    internal ulong SharedBytes => SnapshotNative.SharedBytes(_native);
    internal string[] Boundaries => _boundaries.Order().ToArray();
    internal static string[] RuntimeBoundaries => RuntimeFields.Order().ToArray();
    internal object[] Types => _entries.GroupBy(entry => entry.Target.GetType().FullName)
        .OrderByDescending(group => group.Count()).Take(20).Select(group => (object)new { type = group.Key, count = group.Count() }).ToArray();
    internal string[] Runs => _entries.Where(entry => entry.Target is RunState).Select(entry => Origin(entry.Target)).ToArray();

    internal SnapshotGraph(SnapshotGraph? parent, params object[] roots) : this(parent, true, roots) { }

    internal SnapshotGraph(SnapshotGraph? parent, bool compiled, params object[] roots)
    {
        foreach (object root in roots) Reference(root);
        for (int index = 0; index < _pending.Count; index++)
        {
            if (_pending.Count > 100_000 || _words.Count > 1_000_000)
                throw new NotSupportedException("Snapshot graph exceeds the probe budget.");
            object target = _pending[index];
            _visiting = target;
            _member = "[]";
            int start = _words.Count;
            Type type = target.GetType();
            if (target is Array array && (!compiled || !type.IsSZArray))
            {
                Type element = target.GetType().GetElementType()!;
                foreach (object? item in array) Write(element, item);
            }
            else if (compiled)
            {
                if (!Encoders.TryGetValue(type, out var encoder)) Encoders[type] = encoder = SnapshotEncoder.Compile(type);
                encoder(this, target);
            }
            else
                foreach (FieldInfo field in Layout(type))
                {
                    _member = field.Name;
                    Write(field.FieldType, field.GetValue(target));
                }
            Action<object, ulong[], int, object?[]>? decoder = null;
            if (!type.IsArray || type.IsSZArray)
            {
                if (!Decoders.TryGetValue(type, out decoder)) Decoders[type] = decoder = SnapshotDecoder.Compile(type);
            }
            _entries.Add(new Entry(target, start, _words.Count - start, decoder));
        }
        _buffer = new ulong[_words.Count];
        _native = SnapshotNative.Capture(CollectionsMarshal.AsSpan(_words), parent?._native);
        _referenceTable = _references.ToArray();
        _words.Clear();
        _words.TrimExcess();
        _pending.Clear();
        _visiting = null;
    }

    internal void Member(string name) => _member = name;

    internal void Word(ulong value)
    {
        if (_words.Count >= 1_000_000) throw new NotSupportedException("Snapshot data budget exceeded.");
        _words.Add(value);
    }

    internal void WriteReference(object? value) => Word(checked((ulong)Reference(value)));

    internal ulong[] Words()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _native.Restore(_buffer);
        return (ulong[])_buffer.Clone();
    }

    internal void Restore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _native.Restore(_buffer);
        foreach (Entry entry in _entries)
        {
            if (entry.Decoder != null)
            {
                entry.Decoder(entry.Target, _buffer, entry.Offset, _referenceTable);
                continue;
            }
            int offset = entry.Offset;
            if (entry.Target is Array array)
            {
                Type element = array.GetType().GetElementType()!;
                int[] indices = Enumerable.Range(0, array.Rank).Select(array.GetLowerBound).ToArray();
                for (int item = 0; item < array.Length; item++)
                {
                    array.SetValue(Read(element, ref offset), indices);
                    for (int dimension = array.Rank - 1; dimension >= 0; dimension--)
                        if (++indices[dimension] <= array.GetUpperBound(dimension)) break;
                        else indices[dimension] = array.GetLowerBound(dimension);
                }
            }
            else
                foreach (FieldInfo field in Layout(entry.Target.GetType())) field.SetValue(entry.Target, Read(field.FieldType, ref offset));
            if (offset != entry.Offset + entry.Length) throw new InvalidOperationException("Snapshot schema mismatch.");
        }
    }

    private int Reference(object? value)
    {
        if (value == null) return 0;
        if (_ids.TryGetValue(value, out int id)) return id;
        if (_ids.Count >= 100_000) throw new NotSupportedException("Snapshot reference budget exceeded.");
        id = _references.Count;
        _references.Add(value);
        _ids.Add(value, id);
        _origins.Add(value, (_visiting, _member));
        Type type = value.GetType();
        if (value is string or Type or MemberInfo) return id;
        if (value is Delegate callback)
        {
            foreach (Delegate part in callback.GetInvocationList()) Reference(part.Target);
            return id;
        }
        if (value is AbstractModel { IsMutable: false }) return id;
        if (value is RunManager or NativeCombat or INetGameService || type.Namespace == "MegaCrit.Sts2.Core.Logging"
            || type.FullName!.StartsWith("System.Collections.Generic.GenericEqualityComparer", StringComparison.Ordinal)
            || type.FullName.StartsWith("System.Collections.Generic.ObjectEqualityComparer", StringComparison.Ordinal)
            || type.FullName.StartsWith("System.Collections.Generic.EnumEqualityComparer", StringComparison.Ordinal)
            || value is StringComparer || type.FullName.StartsWith("System.Collections.Generic.NonRandomizedStringEqualityComparer", StringComparison.Ordinal))
        {
            _boundaries.Add(type.FullName!);
            return id;
        }
        if (value is GodotObject or Task or CancellationTokenSource || type.FullName!.Contains("TaskCompletionSource", StringComparison.Ordinal))
            throw new NotSupportedException($"Unowned runtime state: {type.FullName}");
        if (!type.IsArray && type.Assembly != typeof(RunState).Assembly
            && !(type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) ?? false)
            && type.Namespace != "System.Linq"
            && !type.IsValueType)
            throw new NotSupportedException($"Unknown snapshot owner: {type.FullName}");
        _pending.Add(value);
        return id;
    }

    internal static FieldInfo[] Layout(Type type)
    {
        if (Layouts.TryGetValue(type, out FieldInfo[]? fields)) return fields;
        var result = new List<FieldInfo>();
        for (Type? current = type; current != null; current = current.BaseType)
            foreach (FieldInfo field in current.GetFields(Flags))
            {
                Type value = field.FieldType;
                if (typeof(Task).IsAssignableFrom(value) || value == typeof(CancellationTokenSource)
                    || value == typeof(Lock) || value.FullName!.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal))
                {
                    string name = current.FullName + "." + field.Name;
                    if (!KnownRuntimeFields.Contains(name)) throw new NotSupportedException("Unmapped runtime field: " + name);
                    RuntimeFields.Add(name);
                    continue;
                }
                result.Add(field);
            }
        return Layouts[type] = result.ToArray();
    }

    private void Write(Type type, object? value)
    {
        if (_words.Count >= 1_000_000) throw new NotSupportedException("Snapshot data budget exceeded.");
        if (!type.IsValueType) { _words.Add(checked((ulong)Reference(value))); return; }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            _words.Add(value == null ? 0u : 1u);
            if (value != null) Write(underlying, value);
            return;
        }
        if (type.IsEnum) { Write(Enum.GetUnderlyingType(type), value); return; }
        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean: _words.Add((bool)value! ? 1u : 0u); return;
            case TypeCode.Char: _words.Add((char)value!); return;
            case TypeCode.Byte:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64: _words.Add(Convert.ToUInt64(value)); return;
            case TypeCode.SByte:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64: _words.Add(unchecked((ulong)Convert.ToInt64(value))); return;
            case TypeCode.Single: _words.Add(BitConverter.SingleToUInt32Bits((float)value!)); return;
            case TypeCode.Double: _words.Add(BitConverter.DoubleToUInt64Bits((double)value!)); return;
        }
        if (type == typeof(nint) || type == typeof(nuint)) throw new NotSupportedException("Native pointers cannot be captured.");
        foreach (FieldInfo field in Layout(type)) Write(field.FieldType, value == null ? null : field.GetValue(value));
    }

    private object? Read(Type type, ref int offset)
    {
        if (!type.IsValueType) return _references[checked((int)_buffer[offset++])];
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return _buffer[offset++] == 0 ? null : Read(underlying, ref offset);
        if (type.IsEnum) return Enum.ToObject(type, Read(Enum.GetUnderlyingType(type), ref offset)!);
        TypeCode code = Type.GetTypeCode(type);
        if (code is >= TypeCode.Boolean and <= TypeCode.Double)
        {
            ulong word = _buffer[offset++];
            return code switch
            {
                TypeCode.Boolean => word != 0,
                TypeCode.Char => (char)word,
                TypeCode.Byte => (byte)word,
                TypeCode.UInt16 => (ushort)word,
                TypeCode.UInt32 => (uint)word,
                TypeCode.UInt64 => word,
                TypeCode.SByte => unchecked((sbyte)word),
                TypeCode.Int16 => unchecked((short)word),
                TypeCode.Int32 => unchecked((int)word),
                TypeCode.Int64 => unchecked((long)word),
                TypeCode.Single => BitConverter.UInt32BitsToSingle((uint)word),
                TypeCode.Double => BitConverter.UInt64BitsToDouble(word),
                _ => throw new InvalidOperationException("Unknown scalar.")
            };
        }
        object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        foreach (FieldInfo field in Layout(type)) field.SetValue(instance, Read(field.FieldType, ref offset));
        return instance;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _native.Dispose();
        _entries.Clear();
        _references.Clear();
        _pending.Clear();
        _ids.Clear();
        _words.Clear();
        _origins.Clear();
        _referenceTable = [];
    }

    private string Origin(object target)
    {
        var path = new List<string>();
        object? current = target;
        while (current != null && _origins.TryGetValue(current, out var origin))
        {
            path.Add(current.GetType().Name + ":" + origin.Member);
            current = origin.Parent;
        }
        path.Reverse();
        return string.Join("/", path);
    }

    private sealed record Entry(object Target, int Offset, int Length, Action<object, ulong[], int, object?[]>? Decoder);
}
