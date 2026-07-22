using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Turso;
using Turso.Data.Sqlite;
using Turso.Raw.Public;
using Turso.Raw.Public.Value;
using RawException = Turso.Raw.Public.TursoException;

namespace Turso.Data.Native;

internal interface INativeContext
{
    void Release();
}

internal sealed class RawSqliteNativeProviderFactory : SqliteNativeProviderFactory
{
    private static readonly TursoScalarFunctionCallback ScalarFunctionCallback = InvokeScalarFunction;
    private static readonly TursoAggregateInitCallback AggregateInitCallback = InitializeAggregate;
    private static readonly TursoAggregateStepCallback AggregateStepCallback = StepAggregate;
    private static readonly TursoAggregateFinalCallback AggregateFinalCallback = FinalizeAggregate;
    private static readonly TursoContextDestructorCallback ContextDestructorCallback = NoopContextDestructor;
    private static readonly TursoContextDestructorCallback AggregateDestructorCallback = DestroyAggregate;
    private static readonly TursoValueDestructorCallback ValueDestructorCallback = DestroyFunctionValue;
    private static readonly TursoCollationCallback CollationCallback = InvokeCollation;

    public override void RegisterScalarFunction(
        TursoNativeDatabase database,
        string name,
        int argc,
        bool isDeterministic,
        Func<object?[], object?> invoke)
    {
        var rawDatabase = GetDatabase(database);
        var context = GCHandle.Alloc(new RawScalarFunctionRegistration(invoke));
        try
        {
            Execute(() => TursoBindings.RegisterScalarFunction(
                rawDatabase.Handle,
                name,
                argc,
                isDeterministic,
                GCHandle.ToIntPtr(context),
                ScalarFunctionCallback,
                ContextDestructorCallback,
                ValueDestructorCallback));
            rawDatabase.AddNativeContext(context);
        }
        catch
        {
            context.Free();
            throw;
        }
    }

    public override void RegisterAggregateFunction(
        TursoNativeDatabase database,
        string name,
        int argc,
        bool isDeterministic,
        object? seed,
        Func<object?, object?[], object?> step,
        Func<object?, object?> resultSelector)
    {
        var rawDatabase = GetDatabase(database);
        var context = GCHandle.Alloc(new RawAggregateRegistration(seed, step, resultSelector));
        try
        {
            Execute(() => TursoBindings.RegisterAggregateFunction(
                rawDatabase.Handle,
                name,
                argc,
                isDeterministic,
                GCHandle.ToIntPtr(context),
                AggregateInitCallback,
                AggregateStepCallback,
                AggregateFinalCallback,
                ContextDestructorCallback,
                AggregateDestructorCallback,
                ValueDestructorCallback));
            rawDatabase.AddNativeContext(context);
        }
        catch
        {
            context.Free();
            throw;
        }
    }

    public override void UnregisterFunctions(TursoNativeDatabase database, string name)
        => Execute(() => TursoBindings.UnregisterFunction(GetDatabase(database).Handle, name));

    public override void RegisterCollation(
        TursoNativeDatabase database,
        string name,
        Func<string, string, int> compare)
    {
        var rawDatabase = GetDatabase(database);
        var context = GCHandle.Alloc(compare);
        try
        {
            Execute(() => TursoBindings.RegisterCollation(
                rawDatabase.Handle,
                name,
                GCHandle.ToIntPtr(context),
                CollationCallback,
                ContextDestructorCallback));
            rawDatabase.AddNativeContext(context);
        }
        catch
        {
            context.Free();
            throw;
        }
    }

    public override void UnregisterCollation(TursoNativeDatabase database, string name)
        => Execute(() => TursoBindings.UnregisterCollation(GetDatabase(database).Handle, name));

    public override void EnableExtensions(TursoNativeDatabase database, bool enable)
        => Execute(() => TursoBindings.EnableLoadExtension(GetDatabase(database).Handle, enable));

    public override void LoadExtension(TursoNativeDatabase database, string file)
        => Execute(() => TursoBindings.LoadExtension(GetDatabase(database).Handle, file));

    private static RawNativeDatabase GetDatabase(TursoNativeDatabase database)
        => database as RawNativeDatabase
           ?? throw new InvalidOperationException("The native database was not created by the Turso.Raw provider.");

    private static TursoExtensionValue InvokeScalarFunction(
        IntPtr context,
        int argc,
        IntPtr argv,
        IntPtr contextDestructor,
        IntPtr valueDestructor)
    {
        try
        {
            var registration = (RawScalarFunctionRegistration?)GCHandle.FromIntPtr(context).Target
                ?? throw new ObjectDisposedException(nameof(RawScalarFunctionRegistration));
            return CreateResult(registration.Invoke(ReadArguments(argc, argv)));
        }
        catch (SqliteException exception)
        {
            return CreateError(
                "__turso_sqlite_error__:"
                + exception.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)
                + ":"
                + exception.Message);
        }
        catch (Exception exception)
        {
            return CreateError(exception.Message);
        }
    }

    private static IntPtr InitializeAggregate(IntPtr context)
    {
        var registration = (RawAggregateRegistration?)GCHandle.FromIntPtr(context).Target
            ?? throw new ObjectDisposedException(nameof(RawAggregateRegistration));
        return registration.CreateInvocationHandle();
    }

    private static TursoExtensionValue StepAggregate(IntPtr context, IntPtr aggregateContext, int argc, IntPtr argv)
    {
        try
        {
            var invocation = (RawAggregateInvocation?)GCHandle.FromIntPtr(aggregateContext).Target
                ?? throw new ObjectDisposedException(nameof(RawAggregateInvocation));
            invocation.Step(ReadArguments(argc, argv));
            return CreateResult(null);
        }
        catch (SqliteException exception)
        {
            return CreateError(
                "__turso_sqlite_error__:"
                + exception.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)
                + ":"
                + exception.Message);
        }
        catch (Exception exception)
        {
            return CreateError(exception.Message);
        }
    }

    private static TursoExtensionValue FinalizeAggregate(IntPtr context, IntPtr aggregateContext)
    {
        try
        {
            var invocation = (RawAggregateInvocation?)GCHandle.FromIntPtr(aggregateContext).Target
                ?? throw new ObjectDisposedException(nameof(RawAggregateInvocation));
            return CreateResult(invocation.FinalizeResult());
        }
        catch (SqliteException exception)
        {
            return CreateError(
                "__turso_sqlite_error__:"
                + exception.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)
                + ":"
                + exception.Message);
        }
        catch (Exception exception)
        {
            return CreateError(exception.Message);
        }
    }

    private static void DestroyAggregate(IntPtr aggregateContext)
    {
        if (aggregateContext == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(aggregateContext);
        if (handle.Target is RawAggregateInvocation invocation)
            invocation.Registration.FreeInvocation(handle);
        else if (handle.IsAllocated)
            handle.Free();
    }

    private static int InvokeCollation(IntPtr context, IntPtr leftPtr, UIntPtr leftLen, IntPtr rightPtr, UIntPtr rightLen)
    {
        var compare = (Func<string, string, int>?)GCHandle.FromIntPtr(context).Target
            ?? throw new ObjectDisposedException("Native collation registration");
        return compare(
            ReadUtf8(leftPtr, checked((int)leftLen)),
            ReadUtf8(rightPtr, checked((int)rightLen)));
    }

    private static object?[] ReadArguments(int argc, IntPtr argv)
    {
        if (argc == 0)
            return [];

        var arguments = new object?[argc];
        var size = Marshal.SizeOf<TursoExtensionValue>();
        for (var index = 0; index < argc; index++)
        {
            var value = Marshal.PtrToStructure<TursoExtensionValue>(IntPtr.Add(argv, index * size));
            arguments[index] = value.ValueType switch
            {
                TursoExtensionValueType.Null => null,
                TursoExtensionValueType.Integer => value.Value.IntValue,
                TursoExtensionValueType.Float => value.Value.RealValue,
                TursoExtensionValueType.Text => ReadText(value.Value.TextValue),
                TursoExtensionValueType.Blob => ReadBlob(value.Value.BlobValue),
                _ => null,
            };
        }

        return arguments;
    }

    private static TursoExtensionValue CreateResult(object? value)
    {
        if (value is null or DBNull)
            return new TursoExtensionValue { ValueType = TursoExtensionValueType.Null };

        return value switch
        {
            bool boolValue => CreateInteger(boolValue ? 1 : 0),
            byte byteValue => CreateInteger(byteValue),
            sbyte sbyteValue => CreateInteger(sbyteValue),
            short shortValue => CreateInteger(shortValue),
            ushort ushortValue => CreateInteger(ushortValue),
            int intValue => CreateInteger(intValue),
            uint uintValue => CreateInteger(uintValue),
            long longValue => CreateInteger(longValue),
            float floatValue => CreateReal(floatValue),
            double doubleValue => CreateReal(doubleValue),
            decimal decimalValue => CreateText(decimalValue.ToString(CultureInfo.InvariantCulture)),
            byte[] bytes => CreateBlob(bytes),
            _ => CreateText(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };
    }

    private static TursoExtensionValue CreateInteger(long value)
        => new() { ValueType = TursoExtensionValueType.Integer, Value = new TursoExtensionValueUnion { IntValue = value } };

    private static TursoExtensionValue CreateReal(double value)
        => new() { ValueType = TursoExtensionValueType.Float, Value = new TursoExtensionValueUnion { RealValue = value } };

    private static TursoExtensionValue CreateText(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var text = new ExtensionTextValue { Subtype = 0, Text = AllocBytes(bytes), Length = checked((uint)bytes.Length) };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtensionTextValue>());
        Marshal.StructureToPtr(text, pointer, false);
        return new TursoExtensionValue { ValueType = TursoExtensionValueType.Text, Value = new TursoExtensionValueUnion { TextValue = pointer } };
    }

    private static TursoExtensionValue CreateBlob(byte[] bytes)
    {
        var blob = new ExtensionBlobValue { Data = AllocBytes(bytes), Length = (ulong)bytes.Length };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtensionBlobValue>());
        Marshal.StructureToPtr(blob, pointer, false);
        return new TursoExtensionValue { ValueType = TursoExtensionValueType.Blob, Value = new TursoExtensionValueUnion { BlobValue = pointer } };
    }

    private static TursoExtensionValue CreateError(string message)
    {
        var text = CreateText(message);
        var error = new ExtensionErrorValue { Code = 14, Message = text.Value.TextValue };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtensionErrorValue>());
        Marshal.StructureToPtr(error, pointer, false);
        return new TursoExtensionValue { ValueType = TursoExtensionValueType.Error, Value = new TursoExtensionValueUnion { ErrorValue = pointer } };
    }

    private static string ReadUtf8(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length == 0)
            return string.Empty;

        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string ReadText(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return string.Empty;

        var value = Marshal.PtrToStructure<ExtensionTextValue>(pointer);
        return value.Text == IntPtr.Zero || value.Length == 0
            ? string.Empty
            : ReadUtf8(value.Text, checked((int)value.Length));
    }

    private static byte[] ReadBlob(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return [];

        var value = Marshal.PtrToStructure<ExtensionBlobValue>(pointer);
        if (value.Data == IntPtr.Zero || value.Length == 0)
            return [];

        var bytes = new byte[checked((int)value.Length)];
        Marshal.Copy(value.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static IntPtr AllocBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return IntPtr.Zero;

        var data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return data;
    }

    private static void NoopContextDestructor(IntPtr context)
    {
    }

    private static void DestroyFunctionValue(IntPtr result)
    {
        if (result != IntPtr.Zero)
            FreeExtensionValue(Marshal.PtrToStructure<TursoExtensionValue>(result));
    }

    private static void FreeExtensionValue(TursoExtensionValue value)
    {
        switch (value.ValueType)
        {
            case TursoExtensionValueType.Text:
                FreeText(value.Value.TextValue);
                break;
            case TursoExtensionValueType.Blob:
                FreeBlob(value.Value.BlobValue);
                break;
            case TursoExtensionValueType.Error:
                FreeError(value.Value.ErrorValue);
                break;
        }
    }

    private static void FreeText(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return;

        var value = Marshal.PtrToStructure<ExtensionTextValue>(pointer);
        if (value.Text != IntPtr.Zero)
            Marshal.FreeHGlobal(value.Text);
        Marshal.FreeHGlobal(pointer);
    }

    private static void FreeBlob(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return;

        var value = Marshal.PtrToStructure<ExtensionBlobValue>(pointer);
        if (value.Data != IntPtr.Zero)
            Marshal.FreeHGlobal(value.Data);
        Marshal.FreeHGlobal(pointer);
    }

    private static void FreeError(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return;

        var value = Marshal.PtrToStructure<ExtensionErrorValue>(pointer);
        FreeText(value.Message);
        Marshal.FreeHGlobal(pointer);
    }

    private static void Execute(Action operation)
    {
        try
        {
            operation();
        }
        catch (RawException exception)
        {
            throw new global::Turso.TursoException(exception.Message);
        }
    }

    private sealed class RawScalarFunctionRegistration(Func<object?[], object?> invoke)
    {
        public object? Invoke(object?[] arguments) => invoke(arguments);
    }

    private sealed class RawAggregateRegistration(
        object? seed,
        Func<object?, object?[], object?> step,
        Func<object?, object?> resultSelector) : INativeContext
    {
        private readonly List<GCHandle> _invocations = [];

        public IntPtr CreateInvocationHandle()
        {
            var handle = GCHandle.Alloc(new RawAggregateInvocation(this, seed, step, resultSelector));
            lock (_invocations)
            {
                _invocations.Add(handle);
            }

            return GCHandle.ToIntPtr(handle);
        }

        public void FreeInvocation(GCHandle handle)
        {
            lock (_invocations)
            {
                _invocations.Remove(handle);
            }

            if (handle.IsAllocated)
                handle.Free();
        }

        public void Release()
        {
            lock (_invocations)
            {
                foreach (var handle in _invocations)
                {
                    if (handle.IsAllocated)
                        handle.Free();
                }

                _invocations.Clear();
            }
        }
    }

    private sealed class RawAggregateInvocation(
        RawAggregateRegistration registration,
        object? seed,
        Func<object?, object?[], object?> step,
        Func<object?, object?> resultSelector)
    {
        private object? _accumulator = seed;

        public RawAggregateRegistration Registration { get; } = registration;

        public void Step(object?[] arguments)
        {
            _accumulator = step(_accumulator, arguments);
        }

        public object? FinalizeResult() => resultSelector(_accumulator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtensionTextValue
    {
        public int Subtype;
        public IntPtr Text;
        public uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtensionBlobValue
    {
        public IntPtr Data;
        public ulong Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtensionErrorValue
    {
        public int Code;
        public IntPtr Message;
    }
}
