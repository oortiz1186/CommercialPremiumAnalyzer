# CommercialPremiumAnalyzer

Herramienta de solo lectura para investigar los cambios que realiza CONTPAQi Comercial Premium sobre SQL Server.

## Alcance inicial

- Crear snapshots con conteo de registros y valor máximo de la llave primaria.
- Comparar dos snapshots para detectar tablas modificadas.
- Comparar dos filas de una tabla columna por columna.
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

La opción `--prefix adm` está activa por omisión y evita recorrer tablas ajenas a Comercial Premium.

## Crear snapshot después

Después de crear una cotización manual en la empresa de pruebas:

```powershell
dotnet run -- snapshot --output despues-cotizacion.json
```

## Comparar snapshots

```powershell
dotnet run -- compare-snapshots antes-cotizacion.json despues-cotizacion.json
```

## Comparar dos documentos

```powershell
dotnet run -- compare-rows `
  --table dbo.admDocumentos `
  --key CIDDOCUMENTO `
  --a 8295 `
  --b 8296
```

## Comparar dos movimientos

```powershell
dotnet run -- compare-rows `
  --table dbo.admMovimientos `
  --key CIDMOVIMIENTO `
  --a 11695 `
  --b 11696
```

El comparador imprime solamente las columnas diferentes.

## Seguridad

Usa exclusivamente `adMIDA_PRUEBAS`. Para las primeras pruebas se recomienda crear un inicio de sesión SQL con permisos `SELECT` únicamente y no usar la cuenta `sa`.
