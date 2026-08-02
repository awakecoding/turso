using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Ahtola;
using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

public class SqliteDataReader : DbDataReader, IConnectionOwnedReader
{
    private readonly SqliteCommand _command;
    private readonly SqliteConnection _connection;
    private SqliteStatementAdapter? _statement;
    private string _currentSql = string.Empty;
    private readonly List<string> _remainingSql = new();
    private readonly CommandBehavior _behavior;
    private readonly Action _closeCallback;
    private int _recordsAffected;
    private bool _isClosed;
    private bool _hasCurrentRow;
    private bool _hasPrefetchedRow;
    private bool _hadResultSet;
    private bool _hadRecordsAffectedStatement;
    private bool _currentStatementRowsAffectedCounted;

    private enum ReaderValueKind
    {
        Empty,
        Null,
        Integer,
        Real,
        Text,
        Blob,
    }

    private readonly struct ReaderValue
    {
        private readonly AhtolaValue _nativeValue;
        private readonly ManagedResultValue _managedValue;
        private readonly bool _isManaged;

        private ReaderValue(AhtolaValue nativeValue)
        {
            _nativeValue = nativeValue;
        }

        private ReaderValue(ManagedResultValue managedValue)
        {
            _managedValue = managedValue;
            _isManaged = true;
        }

        public ReaderValueKind Kind => _isManaged
            ? _managedValue.Kind switch
            {
                ManagedResultValueKind.Null => ReaderValueKind.Null,
                ManagedResultValueKind.Integer => ReaderValueKind.Integer,
                ManagedResultValueKind.Real => ReaderValueKind.Real,
                ManagedResultValueKind.Text => ReaderValueKind.Text,
                ManagedResultValueKind.Blob => ReaderValueKind.Blob,
                _ => throw new InvalidOperationException($"Unknown managed result value kind {_managedValue.Kind}."),
            }
            : _nativeValue.ValueType switch
            {
                AhtolaValueType.Empty => ReaderValueKind.Empty,
                AhtolaValueType.Null => ReaderValueKind.Null,
                AhtolaValueType.Integer => ReaderValueKind.Integer,
                AhtolaValueType.Real => ReaderValueKind.Real,
                AhtolaValueType.Text => ReaderValueKind.Text,
                AhtolaValueType.Blob => ReaderValueKind.Blob,
                _ => throw new InvalidOperationException($"Unknown native result value type {_nativeValue.ValueType}."),
            };

        public long Integer => _isManaged ? _managedValue.AsInteger() : _nativeValue.IntValue;

        public double Real => _isManaged ? _managedValue.AsReal() : _nativeValue.RealValue;

        public string Text => _isManaged ? _managedValue.AsText() : _nativeValue.StringValue;

        public byte[] Blob => _isManaged ? _managedValue.AsBlob().ToArray() : _nativeValue.BlobValue;

        public static ReaderValue FromNative(AhtolaValue value) => new(value);

        public static ReaderValue FromManaged(ManagedResultValue value) => new(value);
    }

    internal SqliteDataReader(
        SqliteCommand command,
        SqliteStatementAdapter statement,
        string currentSql,
        List<string> remainingSql,
        int recordsAffected,
        CommandBehavior behavior,
        Action closeCallback)
        : this(
            command,
            statement,
            currentSql,
            remainingSql,
            recordsAffected,
            hadRecordsAffectedStatement: false,
            behavior,
            closeCallback)
    {
    }

    internal SqliteDataReader(
        SqliteCommand command,
        SqliteStatementAdapter statement,
        string currentSql,
        List<string> remainingSql,
        int recordsAffected,
        bool hadRecordsAffectedStatement,
        CommandBehavior behavior,
        Action closeCallback)
    {
        _command = command;
        _connection = command.Connection
            ?? throw new InvalidOperationException("A data reader requires an associated connection.");
        _statement = statement;
        _currentSql = currentSql;
        _hadResultSet = true;
        _remainingSql = remainingSql;
        _recordsAffected = recordsAffected;
        _hadRecordsAffectedStatement = hadRecordsAffectedStatement;
        _behavior = behavior;
        _closeCallback = closeCallback;
        ((ILocalReaderConnection)_connection).ReaderOpened(this);
    }

    internal SqliteDataReader(SqliteCommand command, int recordsAffected, CommandBehavior behavior, Action closeCallback)
    {
        _command = command;
        _connection = command.Connection
            ?? throw new InvalidOperationException("A data reader requires an associated connection.");
        _recordsAffected = recordsAffected;
        _behavior = behavior;
        _closeCallback = closeCallback;
        ((ILocalReaderConnection)_connection).ReaderOpened(this);
    }

    public override int Depth => 0;

    public override int FieldCount
    {
        get
        {
            EnsureOpen();
            if (_statement is null)
                return 0;

            var statement = GetStatement();
            return statement.UsesManagedResults
                ? statement.ManagedResultMetadata.ColumnCount
                : statement.ColumnCount;
        }
    }

    public override bool HasRows
    {
        get
        {
            EnsureOpen();
            if (_statement is null)
                return false;

            return _command.Connection?.UsesManagedDatabase == true
                ? GetStatement().HasRows()
                : !Regex.IsMatch(_currentSql, @"\bWHERE\b\s+0\s*=\s*1\b", RegexOptions.IgnoreCase);
        }
    }

    public override bool IsClosed => _isClosed
        || _command.Connection?.State != ConnectionState.Open
        || (_statement?.IsInvalid ?? false);

    public override int RecordsAffected
    {
        get
        {
            if (_recordsAffected > 0 || _hadRecordsAffectedStatement)
                return _recordsAffected;

            return _statement is null ? _hadResultSet ? -1 : _recordsAffected : -1;
        }
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool GetBoolean(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        if (value.Kind == ReaderValueKind.Text && bool.TryParse(value.Text, out var boolValue))
            return boolValue;

        return GetInt64(ordinal) != 0;
    }

    public override byte GetByte(int ordinal)
    {
        EnsureOpen();
        return (byte)GetInt64(ordinal);
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        EnsureOpen();
        return CopyValue(GetBlobValue(ordinal), dataOffset, buffer, bufferOffset, length);
    }

    public override char GetChar(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        if (value.Kind == ReaderValueKind.Text && value.Text.Length == 1)
            return value.Text[0];

        // Microsoft.Data.Sqlite parity: anything else coerces through the INTEGER column
        // path — TEXT parses a CAST-style digit prefix ('' or 'abc' -> 0), REAL truncates —
        // and the checked cast surfaces out-of-range values like MDS does.
        var coerced = value.Kind switch
        {
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Real => (long)value.Real,
            ReaderValueKind.Text => ParseTextAsIntegerPrefix(value.Text),
            ReaderValueKind.Null => throw new InvalidOperationException(
                Properties.Resources.CalledOnNullValue(ordinal)),
            _ => GetInt64(ordinal),
        };

        return checked((char)coerced);
    }

    // CAST-style text-to-integer conversion matching the native column-int64 API:
    // skips leading ASCII whitespace, accepts an optional sign, consumes a digit
    // prefix, yields 0 when no digits are present, and clamps on overflow.
    private static long ParseTextAsIntegerPrefix(string text)
    {
        var index = 0;
        while (index < text.Length && text[index] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
        {
            index++;
        }

        var negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            index++;
        }

        ulong magnitude = 0;
        var sawDigit = false;
        var overflow = false;
        while (index < text.Length && text[index] >= '0' && text[index] <= '9')
        {
            sawDigit = true;
            if (!overflow)
            {
                var digit = (ulong)(text[index] - '0');
                if (magnitude > (ulong.MaxValue - digit) / 10)
                {
                    overflow = true;
                }
                else
                {
                    magnitude = magnitude * 10 + digit;
                }
            }

            index++;
        }

        if (!sawDigit)
        {
            return 0;
        }

        if (negative)
        {
            return overflow || magnitude > (ulong)long.MaxValue + 1 ? long.MinValue : -(long)magnitude;
        }

        return overflow || magnitude > (ulong)long.MaxValue ? long.MaxValue : (long)magnitude;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        EnsureOpen();
        return CopyValue(GetTextValue(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);
    }

    public override string GetDataTypeName(int ordinal)
    {
        EnsureOpen();
        ValidateOrdinal(ordinal);
        var declaredType = GetDeclaredTypeName(ordinal);
        if (!string.IsNullOrEmpty(declaredType))
            return declaredType;

        return CurrentValueKind(ordinal) switch
        {
            ReaderValueKind.Null => "BLOB",
            ReaderValueKind.Integer => "INTEGER",
            ReaderValueKind.Real => "REAL",
            ReaderValueKind.Text => "TEXT",
            ReaderValueKind.Blob => "BLOB",
            ReaderValueKind.Empty => InferDataTypeName(GetName(ordinal)),
            _ => throw new InvalidEnumArgumentException()
        };
    }

    public override DateTime GetDateTime(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Text => ParseDateTime(value.Text),
            ReaderValueKind.Real => DateTime.FromOADate(value.Real - 2415018.5),
            ReaderValueKind.Integer => DateTime.FromOADate(value.Integer - 2415018.5),
            _ => DateTime.Parse(GetString(ordinal), CultureInfo.InvariantCulture)
        };
    }

    public virtual DateTimeOffset GetDateTimeOffset(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Text => ParseDateTimeOffset(value.Text),
            ReaderValueKind.Real => new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(value.Real - 2415018.5), DateTimeKind.Unspecified), TimeSpan.Zero),
            ReaderValueKind.Integer => new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(value.Integer - 2415018.5), DateTimeKind.Unspecified), TimeSpan.Zero),
            _ => ParseDateTimeOffset(GetString(ordinal))
        };
    }

    public override decimal GetDecimal(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Text => decimal.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture),
            // Format through "G15" instead of Convert.ToDecimal(double): .NET 11 changed
            // double-to-decimal conversion to keep the exact binary expansion, but SQLite's
            // own REAL-to-decimal path (and Microsoft.Data.Sqlite, which parses the 15-digit
            // text form) rounds to 15 significant digits.
            ReaderValueKind.Real => decimal.Parse(value.Real.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
            ReaderValueKind.Integer => value.Integer,
            _ => Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture)
        };
    }

    public override double GetDouble(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Text => double.Parse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => value.Real
        };
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal)
    {
        EnsureOpen();
        ValidateOrdinal(ordinal);
        var valueType = CurrentValueKind(ordinal);
        var declaredType = GetDeclaredTypeName(ordinal);
        if (!string.IsNullOrEmpty(declaredType))
            return GetClrTypeFromSqliteType(declaredType, valueType);

        return GetClrTypeFromSqliteType(GetDataTypeName(ordinal), valueType);
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        EnsureOpen();
        var value = GetValue(ordinal);
        if (value == DBNull.Value)
        {
            // Microsoft.Data.Sqlite surfaces DBNull.Value for object/DBNull reads so callers
            // (e.g. EF Core's detailed read-error path) can distinguish NULL from cast errors.
            if (typeof(T) == typeof(DBNull) || typeof(T) == typeof(object))
                return (T)value;

            throw new InvalidOperationException(Properties.Resources.CalledOnNullValue(ordinal));
        }

        if (typeof(T) == typeof(DBNull))
            throw new InvalidCastException();

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(DateOnly))
            return (T)(object)DateOnly.FromDateTime(GetDateTime(ordinal));

        if (targetType == typeof(TimeOnly))
            return (T)(object)TimeOnly.FromTimeSpan(GetTimeSpan(ordinal));

        if (targetType == typeof(DateTime))
            return (T)(object)GetDateTime(ordinal);

        if (targetType == typeof(DateTimeOffset))
            return (T)(object)GetDateTimeOffset(ordinal);

        if (targetType == typeof(TimeSpan))
            return (T)(object)GetTimeSpan(ordinal);

        if (targetType == typeof(decimal))
            return (T)(object)GetDecimal(ordinal);

        if (targetType == typeof(Guid))
            return (T)(object)GetGuid(ordinal);

        if (targetType == typeof(Stream))
            return (T)(object)GetStream(ordinal);

        if (targetType == typeof(TextReader))
            return (T)(object)GetTextReader(ordinal);

        if (targetType.IsEnum)
            return (T)Enum.ToObject(targetType, Convert.ChangeType(value, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture));

        if (targetType != typeof(T) && value.GetType() == targetType)
            return (T)value;

        // Microsoft.Data.Sqlite reads unsigned types with unchecked casts so two's-complement
        // values written through the matching unchecked parameter binds round-trip.
        if (targetType == typeof(ulong))
            return (T)(object)unchecked((ulong)GetInt64(ordinal));

        if (targetType == typeof(uint))
            return (T)(object)unchecked((uint)GetInt64(ordinal));

        if (targetType == typeof(ushort))
            return (T)(object)unchecked((ushort)GetInt64(ordinal));

        if (targetType == typeof(byte))
            return (T)(object)unchecked((byte)GetInt64(ordinal));

        if (targetType == typeof(sbyte))
            return (T)(object)unchecked((sbyte)GetInt64(ordinal));

        // Microsoft.Data.Sqlite routes char reads (including char?) through GetChar,
        // whose TEXT coercion Convert.ChangeType cannot reproduce (e.g. '' -> '\0').
        if (targetType == typeof(char))
            return (T)(object)GetChar(ordinal);

        return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    public override float GetFloat(int ordinal)
    {
        EnsureOpen();
        return (float)GetDouble(ordinal);
    }

    public override Guid GetGuid(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return ToGuid(value);
    }

    public override short GetInt16(int ordinal)
    {
        EnsureOpen();
        return (short)GetInt64(ordinal);
    }

    public override int GetInt32(int ordinal)
    {
        EnsureOpen();
        return (int)GetInt64(ordinal);
    }

    public override long GetInt64(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Real => (long)value.Real,
            ReaderValueKind.Text => long.Parse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture)
        };
    }

    public override string GetName(int ordinal)
    {
        EnsureOpen();
        var statement = GetStatement();
        ValidateOrdinal(ordinal);
        return statement.UsesManagedResults
            ? statement.ManagedResultMetadata.GetColumn(ordinal).Name
            : statement.GetName(ordinal);
    }

    public override Stream GetStream(int ordinal)
    {
        EnsureOpen();
        return new MemoryStream(GetBlobValue(ordinal).ToArray(), writable: false);
    }

    public override TextReader GetTextReader(int ordinal)
    {
        EnsureOpen();
        return new StringReader(GetTextValue(ordinal));
    }

    public override int GetOrdinal(string name)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(name);
        _ = GetStatement();
        for (var i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.Ordinal))
                return i;
        }

        string? match = null;
        var matchOrdinal = -1;
        for (var i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                if (match is not null)
                    throw new InvalidOperationException(Properties.Resources.AmbiguousColumnName(name, match, GetName(i)));

                match = GetName(i);
                matchOrdinal = i;
            }
        }

        if (match is not null)
            return matchOrdinal;

        throw new ArgumentOutOfRangeException(nameof(name), name, $"Column {name} was not found.");
    }

    public override DataTable GetSchemaTable()
    {
        EnsureOpen();
        var schema = new DataTable("SchemaTable");
        schema.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        schema.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        schema.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        schema.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        schema.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        schema.Columns.Add("BaseServerName", typeof(string));
        schema.Columns.Add("BaseCatalogName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        schema.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        schema.Columns.Add("DataTypeName", typeof(string));
        schema.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
        schema.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        schema.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));

        var tableName = TryGetSelectSource(out var parsedTableName, out var selections) ? parsedTableName : null;
        var tableColumns = tableName is null ? new Dictionary<string, SchemaColumnInfo>(StringComparer.OrdinalIgnoreCase) : GetTableColumns(tableName);

        for (var i = 0; i < FieldCount; i++)
        {
            var columnName = GetName(i);
            var selection = i < selections.Count ? selections[i] : columnName;
            var baseColumnName = ResolveBaseColumnName(selection, columnName, tableColumns);
            SchemaColumnInfo? columnInfo = null;
            var hasBaseColumn = baseColumnName is not null && tableName is not null && tableColumns.TryGetValue(baseColumnName, out columnInfo);
            var info = hasBaseColumn
                ? columnInfo ?? throw new InvalidOperationException(Properties.Resources.NoData)
                : null;
            var valueType = _hasCurrentRow ? ReadValue(i).Kind : ReaderValueKind.Empty;
            if (valueType == ReaderValueKind.Empty && !string.IsNullOrEmpty(info?.TypeName))
                valueType = GetValueKindFromTypeName(info.TypeName);
            else if (valueType is ReaderValueKind.Empty or ReaderValueKind.Null)
                valueType = GetSampleValueType(i);
            var dataTypeName = info is not null
                ? StripTypeLength(info.TypeName)
                : GetDataTypeNameFromValueType(valueType, selection);
            var dataType = info is not null
                ? GetClrTypeFromSqliteType(info.TypeName, valueType)
                : GetClrTypeFromValueType(valueType);
            var isExpression = info is null;
            var isAliased = info is null
                || !string.Equals(info.Name, columnName, StringComparison.Ordinal);
            var row = schema.NewRow();
            row[SchemaTableColumn.ColumnName] = columnName;
            row[SchemaTableColumn.ColumnOrdinal] = i;
            row[SchemaTableColumn.ColumnSize] = -1;
            row[SchemaTableColumn.NumericPrecision] = DBNull.Value;
            row[SchemaTableColumn.NumericScale] = DBNull.Value;
            row[SchemaTableColumn.IsUnique] = info is not null ? !isAliased && info.IsUnique : DBNull.Value;
            row[SchemaTableColumn.IsKey] = info is not null ? info.IsKey : DBNull.Value;
            row["BaseServerName"] = "";
            row["BaseCatalogName"] = info is not null ? "main" : DBNull.Value;
            row[SchemaTableColumn.BaseColumnName] = info is not null ? info.Name : DBNull.Value;
            row[SchemaTableColumn.BaseSchemaName] = DBNull.Value;
            row[SchemaTableColumn.BaseTableName] = info is not null ? tableName : DBNull.Value;
            row[SchemaTableColumn.DataType] = dataType;
            row["DataTypeName"] = dataTypeName;
            row[SchemaTableColumn.AllowDBNull] = info is not null ? isAliased || info.AllowNull : DBNull.Value;
            row[SchemaTableColumn.IsAliased] = isAliased;
            row[SchemaTableColumn.IsExpression] = isExpression;
            row[SchemaTableOptionalColumn.IsAutoIncrement] = hasBaseColumn ? false : DBNull.Value;
            row[SchemaTableColumn.IsLong] = DBNull.Value;
            row[SchemaTableColumn.ProviderType] = (int)GetSqliteType(valueType);
            schema.Rows.Add(row);
        }

        return schema;
    }

    public override string GetString(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Text => value.Text,
            ReaderValueKind.Integer => value.Integer.ToString(CultureInfo.InvariantCulture),
            ReaderValueKind.Real => value.Real.ToString(CultureInfo.InvariantCulture),
            ReaderValueKind.Blob => Encoding.UTF8.GetString(value.Blob),
            _ => Convert.ToString(GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    public virtual TimeSpan GetTimeSpan(int ordinal)
    {
        EnsureOpen();
        var value = GetTypedValue(ordinal);
        return value.Kind switch
        {
            ReaderValueKind.Real => TimeSpan.FromDays(value.Real),
            ReaderValueKind.Integer => TimeSpan.FromDays(value.Integer),
            _ => TimeSpan.Parse(GetString(ordinal), CultureInfo.InvariantCulture)
        };
    }

    public override object GetValue(int ordinal)
    {
        EnsureOpen();
        EnsureHasCurrentRow();
        var value = ReadValue(ordinal);
        if (IsGuidType(GetDeclaredTypeName(ordinal)) && value.Kind is ReaderValueKind.Blob or ReaderValueKind.Text)
            return ToGuid(value);

        return value.Kind switch
        {
            ReaderValueKind.Null or ReaderValueKind.Empty => DBNull.Value,
            ReaderValueKind.Integer => value.Integer,
            ReaderValueKind.Real => value.Real,
            ReaderValueKind.Text => value.Text,
            ReaderValueKind.Blob => value.Blob,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public override int GetValues(object[] values)
    {
        EnsureOpen();
        _ = GetStatement();
        EnsureHasCurrentRow();
        ArgumentNullException.ThrowIfNull(values);

        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = GetValue(i);

        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        EnsureOpen();
        EnsureHasCurrentRow();
        var valueType = ReadValue(ordinal).Kind;
        return valueType is ReaderValueKind.Null or ReaderValueKind.Empty;
    }

    public override bool NextResult()
        => _command.RunOperation(NextResultCore);

    private bool NextResultCore(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_statement is null)
            return false;
        while (GetStatement().Read(cancellationToken))
        {
        }

        CountCurrentStatementRowsAffected();

        _hasCurrentRow = false;
        _hasPrefetchedRow = false;
        _statement.Dispose();
        _statement = null;
        _currentStatementRowsAffectedCounted = false;

        try
        {
            while (_remainingSql.Count > 0)
            {
                var sql = _remainingSql[0];
                _remainingSql.RemoveAt(0);
                if (_command.TryHandleFacadeStatement(sql, out var rewrittenSql))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                var statement = _command.PrepareSingleStatement(rewrittenSql);
                if (cancellationToken.IsCancellationRequested)
                {
                    statement.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (statement.ColumnCount > 0)
                {
                    _statement = statement;
                    _currentSql = rewrittenSql;
                    _hadResultSet = true;
                    _currentStatementRowsAffectedCounted = false;
                    _hasPrefetchedRow = statement.Read(cancellationToken);
                    return true;
                }

                while (statement.Read(cancellationToken))
                {
                }

                if (SqliteCommand.CountsRowsAffected(sql))
                {
                    _hadRecordsAffectedStatement = true;
                    _recordsAffected += statement.RowsAffected;
                }
                _command.MarkTransactionCompletedExternally(sql);
                statement.Dispose();
            }
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            _statement?.Dispose();
            _statement = null;
            _hasPrefetchedRow = false;
            _remainingSql.Clear();
            throw SqliteCommand.ToSqliteException(ex);
        }

        return false;
    }

    public override bool Read()
        => _command.RunOperation(ReadCore);

    private bool ReadCore(CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (_statement is null)
            return false;
        if (_hasPrefetchedRow)
        {
            _hasPrefetchedRow = false;
            _hasCurrentRow = true;
            return true;
        }

        try
        {
            _hasCurrentRow = GetStatement().Read(cancellationToken);
            if (!_hasCurrentRow)
                CountCurrentStatementRowsAffected();
            return _hasCurrentRow;
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            throw SqliteCommand.ToSqliteException(ex);
        }
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => _command.RunOperationAsync(ReadCore, cancellationToken);

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => _command.RunOperationAsync(NextResultCore, cancellationToken);

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => CompleteAsync(() => IsDBNull(ordinal), cancellationToken);

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
        => CompleteAsync(() => GetFieldValue<T>(ordinal), cancellationToken);

    private static Task<T> CompleteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Complete(operation);
    }

    private static Task<T> Complete<T>(Func<T> operation)
    {
        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseCore(throwOnError: false);

        base.Dispose(disposing);
    }

    public override void Close() => CloseCore();

    void IConnectionOwnedReader.CloseFromConnection()
    {
        if (_isClosed)
            return;

        _statement?.Dispose();
        _statement = null;
        _remainingSql.Clear();
        _hasPrefetchedRow = false;
        _hasCurrentRow = false;
        FinishClose(closeConnection: false);
    }

    private void CloseCore(bool throwOnError = true)
    {
        if (_isClosed)
            return;

        try
        {
            if (_statement is not null)
            {
                while (GetStatement().Read())
                {
                }

                CountCurrentStatementRowsAffected();

                _statement.Dispose();
                _statement = null;
                _hasPrefetchedRow = false;
                _currentStatementRowsAffectedCounted = false;
            }

            DrainRemainingStatements();
        }
        catch (Exception ex) when (ex is AhtolaException or EmbeddedSqlException)
        {
            _statement?.Dispose();
            _statement = null;
            _remainingSql.Clear();
            FinishClose();
            if (throwOnError)
                throw SqliteCommand.ToSqliteException(ex);
            return;
        }
        catch (SqliteException)
        {
            _statement?.Dispose();
            _statement = null;
            _remainingSql.Clear();
            FinishClose();
            if (throwOnError)
                throw;
            return;
        }

        FinishClose();
    }

    private void FinishClose(bool closeConnection = true)
    {
        if (_isClosed)
            return;

        _closeCallback();
        ((ILocalReaderConnection)_connection).ReaderClosed(this);
        if (closeConnection && (_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
            _connection.Close();

        _isClosed = true;
    }

    private void EnsureOpen([CallerMemberName] string operation = "")
    {
        if (IsClosed)
            throw new InvalidOperationException(Properties.Resources.DataReaderClosed(NormalizeOperationName(operation)));
    }

    private static string NormalizeOperationName(string operation)
        => operation.StartsWith("get_", StringComparison.Ordinal)
            ? operation[4..]
            : operation;

    private SqliteStatementAdapter GetStatement()
    {
        if (_statement is null)
            throw new InvalidOperationException(Properties.Resources.NoData);

        return _statement;
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, null);
    }

    private void EnsureHasCurrentRow()
    {
        if (!_hasCurrentRow)
            throw new InvalidOperationException(Properties.Resources.NoData);
    }

    private void CountCurrentStatementRowsAffected()
    {
        if (_statement is not null
            && !_currentStatementRowsAffectedCounted
            && SqliteCommand.CountsRowsAffected(_currentSql))
        {
            _hadRecordsAffectedStatement = true;
            _recordsAffected += GetStatement().RowsAffected;
            _currentStatementRowsAffectedCounted = true;
        }
    }

    private string GetDeclaredTypeName(int ordinal)
    {
        if (TryGetSelectSource(out var tableName, out var selections))
        {
            var tableColumns = GetTableColumns(tableName);
            var columnName = GetName(ordinal);
            var selection = ordinal < selections.Count ? selections[ordinal] : columnName;
            var baseColumnName = ResolveBaseColumnName(selection, columnName, tableColumns);
            if (baseColumnName is not null && tableColumns.TryGetValue(baseColumnName, out var columnInfo))
                return StripTypeLength(columnInfo.TypeName);
        }

        var match = Regex.Match(_command.CommandText, @"^\s*SELECT\s+(?<column>[\w\[\]""`]+)\s+FROM\s+(?<table>[\w\[\]""`]+)", RegexOptions.IgnoreCase);
        if (!match.Success || _command.Connection is null)
            return string.Empty;

        var column = UnquoteIdentifier(match.Groups["column"].Value);
        if (!string.Equals(column, GetName(ordinal), StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var table = UnquoteIdentifier(match.Groups["table"].Value);
        using var suspension = _command.Connection.SuspendHooks();
        using var command = _command.Connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return StripTypeLength(reader.GetString(2));
        }

        return string.Empty;
    }

    private static string InferDataTypeName(string expression)
    {
        var trimmed = expression.Trim();
        if (trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return "BLOB";
        if (trimmed.StartsWith("X'", StringComparison.OrdinalIgnoreCase))
            return "BLOB";
        if (trimmed.StartsWith('\''))
            return "TEXT";
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return "INTEGER";
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return "REAL";

        return "BLOB";
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string UnquoteIdentifier(string identifier)
    {
        var trimmed = identifier.Trim();
        if (trimmed.Length < 2)
            return trimmed;

        return (trimmed[0], trimmed[^1]) switch
        {
            ('"', '"') => trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
            ('[', ']') => trimmed[1..^1].Replace("]]", "]", StringComparison.Ordinal),
            ('`', '`') => trimmed[1..^1].Replace("``", "`", StringComparison.Ordinal),
            _ => trimmed
        };
    }

    private static string StripTypeLength(string typeName)
    {
        var index = typeName.IndexOf('(');
        return index < 0 ? typeName : typeName[..index];
    }

    private bool TryGetSelectSource(out string tableName, out List<string> selections)
    {
        tableName = string.Empty;
        selections = new List<string>();
        var match = Regex.Match(
            _currentSql,
            @"^\s*SELECT\s+(?<select>.*?)\s+FROM\s+(?<table>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
        {
            tableName = UnquoteIdentifier(match.Groups["table"].Value);
            selections = SplitSelectList(match.Groups["select"].Value);
            if (selections.Count == 1 && selections[0] == "*")
                selections = Enumerable.Range(0, FieldCount).Select(GetName).ToList();

            return true;
        }

        // DML ... RETURNING <cols>: the result columns come from the target table, so the
        // declared column types resolve via PRAGMA table_info of that table. Without this,
        // GetSchemaTable/GetDeclaredTypeName fall back to GetSampleValueType, which would
        // RE-EXECUTE the DML once per column (INSERT...RETURNING -> N+1 inserts) and report
        // Byte[]/BLOB for every column before the first Read.
        var returningMatch = Regex.Match(
            _currentSql,
            @"\bRETURNING\s+(?<cols>.+?)\s*;?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!returningMatch.Success)
            return false;

        var tableMatch = Regex.Match(
            _currentSql,
            @"(?:INSERT\s+(?:OR\s+\w+\s+)?INTO|UPDATE|DELETE\s+FROM)\s+(?<table>""(?:[^""]|"""")+""|\[[^\]]+\]|`[^`]+`|[\w]+)",
            RegexOptions.IgnoreCase);
        if (!tableMatch.Success)
            return false;

        tableName = UnquoteIdentifier(tableMatch.Groups["table"].Value);
        selections = SplitSelectList(returningMatch.Groups["cols"].Value);
        if (selections.Count == 1 && selections[0] == "*")
            selections = Enumerable.Range(0, FieldCount).Select(GetName).ToList();

        return true;
    }

    private static List<string> SplitSelectList(string selectList)
    {
        var selections = new List<string>();
        var start = 0;
        var quote = false;
        for (var i = 0; i < selectList.Length; i++)
        {
            if (selectList[i] == '\'')
                quote = !quote;
            else if (!quote && selectList[i] == ',')
            {
                selections.Add(selectList[start..i].Trim());
                start = i + 1;
            }
        }

        selections.Add(selectList[start..].Trim());
        return selections;
    }

    private Dictionary<string, SchemaColumnInfo> GetTableColumns(string tableName)
    {
        var columns = new Dictionary<string, SchemaColumnInfo>(StringComparer.OrdinalIgnoreCase);
        if (_command.Connection is null)
            return columns;

        using var suspension = _command.Connection.SuspendHooks();
        using (var command = _command.Connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                columns[name] = new SchemaColumnInfo(
                    name,
                    reader.GetString(2),
                    reader.GetInt64(3) == 0,
                    reader.GetInt64(5) != 0,
                    false);
            }
        }

        using (var indexCommand = _command.Connection.CreateCommand())
        {
            indexCommand.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            using var indexes = indexCommand.ExecuteReader();
            while (indexes.Read())
            {
                if (indexes.GetInt64(2) == 0 || indexes.GetInt64(4) != 0)
                    continue;

                var indexName = indexes.GetString(1);
                using var infoCommand = _command.Connection.CreateCommand();
                infoCommand.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)});";
                using var indexInfo = infoCommand.ExecuteReader();
                var indexedColumns = new List<string>();
                while (indexInfo.Read())
                {
                    if (!indexInfo.IsDBNull(2))
                        indexedColumns.Add(indexInfo.GetString(2));
                }

                if (indexedColumns.Count == 1 && columns.TryGetValue(indexedColumns[0], out var column))
                    columns[indexedColumns[0]] = column with { IsUnique = true };
            }
        }

        return columns;
    }

    private static string? ResolveBaseColumnName(string selection, string columnName, Dictionary<string, SchemaColumnInfo> tableColumns)
    {
        var withoutAlias = Regex.Replace(selection, @"\s+AS\s+.*$", "", RegexOptions.IgnoreCase).Trim();
        var candidate = UnquoteIdentifier(withoutAlias);
        if (tableColumns.ContainsKey(candidate))
            return candidate;
        if (selection.Length != withoutAlias.Length)
            return null;

        return tableColumns.ContainsKey(columnName) && !Regex.IsMatch(selection, @"[+\-*/()]")
            ? columnName
            : null;
    }

    private static string GetDataTypeNameFromValueType(ReaderValueKind valueType, string selection)
    {
        if (valueType == ReaderValueKind.Blob && Regex.IsMatch(selection, @"[+\-*/]"))
            return "INTEGER";

        return valueType switch
        {
            ReaderValueKind.Integer => "INTEGER",
            ReaderValueKind.Real => "REAL",
            ReaderValueKind.Text => "TEXT",
            _ => "BLOB"
        };
    }

    private ReaderValueKind GetSampleValueType(int ordinal)
    {
        // Re-executing the statement to sample a value type is only safe for read-only SELECTs.
        // DML (INSERT/UPDATE/DELETE/REPLACE, including ... RETURNING) would mutate the database
        // again on each call — once per column — corrupting rows. Declared types for RETURNING
        // are resolved via TryGetSelectSource; this fallback must stay read-only and otherwise
        // report BLOB, matching Microsoft.Data.Sqlite (which exposes a NULL declared type for
        // non-table expressions).
        if (IsMutableStatement(_currentSql))
            return ReaderValueKind.Blob;

        using var statement = _command.PrepareSingleStatement(_currentSql);
        while (statement.Read())
        {
            var value = statement.UsesManagedResults
                ? ReaderValue.FromManaged(statement.ManagedCurrentRow.GetValue(ordinal))
                : ReaderValue.FromNative(statement.GetNativeValue(ordinal));
            if (value.Kind is not ReaderValueKind.Null and not ReaderValueKind.Empty)
                return value.Kind;
        }

        return ReaderValueKind.Blob;
    }

    private static bool IsMutableStatement(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();
        return StartsWithKeyword(trimmed, "INSERT")
            || StartsWithKeyword(trimmed, "UPDATE")
            || StartsWithKeyword(trimmed, "DELETE")
            || StartsWithKeyword(trimmed, "REPLACE");
    }

    private static bool StartsWithKeyword(ReadOnlySpan<char> text, string keyword)
    {
        if (text.Length < keyword.Length)
            return false;

        if (!text[..keyword.Length].Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        return text.Length == keyword.Length || !char.IsLetterOrDigit(text[keyword.Length]);
    }

    private static ReaderValueKind GetValueKindFromTypeName(string typeName)
    {
        var normalized = StripTypeLength(typeName).ToUpperInvariant();
        if (normalized.Contains("INT"))
            return ReaderValueKind.Integer;
        if (normalized.Contains("CHAR") || normalized.Contains("CLOB") || normalized.Contains("TEXT"))
            return ReaderValueKind.Text;
        if (normalized.Contains("REAL") || normalized.Contains("FLOA") || normalized.Contains("DOUB"))
            return ReaderValueKind.Real;

        return ReaderValueKind.Blob;
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type GetClrTypeFromSqliteType(string typeName, ReaderValueKind fallback)
    {
        if (IsGuidType(typeName))
            return typeof(Guid);

        var normalized = typeName.ToUpperInvariant();
        if (normalized.Length == 0)
            return GetClrTypeFromValueType(fallback);
        if (normalized.Contains("INT"))
            return typeof(long);
        if (normalized.Contains("CHAR") || normalized.Contains("CLOB") || normalized.Contains("TEXT"))
            return typeof(string);
        if (normalized.Contains("REAL") || normalized.Contains("FLOA") || normalized.Contains("DOUB"))
            return typeof(double);
        if (normalized.Contains("BLOB"))
            return typeof(byte[]);

        return typeof(string);
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    private static Type GetClrTypeFromValueType(ReaderValueKind valueType)
        => valueType switch
        {
            ReaderValueKind.Integer => typeof(long),
            ReaderValueKind.Real => typeof(double),
            ReaderValueKind.Text => typeof(string),
            _ => typeof(byte[])
        };

    private static bool IsGuidType(string typeName)
    {
        var normalized = StripTypeLength(typeName).Trim();
        return normalized.Equals("GUID", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("UNIQUEIDENTIFIER", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SchemaColumnInfo(string Name, string TypeName, bool AllowNull, bool IsKey, bool IsUnique);

    private ReaderValue GetTypedValue(int ordinal)
    {
        EnsureHasCurrentRow();
        ValidateOrdinal(ordinal);
        var value = ReadValue(ordinal);
        if (value.Kind is ReaderValueKind.Null or ReaderValueKind.Empty)
            throw new InvalidOperationException(Properties.Resources.CalledOnNullValue(ordinal));

        return value;
    }

    private static DateTime ParseDateTime(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTimeOffset)
            && HasOffset(value))
            return dateTimeOffset.UtcDateTime;

        return DateTime.Parse(value, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        if (HasOffset(value))
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

        return new DateTimeOffset(DateTime.Parse(value, CultureInfo.InvariantCulture), TimeSpan.Zero);
    }

    private static bool HasOffset(string value)
    {
        var timeSeparator = value.IndexOf(':', StringComparison.Ordinal);
        return timeSeparator >= 0
               && (value.EndsWith('Z')
                   || value.LastIndexOf('+') > timeSeparator
                   || value.LastIndexOf('-') > timeSeparator);
    }

    private static long CopyValue<T>(T[] source, long dataOffset, T[]? buffer, int bufferOffset, int length)
    {
        if (dataOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(dataOffset), dataOffset, message: null);
        if (buffer is null)
            return source.LongLength;
        if (bufferOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(bufferOffset), bufferOffset, message: null);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, message: null);
        if (bufferOffset > buffer.Length - length)
            throw new ArgumentException(Properties.Resources.InvalidOffsetAndCount);
        if (dataOffset >= source.LongLength)
            return 0;

        var count = (int)Math.Min(length, source.LongLength - dataOffset);
        Array.Copy(source, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    private byte[] GetBlobValue(int ordinal)
    {
        var value = GetTypedValue(ordinal);
        return value.Kind == ReaderValueKind.Blob
            ? value.Blob
            : throw new InvalidCastException("The requested value is not a BLOB.");
    }

    private string GetTextValue(int ordinal)
    {
        var value = GetTypedValue(ordinal);
        return value.Kind == ReaderValueKind.Text
            ? value.Text
            : throw new InvalidCastException("The requested value is not TEXT.");
    }

    private static Guid ToGuid(ReaderValue value)
        => value.Kind == ReaderValueKind.Blob
            ? value.Blob.Length == 16
                ? new Guid(value.Blob)
                : Guid.Parse(Encoding.UTF8.GetString(value.Blob))
            : Guid.Parse(value.Text);

    private void DrainRemainingStatements()
    {
        try
        {
            foreach (var sql in _remainingSql)
            {
                if (_command.TryHandleFacadeStatement(sql, out var rewrittenSql))
                    continue;

                using var statement = _command.PrepareSingleStatement(rewrittenSql);
                while (statement.Read())
                {
                }

                if (SqliteCommand.CountsRowsAffected(rewrittenSql))
                {
                    _hadRecordsAffectedStatement = true;
                    _recordsAffected += statement.RowsAffected;
                }
                _command.MarkTransactionCompletedExternally(sql);
            }
        }
        finally
        {
            _remainingSql.Clear();
        }
    }

    private static SqliteType GetSqliteType(ReaderValueKind valueType)
    {
        return valueType switch
        {
            ReaderValueKind.Integer => SqliteType.Integer,
            ReaderValueKind.Real => SqliteType.Real,
            ReaderValueKind.Blob => SqliteType.Blob,
            _ => SqliteType.Text,
        };
    }

    private ReaderValue ReadValue(int ordinal)
    {
        var statement = GetStatement();
        return statement.UsesManagedResults
            ? ReaderValue.FromManaged(statement.ManagedCurrentRow.GetValue(ordinal))
            : ReaderValue.FromNative(statement.GetNativeValue(ordinal));
    }

    /// <summary>
    /// Returns the value kind of the current row, or <see cref="ReaderValueKind.Empty"/> when the
    /// reader is not positioned on a row. Type metadata has to stay readable before the first
    /// <c>Read</c> because <see cref="System.Data.Common.DbDataAdapter"/> maps the result schema
    /// before it fetches any rows.
    /// </summary>
    private ReaderValueKind CurrentValueKind(int ordinal)
        => _hasCurrentRow ? ReadValue(ordinal).Kind : ReaderValueKind.Empty;
}
