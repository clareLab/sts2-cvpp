using System.Reflection;
using System.Reflection.Emit;

namespace cvpp;

internal static class SnapshotEncoder
{
    private static readonly MethodInfo Word = typeof(SnapshotGraph).GetMethod(nameof(SnapshotGraph.Word), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Reference = typeof(SnapshotGraph).GetMethod(nameof(SnapshotGraph.WriteReference), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Member = typeof(SnapshotGraph).GetMethod(nameof(SnapshotGraph.Member), BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static Action<SnapshotGraph, object> Compile(Type type)
    {
        var method = new DynamicMethod("CaptureSnapshot", typeof(void), [typeof(SnapshotGraph), typeof(object)], typeof(SnapshotEncoder).Module, true);
        ILGenerator il = method.GetILGenerator();
        if (type.IsArray)
        {
            if (!type.IsSZArray) throw new NotSupportedException("Compiled snapshot arrays require zero-based vectors.");
            Type element = type.GetElementType()!;
            LocalBuilder index = il.DeclareLocal(typeof(int));
            Label body = il.DefineLabel();
            Label test = il.DefineLabel();
            il.Emit(OpCodes.Br, test);
            il.MarkLabel(body);
            Value(il, element, () =>
            {
                Target(il, type);
                il.Emit(OpCodes.Ldloc, index);
                il.Emit(OpCodes.Ldelem, element);
            });
            il.Emit(OpCodes.Ldloc, index);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, index);
            il.MarkLabel(test);
            il.Emit(OpCodes.Ldloc, index);
            Target(il, type);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Blt, body);
        }
        else
            foreach (FieldInfo field in SnapshotGraph.Layout(type))
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldstr, field.Name);
                il.Emit(OpCodes.Call, Member);
                Value(il, field.FieldType, () => { Target(il, type); il.Emit(OpCodes.Ldfld, field); });
            }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<SnapshotGraph, object>>();
    }

    private static void Target(ILGenerator il, Type type)
    {
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(type.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, type);
    }

    private static void Value(ILGenerator il, Type type, Action load)
    {
        if (!type.IsValueType)
        {
            il.Emit(OpCodes.Ldarg_0);
            load();
            il.Emit(OpCodes.Call, Reference);
            return;
        }
        if (type.IsEnum) { Value(il, Enum.GetUnderlyingType(type), load); return; }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            LocalBuilder nullable = il.DeclareLocal(type);
            Label done = il.DefineLabel();
            load();
            il.Emit(OpCodes.Stloc, nullable);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloca, nullable);
            il.Emit(OpCodes.Call, type.GetProperty("HasValue")!.GetMethod!);
            il.Emit(OpCodes.Conv_U8);
            il.Emit(OpCodes.Call, Word);
            il.Emit(OpCodes.Ldloca, nullable);
            il.Emit(OpCodes.Call, type.GetProperty("HasValue")!.GetMethod!);
            il.Emit(OpCodes.Brfalse, done);
            Value(il, underlying, () => { il.Emit(OpCodes.Ldloca, nullable); il.Emit(OpCodes.Call, type.GetProperty("Value")!.GetMethod!); });
            il.MarkLabel(done);
            return;
        }
        TypeCode code = Type.GetTypeCode(type);
        if (code is >= TypeCode.Boolean and <= TypeCode.Double)
        {
            il.Emit(OpCodes.Ldarg_0);
            load();
            switch (code)
            {
                case TypeCode.Single:
                    il.Emit(OpCodes.Call, typeof(BitConverter).GetMethod(nameof(BitConverter.SingleToUInt32Bits))!);
                    il.Emit(OpCodes.Conv_U8);
                    break;
                case TypeCode.Double:
                    il.Emit(OpCodes.Call, typeof(BitConverter).GetMethod(nameof(BitConverter.DoubleToUInt64Bits))!);
                    break;
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64: il.Emit(OpCodes.Conv_I8); break;
                default: il.Emit(OpCodes.Conv_U8); break;
            }
            il.Emit(OpCodes.Call, Word);
            return;
        }
        if (type == typeof(nint) || type == typeof(nuint)) throw new NotSupportedException("Native pointers cannot be captured.");
        LocalBuilder value = il.DeclareLocal(type);
        load();
        il.Emit(OpCodes.Stloc, value);
        foreach (FieldInfo field in SnapshotGraph.Layout(type))
            Value(il, field.FieldType, () => { il.Emit(OpCodes.Ldloca, value); il.Emit(OpCodes.Ldfld, field); });
    }
}
