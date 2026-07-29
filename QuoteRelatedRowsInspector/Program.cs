using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string Variable = "COMMERCIAL_PREMIUM_CONNECTION";

var connectionString = Environment.GetEnvironmentVariable(Variable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"Falta la variable de entorno {Variable}.");
    return;
}

var documentId = GetLong(args, "--document-id", 8297);
var output = GetOption(args, "--output") ?? $"quote-related-{documentId}.json";
var top = (int)GetLong(args, "--top", 20);

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

var document = await ReadRowsAsync(connection,
    "SELECT * FROM dbo.admDocumentos WHERE CIDDOCUMENTO = @DocumentId;",
    new SqlParameter("@DocumentId", SqlDbType.BigInt) { Value = documentId });

if (document.Count == 0)
    throw new InvalidOperationException($"No existe la cotización {documentId}.");

var movements = await ReadRowsAsync(connection,
    "SELECT * FROM dbo.admMovimientos WHERE CIDDOCUMENTO = @DocumentId ORDER BY CIDMOVIMIENTO;",
    new SqlParameter("@DocumentId", SqlDbType.BigInt) { Value = documentId });

var addressRelation = await FindExistingColumnAsync(connection, "admDomicilios", "CIDDOCUMENTO", "CIDCATALOGO");
var addresses = addressRelation is null
    ? []
    : await ReadRowsAsync(connection,
        $"SELECT * FROM dbo.admDomicilios WHERE [{addressRelation}] = @DocumentId ORDER BY CIDDIRECCION;",
        new SqlParameter("@DocumentId", SqlDbType.BigInt) { Value = documentId });

var accumulatedSchema = await ReadSchemaAsync(connection, "admAcumulados");
var logSchema = await ReadSchemaAsync(connection, "admBitacoras");

var latestAccumulated = await ReadRowsAsync(connection,
    $"SELECT TOP ({top}) * FROM dbo.admAcumulados ORDER BY CIDACUMULADO DESC;");
var latestLogs = await ReadRowsAsync(connection,
    $"SELECT TOP ({top}) * FROM dbo.admBitacoras ORDER BY IDBITACORA DESC;");

var movementIds = movements
    .Select(row => ReadLong(row, "CIDMOVIMIENTO"))
    .Where(value => value.HasValue)
    .Select(value => value!.Value)
    .ToArray();

var identifiers = new HashSet<long>(movementIds) { documentId };
var accumulatedMatches = latestAccumulated
    .Where(row => HasNumericMatch(row, identifiers))
    .ToList();
var logMatches = latestLogs
    .Where(row => HasNumericMatch(row, identifiers))
    .ToList();

var report = new
{
    Database = connection.Database,
    CreatedAt = DateTimeOffset.Now,
    DocumentId = documentId,
    MovementIds = movementIds,
    AddressRelationColumn = addressRelation,
    Document = document.Single(),
    Movements = movements,
    Addresses = addresses,
    AdmAcumuladosSchema = accumulatedSchema,
    AdmBitacorasSchema = logSchema,
    LatestAdmAcumulados = latestAccumulated,
    LatestAdmBitacoras = latestLogs,
    NumericMatchesInAdmAcumulados = accumulatedMatches,
    NumericMatchesInAdmBitacoras = logMatches
};

await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Cotización: {documentId}");
Console.WriteLine($"Movimientos: {movements.Count} ({string.Join(", ", movementIds)})");
Console.WriteLine($"Domicilios: {addresses.Count} por {addressRelation ?? "sin relación detectada"}");
Console.WriteLine($"Últimos acumulados inspeccionados: {latestAccumulated.Count}");
Console.WriteLine($"Coincidencias numéricas en acumulados: {accumulatedMatches.Count}");
Console.WriteLine($"Últimas bitácoras inspeccionadas: {latestLogs.Count}");
Console.WriteLine($"Coincidencias numéricas en bitácoras: {logMatches.Count}");
Console.WriteLine($"Reporte: {Path.GetFullPath(output)}");

static bool HasNumericMatch(IReadOnlyDictionary<string, object?> row, HashSet<long> ids)
{
    foreach (var value in row.Values)
    {
        if (value is null) continue;
        try
        {
            var number = Convert.ToInt64(value);
            if (ids.Contains(number)) return true;
        }
        catch
        {
            // Ignorar valores no enteros.
        }
    }

    return false;
}

static long? ReadLong(IReadOnlyDictionary<string, object?> row, string column)
{
    if (!row.TryGetValue(column, out var value) || value is null) return null;
    try { return Convert.ToInt64(value); }
    catch { return null; }
}

static async Task<string?> FindExistingColumnAsync(SqlConnection connection, string table, params string[] candidates)
{
    foreach (var candidate in candidates)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            WHERE t.name = @Table AND c.name = @Column;
            """, connection);
        command.Parameters.AddWithValue("@Table", table);
        command.Parameters.AddWithValue("@Column", candidate);
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return candidate;
    }
    return null;
}

static async Task<List<Dictionary<string, object?>>> ReadSchemaAsync(SqlConnection connection, string table)
{
    return await ReadRowsAsync(connection, """
        SELECT c.column_id AS ColumnId, c.name AS ColumnName, ty.name AS DataType,
               c.max_length AS MaxLength, c.precision AS Precision, c.scale AS Scale,
               c.is_nullable AS IsNullable
        FROM sys.tables t
        JOIN sys.columns c ON c.object_id = t.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE t.name = @Table
        ORDER BY c.column_id;
        """, new SqlParameter("@Table", SqlDbType.NVarChar, 128) { Value = table });
}

static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(
    SqlConnection connection,
    string sql,
    params SqlParameter[] parameters)
{
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
    command.Parameters.AddRange(parameters);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i));
        rows.Add(row);
    }
    return rows;
}

static object? Normalize(object? value) => value switch
{
    byte[] bytes => Convert.ToHexString(bytes),
    DateTime date => date.ToString("O"),
    DateTimeOffset date => date.ToString("O"),
    _ => value
};

static string? GetOption(string[] values, string name)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static long GetLong(string[] values, string name, long fallback)
{
    var raw = GetOption(values, name);
    return raw is null ? fallback : long.Parse(raw);
}
