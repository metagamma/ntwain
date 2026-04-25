# Fi6800 Scanner

App de demostración para Fujitsu fi-6800 + PaperStream IP usando NTwain v3 + Cyotek ImageBox.

**Estado actual: Fase 1+2+3 (MVP funcional)**

## Build

```bash
dotnet restore Fi6800Scanner.sln
dotnet build Fi6800Scanner.sln -c Debug
```

Salida: `Fi6800Scanner.App\bin\x86\Debug\net462\Fi6800Scanner.App.exe`

Configuración: **x86 + net462** (necesario para compatibilidad con drivers PaperStream IP).

## Pre-requisitos

- Windows 10/11 (con KB5055523 si Win11 24H2)
- PaperStream IP TWAIN x86 instalado
- Driver del fi-6800 (debe aparecer en "PaperStream Capture" como sanity check)
- .NET Framework 4.6.2 (incluido en Windows 10+ por defecto)

## Uso

1. **Pre-flight** — verifica arquitectura, PaperStream instalado, TWAINDSM disponible
2. **Discover (probe)** — abre el source y enumera todas las capabilities; genera snapshot
3. **Diagnóstico** — muestra todas las caps detectadas, IDs custom y features; permite guardar JSON
4. **Escanear** — usa perfil default (RGB 300 DPI A4 duplex con auto-procesos)

## Arquitectura

```
Fi6800Scanner.Core/           biblioteca .NET (sin UI)
  Models/                     ScannedPage, ScanProfile, snapshot
  Capabilities/               ScannerProbe, ProfileApplicator
  Services/                   IScannerService, Fi6800ScannerService
  Diagnostics/                PreflightChecker

Fi6800Scanner.App/            WinForms exe
  Forms/                      MainForm, DiagnosticsDialog
  Controls/                   ImageViewerPanel (Cyotek), PageThumbnailListView
```

## Pendiente (fases siguientes)

- **Fase 4**: SettingsDialog completo con tabs (imagen / resolución / auto / compresión / patch / barcodes / multifeed / imprinter)
- **Fase 5**: badges de patch/barcode en thumbnails, separación de lotes con JobControl
- **Fase 6**: UI de imprinter, manejo de PaperJam/PaperDoubleFeed con recuperación
- **Fase 7**: Multi Image Output (multi-stream color + bitonal)
- **Fase 9**: persistencia (TIFF multi-página, PDF, custom DSData del driver)
- **Fase 10**: logging robusto con Serilog, tracking de errores

## Limitaciones conocidas

- `ICapPhysicalHeight` es read-only en NTwain v3 → long page real requiere `DGControl.Capability` directo o cap custom de PaperStream (no implementado).
- Caps custom de PaperStream (rango ≥0x8000) se enumeran pero su semántica es desconocida — PFU no publica la tabla.
- `XferMech.File` no probado; default es `Native` que limita lotes grandes a la RAM disponible.
- App es x86 — para drivers x64 puros recompilar como x64 (no recomendado para fi-6800 cuyo driver más estable es x86).
