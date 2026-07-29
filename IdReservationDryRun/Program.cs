using System.Data;
using Microsoft.Data.SqlClient;

const string ConnectionVariable = "COMMERCIAL_PREMIUM_CONNECTION";

var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"Falta la variable de entorno {ConnectionVariable}.");
    return;
}

var conceptId = GetIntOption(args, "--concept-id", 1);
var movementCount = GetIntOption(args, "--movements", 1);
var addressCount = GetIntOption(args, "--addresses", 2);
var accumulatedCount = GetIntOption(args, "--accumulated", 2);

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

if (!connection.Database.Equals("adMIDA_PRUEBAS", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Esta prueba solo puede ejecutarse en adMIDA_PRUEBAS.");

await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);

try
{
    var lockResult = await AcquireApplicationLockAsync(connection, transaction);
    if (lockResult < 0)
        throw new InvalidOperationException($"No fue posible adquirir el bloqueo de aplicación. Código: {lockResult}.");

    var documentId = await ReadNextIdAsync(connection, transaction, "admDocumentos", "CIDDOCUMENTO");
    var firstMovementId = await ReadNextIdAsync(connection, transaction, "admMovimientos", "CIDMOVIMIENTO");
    var firstAddressId = await ReadNextIdAsync(connection, transaction, "admDomicilios", "CIDDIRECCION");
    var firstAccumulatedId = await ReadNextIdAsync(connection, transaction, "admAcumulados", "CIDACUMULADO");
    var bitacoraId = await ReadNextIdAsync(connection, transaction, "admBitacoras", "IDBITACORA");
    var folio = await ReadNextFolioAsync(connection, transaction, conceptId);

    Console.WriteLine("RESERVA SIMULADA (NO SE INSERTÓ NADA)");
    Console.WriteLine($"  Documento: {documentId}");
    Console.WriteLine($"  Movimientos: {firstMovementId} a {firstMovementId + movementCount - 1}");
    Console.WriteLine($"  Domicilios: {firstAddressId} a {firstAddressId + addressCount - 1}");
    Console.WriteLine($"  Acumulados: {firstAccumulatedId} a {firstAccumulatedId + accumulatedCount - 1}");
    Console.WriteLine($"  Bitácora: {bitacoraId}");
    Console.WriteLine($"  Concepto: {conceptId}");
    Console.WriteLine($"  Folio actual + 1: {folio}");
    Console.WriteLine();
    Console.WriteLine("La transacción se revertirá ahora.");
}
finally
{
    await transaction.RollbackAsync();
}

static async Task<int> AcquireApplicationLockAsync(SqlConnection connection, SqlTransaction transaction)
{
    const string sql = """
        DECLARE @Result int;
        EXEC @Result = sys.sp_getapplock
            @Resource = N'LicenciasMida:CommercialPremium:QuoteIds',
            @LockMode = N'Exclusive',
            @LockOwner = N'Transaction',
            @LockTimeout = 10000;
        SELECT @Result;
        """;

    await using var command = new SqlCommand(sql, connection, transaction);
    return Convert.ToInt32(await command.ExecuteScalarAsync());
}

static async Task<long> ReadNextIdAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    string table,
    string column)
{
    var sql = $"SELECT ISNULL(MAX([{column}]), 0) + 1 FROM dbo.[{table}] WITH (UPDLOCK, HOLDLOCK);";
    await using var command = new SqlCommand(sql, connection, transaction);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<long> ReadNextFolioAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    int conceptId)
{
    const string sql = """
        SELECT CONVERT(bigint, ISNULL(CNOFOLIO, 0)) + 1
        FROM dbo.admConceptos WITH (UPDLOCK, HOLDLOCK)
        WHERE CIDCONCEPTODOCUMENTO = @ConceptId;
        """;

    await using var command = new SqlCommand(sql, connection, transaction);
    command.Parameters.Add("@ConceptId", SqlDbType.Int).Value = conceptId;
    var value = await command.ExecuteScalarAsync();

    if (value is null or DBNull)
        throw new InvalidOperationException($"No existe el concepto {conceptId}.");

    return Convert.ToInt64(value);
}

static int GetIntOption(string[] values, string name, int defaultValue)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
        return defaultValue;
    if (index + 1 >= values.Length || !int.TryParse(values[index + 1], out var result) || result < 1)
        throw new ArgumentException($"{name} debe ser un entero mayor que cero.");
    return result;
}
