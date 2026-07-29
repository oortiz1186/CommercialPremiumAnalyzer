using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string connectionEnvironmentVariable = "COMMERCIAL_PREMIUM_CONNECTION";

if (args.Length == 0)
{
    PrintHelp();
    return;
}

var connectionString = Environment.GetEnvironmentVariable(connectionEnvironmentVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"Falta la variable de entorno {connectionEnvironmentVariable}.");
    Environment.ExitCode = 2;
    return;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "snapshot":
            await CreateSnapshotAsync(connectionString, args.Skip(1).ToArray());
            break;
        case "compare-snapshots":
            await CompareSnapshotsAsync(connectionString, args.Skip(1).ToArray());
            break;
        case "compare-rows":
            await CompareRowsAsync(connectionString, args.Skip(1).ToArray());
            break;
        default:
            PrintHelp();
            Environment.ExitCode = 1;
            break;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}

static async Task CreateSnapshotAsync(string connectionString, string[] commandArgs)
{
    var outputPath = GetOption(commandArgs, "--output")
        ?? $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.json";
    var prefix = GetOption(commandArgs, "--prefix") ?? "adm";

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    var tables = await ReadTablesAsync(connection, prefix);
    var results = new List<TableSnapshot>();

    foreach (var table in tables)
    {
        var fullTableName = $"{Quote(table.SchemaName)}.{Quote(table.TableName)}";
        var count = await ExecuteScalarLongAsync(connection, $"SELECT COUNT_BIG(*) FROM {fullTableName};");
        var keyColumn = await FindLikelyKeyColumnAsync(connection, table.SchemaName, table.TableName);
        string? maximumValue = null;

        if (keyColumn is not null)
        {
            maximumValue = await ExecuteScalarStringAsync(
                connection,
                $"SELECT CONVERT(nvarchar(200), MAX({Quote(keyColumn)})) FROM {fullTableName};");
        }

        var checksum = await TryReadTableChecksumAsync(connection, fullTableName);
        results.Add(new TableSnapshot(
            table.SchemaName,
            table.TableName,
            count,
            keyColumn,
            maximumValue,
            checksum));

        Console.WriteLine(
            $"{table.SchemaName}.{table.TableName}: {count:N0} registros | checksum: {checksum ?? "no disponible"}");
    }

    var snapshot = new DatabaseSnapshot(connection.Database, DateTimeOffset.Now, results);
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(snapshot, JsonOptions()));
    Console.WriteLine($"Snapshot guardado en: {Path.GetFullPath(outputPath)}");
}

static async Task CompareSnapshotsAsync(string connectionString, string[] commandArgs)
{
    if (commandArgs.Length < 2)
        throw new ArgumentException(
            "Uso: compare-snapshots <antes.json> <despues.json> [--output diferencias.json] [--include-new-rows true]");

    var beforePath = commandArgs[0];
    var afterPath = commandArgs[1];
    var outputPath = GetOption(commandArgs, "--output");
    var includeNewRows = !string.Equals(
        GetOption(commandArgs, "--include-new-rows"),
        "false",
        StringComparison.OrdinalIgnoreCase);

    var before = JsonSerializer.Deserialize<DatabaseSnapshot>(File.ReadAllText(beforePath), JsonOptions())
        ?? throw new InvalidOperationException("No fue posible leer el snapshot anterior.");
    var after = JsonSerializer.Deserialize<DatabaseSnapshot>(File.ReadAllText(afterPath), JsonOptions())
        ?? throw new InvalidOperationException("No fue posible leer el snapshot posterior.");

    if (!string.Equals(before.DatabaseName, after.DatabaseName, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Los snapshots pertenecen a bases de datos diferentes.");

    var beforeTables = before.Tables.ToDictionary(
        x => $"{x.SchemaName}.{x.TableName}",
        StringComparer.OrdinalIgnoreCase);
    var afterTables = after.Tables.ToDictionary(
        x => $"{x.SchemaName}.{x.TableName}",
        StringComparer.OrdinalIgnoreCase);
    var names = beforeTables.Keys
        .Union(afterTables.Keys, StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x);

    var changes = new List<TableDifference>();
    SqlConnection? connection = null;

    if (includeNewRows)
    {
        connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
    }

    try
    {
        foreach (var name in names)
        {
            beforeTables.TryGetValue(name, out var oldValue);
            afterTables.TryGetValue(name, out var newValue);

            var oldCount = oldValue?.RowCount ?? 0;
            var newCount = newValue?.RowCount ?? 0;
            var oldMaximum = oldValue?.MaximumKeyValue;
            var newMaximum = newValue?.MaximumKeyValue;
            var oldChecksum = oldValue?.ContentChecksum;
            var newChecksum = newValue?.ContentChecksum;

            var countChanged = oldCount != newCount;
            var maximumChanged = !string.Equals(oldMaximum, newMaximum, StringComparison.Ordinal);
            var checksumChanged = oldChecksum.HasValue && newChecksum.HasValue && oldChecksum != newChecksum;

            if (!countChanged && !maximumChanged && !checksumChanged)
                continue;

            var changeType = countChanged
                ? newCount > oldCount ? "INSERT" : "DELETE"
                : "UPDATE";

            var newRows = new List<Dictionary<string, object?>>();
            if (includeNewRows && connection is not null && newCount > oldCount && oldValue is not null && newValue is not null)
            {
                newRows = await ReadNewRowsAsync(connection, oldValue, newValue);
            }

            var difference = new TableDifference(
                newValue?.SchemaName ?? oldValue?.SchemaName ?? "dbo",
                newValue?.TableName ?? oldValue?.TableName ?? name,
                changeType,
                oldCount,
                newCount,
                newCount - oldCount,
                newValue?.KeyColumn ?? oldValue?.KeyColumn,
                oldMaximum,
                newMaximum,
                oldChecksum,
                newChecksum,
                checksumChanged,
                newRows);

            changes.Add(difference);
            PrintTableDifference(difference);
        }
    }
    finally
    {
        if (connection is not null)
            await connection.DisposeAsync();
    }

    if (changes.Count == 0)
    {
        Console.WriteLine("No se detectaron cambios.");
        return;
    }

    var report = new SnapshotComparisonReport(
        before.DatabaseName,
        before.CreatedAt,
        after.CreatedAt,
        DateTimeOffset.Now,
        changes);

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, JsonOptions()));
        Console.WriteLine($"Reporte JSON guardado en: {Path.GetFullPath(outputPath)}");
    }
}

static void PrintTableDifference(TableDifference difference)
{
    Console.WriteLine($"{difference.SchemaName}.{difference.TableName}");
    Console.WriteLine($"  Tipo detectado: {difference.ChangeType}");
    Console.WriteLine(
        $"  Registros: {difference.BeforeRowCount:N0} -> {difference.AfterRowCount:N0} " +
        $"({difference.RowCountDelta:+#;-#;0})");

    if (!string.Equals(difference.BeforeMaximumKey, difference.AfterMaximumKey, StringComparison.Ordinal))
    {
        Console.WriteLine(
            $"  Máximo {difference.KeyColumn}: " +
            $"{difference.BeforeMaximumKey ?? "NULL"} -> {difference.AfterMaximumKey ?? "NULL"}");
    }

    if (difference.ContentChanged)
    {
        Console.WriteLine(
            $"  Contenido modificado: {difference.BeforeChecksum?.ToString() ?? "NULL"} -> " +
            $"{difference.AfterChecksum?.ToString() ?? "NULL"}");
    }

    if (difference.NewRows.Count > 0)
    {
        Console.WriteLine($"  Registros nuevos encontrados: {difference.NewRows.Count}");
        foreach (var row in difference.NewRows)
        {
            var keyValue = difference.KeyColumn is not null && row.TryGetValue(difference.KeyColumn, out var value)
                ? FormatValue(value)
                : "sin llave";
            Console.WriteLine($"    {difference.KeyColumn ?? "Registro"} = {keyValue}");
        }
    }
}

static async Task<List<Dictionary<string, object?>>> ReadNewRowsAsync(
    SqlConnection connection,
    TableSnapshot before,
    TableSnapshot after)
{
    if (string.IsNullOrWhiteSpace(after.KeyColumn) ||
        string.IsNullOrWhiteSpace(before.MaximumKeyValue) ||
        string.IsNullOrWhiteSpace(after.MaximumKeyValue))
        return [];

    if (!decimal.TryParse(before.MaximumKeyValue, out var minimum) ||
        !decimal.TryParse(after.MaximumKeyValue, out var maximum) ||
        maximum <= minimum)
        return [];

    var sql = $"""
        SELECT *
        FROM {Quote(after.SchemaName)}.{Quote(after.TableName)}
        WHERE {Quote(after.KeyColumn)} > @Minimum
          AND {Quote(after.KeyColumn)} <= @Maximum
        ORDER BY {Quote(after.KeyColumn)};
        """;

    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    command.Parameters.AddWithValue("@Minimum", minimum);
    command.Parameters.AddWithValue("@Maximum", maximum);
    await using var reader = await command.ExecuteReaderAsync();

    var rows = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
            row[reader.GetName(index)] = NormalizeJsonValue(reader.IsDBNull(index) ? null : reader.GetValue(index));
        rows.Add(row);
    }

    return rows;
}

static async Task CompareRowsAsync(string connectionString, string[] commandArgs)
{
    var table = GetOption(commandArgs, "--table") ?? throw new ArgumentException("Falta --table.");
    var key = GetOption(commandArgs, "--key") ?? throw new ArgumentException("Falta --key.");
    var firstValue = GetOption(commandArgs, "--a") ?? throw new ArgumentException("Falta --a.");
    var secondValue = GetOption(commandArgs, "--b") ?? throw new ArgumentException("Falta --b.");
    var outputPath = GetOption(commandArgs, "--output");

    var (schemaName, tableName) = ParseTableName(table);

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    await ValidateTableAndColumnAsync(connection, schemaName, tableName, key);
    var first = await ReadRowAsync(connection, schemaName, tableName, key, firstValue);
    var second = await ReadRowAsync(connection, schemaName, tableName, key, secondValue);

    var differences = new List<ColumnDifference>();
    foreach (var column in first.Keys.Union(second.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
    {
        first.TryGetValue(column, out var firstColumnValue);
        second.TryGetValue(column, out var secondColumnValue);

        if (ValuesEqual(firstColumnValue, secondColumnValue))
            continue;

        differences.Add(new ColumnDifference(
            column,
            NormalizeJsonValue(firstColumnValue),
            NormalizeJsonValue(secondColumnValue)));

        Console.WriteLine(column);
        Console.WriteLine($"  A: {FormatValue(firstColumnValue)}");
        Console.WriteLine($"  B: {FormatValue(secondColumnValue)}");
    }

    Console.WriteLine(differences.Count == 0
        ? "Los registros no tienen diferencias."
        : $"Diferencias encontradas: {differences.Count}");

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        var report = new RowComparisonReport(
            schemaName,
            tableName,
            key,
            firstValue,
            secondValue,
            DateTimeOffset.Now,
            differences);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, JsonOptions()));
        Console.WriteLine($"Reporte JSON guardado en: {Path.GetFullPath(outputPath)}");
    }
}

static async Task<List<TableInfo>> ReadTablesAsync(SqlConnection connection, string prefix)
{
    const string sql = """
        SELECT s.name AS SchemaName, t.name AS TableName
        FROM sys.tables t
        INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
          AND t.name LIKE @Prefix + '%'
        ORDER BY s.name, t.name;
        """;

    var tables = new List<TableInfo>();
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@Prefix", SqlDbType.NVarChar, 128).Value = prefix;
    await using var reader = await command.ExecuteReaderAsync();

    while (await reader.ReadAsync())
        tables.Add(new TableInfo(reader.GetString(0), reader.GetString(1)));

    return tables;
}

static async Task<string?> FindLikelyKeyColumnAsync(SqlConnection connection, string schemaName, string tableName)
{
    const string sql = """
        SELECT TOP (1) c.name
        FROM sys.indexes i
        INNER JOIN sys.index_columns ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        INNER JOIN sys.tables t ON t.object_id = i.object_id
        INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = @SchemaName
          AND t.name = @TableName
          AND i.is_primary_key = 1
          AND ic.key_ordinal = 1;
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@SchemaName", SqlDbType.NVarChar, 128).Value = schemaName;
    command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = tableName;
    return (string?)await command.ExecuteScalarAsync();
}

static async Task<long?> TryReadTableChecksumAsync(SqlConnection connection, string fullTableName)
{
    try
    {
        var value = await ExecuteScalarAsync(
            connection,
            $"SELECT CONVERT(bigint, CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM {fullTableName};");
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }
    catch (SqlException exception)
    {
        Console.WriteLine($"  Aviso: no se pudo calcular checksum para {fullTableName}: {exception.Message}");
        return null;
    }
}

static async Task<Dictionary<string, object?>> ReadRowAsync(
    SqlConnection connection,
    string schemaName,
    string tableName,
    string keyColumn,
    string keyValue)
{
    var sql = $"SELECT * FROM {Quote(schemaName)}.{Quote(tableName)} WHERE {Quote(keyColumn)} = @KeyValue;";
    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@KeyValue", SqlDbType.NVarChar, 4000).Value = keyValue;
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);

    if (!await reader.ReadAsync())
        throw new InvalidOperationException($"No se encontró el registro {keyColumn}={keyValue}.");

    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < reader.FieldCount; index++)
        values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);

    return values;
}

static async Task ValidateTableAndColumnAsync(
    SqlConnection connection,
    string schemaName,
    string tableName,
    string columnName)
{
    const string sql = """
        SELECT COUNT(*)
        FROM sys.tables t
        INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
        INNER JOIN sys.columns c ON c.object_id = t.object_id
        WHERE s.name = @SchemaName AND t.name = @TableName AND c.name = @ColumnName;
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@SchemaName", SqlDbType.NVarChar, 128).Value = schemaName;
    command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = tableName;
    command.Parameters.Add("@ColumnName", SqlDbType.NVarChar, 128).Value = columnName;

    if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 0)
        throw new InvalidOperationException("La tabla o columna indicada no existe.");
}

static async Task<long> ExecuteScalarLongAsync(SqlConnection connection, string sql)
{
    var value = await ExecuteScalarAsync(connection, sql);
    return Convert.ToInt64(value);
}

static async Task<string?> ExecuteScalarStringAsync(SqlConnection connection, string sql)
{
    var value = await ExecuteScalarAsync(connection, sql);
    return value is null or DBNull ? null : Convert.ToString(value);
}

static async Task<object?> ExecuteScalarAsync(SqlConnection connection, string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    return await command.ExecuteScalarAsync();
}

static (string SchemaName, string TableName) ParseTableName(string value)
{
    var parts = value.Split('.', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    return parts.Length == 1 ? ("dbo", parts[0]) : (parts[0], parts[1]);
}

static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

static string? GetOption(string[] commandArgs, string name)
{
    var index = Array.FindIndex(commandArgs, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < commandArgs.Length ? commandArgs[index + 1] : null;
}

static bool ValuesEqual(object? first, object? second)
{
    if (first is null && second is null) return true;
    if (first is null || second is null) return false;
    if (first is byte[] firstBytes && second is byte[] secondBytes)
        return firstBytes.SequenceEqual(secondBytes);
    return string.Equals(Convert.ToString(first), Convert.ToString(second), StringComparison.Ordinal);
}

static object? NormalizeJsonValue(object? value) => value switch
{
    null => null,
    DBNull => null,
    byte[] bytes => Convert.ToHexString(bytes),
    DateTime dateTime => dateTime.ToString("O"),
    DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O"),
    TimeSpan timeSpan => timeSpan.ToString(),
    _ => value
};

static string FormatValue(object? value) => value switch
{
    null => "NULL",
    byte[] bytes => Convert.ToHexString(bytes),
    DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
    _ => Convert.ToString(value) ?? string.Empty
};

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
};

static void PrintHelp()
{
    Console.WriteLine("""
        CommercialPremiumAnalyzer

        Variable requerida:
          COMMERCIAL_PREMIUM_CONNECTION

        Comandos:
          snapshot --output antes.json [--prefix adm]

          compare-snapshots antes.json despues.json
            [--output diferencias.json]
            [--include-new-rows true|false]

          compare-rows --table dbo.admDocumentos --key CIDDOCUMENTO
            --a 8295 --b 8296 [--output documento.json]
        """);
}

internal sealed record TableInfo(string SchemaName, string TableName);

internal sealed record TableSnapshot(
    string SchemaName,
    string TableName,
    long RowCount,
    string? KeyColumn,
    string? MaximumKeyValue,
    long? ContentChecksum);

internal sealed record DatabaseSnapshot(
    string DatabaseName,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<TableSnapshot> Tables);

internal sealed record TableDifference(
    string SchemaName,
    string TableName,
    string ChangeType,
    long BeforeRowCount,
    long AfterRowCount,
    long RowCountDelta,
    string? KeyColumn,
    string? BeforeMaximumKey,
    string? AfterMaximumKey,
    long? BeforeChecksum,
    long? AfterChecksum,
    bool ContentChanged,
    IReadOnlyCollection<Dictionary<string, object?>> NewRows);

internal sealed record SnapshotComparisonReport(
    string DatabaseName,
    DateTimeOffset BeforeCreatedAt,
    DateTimeOffset AfterCreatedAt,
    DateTimeOffset ComparedAt,
    IReadOnlyCollection<TableDifference> Tables);

internal sealed record ColumnDifference(string ColumnName, object? ValueA, object? ValueB);

internal sealed record RowComparisonReport(
    string SchemaName,
    string TableName,
    string KeyColumn,
    string ValueA,
    string ValueB,
    DateTimeOffset ComparedAt,
    IReadOnlyCollection<ColumnDifference> Differences);