using System.Reflection;
using System.Reflection.Emit;

namespace cvpp;

internal static class SnapshotDecoder
{
    internal static Action<object, ulong[], int, object?[]> Compile(Type type)
    {
        var method = new DynamicMethod("RestoreSnapshot", typeof(void),
            [typeof(object), typeof(ulong[]), typeof(int), typeof(object[])], typeof(SnapshotDecoder).Module, true);
        ILGenerator il = method.GetILGenerator();
        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1) throw new NotSupportedException("Compiled snapshot arrays require rank one.");
            Type element = type.GetElementType()!;
            LocalBuilder index = il.DeclareLocal(typeof(int));
            Label body = il.DefineLabel();
            Label test = il.DefineLabel();
            il.Emit(OpCodes.Br, test);
            il.MarkLabel(body);
            Target(il, type);
            il.Emit(OpCodes.Ldloc, index);
            Value(il, element);
            il.Emit(OpCodes.Stelem, element);
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
                Target(il, type);
                Value(il, field.FieldType);
                il.Emit(OpCodes.Stfld, field);
            }
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Action<object, ulong[], int, object?[]>>();
    }

    private static void Target(ILGenerator il, Type type)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(type.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, type);
    }

    private static void Word(ILGenerator il)
    {
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldelem_I8);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Starg_S, (byte)2);
    }

    private static void Value(ILGenerator il, Type type)
    {
        if (!type.IsValueType)
        {
            il.Emit(OpCodes.Ldarg_3);
            Word(il);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Castclass, type);
            return;
        }
        if (type.IsEnum) { Value(il, Enum.GetUnderlyingType(type)); return; }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            LocalBuilder nullable = il.DeclareLocal(type);
            Label empty = il.DefineLabel();
            Label done = il.DefineLabel();
            Word(il);
            il.Emit(OpCodes.Brfalse, empty);
            Value(il, underlying);
            il.Emit(OpCodes.Newobj, type.GetConstructor([underlying])!);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(empty);
            il.Emit(OpCodes.Ldloca, nullable);
            il.Emit(OpCodes.Initobj, type);
            il.Emit(OpCodes.Ldloc, nullable);
            il.MarkLabel(done);
            return;
        }
        TypeCode code = Type.GetTypeCode(type);
        if (code is >= TypeCode.Boolean and <= TypeCode.Double)
        {
            Word(il);
            switch (code)
            {
                case TypeCode.Boolean:
                case TypeCode.Byte: il.Emit(OpCodes.Conv_U1); break;
                case TypeCode.SByte: il.Emit(OpCodes.Conv_I1); break;
                case TypeCode.Char:
                case TypeCode.UInt16: il.Emit(OpCodes.Conv_U2); break;
                case TypeCode.Int16: il.Emit(OpCodes.Conv_I2); break;
                case TypeCode.UInt32: il.Emit(OpCodes.Conv_U4); break;
                case TypeCode.Int32: il.Emit(OpCodes.Conv_I4); break;
                case TypeCode.Single:
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Call, typeof(BitConverter).GetMethod(nameof(BitConverter.UInt32BitsToSingle))!);
                    break;
                case TypeCode.Double:
                    il.Emit(OpCodes.Call, typeof(BitConverter).GetMethod(nameof(BitConverter.UInt64BitsToDouble))!);
                    break;
            }
            return;
        }
        LocalBuilder value = il.DeclareLocal(type);
        foreach (FieldInfo field in SnapshotGraph.Layout(type))
        {
            il.Emit(OpCodes.Ldloca, value);
            Value(il, field.FieldType);
            il.Emit(OpCodes.Stfld, field);
        }
        il.Emit(OpCodes.Ldloc, value);
    }
}
