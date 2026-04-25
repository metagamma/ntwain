# Plan de Implementación — Fujitsu fi-6800 + PaperStream IP + NTwain v3 + Cyotek ImageBox

Plan completo para construir una app cliente WinForms que aproveche todas las features del fi-6800 vía PaperStream IP, usando NTwain v3 como puente TWAIN y Cyotek ImageBox como visor.

---

## Tabla de contenidos

1. [Hallazgos de investigación](#1-hallazgos-de-investigación)
2. [Restricciones críticas conocidas](#2-restricciones-críticas-conocidas)
3. [Plan de extracción de información (capability discovery)](#3-plan-de-extracción-de-información-capability-discovery)
4. [Arquitectura propuesta](#4-arquitectura-propuesta)
5. [Estructura del proyecto](#5-estructura-del-proyecto)
6. [Fases de implementación](#6-fases-de-implementación)
7. [Mapa de UI (panels, controles)](#7-mapa-de-ui-panels-controles)
8. [Patrones críticos de código](#8-patrones-críticos-de-código)
9. [Validación con escáner real](#9-validación-con-escáner-real)
10. [Riesgos y mitigaciones](#10-riesgos-y-mitigaciones)

---

## 1. Hallazgos de investigación

### 1.1 — Fujitsu fi-6800 (modelo descontinuado pero soportado)

| Característica | Valor |
|----------------|-------|
| Velocidad | 130 ppm / 260 ipm (color, gris, B&W, A4, 200/300 DPI, simplex/duplex) |
| Resolución óptica | 600 DPI (real) — driver expone 50–600 DPI nativos + 1200 DPI interpolado |
| Tamaños | A8 mínimo (52×74 mm) → A3 máximo (297×420 mm). Long page hasta 3175 mm a 200/300 DPI |
| Modos color | RGB 24-bit, Gray 8-bit, B&W 1-bit, **Auto Color Detection**, **Multi Image Output** (hasta 3 streams/página en una pasada) |
| ADF | 500 hojas; gramaje 20–209 g/m² |
| Volumen diario | 110 000 hojas/día (oficial PFU) |
| Duplex | sí, sensor CCD frontal + trasero |
| Multifeed | 3 sensores ultrasónicos + detección por longitud + función iMFF (Intelligent Multi-Feed: aprende patrones) |
| Imprinter | fi-680PRF (post-front), fi-680PRB (post-back). **Solo post-imprint** — el texto no aparece en la imagen escaneada |
| Active Stacker | bandeja de salida con alineación automática |
| Interfaz | USB 2.0 (SCSI obsoleto) |
| Patch codes | I, II, III, IV, VI, T (los 6 estándar Kodak) |
| Barcodes | Code 39, Code 128, QR Code (oficial); con plugin "2D Barcode for PaperStream": Aztec y otros |

**Fuentes**: datasheet PFU, página global del modelo (`pfu.ricoh.com/global/scanners/fi/discontinued/fi6800/`), Error Recovery Guide.

### 1.2 — PaperStream IP TWAIN

- **Driver TWAIN moderno** (TWAIN 2.x, requiere TWAINDSM, no `TWAIN_32.DLL` legacy).
- **Dos paquetes**: x86 y x64 — la elección depende de la arquitectura del **proceso anfitrión**, no del SO. Ambos pueden coexistir.
- **Capabilities estándar TWAIN bien soportadas**: `ICapPixelType`, `ICapXResolution`, `ICapAutomaticDeskew/Rotate/BorderDetection/AutoDiscardBlankPages`, `CapDuplexEnabled`, `CapFeederEnabled`, `ICapPatchCodeDetectionEnabled`, `ICapBarcodeDetectionEnabled`, `CapDoubleFeedDetection`, `CapPrinter*`.
- **Procesado propietario** (equivalente a VRS de Kofax pero propio): edge repair, blur correction, character emphasis, background smoothing, hole punch removal, front/back merge, Auto Color con "Color Distinction" e "Ignore Background Color", dynamic threshold adaptativo.
- **Multi Image Output**: hasta 3 streams por hoja — driver reporta múltiples imágenes en una sola pasada (típicamente Color + Bitonal + thumbnail). Cliente recibe N `DataTransferred` por hoja física.
- **Capabilities custom (rango 0x8000+)**: PFU/Ricoh **no publica** la tabla. Hay que enumerarlas runtime con `CAP_SUPPORTEDCAPS` filtrando ≥ 0x8000.
- **Confirmado funciona con NTwain v3**: usa DSM2 por default y soporta x86/x64.

### 1.3 — Cyotek ImageBox

- **NuGet**: `CyotekImageBox` (no `Cyotek.Windows.Forms.ImageBox`). Versión 1.3.1.
- **TFM**: `net20` declarado, compatible con `net462` … `net8.0-windows`.
- **Licencia**: MIT.
- **API útil**: `Image`, `Zoom`, `ZoomToFit()`, `ZoomIn/Out()`, `ActualSize()`, `SizeMode`, `SelectionMode` (None/Rectangle/Zoom), `SelectionRegion`, `GetSelectedImage()`, `PointToImage()`, `GridDisplayMode`, `ShowPixelGrid`.
- **NO tiene**: control de thumbnails (usar `ListView` + `ImageList`), rotación nativa (rotar el Bitmap y reasignar), soporte PDF.
- **Memoria**: imágenes >8000×8000 o muchas páginas en RAM disparan `OutOfMemoryException` → usar streaming a archivos temporales para lotes grandes.

---

## 2. Restricciones críticas conocidas

| Riesgo | Impacto | Mitigación |
|--------|---------|------------|
| Caps custom de PaperStream no documentadas públicamente | No podemos hardcodear features avanzadas | Enumerar runtime y exponer las que soporten un wrapper típico |
| x86 vs x64 mismatch entre app y driver instalado | Source no aparece o falla al abrir | Detectar arquitectura y abortar con mensaje claro; idealmente compilar la app x86 |
| Windows 11 24H2 rompió TWAIN-bridge | Driver no se enumera | Documentar requisito KB5055523 + última versión PSIP |
| Multi Image Output cambia el conteo de imágenes por página física | Lógica de "página N" se descuadra | Detectar por `ImageInfo.PixelType` + `PageNumber` extendido en ExtImageInfo |
| Long page mode requiere cap propietaria | Hojas largas se cortan | Exponerlo como switch; probar en escáner antes de release |
| Patch detection en RGB puede fallar (ver §19 del REFERENCE) | Separación por patch no funciona en color | Estrategia dual-stream: bitonal para detección, color para archivo |
| Imprinter es post (no afecta imagen) | Si necesitas endorso visible, otra solución | Digital endorser del driver vía cap propietaria |
| fi-6800 está descontinuado | Próximas versiones de PSIP podrían no listarlo | Validar versión PSIP instalada antes de release |
| `OutOfMemoryException` con lotes RGB grandes en RAM | App crashea con 1000+ páginas | Usar `XferMech.File` + thumbnails downsampled en `ImageList`, originales en disco |

---

## 3. Plan de extracción de información (capability discovery)

Esta es la parte central del plan: **NO hardcodear**, descubrir runtime qué soporta el escáner concreto.

### 3.1 — Fases de descubrimiento

```
[Pre-flight checks]
   ↓
[Listar fuentes y filtrar por nombre "fi-6800" o "PaperStream IP fi-6800"]
   ↓
[Abrir source (estado 4)]
   ↓
[Enumerar TODAS las capabilities soportadas]
   ↓
[Por cada cap conocida: leer GetValues() / GetCurrent() / GetDefault()]
   ↓
[Detectar caps custom (CAP_ID >= 0x8000) — registrar IDs aunque no sepamos qué hacen]
   ↓
[Detectar features compuestos (Multi Image Output, iMFF, long page) por probing]
   ↓
[Persistir el snapshot en JSON para offline tooling]
```

### 3.2 — Pre-flight checks

Antes incluso de abrir la sesión TWAIN:

```csharp
// 1. ¿Es Windows 10/11 con KB5055523 o posterior si 24H2?
// 2. ¿Hay PSIP instalado?
//    Registry: HKLM\SOFTWARE\PFU\PSIP\* o HKLM\SOFTWARE\WOW6432Node\PFU\
// 3. ¿Coincide arquitectura del driver con la de la app?
//    Si app es x86, debe haber PSIP x86 instalado.
//    Si app es x64, debe haber PSIP x64 instalado.
// 4. ¿USB del fi-6800 detectable?
//    Listar dispositivos USB con clase de imaging.
// 5. ¿TWAINDSM.dll disponible?
```

### 3.3 — Enumeración de capabilities

NTwain v3 **no expone directamente** una API para "listar todas las caps soportadas", pero la cap estándar `CAP_SUPPORTEDCAPS` lo hace. Hay que llamarla via `DGControl.Capability` o usar el wrapper genérico:

```csharp
// Cap genérica que retorna la lista de IDs soportados
var supportedCaps = source.Capabilities.CapSupportedCaps;
if (supportedCaps.IsSupported)
{
    var ids = supportedCaps.GetValues().ToList();
    foreach (var id in ids)
    {
        // id es un CapabilityId enum (0x0001 - 0x1500 estándar, 0x8000+ custom)
        Log($"Cap soportada: {id} (0x{(int)id:X4})");
    }
}
```

### 3.4 — Categorías a inspeccionar (ordenadas)

Para cada categoría: probar `IsSupported`, leer `GetValues()`, registrar resultado en un objeto `Fi6800CapabilitySnapshot`.

```
A. Identidad y básicas
   - source.Identity (Manufacturer, ProductFamily, ProductName, Version)
   - CapDeviceOnline, CapDeviceTimeDate
   - CapAuthor, CapCaption (info opcional)

B. Modos de imagen
   - ICapPixelType (lista de PixelType: BlackWhite/Gray/RGB esperados)
   - ICapBitDepth (1, 8, 24)
   - ICapAutomaticColorEnabled  ← clave: "Auto Color Detection"
   - ICapAutomaticColorNonColorPixelType
   - ICapPixelFlavor

C. Resolución
   - ICapXResolution, ICapYResolution (Range o Enumeration)
   - ICapXNativeResolution, ICapYNativeResolution
   - ICapXScaling, ICapYScaling

D. Geometría / tamaño
   - ICapSupportedSizes (lista de SupportedSize: A3/A4/Letter/Legal...)
   - ICapPhysicalWidth, ICapPhysicalHeight
   - ICapMaxFrames (1 vs múltiple)
   - ICapFrames (TWFrame array)
   - CapFeederEnabled

E. Long page
   - ICapMaxFrames + cap custom (probar "ICapMaxFrameLength" si existe en supported caps)
   - O setear ICapSupportedSizes a None y leer rango de PhysicalHeight

F. Duplex / Feeder
   - CapDuplex, CapDuplexEnabled
   - CapFeederLoaded, CapFeederEnabled
   - CapAutoFeed, CapPaperHandling
   - CapCameraSide, CapCameraEnabled  ← para Multi Image Output

G. Detecciones automáticas
   - ICapAutomaticDeskew
   - ICapAutomaticRotate
   - ICapAutomaticBorderDetection
   - ICapAutoDiscardBlankPages (+ probar caps custom para "sensitivity")
   - ICapAutomaticLengthDetection
   - ICapAutoSize

H. Calidad / procesado
   - ICapBrightness, ICapContrast, ICapThreshold, ICapGamma
   - ICapShadow, ICapHighlight
   - ICapFilter (drop-out colors)
   - ICapNoiseFilter
   - ICapHalftones (para B&W)

I. Compresión y formato
   - ICapCompression (lista por PixelType actual)
   - ICapImageFileFormat (lista: Tiff, Jpeg, Pdf, etc.)
   - ICapJpegQuality, ICapJpegSubsampling
   - ICapJpegPixelType

J. Multifeed
   - CapDoubleFeedDetection (por longitud / ultrasonic / ambos)
   - CapDoubleFeedDetectionLength
   - CapDoubleFeedDetectionSensitivity
   - CapDoubleFeedDetectionResponse (Stop / Continue)
   - Caps custom para iMFF (probar IDs ≥ 0x8000 con nombres en GetHelp)

K. Patch codes
   - ICapPatchCodeDetectionEnabled
   - ICapSupportedPatchCodeTypes (lista PatchCode)
   - ICapPatchCodeMaxRetries, ICapPatchCodeMaxSearchPriorities
   - ICapPatchCodeSearchMode, ICapPatchCodeSearchPriorities

L. Barcodes
   - ICapBarcodeDetectionEnabled
   - ICapSupportedBarcodeTypes (lista BarcodeType)
   - ICapBarcodeMaxRetries, ICapBarcodeSearchMode
   - ICapBarcodeMaxSearchPriorities

M. Imprinter / endorser
   - CapPrinter, CapPrinterEnabled, CapPrinterIndex
   - CapPrinterMode, CapPrinterString, CapPrinterStringList
   - CapPrinterCharRotation, CapPrinterFontStyle
   - CapPrinterIndexLeadChar, CapPrinterIndexNumDigits, CapPrinterIndexStep, CapPrinterIndexTrigger

N. Job control
   - CapJobControl (None/IncludeStop/ExcludeStop/IncludeContinue/ExcludeContinue)
   - CapXferCount

O. Extended Image Info disponibles
   - ICapExtImageInfo (boolean)
   - ICapSupportedExtImageInfo (lista de ExtendedImageInfo: PatchCode, BarcodeText, PageSide, PageNumber, SkewFinalAngle, etc.)

P. Custom DSData (perfiles del driver)
   - CapCustomDSData
   - DataSource.Settings (helper de NTwain)

Q. Caps custom (≥ 0x8000)
   - Para cada ID custom: intentar GetCurrent / GetDefault / GetValues
   - Llamar GetHelp y GetLabel si soportadas (vía DGControl.Capability)
   - Registrar ID, valor actual, tipo (TWTY_*) y rango — para construir UI dinámica
```

### 3.5 — Output del descubrimiento

Generar un JSON snapshot offline que sirva para:
- Documentar exactamente qué soporta el escáner físico instalado.
- Construir UI dinámicamente.
- Detectar regresiones cuando cambie versión de PSIP.

```json
{
  "scannerName": "PaperStream IP fi-6800",
  "version": "1.42.0.x",
  "probedAt": "2026-04-25T16:30:00Z",
  "pixelTypes": ["BlackWhite", "Gray", "RGB"],
  "resolutionRange": { "min": 50, "max": 600, "step": 1 },
  "supportedSizes": ["A3", "A4", "USLetter", "USLegal", "A5", "B5", "BusinessCard", "MaxSize"],
  "patchCodesSupported": ["Patch1", "Patch2", "Patch3", "Patch4", "Patch6", "PatchT"],
  "barcodesSupported": ["Code39", "Code128", "QRCode"],
  "extImageInfoSupported": ["PatchCode", "BarcodeText", "BarcodeType", "PageSide", "PageNumber", "SkewFinalAngle"],
  "customCaps": [
    { "id": "0x8001", "label": "Color Distinction Sensitivity", "type": "TWTY_INT16", "current": 5 },
    { "id": "0x8042", "label": "iMFF Mode", "type": "TWTY_UINT16", "current": 0 }
  ],
  "features": {
    "multiImageOutput": true,
    "longPage": true,
    "imprinter": false,
    "duplex": true
  }
}
```

---

## 4. Arquitectura propuesta

```
┌─────────────────────────────────────────────────┐
│  WinForms UI (cyotek + ListView thumbnails)     │
└────────────────┬────────────────────────────────┘
                 │ data binding
┌────────────────▼────────────────────────────────┐
│  ScanViewModel (INotifyPropertyChanged)         │
│  - ObservableCollection<ScannedPage>            │
│  - Commands: Scan, Cancel, SaveAll, Settings    │
└────────────────┬────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────┐
│  IScannerService                                │
│  - DiscoverAsync(): Fi6800CapabilitySnapshot    │
│  - ScanAsync(profile, ct): IList<ScannedPage>   │
│  - Cancel()                                     │
└────────────────┬────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────┐
│  Fi6800ScannerService (impl)                    │
│  - Wraps NTwain TwainSession + DataSource       │
│  - Aplica perfil → caps                         │
│  - Maneja eventos → ScannedPage[]               │
└────────────────┬────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────┐
│  NTwain v3 (TwainSession, DataSource, Caps)     │
└────────────────┬────────────────────────────────┘
                 │
┌────────────────▼────────────────────────────────┐
│  PaperStream IP TWAIN driver                    │
└────────────────┬────────────────────────────────┘
                 │  USB
┌────────────────▼────────────────────────────────┐
│  Fujitsu fi-6800 hardware                       │
└─────────────────────────────────────────────────┘
```

### 4.1 — Modelo de datos

```csharp
public class ScannedPage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int PageNumber { get; init; }
    public PageSide Side { get; init; }       // Front / Rear
    public PixelType PixelType { get; init; }
    public string TempFilePath { get; init; } // si XferMech.File
    public Bitmap Thumbnail { get; init; }    // 200×280 px aprox
    public Bitmap FullImage { get; set; }     // lazy-loaded
    public PatchCode? DetectedPatch { get; init; }
    public IReadOnlyList<DetectedBarcode> Barcodes { get; init; }
    public double SkewAngleDegrees { get; init; }
    public DateTime CapturedAt { get; init; }
}

public class DetectedBarcode { public BarcodeType Type; public string Text; public Point Position; }

public class ScanProfile
{
    public PixelType PixelType { get; set; } = PixelType.RGB;
    public bool AutoColor { get; set; } = false;
    public PixelType AutoColorNonColorMode { get; set; } = PixelType.Gray;
    public int Dpi { get; set; } = 300;
    public SupportedSize PaperSize { get; set; } = SupportedSize.A4;
    public bool LongPage { get; set; } = false;
    public int LongPageMaxLengthMm { get; set; } = 3175;
    public bool Duplex { get; set; } = true;
    public bool AutoDeskew { get; set; } = true;
    public bool AutoRotate { get; set; } = true;
    public bool AutoBorder { get; set; } = true;
    public bool DiscardBlankPages { get; set; } = false;
    public int? Rotation90Deg { get; set; }   // null = sin rotación fija
    public int Brightness { get; set; } = 0;
    public int Contrast { get; set; } = 0;
    public CompressionType Compression { get; set; } = CompressionType.None;
    public int JpegQuality { get; set; } = 85;
    public XferMech XferMech { get; set; } = XferMech.File;
    public bool MultiStream { get; set; } = false;
    public bool PatchEnable { get; set; } = false;
    public List<PatchCode> PatchTypes { get; set; } = new();
    public bool BarcodeEnable { get; set; } = false;
    public List<BarcodeType> BarcodeTypes { get; set; } = new();
    public MultifeedMode Multifeed { get; set; } = MultifeedMode.Ultrasonic;
    public int MultifeedSensitivity { get; set; } = 5;
    public ImprinterConfig? Imprinter { get; set; }
}
```

---

## 5. Estructura del proyecto

```
Fi6800Scanner.sln
├── Fi6800Scanner.Core/                 (.NET 6/8, sin UI)
│   ├── Capabilities/
│   │   ├── Fi6800CapabilitySnapshot.cs
│   │   ├── ScannerProbe.cs            ← descubrimiento runtime
│   │   ├── CustomCapInspector.cs      ← maneja IDs ≥ 0x8000
│   │   └── ProfileApplicator.cs       ← aplica ScanProfile a caps
│   ├── Models/
│   │   ├── ScannedPage.cs
│   │   ├── ScanProfile.cs
│   │   ├── ImprinterConfig.cs
│   │   └── DetectedBarcode.cs
│   ├── Services/
│   │   ├── IScannerService.cs
│   │   ├── Fi6800ScannerService.cs    ← implementación NTwain
│   │   ├── ScanResultPersister.cs     ← TIFF multi-page, PDF, etc.
│   │   └── ScanProgressTracker.cs
│   └── Diagnostics/
│       ├── PreflightChecker.cs        ← arquitectura, KB5055523, etc.
│       └── SnapshotJsonExporter.cs
│
├── Fi6800Scanner.App/                  (WinForms net6.0-windows o net462)
│   ├── Forms/
│   │   ├── MainForm.cs                 ← layout principal
│   │   ├── SettingsDialog.cs           ← perfil de escaneo
│   │   ├── ImprinterDialog.cs
│   │   └── DiagnosticsDialog.cs        ← info del snapshot
│   ├── Controls/
│   │   ├── PageThumbnailListView.cs    ← ListView wrapper
│   │   ├── ImageViewerPanel.cs         ← Cyotek ImageBox + toolbar
│   │   └── ScanProgressBar.cs
│   ├── ViewModels/
│   │   └── MainViewModel.cs
│   └── Program.cs
│
└── Fi6800Scanner.Tests/                (xUnit)
    ├── ProfileApplicatorTests.cs       ← unit tests con fake source
    ├── PreflightCheckerTests.cs
    └── IntegrationTests.cs              ← [Trait("RequiresScanner")]
```

---

## 6. Fases de implementación

### Fase 0 — Preparación (1 día)

- [ ] Verificar PaperStream IP instalado; si no, instalar la versión que aún liste fi-6800.
- [ ] Verificar Windows update KB5055523 si Win11 24H2.
- [ ] Conectar fi-6800 por USB; verificar que aparece en la app PaperStream Capture (sanity check).
- [ ] Crear solución, agregar `NTwain` 3.7.5, `CyotekImageBox` 1.3.1.
- [ ] Decidir compilar como **x86** (recomendado) o x64, instalar el PSIP correspondiente.

### Fase 1 — Pre-flight + descubrimiento (2 días)

- [ ] `PreflightChecker`: arquitectura, registry PSIP, USB device.
- [ ] `ScannerProbe`: enumera caps, lee valores; produce `Fi6800CapabilitySnapshot`.
- [ ] `SnapshotJsonExporter`: serializa a JSON.
- [ ] `DiagnosticsDialog`: muestra el snapshot (read-only) — útil para soporte y testing.
- [ ] **Smoke test**: ejecutar discovery contra el escáner real y guardar el JSON. Este JSON guía las fases siguientes.

### Fase 2 — Escaneo simplex básico (1 día)

- [ ] `Fi6800ScannerService.ScanAsync` con perfil mínimo (B&W, A4, 300 DPI, simplex).
- [ ] `MainForm` con un botón "Scan" y un Cyotek ImageBox.
- [ ] `OnDataTransferred`: convertir `NativeData` → `Bitmap` → mostrar en ImageBox.
- [ ] Cierre limpio en `SourceDisabled`.

### Fase 3 — Lote ADF + thumbnails (2 días)

- [ ] `XferCount = -1`, feeder enabled.
- [ ] `ListView` con `ImageList` (thumbnails 200×280) en panel izquierdo, ImageBox a la derecha.
- [ ] Selección en ListView carga la imagen completa en ImageBox.
- [ ] Si hay muchas páginas: usar `XferMech.File`, persistir originales en `%TEMP%\Fi6800Scanner\<sessionId>\`, mantener solo thumbnails en RAM.

### Fase 4 — UI de configuración (perfil) (3 días)

- [ ] `SettingsDialog` con tabs:
  - **Imagen**: PixelType, BitDepth, Brightness, Contrast, Threshold, Filter (dropout)
  - **Resolución y tamaño**: DPI, paper size, long page, frame personalizado
  - **Auto-procesos**: deskew, rotate, border, discard blank pages
  - **Compresión**: codec, JPEG quality, file format
  - **Duplex / Multi-Stream**: duplex on/off, multi-image output config
- [ ] Cargar opciones disponibles desde `Fi6800CapabilitySnapshot` (no hardcodear).
- [ ] Botón "Save profile to JSON" / "Load profile" para perfiles reutilizables.

### Fase 5 — Detecciones y separación de lotes (2 días)

- [ ] Tab **Patch codes** en SettingsDialog: enable + tipos a detectar.
- [ ] Tab **Barcodes**: enable + tipos.
- [ ] `JobControl.IncludeStop` cuando patch separator detectado.
- [ ] En `ScannedPage.DetectedPatch` y `Barcodes`: leer Extended Image Info.
- [ ] UI: badges en thumbnails que muestren patch/barcode detectado.

### Fase 6 — Multifeed + Imprinter (2 días)

- [ ] Tab **Multifeed**: ultrasonic on/off, sensitivity, length tolerance, response (stop/continue).
- [ ] Manejo de `ConditionCode.PaperDoubleFeed` con UI de recuperación (continuar/cancelar/eliminar último).
- [ ] Tab **Imprinter** (si el snapshot reporta soporte): mode, string, contador, fuente, posición.

### Fase 7 — Multi Image Output (multi-stream) (2 días)

- [ ] Detectar soporte vía caps custom o `CapCameraEnabled` en `/Camera_Color_*` y `/Camera_Bitonal_*` (DAT_FILESYSTEM).
- [ ] UI: toggle "Multi-stream: Color + B&W simultáneo".
- [ ] En `OnDataTransferred`: usar `e.ImageInfo.PixelType` y `ExtendedImageInfo.PageSide` para clasificar.
- [ ] Mostrar en thumbnails cada stream con indicador.

### Fase 8 — Cyotek viewer avanzado (1 día)

- [ ] Toolbar sobre ImageBox: zoom in/out, fit, actual size, rotate left/right (rota el Bitmap), select area, crop, save selection.
- [ ] Status bar: coordenadas pixel bajo cursor, zoom %, tamaño imagen original.
- [ ] Pixel grid en zoom alto (`ShowPixelGrid = true` cuando zoom > 800%).

### Fase 9 — Persistencia / exportación (2 días)

- [ ] Guardar todo el lote como TIFF multi-página.
- [ ] Guardar como PDF (PdfSharp o similar).
- [ ] Guardar individuales (TIFF, JPEG, PNG).
- [ ] Persistir/restaurar configuración del driver vía `DataSource.Settings` (DAT_CUSTOMDSDATA).

### Fase 10 — Cancelación, errores y robustez (2 días)

- [ ] Botón "Cancel" durante lote — `e.CancelAll = true` en `TransferReady`.
- [ ] Manejo amigable de errores: `PaperJam`, `NoMedia`, `CheckDeviceOnline`, `DocTooLight/Dark`.
- [ ] Logging con Serilog adaptado a `NTwain.ILog`.
- [ ] `ForceStepDown` como fallback en cierre.

### Fase 11 — Tests + documentación (2 días)

- [ ] Unit tests para `ProfileApplicator` con un fake `IDataSource`-like.
- [ ] Integration tests `[Trait("RequiresScanner")]` para CI manual.
- [ ] README con screenshots, requisitos, troubleshooting.
- [ ] CHANGELOG.

**Total estimado: ~20 días persona** (puede paralelizarse Fases 4-7).

---

## 7. Mapa de UI (panels, controles)

```
┌─────────────────────────────────────────────────────────────────────────┐
│ MainForm                                                       [- □ X]  │
├─────────────────────────────────────────────────────────────────────────┤
│ [Scan] [Cancel] [Settings] [Save…] [Diagnostics]    Profile: [Default▼] │
├──────────────┬──────────────────────────────────────────────────────────┤
│              │ ┌────────────────────────────────────────────────────┐   │
│  Pages       │ │ [Zoom-] [Fit] [1:1] [Zoom+] [Rotate ↻] [Rotate ↺]│   │
│  ListView    │ │ [Crop] [Save selection]                Pixel x,y │   │
│  with        │ ├────────────────────────────────────────────────────┤   │
│  thumbnails  │ │                                                    │   │
│  + badges    │ │           Cyotek ImageBox                         │   │
│  for patch / │ │           (current page full image)               │   │
│  barcode     │ │                                                    │   │
│              │ │                                                    │   │
│              │ │                                                    │   │
│              │ │                                                    │   │
│              │ └────────────────────────────────────────────────────┘   │
│              │ Page 3 of 47 │ Pending: 44 │ Patch: III │ Side: Front     │
└──────────────┴──────────────────────────────────────────────────────────┘
│ Status: Scanning…    Throughput: 128 ppm    Multifeed: 0    Errors: 0   │
└─────────────────────────────────────────────────────────────────────────┘
```

### SettingsDialog (tabs)

```
[Imagen][Resolución][Auto][Compresión][Duplex][Patch][Barcodes][Multifeed][Imprinter]
```

Cada tab popula sus controles desde el `Fi6800CapabilitySnapshot`.

---

## 8. Patrones críticos de código

### 8.1 — `ScannerProbe.DiscoverAsync` (esquema)

```csharp
public class ScannerProbe
{
    public async Task<Fi6800CapabilitySnapshot> DiscoverAsync(TwainSession session, DataSource source)
    {
        var snap = new Fi6800CapabilitySnapshot
        {
            ProductName  = source.Name,
            Manufacturer = source.Identity.Manufacturer.ToString(),
            Version      = source.Identity.Version.Info.ToString(),
            ProbedAt     = DateTime.UtcNow
        };

        // 1) Lista cruda de IDs soportadas
        var supported = source.Capabilities.CapSupportedCaps;
        if (supported.IsSupported)
        {
            var ids = supported.GetValues().ToList();
            snap.SupportedCapIds = ids.Select(id => $"0x{(int)id:X4}").ToList();
            snap.CustomCapIds    = ids.Where(id => (int)id >= 0x8000)
                                      .Select(id => $"0x{(int)id:X4}").ToList();
        }

        // 2) PixelType
        if (source.Capabilities.ICapPixelType.IsSupported)
            snap.PixelTypes = source.Capabilities.ICapPixelType.GetValues().ToList();

        // 3) Resolución
        if (source.Capabilities.ICapXResolution.IsSupported)
            snap.ResolutionValues = source.Capabilities.ICapXResolution
                .GetValues().Select(f => (float)f).ToList();

        // 4) Tamaños
        if (source.Capabilities.ICapSupportedSizes.IsSupported)
            snap.SupportedSizes = source.Capabilities.ICapSupportedSizes.GetValues().ToList();

        // 5) Patch codes
        if (source.Capabilities.ICapSupportedPatchCodeTypes.IsSupported)
            snap.PatchCodes = source.Capabilities.ICapSupportedPatchCodeTypes.GetValues().ToList();

        // 6) Barcodes
        if (source.Capabilities.ICapSupportedBarcodeTypes.IsSupported)
            snap.Barcodes = source.Capabilities.ICapSupportedBarcodeTypes.GetValues().ToList();

        // 7) ExtImageInfo disponibles
        if (source.Capabilities.ICapSupportedExtImageInfo.IsSupported)
            snap.ExtImageInfo = source.Capabilities.ICapSupportedExtImageInfo.GetValues().ToList();

        // 8) Booleans simples (todas estas se preguntan IsSupported)
        snap.Features = new Features
        {
            Duplex                = source.Capabilities.CapDuplex.IsSupported,
            AutoColor             = source.Capabilities.ICapAutomaticColorEnabled.IsSupported,
            AutoDeskew            = source.Capabilities.ICapAutomaticDeskew.IsSupported,
            AutoRotate            = source.Capabilities.ICapAutomaticRotate.IsSupported,
            AutoBorder            = source.Capabilities.ICapAutomaticBorderDetection.IsSupported,
            DiscardBlank          = source.Capabilities.ICapAutoDiscardBlankPages.IsSupported,
            DoubleFeedDetection   = source.Capabilities.CapDoubleFeedDetection.IsSupported,
            Imprinter             = source.Capabilities.CapPrinter.IsSupported,
            // ... etc
        };

        // 9) Caps custom — para cada ID 0x8000+, leer GetCurrent vía API genérica
        //    NTwain v3 expone DGControl.Capability.Get/Set para caps arbitrarias
        foreach (var customId in snap.CustomCapIds)
            snap.CustomCaps.Add(InspectCustomCap(source, customId));

        return snap;
    }
}
```

### 8.2 — `ProfileApplicator` (orden estricto)

```csharp
public static class ProfileApplicator
{
    public static void Apply(DataSource source, ScanProfile p, Fi6800CapabilitySnapshot snap)
    {
        var c = source.Capabilities;

        // ORDEN: Mech → PixelType → BitDepth → Resolución → Compression → resto
        TrySet(c.ICapXferMech, p.XferMech);

        if (p.AutoColor && snap.Features.AutoColor)
        {
            TrySet(c.ICapAutomaticColorEnabled, BoolType.True);
            TrySet(c.ICapAutomaticColorNonColorPixelType, p.AutoColorNonColorMode);
        }
        else
        {
            TrySet(c.ICapAutomaticColorEnabled, BoolType.False);
            TrySet(c.ICapPixelType, p.PixelType);
        }

        TrySet(c.ICapXResolution, (TWFix32)p.Dpi);
        TrySet(c.ICapYResolution, (TWFix32)p.Dpi);

        if (p.LongPage)
        {
            TrySet(c.ICapSupportedSizes, SupportedSize.None);
            // setear PhysicalHeight a longitud máxima en pulgadas
            TrySet(c.ICapPhysicalHeight, (TWFix32)(p.LongPageMaxLengthMm / 25.4f));
        }
        else
        {
            TrySet(c.ICapSupportedSizes, p.PaperSize);
        }

        TrySet(c.CapDuplexEnabled, p.Duplex ? BoolType.True : BoolType.False);
        TrySet(c.CapFeederEnabled, BoolType.True);
        TrySet(c.CapAutoFeed, BoolType.True);
        TrySet(c.CapXferCount, -1);

        TrySet(c.ICapAutomaticDeskew, p.AutoDeskew ? BoolType.True : BoolType.False);
        TrySet(c.ICapAutomaticBorderDetection, p.AutoBorder ? BoolType.True : BoolType.False);

        if (p.Rotation90Deg.HasValue)
            TrySet(c.ICapRotation, (TWFix32)p.Rotation90Deg.Value);
        else if (p.AutoRotate)
            TrySet(c.ICapAutomaticRotate, BoolType.True);

        if (p.DiscardBlankPages)
            TrySet(c.ICapAutoDiscardBlankPages, BoolType.True);

        TrySet(c.ICapBrightness, (TWFix32)p.Brightness);
        TrySet(c.ICapContrast,   (TWFix32)p.Contrast);

        TrySet(c.ICapCompression, p.Compression);
        if (p.Compression == CompressionType.Jpeg)
            TrySet(c.ICapJpegQuality, p.JpegQuality);

        if (p.PatchEnable)
        {
            TrySet(c.ICapPatchCodeDetectionEnabled, BoolType.True);
            TrySet(c.CapJobControl, JobControl.IncludeStop);
            // patch types: vía SearchPriorities
        }

        if (p.BarcodeEnable)
            TrySet(c.ICapBarcodeDetectionEnabled, BoolType.True);

        // Multifeed
        if (p.Multifeed != MultifeedMode.Off)
            TrySet(c.CapDoubleFeedDetection, /* convertir enum */);

        // Imprinter (si configurado)
        if (p.Imprinter != null)
            ApplyImprinter(c, p.Imprinter);
    }

    private static void TrySet<T>(ICapWrapper<T> cap, T value)
    {
        if (cap?.IsSupported != true || !cap.CanSet) return;
        cap.SetValue(value);
    }
}
```

### 8.3 — Manejo de Multi Image Output

```csharp
private void OnDataTransferred(object s, DataTransferredEventArgs e)
{
    // Multi-stream: cada hoja física puede generar 1, 2 o 3 eventos
    var info = e.GetExtImageInfo(
        ExtendedImageInfo.PageNumber,
        ExtendedImageInfo.PageSide,
        ExtendedImageInfo.PatchCode,
        ExtendedImageInfo.BarcodeText);

    var pageNumber = info[ExtendedImageInfo.PageNumber]?.ReadValues<uint>().FirstOrDefault();
    var pageSide   = info[ExtendedImageInfo.PageSide]?.ReadValues<ushort>().FirstOrDefault();
    var pixelType  = e.ImageInfo.PixelType;

    var page = new ScannedPage
    {
        PageNumber = (int)(pageNumber ?? 0),
        Side       = pageSide == 1 ? PageSide.Rear : PageSide.Front,
        PixelType  = pixelType,
        // ...
    };

    // Cada stream es una "ScannedPage" diferente — la UI las agrupa por PageNumber
    pages.Add(page);
}
```

### 8.4 — Cyotek ImageBox integración

```csharp
public partial class ImageViewerPanel : UserControl
{
    private readonly ImageBox imageBox = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = ImageBoxSizeMode.Fit,
        GridDisplayMode = ImageBoxGridDisplayMode.Client,
        AllowZoom = true,
        SelectionMode = ImageBoxSelectionMode.Rectangle
    };

    public void Display(Bitmap bmp)
    {
        imageBox.Image?.Dispose();
        imageBox.Image = bmp;
        imageBox.ZoomToFit();
    }

    public void RotateLeft()
    {
        if (imageBox.Image is Bitmap b) { b.RotateFlip(RotateFlipType.Rotate270FlipNone); imageBox.Invalidate(); }
    }

    public void RotateRight()
    {
        if (imageBox.Image is Bitmap b) { b.RotateFlip(RotateFlipType.Rotate90FlipNone);  imageBox.Invalidate(); }
    }

    public Bitmap CropSelection() => imageBox.GetSelectedImage();
}
```

---

## 9. Validación con escáner real

Antes de declarar "completo", probar con el fi-6800 físico:

- [ ] Escaneo simplex 1 página B&W A4 — verificar tamaño esperado
- [ ] Lote 50 páginas duplex RGB — medir velocidad real (debería ~110 ppm color)
- [ ] Patch code separator (imprimir un Patch III) — verifica que separa lotes
- [ ] Barcode QR en hoja — verifica `BarcodeText` llega
- [ ] Long page (3 m) — funciona sin error
- [ ] Multifeed deliberado (2 hojas pegadas) — driver lo detecta
- [ ] Cancel durante lote — no cuelga
- [ ] Hoja en blanco con `DiscardBlankPages` — se filtra
- [ ] 500 hojas seguidas — no hay leak de memoria
- [ ] `PaperJam` simulado — UI lo reporta y permite continuar tras despeje
- [ ] Multi-stream Color + B&W — recibimos 2 imágenes por hoja física
- [ ] Snapshot JSON — generar y comparar entre versiones de PSIP

---

## 10. Riesgos y mitigaciones

| Riesgo | Probabilidad | Impacto | Mitigación |
|--------|--------------|---------|------------|
| Caps custom de PaperStream sin documentar | Alta | Medio | Discovery runtime + UI dinámica que ofrece solo lo soportado |
| fi-6800 descontinuado, próximas PSIP no lo listen | Media | Alto | Fijar versión PSIP en el deployment, advertir al cliente |
| Win11 24H2 sin KB5055523 | Media | Alto | Pre-flight check obligatorio |
| `OutOfMemoryException` con lotes RGB grandes | Alta si no se previene | Alto | `XferMech.File`, thumbnails downsampled, originales en disco |
| Driver instalado en arquitectura distinta a la app | Media | Crítico (no funciona) | Pre-flight + mensaje claro + recomendar compilar x86 |
| Hardware imprinter no instalado pero perfil lo activa | Baja | Bajo | Detectar `CapPrinter.IsSupported = false` y deshabilitar tab |
| Patch detection en RGB falla (ver §19 REFERENCE) | Alta | Medio | Estrategia dual-stream o fallback a B&W |
| Cyotek `Image.RotateFlip` mutates source | Alta | Bajo | Clonar bitmap antes de rotar si el original se persiste |
| Multi Image Output rompe lógica "1 ScannedPage = 1 hoja" | Alta | Alto | Modelar `ScannedPage.PageNumber` como hoja física, agrupar streams en UI |

---

## Próximos pasos

Si confirmas el plan, procedo con la implementación en este orden:

1. **Fase 1 completa** (Pre-flight + Discovery + Diagnostics dialog) — el snapshot JSON nos dice exactamente qué soporta el escáner físico antes de seguir.
2. **Fase 2-3** (escaneo básico + thumbnails) para tener un MVP funcional.
3. **Fases 4-7** en paralelo según prioridad de tu caso de uso.

**Decisión que necesito de ti antes de codificar**:

- ¿x86 o x64 para la app? (recomendación: **x86**, funciona con cualquier driver TWAIN del fi-6800).
- ¿TFM target? (`net462`, `net6.0-windows` o `net8.0-windows`).
- ¿Qué casos de uso priorizar? Producción de texto OCR-friendly, archivo color de alta calidad, separación por patch codes, etc.
- ¿Tienes el fi-6800 físico disponible para probing? Sin él, parte de la Fase 1 se hace contra el simulador del PSIP (si existe en esa versión) o se asume basado en la spec genérica.

---

## Referencias

- [fi-6800 Global product page](https://www.pfu.ricoh.com/global/scanners/fi/discontinued/fi6800/)
- [fi-6800 datasheet (PFU)](https://www.pfu.ricoh.com/global/scanners/fi/discontinued/fi6800/specifications.html)
- [PaperStream IP solution](https://www.pfu-us.ricoh.com/scanners/fi/solutions/paperstream-ip)
- [PaperStream IP TWAIN downloads](https://www.pfu.ricoh.com/global/scanners/fi/support/software/)
- [Error Recovery Guide fi-6800/fi-6400](https://www.pfu.ricoh.com/global/scanners/erg/contents-68.html)
- [Win11 24H2 TWAIN fix KB5055523](https://learn.microsoft.com/en-us/answers/questions/3853422/)
- [Cyotek ImageBox repo](https://github.com/cyotek/Cyotek.Windows.Forms.ImageBox)
- [CyotekImageBox NuGet 1.3.1](https://www.nuget.org/packages/CyotekImageBox/)
- `NTWAIN_V3_GUIDE.md` (este repo) — flujo paso a paso
- `NTWAIN_V3_REFERENCE.md` (este repo) — referencia exhaustiva
