using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string ConnectionVariable = "COMMERCIAL_PREMIUM_CONNECTION";

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
            await CaptureAsync(args.Skip(1).ToArray());
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
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}

static async Task CaptureAsync(string[] commandArgs)
{
    var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException($"Falta la variable de entorno {ConnectionVariable}.");

    var documentId = GetRequiredLong(commandArgs, "--document-id");
    var outputPath = GetOption(commandArgs, "--output") ?? $"quote-{documentId}.json";

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    var documentKey = await ResolveRequiredColumnAsync(connection, "admDocumentos", "CIDDOCUMENTO");
    var document = await ReadSingleByColumnAsync(connection, "admDocumentos", documentKey, documentId)
        ?? throw new InvalidOperationException($"No existe admDocumentos.{documentKey}={documentId}.");

    var movementLink = await ResolveRequiredColumnAsync(connection, "admMovimientos", "CIDDOCUMENTO");
    var movements = await ReadManyByColumnAsync(connection, "admMovimientos", movementLink, documentId, "CIDMOVIMIENTO");

    var addressLink = await ResolveOptionalColumnAsync(
        connection,
        "admDomicilios",
        "CIDDOCUMENTO",
        "CIDCATALOGO",
        "CIDOWNER");

    var addresses = addressLink is null
        ? []
        : await ReadManyByColumnAsync(connection, "admDomicilios", addressLink, documentId, "CIDDIRECCION");

    Dictionary<string, object?>? concept = null;
    if (TryReadInt64(document, "CIDCONCEPTODOCUMENTO", out var conceptId))
    {
        var conceptKey = await ResolveRequiredColumnAsync(connection, "admConceptos", "CIDCONCEPTODOCUMENTO");
        concept = await ReadSingleByColumnAsync(connection, "admConceptos", conceptKey, conceptId);
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
        addressLink,
        schemas);

    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(package, JsonOptions()));

    Console.WriteLine($"Cotización capturada: {documentId}");
    Console.WriteLine($"Movimientos: {movements.Count}");
    Console.WriteLine($"Domicilios: {addresses.Count}");
    Console.WriteLine($"Columna de relación en admDomicilios: {addressLink ?? "no encontrada"}");
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
        ?? throw new InvalidOperationException("No fue posible leer el paquete.");

    var sql = new StringBuilder();
    sql.AppendLine("/* PLANTILLA DE INVESTIGACIÓN. SOLO adMIDA_PRUEBAS. */");
    sql.AppendLine("SET XACT_ABORT ON;");
    sql.AppendLine("BEGIN TRAN;");
    sql.AppendLine("DECLARE @NuevoDocumentoId int = 0; -- PENDIENTE");
    sql.AppendLine("DECLARE @NuevoFolio int = 0; -- PENDIENTE");
    sql.AppendLine("DECLARE @NuevoGuid uniqueidentifier = NEWID();");
    sql.AppendLine();

    AppendInsert(sql, "admDocumentos", package.Document);
    foreach (var movement in package.Movements)
        AppendInsert(sql, "admMovimientos", movement);
    foreach (var address in package.Addresses)
        AppendInsert(sql, "admDomicilios", address);

    sql.AppendLine("-- PENDIENTE: IDs seguros, acumulados, bitácora y actualización bloqueada de folio.");
    sql.AppendLine("ROLLBACK;");

    await File.WriteAllTextAsync(outputPath, sql.ToString());
    Console.WriteLine($"Plantilla guardada en: {Path.GetFullPath(outputPath)}");
}

static void AppendInsert(StringBuilder sql, string table, IReadOnlyDictionary<string, object?> row)
{
    var columns = row.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    sql.AppendLine($"-- dbo.{table}");
    sql.AppendLine($"INSERT INTO dbo.[{table}] ({string.Join(", ", columns.Select(c => $"[{c}]"))})");
    sql.AppendLine("VALUES (");
    sql.AppendLine(string.Join(",\n", columns.Select(c => $"    {TemplateValue(c, row[c])}")));
    sql.AppendLine(");");
    sql.AppendLine();
}

static string TemplateValue(string column, object? value)
{
    if (column.Equals("CIDDOCUMENTO", StringComparison.OrdinalIgnoreCase)) return "@NuevoDocumentoId";
    if (column.Equals("CFOLIO", StringComparison.OrdinalIgnoreCase)) return "@NuevoFolio";
    if (column.Equals("CGUIDDOCUMENTO", StringComparison.OrdinalIgnoreCase)) return "@NuevoGuid";
    if (column.Equals("CTIMESTAMP", StringComparison.OrdinalIgnoreCase)) return "SYSDATETIME()";
    if (column is "CIDMOVIMIENTO" or "CIDDIRECCION") return $"/* GENERAR {column} */ NULL";
    return ToSqlLiteral(value);
}

static async Task<string> ResolveRequiredColumnAsync(SqlConnection connection, string table, params string[] candidates) =>
    await ResolveOptionalColumnAsync(connection, table, candidates)
    ?? throw new InvalidOperationException($"No se encontró en dbo.{table} ninguna columna esperada: {string.Join(", ", candidates)}.");

static async Task<string?> ResolveOptionalColumnAsync(SqlConnection connection, string table, params string[] candidates)
{
    const string sql = """
        SELECT c.name
        FROM sys.tables t
        INNER JOIN sys.columns c ON c.object_id = t.object_id
        WHERE t.name = @TableName;
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = table;
    await using var reader = await command.ExecuteReaderAsync();
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
    return candidates.FirstOrDefault(columns.Contains);
}

static async Task<Dictionary<string, object?>?> ReadSingleByColumnAsync(SqlConnection connection, string table, string column, long value)
{
    var rows = await ReadManyByColumnAsync(connection, table, column, value, null);
    return rows.FirstOrDefault();
}

static async Task<List<Dictionary<string, object?>>> ReadManyByColumnAsync(SqlConnection connection, string table, string column, long value, string? orderBy)
{
    var order = string.IsNullOrWhiteSpace(orderBy) ? string.Empty : $" ORDER BY [{orderBy}]";
    var sql = $"SELECT * FROM dbo.[{table}] WHERE [{column}] = @Value{order};";
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    command.Parameters.Add("@Value", SqlDbType.BigInt).Value = value;
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
        rows.Add(row);
    }
    return rows;
}

static async Task<IReadOnlyCollection<ColumnDefinition>> ReadSchemaAsync(SqlConnection connection, string table)
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
    command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = table;
    await using var reader = await command.ExecuteReaderAsync();
    var result = new List<ColumnDefinition>();
    while (await reader.ReadAsync())
        result.Add(new ColumnDefinition(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
    return result;
}

static bool TryReadInt64(IReadOnlyDictionary<string, object?> row, string column, out long value)
{
    value = 0;
    return row.TryGetValue(column, out var raw) && raw is not null && long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out value);
}

static object? Normalize(object? value) => value switch
{
    null or DBNull => null,
    byte[] bytes => Convert.ToHexString(bytes),
    DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
    DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
    TimeSpan time => time.ToString(),
    _ => value
};

static string ToSqlLiteral(object? value)
{
    if (value is null) return "NULL";
    if (value is JsonElement e)
        return e.ValueKind switch
        {
            JsonValueKind.Null => "NULL",
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => $"N'{(e.ToString() ?? string.Empty).Replace("'", "''")}'"
        };
    return value switch
    {
        bool b => b ? "1" : "0",
        IFormattable f when value is not string => f.ToString(null, CultureInfo.InvariantCulture) ?? "NULL",
        _ => $"N'{value.ToString()?.Replace("'", "''")}'"
    };
}

static string? GetOption(string[] values, string option)
{
    var index = Array.FindIndex(values, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static string GetRequiredOption(string[] values, string option) =>
    GetOption(values, option) ?? throw new ArgumentException($"Falta {option}.");

static long GetRequiredLong(string[] values, string option) =>
    long.TryParse(GetRequiredOption(values, option), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result
        : throw new ArgumentException($"{option} debe ser numérico.");

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

static void PrintHelp() => Console.WriteLine("""
    QuoteTemplateGenerator

    capture --document-id 8297 --output quote-8297.json
    generate-template --input quote-8297.json --output quote-template-8297.sql
    """);

internal sealed record QuotePackage(
    string DatabaseName,
    DateTimeOffset CapturedAt,
    long DocumentId,
    IReadOnlyDictionary<string, object?> Document,
    IReadOnlyCollection<Dictionary<string, object?>> Movements,
    IReadOnlyCollection<Dictionary<string, object?>> Addresses,
    IReadOnlyDictionary<string, object?>? Concept,
    string? AddressDocumentColumn,
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
