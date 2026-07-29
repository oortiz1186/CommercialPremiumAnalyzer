using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

const string connectionVariable = "COMMERCIAL_PREMIUM_CONNECTION";
const string allowedDatabase = "adMIDA_PRUEBAS";

try
{
    var inputPath = GetRequiredOption(args, "--input");
    if (!File.Exists(inputPath))
        throw new FileNotFoundException($"No existe el archivo {inputPath}.");

    var connectionString = Environment.GetEnvironmentVariable(connectionVariable);
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException($"Falta la variable de entorno {connectionVariable}.");

    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath));
    var root = json.RootElement;
    var documentRow = GetRequiredProperty(root, "Document");
    var movements = GetRequiredProperty(root, "Movements").EnumerateArray().ToArray();
    var addresses = GetRequiredProperty(root, "Addresses").EnumerateArray().ToArray();

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    if (!connection.Database.Equals(allowedDatabase, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Ejecución bloqueada. Base actual: {connection.Database}. Solo se permite {allowedDatabase}.");

    var conceptId = ReadInt64(documentRow, "CIDCONCEPTODOCUMENTO");

    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
    try
    {
        await AcquireApplicationLockAsync(connection, transaction);

        var newDocumentId = await NextIdAsync(connection, transaction, "admDocumentos", "CIDDOCUMENTO");
        var firstMovementId = await NextIdAsync(connection, transaction, "admMovimientos", "CIDMOVIMIENTO");
        var firstAddressId = await NextIdAsync(connection, transaction, "admDomicilios", "CIDDIRECCION");
        var newFolio = await ReadNextFolioAsync(connection, transaction, conceptId);
        var newGuid = Guid.NewGuid();

        var documentOverrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CIDDOCUMENTO"] = newDocumentId,
            ["CFOLIO"] = newFolio,
            ["CGUIDDOCUMENTO"] = newGuid
        };

        await InsertCloneAsync(connection, transaction, "admDocumentos", documentRow, documentOverrides);

        for (var index = 0; index < movements.Length; index++)
        {
            var overrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CIDMOVIMIENTO"] = firstMovementId + index,
                ["CIDDOCUMENTO"] = newDocumentId
            };
            await InsertCloneAsync(connection, transaction, "admMovimientos", movements[index], overrides);
        }

        for (var index = 0; index < addresses.Length; index++)
        {
            var overrides = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["CIDDIRECCION"] = firstAddressId + index,
                ["CIDCATALOGO"] = newDocumentId
            };
            await InsertCloneAsync(connection, transaction, "admDomicilios", addresses[index], overrides);
        }

        await using (var update = new SqlCommand("""
            UPDATE dbo.admConceptos
            SET CNOFOLIO = @Folio
            WHERE CIDCONCEPTODOCUMENTO = @ConceptoId;
            """, connection, transaction))
        {
            update.Parameters.Add("@Folio", SqlDbType.Float).Value = newFolio;
            update.Parameters.Add("@ConceptoId", SqlDbType.BigInt).Value = conceptId;
            if (await update.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("No se actualizó exactamente un concepto.");
        }

        var documentCount = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM dbo.admDocumentos WHERE CIDDOCUMENTO = @Id;", newDocumentId);
        var movementCount = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM dbo.admMovimientos WHERE CIDDOCUMENTO = @Id;", newDocumentId);
        var addressCount = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM dbo.admDomicilios WHERE CIDCATALOGO = @Id;", newDocumentId);

        Console.WriteLine("WRITER DE PRUEBA EJECUTADO DENTRO DE TRANSACCIÓN");
        Console.WriteLine($"  Base: {connection.Database}");
        Console.WriteLine($"  Documento nuevo: {newDocumentId}");
        Console.WriteLine($"  Folio nuevo: {newFolio}");
        Console.WriteLine($"  GUID nuevo: {newGuid}");
        Console.WriteLine($"  Documentos validados: {documentCount}");
        Console.WriteLine($"  Movimientos validados: {movementCount}");
        Console.WriteLine($"  Domicilios validados: {addressCount}");

        if (documentCount != 1 || movementCount != movements.Length || addressCount != addresses.Length)
            throw new InvalidOperationException("La validación interna no coincide con el paquete de origen.");

        Console.WriteLine();
        Console.WriteLine("ROLLBACK obligatorio: no se guardó ningún cambio.");
        await transaction.RollbackAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.ToString());
    Environment.ExitCode = 1;
}

static async Task InsertCloneAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    string tableName,
    JsonElement source,
    IReadOnlyDictionary<string, object?> overrides)
{
    var schema = await ReadWritableSchemaAsync(connection, transaction, tableName);
    var sourceProperties = source.EnumerateObject()
        .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);

    var columns = schema
        .Where(column => overrides.ContainsKey(column.Name) || sourceProperties.ContainsKey(column.Name))
        .ToArray();

    if (columns.Length == 0)
        throw new InvalidOperationException($"No se encontraron columnas insertables para {tableName}.");

    var columnSql = string.Join(", ", columns.Select(column => $"[{column.Name}]"));
    var parameterSql = string.Join(", ", columns.Select((_, index) => $"@p{index}"));
    var sql = $"INSERT INTO dbo.[{tableName}] ({columnSql}) VALUES ({parameterSql});";

    await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
    for (var index = 0; index < columns.Length; index++)
    {
        var column = columns[index];
        object? value;
        if (!overrides.TryGetValue(column.Name, out value))
            value = ConvertJsonValue(sourceProperties[column.Name], column.DataType);

        var parameter = command.Parameters.Add($"@p{index}", column.SqlDbType);
        if (column.Size > 0 && column.Size <= 8000)
            parameter.Size = column.Size;
        parameter.Value = value ?? DBNull.Value;
    }

    if (await command.ExecuteNonQueryAsync() != 1)
        throw new InvalidOperationException($"No se insertó exactamente una fila en {tableName}.");
}

static async Task<List<WritableColumn>> ReadWritableSchemaAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    string tableName)
{
    const string sql = """
        SELECT c.name, ty.name, c.max_length
        FROM sys.tables t
        INNER JOIN sys.columns c ON c.object_id = t.object_id
        INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE t.name = @TableName
          AND c.is_identity = 0
          AND c.is_computed = 0
          AND ty.name NOT IN ('timestamp', 'rowversion')
        ORDER BY c.column_id;
        """;

    await using var command = new SqlCommand(sql, connection, transaction);
    command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = tableName;
    await using var reader = await command.ExecuteReaderAsync();

    var result = new List<WritableColumn>();
    while (await reader.ReadAsync())
    {
        var typeName = reader.GetString(1);
        result.Add(new WritableColumn(
            reader.GetString(0),
            typeName,
            ToSqlDbType(typeName),
            reader.GetInt16(2)));
    }

    return result;
}

static object? ConvertJsonValue(JsonElement value, string dataType)
{
    if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        return null;

    return dataType.ToLowerInvariant() switch
    {
        "int" => value.GetInt32(),
        "bigint" => value.GetInt64(),
        "smallint" => value.GetInt16(),
        "tinyint" => value.GetByte(),
        "bit" => value.ValueKind == JsonValueKind.True || value.GetInt32() != 0,
        "float" => value.GetDouble(),
        "real" => value.GetSingle(),
        "decimal" or "numeric" or "money" or "smallmoney" => value.GetDecimal(),
        "uniqueidentifier" => Guid.Parse(value.GetString()!),
        "date" or "datetime" or "datetime2" or "smalldatetime" => DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "datetimeoffset" => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "time" => TimeSpan.Parse(value.GetString()!, CultureInfo.InvariantCulture),
        "binary" or "varbinary" or "image" => Convert.FromHexString(value.GetString() ?? string.Empty),
        _ => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
    };
}

static SqlDbType ToSqlDbType(string typeName) => typeName.ToLowerInvariant() switch
{
    "int" => SqlDbType.Int,
    "bigint" => SqlDbType.BigInt,
    "smallint" => SqlDbType.SmallInt,
    "tinyint" => SqlDbType.TinyInt,
    "bit" => SqlDbType.Bit,
    "float" => SqlDbType.Float,
    "real" => SqlDbType.Real,
    "decimal" or "numeric" => SqlDbType.Decimal,
    "money" => SqlDbType.Money,
    "smallmoney" => SqlDbType.SmallMoney,
    "uniqueidentifier" => SqlDbType.UniqueIdentifier,
    "date" => SqlDbType.Date,
    "datetime" => SqlDbType.DateTime,
    "datetime2" => SqlDbType.DateTime2,
    "smalldatetime" => SqlDbType.SmallDateTime,
    "datetimeoffset" => SqlDbType.DateTimeOffset,
    "time" => SqlDbType.Time,
    "char" => SqlDbType.Char,
    "varchar" => SqlDbType.VarChar,
    "text" => SqlDbType.Text,
    "nchar" => SqlDbType.NChar,
    "nvarchar" => SqlDbType.NVarChar,
    "ntext" => SqlDbType.NText,
    "binary" => SqlDbType.Binary,
    "varbinary" => SqlDbType.VarBinary,
    "image" => SqlDbType.Image,
    _ => throw new NotSupportedException($"Tipo SQL no soportado: {typeName}")
};

static async Task AcquireApplicationLockAsync(SqlConnection connection, SqlTransaction transaction)
{
    await using var command = new SqlCommand("""
        DECLARE @Result int;
        EXEC @Result = sys.sp_getapplock
            @Resource = N'LicenciasMida:CommercialPremium:QuoteWriter',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 15000;
        SELECT @Result;
        """, connection, transaction);

    var result = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    if (result < 0)
        throw new InvalidOperationException($"No fue posible obtener el bloqueo de escritura. Código: {result}.");
}

static async Task<long> NextIdAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    string table,
    string column)
{
    var sql = $"SELECT ISNULL(MAX([{column}]), 0) + 1 FROM dbo.[{table}] WITH (UPDLOCK, HOLDLOCK);";
    await using var command = new SqlCommand(sql, connection, transaction);
    return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

static async Task<long> ReadNextFolioAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    long conceptId)
{
    await using var command = new SqlCommand("""
        SELECT CONVERT(bigint, CNOFOLIO) + 1
        FROM dbo.admConceptos WITH (UPDLOCK, HOLDLOCK)
        WHERE CIDCONCEPTODOCUMENTO = @ConceptoId;
        """, connection, transaction);
    command.Parameters.Add("@ConceptoId", SqlDbType.BigInt).Value = conceptId;
    var value = await command.ExecuteScalarAsync();
    return value is null or DBNull
        ? throw new InvalidOperationException($"No existe el concepto {conceptId}.")
        : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

static async Task<long> CountAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    string sql,
    long id)
{
    await using var command = new SqlCommand(sql, connection, transaction);
    command.Parameters.Add("@Id", SqlDbType.BigInt).Value = id;
    return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

static JsonElement GetRequiredProperty(JsonElement element, string propertyName)
{
    foreach (var property in element.EnumerateObject())
        if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            return property.Value;

    throw new InvalidOperationException($"El paquete no contiene {propertyName}.");
}

static long ReadInt64(JsonElement row, string columnName)
{
    var value = GetRequiredProperty(row, columnName);
    return value.ValueKind == JsonValueKind.Number
        ? value.GetInt64()
        : long.Parse(value.GetString()!, CultureInfo.InvariantCulture);
}

static string? GetOption(string[] values, string name)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static string GetRequiredOption(string[] values, string name) =>
    GetOption(values, name) ?? throw new ArgumentException($"Falta {name}.");

internal sealed record WritableColumn(
    string Name,
    string DataType,
    SqlDbType SqlDbType,
    int Size);
