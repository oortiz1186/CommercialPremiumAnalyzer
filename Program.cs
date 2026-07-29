using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string ConnectionVariable = "COMMERCIAL_PREMIUM_CONNECTION";

if (args.Length == 0)
{
    PrintHelp();
    return;
}

var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"Falta la variable de entorno {ConnectionVariable}.");
    Environment.ExitCode = 2;
    return;
}

try
{
    var command = args[0].ToLowerInvariant();
    var commandArgs = args.Skip(1).ToArray();

    switch (command)
    {
        case "snapshot":
            await CreateSnapshotAsync(connectionString, commandArgs);
            break;

        case "compare-snapshots":
            await CompareSnapshotsAsync(connectionString, commandArgs);
            break;

        case "compare-rows":
            await CompareRowsAsync(connectionString, commandArgs);
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
    var snapshots = new List<TableSnapshot>();

    foreach (var table in tables)
    {
        var fullTableName = $"{Quote(table.SchemaName)}.{Quote(table.TableName)}";
        var rowCount = await ExecuteScalarLongAsync(
            connection,
            $"SELECT COUNT_BIG(*) FROM {fullTableName};");

        var keyColumn = await FindPrimaryKeyColumnAsync(
            connection,
            table.SchemaName,
            table.TableName);

        string? maximumKeyValue = null;
        if (!string.IsNullOrWhiteSpace(keyColumn))
        {
            maximumKeyValue = await ExecuteScalarStringAsync(
                connection,
                $"SELECT CONVERT(nvarchar(200), MAX({Quote(keyColumn)})) FROM {fullTableName};");
        }

        var checksum = await TryReadTableChecksumAsync(connection, fullTableName);

        snapshots.Add(new TableSnapshot(
            table.SchemaName,
            table.TableName,
            rowCount,
            keyColumn,
            maximumKeyValue,
            checksum));

        var checksumText = checksum?.ToString(CultureInfo.InvariantCulture) ?? "no disponible";
        Console.WriteLine(
            $"{table.SchemaName}.{table.TableName}: {rowCount:N0} registros | checksum: {checksumText}");
    }

    var snapshot = new DatabaseSnapshot(
        connection.Database,
        DateTimeOffset.Now,
        snapshots);

    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(snapshot, JsonOptions()));

    Console.WriteLine($"Snapshot guardado en: {Path.GetFullPath(outputPath)}");
}

static async Task CompareSnapshotsAsync(string connectionString, string[] commandArgs)
{
    if (commandArgs.Length < 2)
    {
        throw new ArgumentException(
            "Uso: compare-snapshots <antes.json> <despues.json> [--output diferencias.json] [--include-new-rows true|false]");
    }

    var before = ReadSnapshot(commandArgs[0]);
    var after = ReadSnapshot(commandArgs[1]);
    var outputPath = GetOption(commandArgs, "--output");
    var includeNewRows = !string.Equals(
        GetOption(commandArgs, "--include-new-rows"),
        "false",
        StringComparison.OrdinalIgnoreCase);

    if (!string.Equals(before.DatabaseName, after.DatabaseName, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Los snapshots pertenecen a bases de datos diferentes.");

    var beforeTables = before.Tables.ToDictionary(
        item => $"{item.SchemaName}.{item.TableName}",
        StringComparer.OrdinalIgnoreCase);

    var afterTables = after.Tables.ToDictionary(
        item => $"{item.SchemaName}.{item.TableName}",
        StringComparer.OrdinalIgnoreCase);

    var tableNames = beforeTables.Keys
        .Union(afterTables.Keys, StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    await using var connection = includeNewRows ? new SqlConnection(connectionString) : null;
    if (connection is not null)
        await connection.OpenAsync();

    var differences = new List<TableDifference>();

    foreach (var tableName in tableNames)
    {
        beforeTables.TryGetValue(tableName, out var oldTable);
        afterTables.TryGetValue(tableName, out var newTable);

        var oldCount = oldTable?.RowCount ?? 0;
        var newCount = newTable?.RowCount ?? 0;
        var oldMaximum = oldTable?.MaximumKeyValue;
        var newMaximum = newTable?.MaximumKeyValue;
        var oldChecksum = oldTable?.ContentChecksum;
        var newChecksum = newTable?.ContentChecksum;

        var countChanged = oldCount != newCount;
        var maximumChanged = !string.Equals(oldMaximum, newMaximum, StringComparison.Ordinal);
        var checksumChanged = oldChecksum.HasValue &&
                              newChecksum.HasValue &&
                              oldChecksum.Value != newChecksum.Value;

        if (!countChanged && !maximumChanged && !checksumChanged)
            continue;

        var changeType = countChanged
            ? newCount > oldCount ? "INSERT" : "DELETE"
            : "UPDATE";

        IReadOnlyCollection<Dictionary<string, object?>> newRows = [];
        if (connection is not null &&
            newCount > oldCount &&
            oldTable is not null &&
            newTable is not null)
        {
            newRows = await ReadNewRowsAsync(connection, oldTable, newTable);
        }

        var difference = new TableDifference(
            newTable?.SchemaName ?? oldTable?.SchemaName ?? "dbo",
            newTable?.TableName ?? oldTable?.TableName ?? tableName,
            changeType,
            oldCount,
            newCount,
            newCount - oldCount,
            newTable?.KeyColumn ?? oldTable?.KeyColumn,
            oldMaximum,
            newMaximum,
            oldChecksum,
            newChecksum,
            checksumChanged,
            newRows);

        differences.Add(difference);
        PrintTableDifference(difference);
    }

    if (differences.Count == 0)
    {
        Console.WriteLine("No se detectaron cambios.");
        return;
    }

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        var report = new SnapshotComparisonReport(
            before.DatabaseName,
            before.CreatedAt,
            after.CreatedAt,
            DateTimeOffset.Now,
            differences);

        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, JsonOptions()));

        Console.WriteLine($"Reporte JSON guardado en: {Path.GetFullPath(outputPath)}");
    }
}

static async Task CompareRowsAsync(string connectionString, string[] commandArgs)
{
    var table = GetRequiredOption(commandArgs, "--table");
    var keyColumn = GetRequiredOption(commandArgs, "--key");
    var valueA = GetRequiredOption(commandArgs, "--a");
    var valueB = GetRequiredOption(commandArgs, "--b");
    var outputPath = GetOption(commandArgs, "--output");

    var (schemaName, tableName) = ParseTableName(table);

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    await ValidateTableAndColumnAsync(
        connection,
        schemaName,
        tableName,
        keyColumn);

    var rowA = await ReadRowAsync(
        connection,
        schemaName,
        tableName,
        keyColumn,
        valueA);

    var rowB = await ReadRowAsync(
        connection,
        schemaName,
        tableName,
        keyColumn,
        valueB);

    var differences = new List<ColumnDifference>();

    foreach (var column in rowA.Keys
                 .Union(rowB.Keys, StringComparer.OrdinalIgnoreCase)
                 .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
    {
        rowA.TryGetValue(column, out var firstValue);
        rowB.TryGetValue(column, out var secondValue);

        if (ValuesEqual(firstValue, secondValue))
            continue;

        differences.Add(new ColumnDifference(
            column,
            NormalizeJsonValue(firstValue),
            NormalizeJsonValue(secondValue)));

        Console.WriteLine(column);
        Console.WriteLine($"  A: {FormatValue(firstValue)}");
        Console.WriteLine($"  B: {FormatValue(secondValue)}");
    }

    Console.WriteLine(differences.Count == 0
        ? "Los registros no tienen diferencias."
        : $"Diferencias encontradas: {differences.Count}");

    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        var report = new RowComparisonReport(
            schemaName,
            tableName,
            keyColumn,
            valueA,
            valueB,
            DateTimeOffset.Now,
            differences);

        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, JsonOptions()));

        Console.WriteLine($"Reporte JSON guardado en: {Path.GetFullPath(outputPath)}");
    }
}

static DatabaseSnapshot ReadSnapshot(string path)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"No existe el archivo: {path}");

    return JsonSerializer.Deserialize<DatabaseSnapshot>(
               File.ReadAllText(path),
               JsonOptions())
           ?? throw new InvalidOperationException($"No fue posible leer el snapshot: {path}");
}

static void PrintTableDifference(TableDifference difference)
{
    Console.WriteLine($"{difference.SchemaName}.{difference.TableName}");
    Console.WriteLine($"  Tipo detectado: {difference.ChangeType}");
    Console.WriteLine(
        $"  Registros: {difference.BeforeRowCount:N0} -> {difference.AfterRowCount:N0} " +
        $"({difference.RowCountDelta:+#;-#;0})");

    if (!string.Equals(
            difference.BeforeMaximumKey,
            difference.AfterMaximumKey,
            StringComparison.Ordinal))
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

    if (difference.NewRows.Count == 0)
        return;

    Console.WriteLine($"  Registros nuevos encontrados: {difference.NewRows.Count}");
    foreach (var row in difference.NewRows)
    {
        var keyValue = difference.KeyColumn is not null &&
                       row.TryGetValue(difference.KeyColumn, out var value)
            ? FormatValue(value)
            : "sin llave";

        Console.WriteLine($"    {difference.KeyColumn ?? "Registro"} = {keyValue}");
    }
}

static async Task<IReadOnlyCollection<Dictionary<string, object?>>> ReadNewRowsAsync(
    SqlConnection connection,
    TableSnapshot before,
    TableSnapshot after)
{
    if (string.IsNullOrWhiteSpace(after.KeyColumn) ||
        string.IsNullOrWhiteSpace(before.MaximumKeyValue) ||
        string.IsNullOrWhiteSpace(after.MaximumKeyValue))
    {
        return [];
    }

    if (!decimal.TryParse(before.MaximumKeyValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var minimum) ||
        !decimal.TryParse(after.MaximumKeyValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var maximum) ||
        maximum <= minimum)
    {
        return [];
    }

    var sql = $"""
        SELECT *
        FROM {Quote(after.SchemaName)}.{Quote(after.TableName)}
        WHERE {Quote(after.KeyColumn)} > @Minimum
          AND {Quote(after.KeyColumn)} <= @Maximum
        ORDER BY {Quote(after.KeyColumn)};
        """;

    await using var command = new SqlCommand(sql, connection)
    {
        CommandTimeout = 120
    };

    command.Parameters.AddWithValue("@Minimum", minimum);
    command.Parameters.AddWithValue("@Maximum", maximum);

    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, object?>>();

    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < reader.FieldCount; index++)
        {
            var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
            row[reader.GetName(index)] = NormalizeJsonValue(value);
        }

        rows.Add(row);
    }

    return rows;
}

static async Task<List<TableInfo>> ReadTablesAsync(
    SqlConnection connection,
    string prefix)
{
    const string sql = """
        SELECT s.name, t.name
        FROM sys.tables t
        INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
          AND t.name LIKE @Prefix + '%'
        ORDER BY s.name, t.name;
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@Prefix", SqlDbType.NVarChar, 128).Value = prefix;

    await using var reader = await command.ExecuteReaderAsync();
    var tables = new List<TableInfo>();

    while (await reader.ReadAsync())
        tables.Add(new TableInfo(reader.GetString(0), reader.GetString(1)));

    return tables;
}

static async Task<string?> FindPrimaryKeyColumnAsync(
    SqlConnection connection,
    string schemaName,
    string tableName)
{
    const string sql = """
        SELECT TOP (1) c.name
        FROM sys.indexes i
        INNER JOIN sys.index_columns ic
            ON ic.object_id = i.object_id
           AND ic.index_id = i.index_id
        INNER JOIN sys.columns c
            ON c.object_id = ic.object_id
           AND c.column_id = ic.column_id
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

    var value = await command.ExecuteScalarAsync();
    return value is null or DBNull ? null : Convert.ToString(value);
}

static async Task<long?> TryReadTableChecksumAsync(
    SqlConnection connection,
    string fullTableName)
{
    try
    {
        var value = await ExecuteScalarAsync(
            connection,
            $"SELECT CONVERT(bigint, CHECKSUM_AGG(BINARY_CHECKSUM(*))) FROM {fullTableName};");

        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }
    catch (SqlException exception)
    {
        Console.WriteLine(
            $"  Aviso: no se pudo calcular checksum para {fullTableName}: {exception.Message}");
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

    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < reader.FieldCount; index++)
        row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);

    return row;
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
        WHERE s.name = @SchemaName
          AND t.name = @TableName
          AND c.name = @ColumnName;
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
    await using var command = new SqlCommand(sql, connection)
    {
        CommandTimeout = 120
    };

    return await command.ExecuteScalarAsync();
}

static (string SchemaName, string TableName) ParseTableName(string value)
{
    var parts = value.Split(
        '.',
        2,
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    return parts.Length == 1 ? ("dbo", parts[0]) : (parts[0], parts[1]);
}

static string Quote(string identifier) =>
    $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

static string? GetOption(string[] commandArgs, string name)
{
    var index = Array.FindIndex(
        commandArgs,
        item => item.Equals(name, StringComparison.OrdinalIgnoreCase));

    return index >= 0 && index + 1 < commandArgs.Length
        ? commandArgs[index + 1]
        : null;
}

static string GetRequiredOption(string[] commandArgs, string name) =>
    GetOption(commandArgs, name)
    ?? throw new ArgumentException($"Falta {name}.");

static bool ValuesEqual(object? first, object? second)
{
    if (first is null && second is null)
        return true;
    if (first is null || second is null)
        return false;
    if (first is byte[] firstBytes && second is byte[] secondBytes)
        return firstBytes.SequenceEqual(secondBytes);

    return string.Equals(
        Convert.ToString(first, CultureInfo.InvariantCulture),
        Convert.ToString(second, CultureInfo.InvariantCulture),
        StringComparison.Ordinal);
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
    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
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

internal sealed record ColumnDifference(
    string ColumnName,
    object? ValueA,
    object? ValueB);

internal sealed record RowComparisonReport(
    string SchemaName,
    string TableName,
    string KeyColumn,
    string ValueA,
    string ValueB,
    DateTimeOffset ComparedAt,
    IReadOnlyCollection<ColumnDifference> Differences);
