# CommercialPremiumAnalyzer

Herramienta de solo lectura para investigar los cambios que realiza CONTPAQi Comercial Premium sobre SQL Server.

## Funciones actuales

- Crear snapshots de las tablas `adm...`.
- Guardar conteo, llave primaria, valor máximo y checksum de contenido.
- Detectar inserciones, eliminaciones y actualizaciones.
- Mostrar automáticamente los registros nuevos entre dos snapshots.
- Exportar comparaciones de snapshots a JSON.
- Comparar dos filas columna por columna.
- Exportar comparaciones de filas a JSON.
- No ejecuta `INSERT`, `UPDATE` ni `DELETE`.

## Requisitos

- .NET 8 SDK.
- Acceso de lectura a la empresa de pruebas de Comercial Premium.
- Variable de entorno `COMMERCIAL_PREMIUM_CONNECTION`.

Ejemplo para PowerShell:

```powershell
$env:COMMERCIAL_PREMIUM_CONNECTION = "Server=WIN-R5DQ363CI37\COMPAC;Database=adMIDA_PRUEBAS;User Id=sa;Password=TU_PASSWORD;TrustServerCertificate=True;Encrypt=False"
```

No guardes contraseñas en el repositorio.

## Compilar

```powershell
dotnet restore
dotnet build
```

## Crear snapshot antes de una operación

```powershell
dotnet run -- snapshot --output antes-cotizacion.json
```

La opción `--prefix adm` está activa por omisión.

Cada tabla guarda:

- cantidad de registros;
- llave primaria detectada;
- valor máximo de la llave;
- checksum de contenido.

El checksum permite detectar casos como `admConceptos.CNOFOLIO`, aunque la cantidad de registros no cambie.

## Crear snapshot después

Después de realizar una operación manual en `adMIDA_PRUEBAS`:

```powershell
dotnet run -- snapshot --output despues-cotizacion.json
```

## Comparar snapshots y exportar el reporte

```powershell
dotnet run -- compare-snapshots `
  antes-cotizacion.json `
  despues-cotizacion.json `
  --output diferencias-cotizacion.json
```

Por omisión, el comando consulta la base actual y agrega al JSON el contenido completo de los registros cuya llave quedó entre el máximo anterior y el máximo posterior.

Para evitar cargar esos registros:

```powershell
dotnet run -- compare-snapshots `
  antes-cotizacion.json `
  despues-cotizacion.json `
  --include-new-rows false
```

Tipos mostrados por el comparador:

- `INSERT`: aumentó el número de registros.
- `DELETE`: disminuyó el número de registros.
- `UPDATE`: no cambió el conteo, pero sí el checksum.

## Comparar dos documentos

```powershell
dotnet run -- compare-rows `
  --table dbo.admDocumentos `
  --key CIDDOCUMENTO `
  --a 8296 `
  --b 8297 `
  --output documento-8296-vs-8297.json
```

## Comparar dos movimientos

```powershell
dotnet run -- compare-rows `
  --table dbo.admMovimientos `
  --key CIDMOVIMIENTO `
  --a 11696 `
  --b 11697 `
  --output movimiento-11696-vs-11697.json
```

El comparador imprime y exporta solamente las columnas diferentes.

## Seguridad

Usa exclusivamente `adMIDA_PRUEBAS`. Para las pruebas se recomienda un inicio de sesión SQL con permisos `SELECT` únicamente y no usar la cuenta `sa`.