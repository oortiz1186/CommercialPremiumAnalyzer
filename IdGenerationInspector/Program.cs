using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string ConnectionVariable = "COMMERCIAL_PREMIUM_CONNECTION";

var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"Falta la variable de entorno {ConnectionVariable}.");
    return;
}

var outputPath = GetOption(args, "--output") ?? "id-generation-report.json";

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

var targets = new[]
{
    new Target("admDocumentos", "CIDDOCUMENTO"),
    new Target("admMovimientos", "CIDMOVIMIENTO"),
    new Target("admDomicilios", "CIDDIRECCION"),
    new Target("admAcumulados", "CIDACUMULADO"),
    new Target("admBitacoras", "IDBITACORA")
};

var targetReports = new List<TargetReport>();
foreach (var target in targets)
{
    var metadata = await ReadTargetMetadataAsync(connection, target);
    targetReports.Add(metadata);

    Console.WriteLine($"{target.TableName}.{target.ColumnName}");
    Console.WriteLine($"  Máximo actual: {metadata.MaximumValue}");
    Console.WriteLine($"  Registros: {metadata.RowCount:N0}");
    Console.WriteLine($"  Identity: {(metadata.IsIdentity ? "sí" : "no")}");
    Console.WriteLine($"  Default: {metadata.DefaultDefinition ?? "ninguno"}");
    Console.WriteLine($"  Trigger(s): {metadata.Triggers.Count}");
}

var sequences = await ReadRowsAsync(connection, """
    SELECT s.name AS SchemaName, seq.name AS SequenceName,
           seq.current_value AS CurrentValue,
           seq.start_value AS StartValue,
           seq.increment AS IncrementValue
    FROM sys.sequences seq
    INNER JOIN sys.schemas s ON s.schema_id = seq.schema_id
    ORDER BY s.name, seq.name;
    """);

var candidateColumns = await ReadRowsAsync(connection, """
    SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName,
           ty.name AS DataType, c.is_nullable AS IsNullable
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    INNER JOIN sys.columns c ON c.object_id = t.object_id
    INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE t.is_ms_shipped = 0
      AND (
        c.name LIKE '%CONSECUTIVO%' OR
        c.name LIKE '%ULTIMO%' OR
        c.name LIKE '%SIGUIENTE%' OR
        c.name LIKE '%FOLIO%' OR
        c.name LIKE '%CONTADOR%'
      )
    ORDER BY t.name, c.column_id;
    """);

var modules = await ReadRowsAsync(connection, """
    SELECT s.name AS SchemaName, o.name AS ObjectName, o.type_desc AS ObjectType,
           LEFT(m.definition, 4000) AS Definition
    FROM sys.sql_modules m
    INNER JOIN sys.objects o ON o.object_id = m.object_id
    INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
    WHERE m.definition LIKE '%CIDDOCUMENTO%'
       OR m.definition LIKE '%CIDMOVIMIENTO%'
       OR m.definition LIKE '%CIDDIRECCION%'
       OR m.definition LIKE '%CIDACUMULADO%'
       OR m.definition LIKE '%IDBITACORA%'
       OR m.definition LIKE '%admDocumentos%'
       OR m.definition LIKE '%admMovimientos%'
       OR m.definition LIKE '%admDomicilios%'
    ORDER BY o.type_desc, s.name, o.name;
    """);

PrintSection("SECUENCIAS SQL", sequences, row =>
    $"{Value(row, "SchemaName")}.{Value(row, "SequenceName")} | actual={Value(row, "CurrentValue")} | inicio={Value(row, "StartValue")} | incremento={Value(row, "IncrementValue")}");

PrintSection("COLUMNAS CANDIDATAS DE CONSECUTIVOS", candidateColumns, row =>
    $"{Value(row, "SchemaName")}.{Value(row, "TableName")}.{Value(row, "ColumnName")} | {Value(row, "DataType")}");

PrintSection("MÓDULOS SQL RELACIONADOS", modules, row =>
    $"{Value(row, "ObjectType")} | {Value(row, "SchemaName")}.{Value(row, "ObjectName")}");

var recommendation = BuildRecommendation(targetReports, sequences, candidateColumns, modules);
Console.WriteLine();
Console.WriteLine("CONCLUSIÓN PRELIMINAR");
Console.WriteLine($"  {recommendation}");

var report = new IdGenerationReport(
    connection.Database,
    DateTimeOffset.Now,
    targetReports,
    sequences,
    candidateColumns,
    modules,
    recommendation);

await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine();
Console.WriteLine($"Reporte guardado en: {Path.GetFullPath(outputPath)}");

static string BuildRecommendation(
    IReadOnlyCollection<TargetReport> targets,
    IReadOnlyCollection<Dictionary<string, object?>> sequences,
    IReadOnlyCollection<Dictionary<string, object?>> candidateColumns,
    IReadOnlyCollection<Dictionary<string, object?>> modules)
{
    var hasSqlGenerator = sequences.Count > 0 ||
                          targets.Any(target => target.IsIdentity || target.DefaultDefinition is not null || target.Triggers.Count > 0) ||
                          modules.Count > 0;

    if (hasSqlGenerator)
        return "Hay mecanismos SQL que deben revisarse antes de definir la estrategia de IDs.";

    var strongCandidates = candidateColumns.Where(row =>
    {
        var table = Value(row, "TableName");
        var column = Value(row, "ColumnName");
        return table.Contains("CONSEC", StringComparison.OrdinalIgnoreCase) ||
               table.Contains("FOLIO", StringComparison.OrdinalIgnoreCase) ||
               column.Contains("CONSECUTIVO", StringComparison.OrdinalIgnoreCase) ||
               column.Contains("ULTIMO", StringComparison.OrdinalIgnoreCase) ||
               column.Contains("SIGUIENTE", StringComparison.OrdinalIgnoreCase) ||
               column.Contains("CONTADOR", StringComparison.OrdinalIgnoreCase);
    }).ToList();

    return strongCandidates.Count > 0
        ? "No hay generación automática en las tablas objetivo, pero existen columnas candidatas que deben inspeccionarse."
        : "No se encontró generación automática ni contador SQL evidente. La hipótesis principal es asignación desde la aplicación mediante MAX(ID)+1; debe implementarse con transacción SERIALIZABLE y bloqueos UPDLOCK/HOLDLOCK.";
}

static void PrintSection(
    string title,
    IReadOnlyCollection<Dictionary<string, object?>> rows,
    Func<Dictionary<string, object?>, string> formatter)
{
    Console.WriteLine();
    Console.WriteLine(title);

    if (rows.Count == 0)
    {
        Console.WriteLine("  Ninguno");
        return;
    }

    foreach (var row in rows)
        Console.WriteLine($"  {formatter(row)}");
}

static string Value(IReadOnlyDictionary<string, object?> row, string column) =>
    row.TryGetValue(column, out var value) && value is not null
        ? Convert.ToString(value) ?? "NULL"
        : "NULL";

static async Task<TargetReport> ReadTargetMetadataAsync(SqlConnection connection, Target target)
{
    const string metadataSql = """
        SELECT c.is_identity,
               dc.definition,
               CAST(ep.value AS nvarchar(4000)) AS Description
        FROM sys.tables t
        INNER JOIN sys.columns c ON c.object_id = t.object_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.extended_properties ep
          ON ep.major_id = t.object_id
         AND ep.minor_id = c.column_id
         AND ep.name = 'MS_Description'
        WHERE t.name = @TableName AND c.name = @ColumnName;
        """;

    bool isIdentity;
    string? defaultDefinition;
    string? description;

    await using (var command = new SqlCommand(metadataSql, connection))
    {
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = target.TableName;
        command.Parameters.Add("@ColumnName", SqlDbType.NVarChar, 128).Value = target.ColumnName;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"No existe {target.TableName}.{target.ColumnName}.");

        isIdentity = reader.GetBoolean(0);
        defaultDefinition = reader.IsDBNull(1) ? null : reader.GetString(1);
        description = reader.IsDBNull(2) ? null : reader.GetString(2);
    }

    var fullName = $"[dbo].[{target.TableName}]";
    var rowCount = await ExecuteLongAsync(connection, $"SELECT COUNT_BIG(*) FROM {fullName};");
    var maximum = await ExecuteStringAsync(
        connection,
        $"SELECT CONVERT(nvarchar(100), MAX([{target.ColumnName}])) FROM {fullName};") ?? "NULL";

    const string triggerSql = """
        SELECT tr.name AS TriggerName, tr.is_disabled AS IsDisabled,
               LEFT(m.definition, 4000) AS Definition
        FROM sys.triggers tr
        INNER JOIN sys.tables t ON t.object_id = tr.parent_id
        LEFT JOIN sys.sql_modules m ON m.object_id = tr.object_id
        WHERE t.name = @TableName
        ORDER BY tr.name;
        """;

    var triggers = new List<Dictionary<string, object?>>();
    await using (var command = new SqlCommand(triggerSql, connection))
    {
        command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = target.TableName;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            triggers.Add(new Dictionary<string, object?>
            {
                ["TriggerName"] = reader.GetString(0),
                ["IsDisabled"] = reader.GetBoolean(1),
                ["Definition"] = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }
    }

    return new TargetReport(
        target.TableName,
        target.ColumnName,
        rowCount,
        maximum,
        isIdentity,
        defaultDefinition,
        description,
        triggers);
}

static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(SqlConnection connection, string sql)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, object?>>();

    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
            row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        rows.Add(row);
    }

    return rows;
}

static async Task<long> ExecuteLongAsync(SqlConnection connection, string sql)
{
    await using var command = new SqlCommand(sql, connection);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<string?> ExecuteStringAsync(SqlConnection connection, string sql)
{
    await using var command = new SqlCommand(sql, connection);
    var value = await command.ExecuteScalarAsync();
    return value is null or DBNull ? null : Convert.ToString(value);
}

static string? GetOption(string[] values, string name)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

internal sealed record Target(string TableName, string ColumnName);

internal sealed record TargetReport(
    string TableName,
    string ColumnName,
    long RowCount,
    string MaximumValue,
    bool IsIdentity,
    string? DefaultDefinition,
    string? Description,
    IReadOnlyCollection<Dictionary<string, object?>> Triggers);

internal sealed record IdGenerationReport(
    string DatabaseName,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<TargetReport> Targets,
    IReadOnlyCollection<Dictionary<string, object?>> Sequences,
    IReadOnlyCollection<Dictionary<string, object?>> CandidateCounterColumns,
    IReadOnlyCollection<Dictionary<string, object?>> SqlModules,
    string Recommendation);