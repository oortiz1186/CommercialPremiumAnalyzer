# CommercialPremiumAnalyzer

Herramienta enfocada en descubrir y reproducir de forma segura la creación de cotizaciones de CONTPAQi Comercial Premium para integrarla posteriormente con Licencias MIDA.

## Objetivo

El objetivo final es que Licencias MIDA pueda enviar una cotización y recibir de Comercial Premium:

- `CIDDOCUMENTO`;
- serie;
- folio;
- total;
- referencia de creación.

Toda la investigación y las pruebas deben hacerse primero en `adMIDA_PRUEBAS`.

## Funciones actuales

- Crear snapshots de las tablas `adm...`.
- Guardar conteo, llave primaria, valor máximo y checksum de contenido.
- Detectar inserciones, eliminaciones y actualizaciones.
- Mostrar automáticamente los registros nuevos entre dos snapshots.
- Exportar comparaciones de snapshots a JSON.
- Comparar dos filas columna por columna.
- Exportar comparaciones de filas a JSON.
- Capturar una cotización real como paquete técnico.
- Generar una plantilla SQL de investigación que termina siempre en `ROLLBACK`.
- No ejecuta escrituras automáticas en Comercial Premium.

## Requisitos

- .NET 8 SDK.
- Acceso de lectura a la empresa de pruebas de Comercial Premium.
- Variable de entorno `COMMERCIAL_PREMIUM_CONNECTION`.

Ejemplo para PowerShell:

```powershell
$env:COMMERCIAL_PREMIUM_CONNECTION = "Server=WIN-R5DQ363CI37\COMPAC;Database=adMIDA_PRUEBAS;User Id=sa;Password=TU_PASSWORD;TrustServerCertificate=True;Encrypt=False"
```

No guardes contraseñas en el repositorio.

## Compilar todos los proyectos

```powershell
dotnet restore
dotnet build

dotnet build .\QuoteTemplateGenerator\QuoteTemplateGenerator.csproj
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

Tipos mostrados:

- `INSERT`: aumentó el número de registros.
- `DELETE`: disminuyó el número de registros.
- `UPDATE`: no cambió el conteo, pero sí el checksum.

## Comparar documentos o movimientos

```powershell
dotnet run -- compare-rows `
  --table dbo.admDocumentos `
  --key CIDDOCUMENTO `
  --a 8296 `
  --b 8297 `
  --output documento-8296-vs-8297.json
```

## Capturar una cotización completa

El proyecto `QuoteTemplateGenerator` extrae:

- `admDocumentos`;
- todos los `admMovimientos` relacionados;
- todos los `admDomicilios` relacionados;
- el registro de `admConceptos`;
- definición de columnas, tipos, nulabilidad, identidad y valores predeterminados.

Ejemplo:

```powershell
dotnet run --project .\QuoteTemplateGenerator\QuoteTemplateGenerator.csproj -- capture `
  --document-id 8297 `
  --output quote-8297.json
```

## Generar plantilla SQL de investigación

```powershell
dotnet run --project .\QuoteTemplateGenerator\QuoteTemplateGenerator.csproj -- generate-template `
  --input quote-8297.json `
  --output quote-template-8297.sql
```

La plantilla:

- no se ejecuta automáticamente;
- contiene `SET XACT_ABORT ON` y `BEGIN TRAN`;
- termina en `ROLLBACK`;
- marca como pendientes los consecutivos de IDs;
- marca como pendientes `admAcumulados` y `admBitacoras`;
- sirve para revisar qué valores se copian, cuáles cambian y cuáles debemos parametrizar en el escritor real.

## Ruta hacia Licencias MIDA

1. Capturar varias cotizaciones reales con clientes, productos, cantidades e importes diferentes.
2. Clasificar campos constantes, variables, calculados y generados.
3. Confirmar la estrategia de consecutivos y bloqueo del folio.
4. Reproducir `admAcumulados` y `admBitacoras`.
5. Crear una cotización de prueba dentro de una transacción controlada.
6. Validarla desde Comercial Premium.
7. Convertir la lógica validada en un servicio consumido por Licencias MIDA.

## Seguridad

Usa exclusivamente `adMIDA_PRUEBAS`. Para investigación se recomienda un inicio de sesión SQL con permisos `SELECT` únicamente y no usar la cuenta `sa`.