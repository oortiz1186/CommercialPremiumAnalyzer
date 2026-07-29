using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string connectionVariable = "COMMERCIAL_PREMIUM_CONNECTION";

if (args.Length == 0)
{
    PrintHelp();
    return;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "capture":
            await CaptureQuoteAsync(args.Skip(1).ToArray());
            break;
        case "generate-template":
            await GenerateTemplateAsync(args.Skip(1).ToArray());
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

static async Task CaptureQuoteAsync(string[] commandArgs)
{
    var connectionString = Environment.GetEnvironmentVariable(connectionVariable);
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException($"Falta la variable de entorno {connectionVariable}.");

    var documentId = GetRequiredLong(commandArgs, "--document-id");
    var outputPath = GetOption(commandArgs, "--output") ?? $"quote-{documentId}.json";

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    var document = await ReadSingleAsync(
        connection,
        "SELECT * FROM dbo.admDocumentos WHERE CIDDOCUMENTO = @Id;",
        documentId);

    if (document is null)
        throw new InvalidOperationException($"No existe admDocumentos.CIDDOCUMENTO={documentId}.");

    var movements = await ReadManyAsync(
        connection,
        "SELECT * FROM dbo.admMovimientos WHERE CIDDOCUMENTO = @Id ORDER BY CIDMOVIMIENTO;",
        documentId);

    var addresses = await ReadManyAsync(
        connection,
        "SELECT * FROM dbo.admDomicilios WHERE CIDDOCUMENTO = @Id ORDER BY CIDDIRECCION;",
        documentId);

    Dictionary<string, object?>? concept = null;
    if (TryReadInt64(document, "CIDCONCEPTODOCUMENTO", out var conceptId))
    {
        concept = await ReadSingleAsync(
            connection,
            "SELECT * FROM dbo.admConceptos WHERE CIDCONCEPTODOCUMENTO = @Id;",
            conceptId);
    }

    var schemas = new Dictionary<string, IReadOnlyCollection<ColumnDefinition>>(StringComparer.OrdinalIgnoreCase)
    {
        ["admDocumentos"] = await ReadSchemaAsync(connection, "admDocumentos"),
        ["admMovimientos"] = await ReadSchemaAsync(connection, "admMovimientos"),
        ["admDomicilios"] = await ReadSchemaAsync(connection, "admDomicilios"),
        ["admConceptos"] = await ReadSchemaAsync(connection, "admConceptos")
    };

    var package = new QuotePackage(
        connection.Database,
        DateTimeOffset.Now,
        documentId,
        document,
        movements,
        addresses,
        concept,
        schemas);

    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(package, JsonOptions()));

    Console.WriteLine($"Cotización capturada: {documentId}");
    Console.WriteLine($"Movimientos: {movements.Count}");
    Console.WriteLine($"Domicilios: {addresses.Count}");
    Console.WriteLine($"Concepto encontrado: {(concept is null ? "no" : "sí")}");
    Console.WriteLine($"Paquete guardado en: {Path.GetFullPath(outputPath)}");
}

static async Task GenerateTemplateAsync(string[] commandArgs)
{
    var inputPath = GetRequiredOption(commandArgs, "--input");
    var outputPath = GetOption(commandArgs, "--output") ?? "quote-template.sql";

    if (!File.Exists(inputPath))
        throw new FileNotFoundException($"No existe el archivo {inputPath}.");

    var package = JsonSerializer.Deserialize<QuotePackage>(await File.ReadAllTextAsync(inputPath), JsonOptions())
        ?? throw new InvalidOperationException("No fue posible leer el paquete de cotización.");

    var sql = new StringBuilder();
    sql.AppendLine("/*");
    sql.AppendLine("  PLANTILLA DE INVESTIGACIÓN - NO EJECUTAR EN PRODUCCIÓN");
    sql.AppendLine("  Base permitida para pruebas: adMIDA_PRUEBAS");
    sql.AppendLine("  La asignación segura de IDs y la relación de admAcumulados/admBitacoras aún deben validarse.");
    sql.AppendLine("*/");
    sql.AppendLine();
    sql.AppendLine("SET XACT_ABORT ON;");
    sql.AppendLine("BEGIN TRAN;");
    sql.AppendLine();
    sql.AppendLine("DECLARE @NuevoDocumentoId int = /* PENDIENTE: estrategia de consecutivo */ 0;");
    sql.AppendLine("DECLARE @NuevoFolio int = /* leer y bloquear admConceptos.CNOFOLIO */ 0;");
    sql.AppendLine("DECLARE @NuevoGuid uniqueidentifier = NEWID();");
    sql.AppendLine();

    AppendInsertTemplate(sql, "admDocumentos", package.Document, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CIDDOCUMENTO", "CFOLIO", "CGUIDDOCUMENTO", "CTIMESTAMP"
    });

    foreach (var movement in package.Movements)
    {
        AppendInsertTemplate(sql, "admMovimientos", movement, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CIDMOVIMIENTO", "CIDDOCUMENTO", "CTIMESTAMP"
        });
    }

    foreach (var address in package.Addresses)
    {
        AppendInsertTemplate(sql, "admDomicilios", address, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CIDDIRECCION", "CIDDOCUMENTO", "CTIMESTAMP"
        });
    }

    sql.AppendLine("-- El folio debe actualizarse dentro de la misma transacción y con bloqueo.");
    sql.AppendLine("UPDATE dbo.admConceptos");
    sql.AppendLine("SET CNOFOLIO = @NuevoFolio");
    sql.AppendLine("WHERE CIDCONCEPTODOCUMENTO = @ConceptoId;");
    sql.AppendLine();
    sql.AppendLine("-- PENDIENTE antes de habilitar escritura:");
    sql.AppendLine("-- 1. Confirmar generación de CIDDOCUMENTO, CIDMOVIMIENTO y CIDDIRECCION.");
    sql.AppendLine("-- 2. Reproducir admAcumulados.");
    sql.AppendLine("-- 3. Reproducir admBitacoras.");
    sql.AppendLine("-- 4. Validar apertura, edición y eliminación desde Comercial Premium.");
    sql.AppendLine();
    sql.AppendLine("ROLLBACK; -- La plantilla nunca confirma cambios.");

    await File.WriteAllTextAsync(outputPath, sql.ToString());
    Console.WriteLine($"Plantilla guardada en: {Path.GetFullPath(outputPath)}");
}

static void AppendInsertTemplate(
    StringBuilder sql,
    string tableName,
    IReadOnlyDictionary<string, object?> row,
    HashSet<string> generatedColumns)
{
    var columns = row.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    sql.AppendLine($"-- Plantilla basada en un registro real de dbo.{tableName}");
    sql.AppendLine($"INSERT INTO dbo.{tableName}");
    sql.AppendLine("(");
    sql.AppendLine(string.Join(",\n", columns.Select(column => $"    [{column}]")));
    sql.AppendLine(")");
    sql.AppendLine("VALUES");
    sql.AppendLine("(");

    var values = columns.Select(column =>
    {
        if (column.Equals("CIDDOCUMENTO", StringComparison.OrdinalIgnoreCase))
            return "    @NuevoDocumentoId";
        if (column.Equals("CFOLIO", StringComparison.OrdinalIgnoreCase))
            return "    @NuevoFolio";
        if (column.Equals("CGUIDDOCUMENTO", StringComparison.OrdinalIgnoreCase))
            return "    @NuevoGuid";
        if (column.Equals("CTIMESTAMP", StringComparison.OrdinalIgnoreCase))
            return "    SYSDATETIME()";
        if (generatedColumns.Contains(column))
            return $"    /* GENERAR {column} */ NULL";

        return $"    {ToSqlLiteral(row[column])}";
    });

    sql.AppendLine(string.Join(",\n", values));
    sql.AppendLine(");");
    sql.AppendLine();
}

static string ToSqlLiteral(object? value)
{
    if (value is null)
        return "NULL";

    if (value is JsonElement element)
        return JsonElementToSql(element);

    return value switch
    {
        string text => $"N'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        bool boolean => boolean ? "1" : "0",
        DateTime date => $"'{date:yyyy-MM-ddTHH:mm:ss.fff}'",
        DateTimeOffset date => $"'{date:yyyy-MM-ddTHH:mm:ss.fffzzz}'",
        Guid guid => $"'{guid}'",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "NULL",
        _ => $"N'{value.ToString()?.Replace("'", "''", StringComparison.Ordinal)}'"
    };
}

static string JsonElementToSql(JsonElement element) => element.ValueKind switch
{
    JsonValueKind.Null or JsonValueKind.Undefined => "NULL",
    JsonValueKind.String => $"N'{(element.GetString() ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}'",
    JsonValueKind.True => "1",
    JsonValueKind.False => "0",
    JsonValueKind.Number => element.GetRawText(),
    _ => $"N'{element.GetRawText().Replace("'", "''", StringComparison.Ordinal)}'"
};

static async Task<Dictionary<string, object?>?> ReadSingleAsync(
    SqlConnection connection,
    string sql,
    long id)
{
    var rows = await ReadManyAsync(connection, sql, id);
    return rows.FirstOrDefault();
}

static async Task<List<Dictionary<string, object?>>> ReadManyAsync(
    SqlConnection connection,
    string sql,
    long id)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    command.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
    await using var reader = await command.ExecuteReaderAsync();

    var rows = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
            row[reader.GetName(index)] = Normalize(reader.IsDBNull(index) ? null : reader.GetValue(index));
        rows.Add(row);
    }

    return rows;
}

static async Task<IReadOnlyCollection<ColumnDefinition>> ReadSchemaAsync(
    SqlConnection connection,
    string tableName)
{
    const string sql = """
        SELECT c.column_id, c.name, ty.name, c.max_length, c.precision, c.scale,
               c.is_nullable, c.is_identity, dc.definition
        FROM sys.tables t
        INNER JOIN sys.columns c ON c.object_id = t.object_id
        INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        WHERE t.name = @TableName
        ORDER BY c.column_id;
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = tableName;
    await using var reader = await command.ExecuteReaderAsync();

    var columns = new List<ColumnDefinition>();
    while (await reader.ReadAsync())
    {
        columns.Add(new ColumnDefinition(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt16(3),
            reader.GetByte(4),
            reader.GetByte(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8)));
    }

    return columns;
}

static bool TryReadInt64(IReadOnlyDictionary<string, object?> row, string column, out long value)
{
    value = 0;
    if (!row.TryGetValue(column, out var raw) || raw is null)
        return false;

    if (raw is JsonElement element && element.ValueKind == JsonValueKind.Number)
        return element.TryGetInt64(out value);

    return long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out value);
}

static object? Normalize(object? value) => value switch
{
    null => null,
    DBNull => null,
    byte[] bytes => Convert.ToHexString(bytes),
    DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
    DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
    TimeSpan time => time.ToString(),
    _ => value
};

static string? GetOption(string[] args, string option)
{
    var index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string GetRequiredOption(string[] args, string option) =>
    GetOption(args, option) ?? throw new ArgumentException($"Falta {option}.");

static long GetRequiredLong(string[] args, string option)
{
    var value = GetRequiredOption(args, option);
    return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result
        : throw new ArgumentException($"{option} debe ser numérico.");
}

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
};

static void PrintHelp()
{
    Console.WriteLine("""
        QuoteTemplateGenerator

        Capturar una cotización existente:
          dotnet run --project QuoteTemplateGenerator -- capture
            --document-id 8297
            --output quote-8297.json

        Generar plantilla SQL no ejecutable:
          dotnet run --project QuoteTemplateGenerator -- generate-template
            --input quote-8297.json
            --output quote-template.sql
        """);
}

internal sealed record QuotePackage(
    string DatabaseName,
    DateTimeOffset CapturedAt,
    long DocumentId,
    IReadOnlyDictionary<string, object?> Document,
    IReadOnlyCollection<Dictionary<string, object?>> Movements,
    IReadOnlyCollection<Dictionary<string, object?>> Addresses,
    IReadOnlyDictionary<string, object?>? Concept,
    IReadOnlyDictionary<string, IReadOnlyCollection<ColumnDefinition>> Schemas);

internal sealed record ColumnDefinition(
    int Ordinal,
    string Name,
    string SqlType,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    bool IsIdentity,
    string? DefaultDefinition);
