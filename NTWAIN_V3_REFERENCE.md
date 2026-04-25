# NTwain v3 — Referencia Completa para Implementación

Documento de referencia complementario a `NTWAIN_V3_GUIDE.md`. Cubre todo lo necesario para que un agente o desarrollador implemente NTwain v3 sin tener que leer el código fuente: setup, deployment, threading profundo, catálogo exhaustivo de enums/capacidades, patrones avanzados (async, multi-página, persistencia, cancelación) y solución de problemas operativos.

---

## Índice

1. [Setup del proyecto cliente](#1-setup-del-proyecto-cliente)
2. [Arquitectura: x86 vs x64 y DSM2](#2-arquitectura-x86-vs-x64-y-dsm2)
3. [App manifest y DPI awareness](#3-app-manifest-y-dpi-awareness)
4. [Threading model en profundidad](#4-threading-model-en-profundidad)
5. [Logging y diagnóstico](#5-logging-y-diagnóstico)
6. [Inyección de dependencias](#6-inyección-de-dependencias)
7. [Catálogo de enums (referencia rápida)](#7-catálogo-de-enums-referencia-rápida)
8. [Catálogo completo de capabilities](#8-catálogo-completo-de-capabilities)
9. [ReturnCode + ConditionCode](#9-returncode--conditioncode)
10. [Tipos especiales (`TWFix32`, `TWFrame`)](#10-tipos-especiales-twfix32-twframe)
11. [Operaciones DGControl directas](#11-operaciones-dgcontrol-directas)
12. [Cancelación: tres niveles](#12-cancelación-tres-niveles)
13. [Multi-página, JobControl y End-of-Job](#13-multi-página-jobcontrol-y-end-of-job)
14. [Patrón `ScanAsync` (Task + CancellationToken)](#14-patrón-scanasync-task--cancellationtoken)
15. [Multi-page TIFF / PDF](#15-multi-page-tiff--pdf)
16. [Persistencia: source y configuración](#16-persistencia-source-y-configuración)
17. [Progress reporting](#17-progress-reporting)
18. [Extended Image Info (patch / barcode / metadata)](#18-extended-image-info-patch--barcode--metadata)
19. [**Modo de escaneo vs detección de patch codes y barcodes**](#19-modo-de-escaneo-vs-detección-de-patch-codes-y-barcodes) ⚠️
20. [**Rotación: orientación, manual y automática**](#20-rotación-orientación-manual-y-automática) ⚠️
21. [**Modos de escaneo en profundidad**](#21-modos-de-escaneo-en-profundidad) ⚠️
22. [Testing y mocking](#22-testing-y-mocking)
23. [Deployment checklist](#23-deployment-checklist)
24. [Limitaciones conocidas de v3](#24-limitaciones-conocidas-de-v3)

---

## 1. Setup del proyecto cliente

### NuGet

```xml
<PackageReference Include="NTwain" Version="3.7.5" />
```

PackageId real: `NTwain` (https://www.nuget.org/packages/ntwain). Versión en `v3` actualmente: 3.7.5 (commit c5e2960). Assembly **firmado** (strong-name).

### TFMs soportados

| TFM | Notas |
|-----|-------|
| `net40` | Compatibilidad legacy (.NET 4.0) |
| `net462` | Recomendado para WinForms/WPF clásicos |
| `net5.0-windows` | Activa `UseWPF=true` y `UseWindowsForms=true` automático |
| `net6.0-windows` | Igual que net5 |

> No hay TFM `net8.0+` en v3. Si tu proyecto exige .NET 8+, debes usar net6.0-windows en compatibilidad o evaluar v4 (en beta).

### Sin dependencias externas

Solo referencia a `System.*`. No requiere paquetes adicionales.

---

## 2. Arquitectura: x86 vs x64 y DSM2

### Realidad del ecosistema TWAIN

- **NTwain.dll es AnyCPU.** El reto está en los drivers del escáner.
- **TWAIN tiene dos DSM (Data Source Managers):**

| DSM | Archivo | Arquitectura | Ubicación |
|-----|---------|--------------|-----------|
| Antiguo | `TWAIN_32.DLL` | Solo x86 | `C:\Windows\System32` (sí, ahí aún en x86 over WOW64) |
| Nuevo (DSM2) | `TWAINDSM.dll` (32) / `TWAINDSM64.dll` (64) | x86 + x64 | App folder, System32, Windows folder |

NTwain selecciona el DSM en `src/NTwain/PlatformInfo.cs` y lo carga en `Dsm.WinNew.cs` / `Dsm.WinOld.cs`.

### Orden de selección

1. Si app es **x64**: usa `TWAINDSM64.dll` (forzoso — `TWAIN_32.DLL` no existe en x64).
2. Si app es **x86** y `PreferNewDSM == true` (default): intenta `TWAINDSM.dll`.
3. Si DSM nuevo no se encuentra: cae a `TWAIN_32.DLL`.

### Configuración

```csharp
// Antes de session.Open(), si el driver legacy lo requiere:
NTwain.PlatformInfo.Current.PreferNewDSM = false;
```

### Decisión práctica

| Escenario | Compilar como |
|-----------|---------------|
| Escáner viejo (pre-2010), driver solo x86 | `<PlatformTarget>x86</PlatformTarget>` |
| Escáner moderno (Kodak, Fujitsu, Brother actuales con DSM2 x64) | `x64` o `AnyCPU` |
| No sabes qué driver tendrá el cliente | **x86 por defecto** — funciona con ambos |

### Detección en runtime

```csharp
bool app64 = NTwain.PlatformInfo.Current.IsApp64Bit; // basado en IntPtr.Size
```

---

## 3. App manifest y DPI awareness

Los samples no traen manifest, pero **producción debería incluirlo**:

```xml
<!-- app.manifest -->
<asmv3:assembly manifestVersion="1.0"
    xmlns="urn:schemas-microsoft-com:asm.v1"
    xmlns:asmv3="urn:schemas-microsoft-com:asm.v3">
  <asmv3:windowsSettings xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">
    <dpiAware>false</dpiAware>
  </asmv3:windowsSettings>
</asmv3:assembly>
```

**Por qué `dpiAware=false`**: los diálogos de los drivers TWAIN son legacy y no son DPI-aware. Si tu app sí lo es, las coordenadas de scan area, los rect del UI nativo y los diálogos modales se desincronizan en monitores HiDPI.

---

## 4. Threading model en profundidad

NTwain v3 **no expone un enum `ThreadingModel`**. Su comportamiento real:

### Mecanismos internos

1. **MessageLoopHook**: cada hook crea (o reutiliza) un thread STA con un dispatcher Win32 que recibe los mensajes de TWAIN del DSM.
2. **TWAIN callbacks nativos** (`TWCallback2` para 2.2+, `TWCallback` legacy): NTwain registra el callback en `TwainSessionInternal.cs:46-59`. El callback retorna inmediato y delega el trabajo al message loop con `BeginInvoke()` o `Invoke()`.
3. **`SynchronizationContext`**: si lo asignas, los eventos se hacen `syncer.Send(...)` (síncrono, bloquea hasta que el handler termina) en ese contexto.

### Dónde corren los eventos

| Configuración | Thread donde se disparan los eventos |
|---------------|--------------------------------------|
| `session.Open()` sin hook ni sync context | Thread interno STA (`InternalMessageLoopHook`) |
| `session.Open(WpfMessageLoopHook(hwnd))` | Dispatcher de la ventana (UI thread WPF) |
| `session.Open(WindowsFormsMessageLoopHook(hwnd))` | Application.MessageLoop (UI thread WinForms) |
| `session.SynchronizationContext = SynchronizationContext.Current` (en UI thread) | UI thread, vía `Send` síncrono |

### Reglas prácticas

- **WPF**: usa `WpfMessageLoopHook` o asigna `session.SynchronizationContext` antes de `Open()`. En cualquier caso, dentro del handler, usa `Dispatcher.BeginInvoke` para tocar UI si NO asignaste el context (los samples lo hacen así).
- **WinForms**: igual, con `WindowsFormsMessageLoopHook` o `Control.BeginInvoke`.
- **Console / servicio**: deja la inicialización default. Los eventos llegan en el thread interno; para acumular resultados usa estructuras thread-safe (`ConcurrentBag<T>`, lock).

### Particularidad HP Scanjet

`TwainSessionInternal.cs:274-287` documenta que algunos drivers (HP Scanjet) requieren `Invoke()` síncrono en lugar de `BeginInvoke()` — NTwain lo maneja internamente, no es algo que configuras.

---

## 5. Logging y diagnóstico

NTwain expone una interfaz `ILog` accesible vía `PlatformInfo.Current.Log`. Default: implementación que usa `System.Diagnostics.Trace`.

### Adaptador a Serilog / NLog / Microsoft.Extensions.Logging

```csharp
public class SerilogAdapter : NTwain.ILog
{
    private readonly Serilog.ILogger _log;
    public SerilogAdapter(Serilog.ILogger log) => _log = log;

    public void Debug(string format, params object[] args) => _log.Debug(format, args);
    public void Info (string format, params object[] args) => _log.Information(format, args);
    public void Error(string format, params object[] args) => _log.Error(format, args);
    public void Error(Exception ex, string format, params object[] args) => _log.Error(ex, format, args);
}

// Configuración (una vez, antes de crear TwainSession):
NTwain.PlatformInfo.Current.Log = new SerilogAdapter(Log.ForContext<App>());
```

### Activar trazas TWAIN (debugging)

`session.StopOnTransferError = true` hace que cualquier error en transferencia detenga el lote — útil en dev, **a desactivar en prod** porque algunos errores son recuperables.

---

## 6. Inyección de dependencias

`TwainSession` mantiene estado mutable interno (state machine, source cache, callbacks). **Registrar como singleton** por proceso.

```csharp
// Microsoft.Extensions.DependencyInjection
services.AddSingleton<TwainSession>(sp =>
{
    var appId = TWIdentity.CreateFromAssembly(
        DataGroups.Image,
        Assembly.GetEntryAssembly());
    return new TwainSession(appId);
});

// Tu servicio de escaneo lo recibe por constructor
services.AddSingleton<IScannerService, ScannerService>();
```

### Lifetime

| Lifetime | Apropiado |
|----------|-----------|
| Singleton | ✅ apps desktop, una ventana principal |
| Scoped | ❌ no aplica (no hay request scope en desktop) |
| Transient | ❌ romperás el state machine |

Si tienes múltiples ventanas concurrentes que escanean — no es soportado por TWAIN; el DSM es global por proceso.

---

## 7. Catálogo de enums (referencia rápida)

### `STATE`

| Estado | Valor |
|--------|-------|
| `DsmUnloaded` | 1 |
| `DsmLoaded` | 2 |
| `DsmOpened` | 3 |
| `SourceOpened` | 4 |
| `SourceEnabled` | 5 |
| `TransferReady` | 6 |
| `Transferring` | 7 |

### `PixelType` (modo de escaneo)

| Valor | Nombre | Uso |
|-------|--------|-----|
| 0 | `BlackWhite` | 1 bpp, ideal documentos texto |
| 1 | `Gray` | 8 bpp, OCR sobre fondo claro |
| 2 | `RGB` | Color 24 bpp (más común) |
| 3 | `Palette` | Indexada |
| 4 | `CMY` | Cyan/Magenta/Yellow |
| 5 | `CMYK` | + Black |
| 6-7 | `YUV` / `YUVK` | |
| 8-9 | `CieXYZ` / `Lab` | |
| 10-11 | `SRGB` / `SCRGB` | |
| 16 | `Infrared` | |

### `XferMech` (mecanismo de transferencia)

| Valor | Nombre | Cuándo usar |
|-------|--------|-------------|
| 0 | `Native` | 1 página, DIB en memoria, simple |
| 1 | `File` | ADF, páginas grandes; driver escribe el archivo |
| 2 | `Memory` | Streaming en chunks, avanzado |
| 4 | `MemFile` | Híbrido |

### `BoolType`

| Valor | Nombre |
|-------|--------|
| 0 | `False` |
| 1 | `True` |

### `SourceEnableMode`

| Nombre | Comportamiento |
|--------|----------------|
| `NoUI` | Inicia escaneo sin diálogo |
| `ShowUI` | Muestra UI del driver, usuario inicia |
| `ShowUIOnly` | Solo muestra UI para configurar, sin escanear |

### `SupportedSize` (tamaños de papel — los más usados)

| Valor | Nombre | Valor | Nombre |
|-------|--------|-------|--------|
| 0 | `None` | 13 | `A6` |
| 1 | `A4` | 19 | `A0` |
| 3 | `USLetter` | 20 | `A1` |
| 4 | `USLegal` | 21 | `A2` |
| 5 | `A5` | 11 | `A3` |
| 9 | `USLedger` | 52 | `USStatement` |
| 10 | `USExecutive` | 53 | `BusinessCard` |
| 2 | `JisB5` | 54 | `MaxSize` |

(Lista completa con A0–A10, B series ISO/JIS, C series, FourA0, TwoA0 — ver `src/NTwain/Data/TwainTypesExtended.cs`. ¡OJO con la corrección reciente: `IsoB9 = 32` tras PR #62!)

### `CompressionType`

| Valor | Nombre | Uso |
|-------|--------|-----|
| 0 | `None` | Sin comprimir |
| 1 | `PackBits` | TIFF clásico |
| 2 | `Group31D` (CCITT G3 1D) | Fax B&W |
| 4 | `Group32D` | Fax B&W mejorado |
| 5 | `Group4` (CCITT G4) | Mejor B&W |
| 6 | `Jpeg` | Color/gris |
| 7 | `Lzw` | TIFF, sin pérdida |
| 8 | `Jbig` | B&W avanzado |
| 9 | `Png` | |
| 10-11 | `Rle4` / `Rle8` | BMP |
| 14 | `Jpeg2000` | |

### `FileFormat` (para `XferMech.File`)

| Valor | Nombre | Valor | Nombre |
|-------|--------|-------|--------|
| 0 | `Tiff` | 6 | `TiffMulti` |
| 2 | `Bmp` | 7 | `Png` |
| 4 | `Jfif` (JPEG) | 9 | `Exif` |
| 5 | `Fpx` | 10 | `Pdf` |
| 8 | `Spiff` | 11 | `Jp2` (JPEG 2000) |
| | | 15 | `PdfA` |
| | | 16 | `PdfA2` |

### `BarcodeType` (cuando `ICapBarcodeDetectionEnabled = True`)

| Valor | Tipo | Valor | Tipo |
|-------|------|-------|------|
| 0 | `ThreeOfNine` (Code 39) | 9 | `Ean8` |
| 1 | `TwoOfFiveInterleaved` | 10 | `Ean13` |
| 4 | `Code128` | 11 | `PostNet` |
| 5 | `Ucc128` | 12 | `Pdf417` |
| 6 | `Codabar` | 19 | `MaxiCode` |
| 7 | `UpcA` | 20 | `QRCode` |
| 8 | `UpcE` | | |

### `PatchCode` (los 6 tipos estándar TWAIN)

| Valor | Tipo |
|-------|------|
| 0 | `Patch1` |
| 1 | `Patch2` |
| 2 | `Patch3` (separador típico de lotes) |
| 3 | `Patch4` |
| 4 | `Patch6` |
| 5 | `PatchT` (toggle) |

### `JobControl`

| Valor | Nombre | Comportamiento |
|-------|--------|----------------|
| 0 | `None` | Sin control de trabajos |
| 1 | `IncludeContinue` | Detector incluye separador, continúa |
| 2 | `IncludeStop` | Incluye separador, detiene |
| 3 | `ExcludeContinue` | Excluye separador, continúa |
| 4 | `ExcludeStop` | Excluye separador, detiene |

### `OrientationType` y `Unit`

```
Orientation: Rot0 (0), Rot90 (1), Rot180 (2), Rot270 (3), Auto (4)
Unit:        Inches (0), Centimeters (1), Picas, Points, Twips, Pixels (5), Millimeters (6)
```

### `DataGroups` (combinables con OR)

| Valor | Nombre |
|-------|--------|
| 0x0 | `None` |
| 0x1 | `Control` |
| 0x2 | `Image` |
| 0x4 | `Audio` |

`TWIdentity.CreateFromAssembly(DataGroups.Image, ...)` declara que la app maneja imágenes. Combina con `| DataGroups.Audio` solo si tu escáner es ese caso raro.

---

## 8. Catálogo completo de capabilities

`Capabilities` (en `DataSource.Capabilities`) expone ~120+ propiedades. Agrupadas:

### Imagen — Pixel/Color

```
ICapPixelType, ICapCompression, ICapBitDepth, ICapBitDepthReduction,
ICapBitOrder, ICapPlanarChunky, ICapPixelFlavor,
ICapAutomaticColorEnabled, ICapAutomaticColorNonColorPixelType,
ICapColorManagementEnabled, ICapImageFilter, ICapNoiseFilter
```

### Imagen — Resolución y geometría

```
ICapXResolution, ICapYResolution,
ICapXNativeResolution, ICapYNativeResolution,
ICapXScaling, ICapYScaling,
ICapPhysicalWidth, ICapPhysicalHeight,
ICapMinimumWidth, ICapMinimumHeight,
ICapMaxFrames, ICapFrames,                    // áreas de escaneo
ICapSupportedSizes, ICapOrientation, ICapRotation,
ICapUnits
```

### Imagen — Transformaciones

```
ICapBrightness, ICapContrast, ICapThreshold,
ICapGamma, ICapShadow, ICapHighlight,
ICapMirror, ICapHalftones, ICapXferMech,
ICapImageFileFormat, ICapJpegQuality, ICapJpegSubsampling
```

### Imagen — Auto-procesos

```
ICapAutoBright,
ICapAutomaticDeskew,
ICapAutomaticRotate,
ICapAutomaticBorderDetection,
ICapAutoDiscardBlankPages,
ICapAutomaticCropUsesFrame,
ICapAutomaticLengthDetection,
ICapAutomaticDeskewMaxAngle,
ICapAutoSize
```

### Imagen — Detección (barcode / patch)

```
ICapBarcodeDetectionEnabled, ICapSupportedBarcodeTypes,
ICapBarcodeMaxRetries, ICapBarcodeMaxSearchPriorities,
ICapBarcodeSearchMode, ICapBarcodeSearchPriorities,
ICapBarcodeTimeout,

ICapPatchCodeDetectionEnabled, ICapSupportedPatchCodeTypes,
ICapPatchCodeMaxRetries, ICapPatchCodeMaxSearchPriorities,
ICapPatchCodeSearchMode, ICapPatchCodeSearchPriorities,
ICapPatchCodeTimeout
```

### Imagen — Extended Image Info

```
ICapExtImageInfo, ICapSupportedExtImageInfo
```

### Control — Feeder / ADF

```
CapFeederEnabled, CapFeederLoaded,
CapAutoFeed, CapClearPage, CapFeedPage, CapRewindPage,
CapFeederOrder, CapFeederPocket, CapFeederAlignment,
CapFeederPrep, CapFeederThumbnailsEnabled,
CapPaperHandling, CapPaperDetectable
```

### Control — Duplex

```
CapDuplex, CapDuplexEnabled,
CapCameraSide, CapCameraOrder,
CapCameraEnabled, CapCameraPreviewUI
```

### Control — UI

```
CapUIControllable, CapEnableDSUIOnly, CapCustomDSData
```

### Control — Hardware

```
CapDeviceOnline, CapDeviceTimeDate,
CapAlarms, CapAlarmVolume,
CapDeviceEvent, CapPowerSupply, CapBatteryPercentage,
CapIndicators, CapIndicatorsMode
```

### Control — Batch

```
CapAutoScan, CapXferCount, CapMaxBatchBuffers,
CapJobControl, CapEndorser
```

### Control — Impresoras (endorser)

```
CapPrinter, CapPrinterEnabled, CapPrinterIndex,
CapPrinterMode, CapPrinterString, CapPrinterStringList,
CapPrinterCharRotation, CapPrinterFontStyle,
CapPrinterIndexLeadChar, CapPrinterIndexMaxValue,
CapPrinterIndexNumDigits, CapPrinterIndexStep,
CapPrinterIndexTrigger
```

### Audio

```
ACapXferMech
```

> Para descubrir runtime qué soporta el driver: itera `source.Capabilities.GetType().GetProperties()` o consulta `DGControl.Capability.GetCurrent` con un `CapabilityId` específico.

---

## 9. ReturnCode + ConditionCode

Después de cualquier op TWAIN, si el RC es `Failure` o `CheckStatus`, lee la `ConditionCode` para saber por qué.

### `ReturnCode`

| Valor | Nombre | Significado |
|-------|--------|-------------|
| 0 | `Success` | OK |
| 1 | `Failure` | Falló (ver `ConditionCode`) |
| 2 | `CheckStatus` | Llamar a `GetStatus` |
| 3 | `Cancel` | Usuario canceló |
| 4 | `DSEvent` | Evento del DS |
| 6 | `XferDone` | Transferencia terminada |
| 7 | `EndOfList` | Fin de enumeración |
| 8 | `InfoNotSupported` | Capability no soportada |
| 9 | `DataNotAvailable` | Sin datos ahora |
| 10 | `Busy` | Ocupado |
| 11 | `ScannerLocked` | Escáner bloqueado |

### `ConditionCode` (los que verás en producción)

| Valor | Nombre | Causa típica |
|-------|--------|--------------|
| 0 | `Success` | — |
| 1 | `Bummer` | Error genérico (revisa logs del driver) |
| 2 | `LowMemory` | Imagen muy grande / app sin RAM |
| 3 | `NoDS` | Source no abierto |
| 5 | `OperationError` | Op inválida |
| 6 | `BadCap` | Capability ID inválido |
| 9 | `BadProtocol` | App está en estado incorrecto |
| 10 | `BadValue` | Valor fuera de rango |
| 11 | `SeqError` | Llamada en estado incorrecto (ej: SetCap en state 5) |
| 13 | `CapUnsupported` | Driver no implementa esta cap |
| 14 | `CapBadOperation` | No se permite Set/Reset en esa cap |
| 15 | `CapSeqError` | Ej: SetCap en state ≠ 4 |
| 16 | `Denied` | Ej: papel atorado y app no recuperó |
| 17 | `FileExists` | File transfer no puede sobrescribir |
| 18 | `FileNotFound` | Path inválido |
| 20 | `PaperJam` | **Atasco** |
| 21 | `PaperDoubleFeed` | **Doble alimentación** |
| 22 | `FileWriteError` | Disco lleno / sin permisos |
| 23 | `CheckDeviceOnline` | Escáner desconectado / apagado |
| 24 | `Interlock` | Tapa abierta |
| 27 | `DocTooLight` | |
| 28 | `DocTooDark` | |
| 29 | `NoMedia` | Bandeja vacía |

### Patrón de manejo

```csharp
private void OnTransferError(object sender, TransferErrorEventArgs e)
{
    var cc = e.SourceStatus?.ConditionCode ?? ConditionCode.Bummer;
    string msg = cc switch
    {
        ConditionCode.PaperJam        => "Atasco de papel — quita el papel y vuelve a intentar.",
        ConditionCode.PaperDoubleFeed => "Doble alimentación detectada.",
        ConditionCode.NoMedia         => "Bandeja vacía.",
        ConditionCode.CheckDeviceOnline => "Escáner no responde — verifica conexión y energía.",
        ConditionCode.Interlock       => "Tapa abierta.",
        ConditionCode.LowMemory       => "Memoria insuficiente.",
        _ => $"Error TWAIN: {cc}"
    };
    NotifyUser(msg);
}
```

---

## 10. Tipos especiales (`TWFix32`, `TWFrame`)

### `TWFix32` (punto fijo 16.16)

Representa números fraccionarios como `int32` con 16 bits enteros + 16 fraccionarios.

```csharp
TWFix32 dpi = (TWFix32)300f;             // conversión implícita desde float/double
TWFix32 zero = TWFix32.Zero;
short whole = dpi.Whole;                 // 300
ushort frac = dpi.Fraction;              // 0
float back = (float)dpi;                 // 300.0
```

Se usa en: resoluciones (`ICapXResolution`), brillo/contraste (`ICapBrightness`), rotación (`ICapRotation`), gamma, threshold, shadow, highlight, dimensiones de frame.

Rango aproximado: -32768 a +32767, precisión 1/65536.

### `TWFrame` (área rectangular)

Para configurar áreas de escaneo personalizadas (`ICapFrames`):

```csharp
var frame = new TWFrame
{
    Left   = (TWFix32)0.0f,
    Top    = (TWFix32)0.0f,
    Right  = (TWFix32)8.5f,
    Bottom = (TWFix32)11.0f
};
// Unidades dependen de ICapUnits (default: pulgadas)
```

### `Enumeration<T>` y `Range<T>` (al leer `GetValues()`)

`source.Capabilities.ICapBrightness.GetValuesRaw()` puede retornar `OneValue<T>`, `Enumeration<T>` o `Range<T>` según cómo el driver expone la cap. `GetValues()` aplana a `IEnumerable<T>`.

---

## 11. Operaciones DGControl directas

Cuando `Capabilities` no expone algo, accede directo a triplets vía `source.DGControl.*`:

| Operación | Uso |
|-----------|-----|
| `PendingXfers.Get(out TWPendingXfers)` | Lee contador actual de páginas pendientes |
| `PendingXfers.EndXfer()` | Termina la transferencia actual (state 7→6) |
| `PendingXfers.Reset()` | Cancela todas las pendientes (state 6→6) |
| `SetupFileXfer.Get(out TWSetupFileXfer)` | Lee path/formato actual |
| `SetupFileXfer.Set(TWSetupFileXfer)` | Configura archivo (en `TransferReady`) |
| `SetupMemXfer.Get(out TWSetupMemXfer)` | Lee preferred memory size |
| `XferGroup.Get(ref DataGroups)` | Detecta si transfer es Image o Audio |
| `Status.GetSource(out TWStatus)` | CC actual (debugging) |
| `StatusUtf8.GetManager()` / `GetSource()` | String legible del último error |
| `CustomDSData.Get/Set` | Persistencia de configuración del driver (ver §16) |

NTwain ya hace `EndXfer` y `Reset` internamente en su transfer logic — solo invócalos manualmente para cancelaciones forzadas.

---

## 12. Cancelación: tres niveles

### Nivel 1 — Saltar página actual

```csharp
session.TransferReady += (s, e) => { e.CancelCurrent = true; };
```

`TransferReadyEventArgs.cs:37`. Se omite la transferencia actual; sigue con las pendientes.

### Nivel 2 — Cancelar lote completo

```csharp
session.TransferReady += (s, e) => { e.CancelAll = true; };
```

`TransferReadyEventArgs.cs:43`. Internamente llama `PendingXfers.Reset()` (`TransferLogic.cs:71`).

### Nivel 3 — Forzar bajada de estados (recuperación)

```csharp
session.ForceStepDown(targetState: 4);   // o 3, 2
```

`TwainSession.cs:392-457`. Ejecuta la secuencia `EndXfer → Reset → DisableDS → CloseDS` ignorando errores. Úsalo cuando el state machine se desincronizó (por ejemplo tras un crash del driver).

### Botón "Cancelar" desde otro thread

```csharp
private bool _stopRequested;

void OnCancelClicked()
{
    _stopRequested = true;
    // Si TransferReady aún no se ha disparado, cancelará en el próximo
}

session.TransferReady += (s, e) =>
{
    e.CancelAll = _stopRequested;
};

// Si el escaneo está colgado en un estado superior:
void OnEmergencyStop()
{
    session.ForceStepDown(4);
}
```

---

## 13. Multi-página, JobControl y End-of-Job

### Páginas en lote ADF

```csharp
source.Capabilities.CapFeederEnabled.SetValue(BoolType.True);
source.Capabilities.CapAutoFeed.SetValue(BoolType.True);
source.Capabilities.CapXferCount.SetValue(-1);    // todas las del feeder
```

### Saber cuántas faltan

`TransferReadyEventArgs.PendingTransferCount` contiene el número del *driver* (puede ser `-1` si no lo soporta).

```csharp
session.TransferReady += (s, e) =>
{
    UpdateProgress(currentPage: ++_done, pending: e.PendingTransferCount);
};
```

### Separar lotes con patch codes

```csharp
// Activar detección
source.Capabilities.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True);

// Decidir qué hacer cuando se detecta separador
source.Capabilities.CapJobControl.SetValue(JobControl.IncludeStop);
//   IncludeStop = el patch sheet llega como página y luego se detiene
//   ExcludeStop = no llega como página (filtrada) y se detiene
//   IncludeContinue / ExcludeContinue = sigue escaneando
```

Cuando llega el separador, `TransferReadyEventArgs.EndOfJob` (enum `EndXferJob`) tiene un valor distinto de `None`:

```csharp
session.TransferReady += (s, e) =>
{
    bool isEndOfBatch = e.EndOfJob != EndXferJob.None;
    if (isEndOfBatch) Log("Patch code detectado — fin de lote");
};
```

### Saber que TODO terminó

- Espera al evento `SourceDisabled` — el lote se cerró y volvimos al state 4.
- O: en `TransferReady`, `e.PendingTransferCount == 0`.

---

## 14. Patrón `ScanAsync` (Task + CancellationToken)

NTwain v3 es event-based. Para envolverlo en async se usa `TaskCompletionSource`:

```csharp
public static class TwainAsync
{
    public static Task<IList<Bitmap>> ScanAsync(
        TwainSession session,
        DataSource source,
        SourceEnableMode mode,
        IntPtr hwnd,
        CancellationToken ct = default)
    {
        var tcs    = new TaskCompletionSource<IList<Bitmap>>();
        var images = new List<Bitmap>();

        EventHandler<TransferReadyEventArgs>    onReady = null;
        EventHandler<DataTransferredEventArgs>  onData  = null;
        EventHandler<TransferErrorEventArgs>    onErr   = null;
        EventHandler                            onDis   = null;

        void Cleanup()
        {
            session.TransferReady    -= onReady;
            session.DataTransferred  -= onData;
            session.TransferError    -= onErr;
            session.SourceDisabled   -= onDis;
        }

        onReady = (s, e) =>
        {
            if (ct.IsCancellationRequested) e.CancelAll = true;
        };

        onData = (s, e) =>
        {
            try
            {
                if (e.NativeData != IntPtr.Zero)
                {
                    using var stream = e.GetNativeImageStream();
                    images.Add(new Bitmap(stream));
                }
                else if (!string.IsNullOrEmpty(e.FileDataPath))
                {
                    images.Add(new Bitmap(e.FileDataPath));
                }
            }
            catch (Exception ex)
            {
                Cleanup();
                tcs.TrySetException(ex);
            }
        };

        onErr = (s, e) =>
        {
            // No fallar el Task — registrar y dejar que el lote termine en SourceDisabled
            // Si quieres fallar: Cleanup(); tcs.TrySetException(...);
        };

        onDis = (s, e) =>
        {
            Cleanup();
            if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
            else                            tcs.TrySetResult(images);
        };

        session.TransferReady    += onReady;
        session.DataTransferred  += onData;
        session.TransferError    += onErr;
        session.SourceDisabled   += onDis;

        ct.Register(() =>
        {
            try { session.ForceStepDown(4); } catch { /* ignorar */ }
        });

        var rc = source.Enable(mode, modal: false, hwnd);
        if (rc != ReturnCode.Success)
        {
            Cleanup();
            tcs.TrySetException(new InvalidOperationException($"Enable falló: {rc}"));
        }

        return tcs.Task;
    }
}
```

### Uso

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var images = await TwainAsync.ScanAsync(session, source,
    SourceEnableMode.NoUI, hwnd, cts.Token);
```

> **Importante**: si tienes un `MessageLoopHook`, `Enable` no bloquea — los eventos llegarán async. Sin hook ni sync context, los eventos llegan en thread interno y el `await` resume en el mismo, así que añade `.ConfigureAwait(false)` o asigna `SynchronizationContext.Current` antes.

---

## 15. Multi-page TIFF / PDF

`XferMech.File` entrega **un archivo por página**. Para combinarlos:

### Multi-page TIFF con `System.Drawing`

```csharp
public static void CombineToMultipageTiff(IList<string> pageFiles, string outputPath)
{
    var tiffCodec = ImageCodecInfo.GetImageEncoders()
        .First(c => c.MimeType == "image/tiff");

    using var first = (Bitmap)Image.FromFile(pageFiles[0]);

    var encParams = new EncoderParameters(2);
    encParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
    encParams.Param[1] = new EncoderParameter(Encoder.Compression, (long)EncoderValue.CompressionLZW);

    first.Save(outputPath, tiffCodec, encParams);

    var addParams = new EncoderParameters(1);
    addParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);

    for (int i = 1; i < pageFiles.Count; i++)
    {
        using var page = (Bitmap)Image.FromFile(pageFiles[i]);
        first.SaveAdd(page, addParams);
    }

    var flush = new EncoderParameters(1);
    flush.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
    first.SaveAdd(flush);
}
```

### Multi-page PDF

NTwain no genera PDF. Opciones:

- **PdfSharp** (`PdfSharp.Drawing.XImage` + `XGraphics.DrawImage`).
- **iText / PdfPig**.
- Driver TWAIN moderno: configura `ICapImageFileFormat = FileFormat.Pdf` o `PdfA` y deja que el driver genere directamente. No todos lo soportan.

---

## 16. Persistencia: source y configuración

### Recordar el último escáner usado

```csharp
// Guardar al cerrar
Settings.Default.LastSourceId = session.CurrentSource.Identity.Id;
Settings.Default.LastSourceName = session.CurrentSource.Name;

// Restaurar (Name es más estable a través de versiones del driver)
var src = session.FirstOrDefault(s => s.Name == Settings.Default.LastSourceName)
       ?? session.DefaultSource;
```

`Identity.Id` puede cambiar entre instalaciones; `Name` es más estable pero puede variar entre versiones.

### Guardar/restaurar configuración del driver (DAT_CUSTOMDSDATA)

```csharp
// Guardar (state 4)
byte[] config = source.Settings;       // wrapper en DataSource.cs:177-229

// Persistir a disco
File.WriteAllBytes("scanner-profile.bin", config);

// Restaurar (state 4)
source.Settings = File.ReadAllBytes("scanner-profile.bin");
```

> El blob es **opaco y específico del driver**. No lo edites a mano. Usar la misma versión del driver para guardar y restaurar.

---

## 17. Progress reporting

`PendingTransferCount` solo está disponible dentro del evento `TransferReady`. Para una UI con barra de progreso:

```csharp
public class ScanProgressTracker
{
    public int CurrentPage   { get; private set; }
    public int Pending       { get; private set; }
    public int? Total        { get; private set; }   // si lo conoces de antemano

    public event Action<ScanProgressTracker> Changed;

    public void OnTransferReady(TransferReadyEventArgs e)
    {
        Pending = e.PendingTransferCount;
        if (Total == null && Pending > 0) Total = CurrentPage + Pending + 1;
        Changed?.Invoke(this);
    }

    public void OnDataTransferred()
    {
        CurrentPage++;
        Changed?.Invoke(this);
    }
}
```

Conecta en handlers:

```csharp
var tracker = new ScanProgressTracker();
session.TransferReady   += (s, e) => tracker.OnTransferReady(e);
session.DataTransferred += (s, e) => tracker.OnDataTransferred();
tracker.Changed += t =>
    Dispatcher.BeginInvoke(() => StatusLabel.Text =
        $"Página {t.CurrentPage} de {t.Total?.ToString() ?? "?"} (pendientes: {t.Pending})");
```

---

## 18. Extended Image Info (patch / barcode / metadata)

Activado vía:

```csharp
source.Capabilities.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True);
source.Capabilities.ICapBarcodeDetectionEnabled.SetValue(BoolType.True);
```

En `DataTransferred`:

```csharp
private void OnDataTransferred(object sender, DataTransferredEventArgs e)
{
    var info = e.GetExtImageInfo(
        ExtendedImageInfo.PatchCode,
        ExtendedImageInfo.BarcodeCount,
        ExtendedImageInfo.BarcodeText,
        ExtendedImageInfo.BarcodeType,
        ExtendedImageInfo.PageNumber,
        ExtendedImageInfo.PageSide,
        ExtendedImageInfo.SkewFinalAngle);

    var patch = info[ExtendedImageInfo.PatchCode]
        ?.ReadValues<ushort>().FirstOrDefault();
    var barcodes = info[ExtendedImageInfo.BarcodeText]
        ?.ReadValues<string>().ToList();
    var skew = info[ExtendedImageInfo.SkewFinalAngle]
        ?.ReadValues<TWFix32>().FirstOrDefault();
}
```

### Campos útiles de `ExtendedImageInfo`

| Categoría | Campos |
|-----------|--------|
| Patch | `PatchCode` |
| Barcode | `BarcodeCount`, `BarcodeText`, `BarcodeType`, `BarcodeX`, `BarcodeY`, `BarcodeRotation`, `BarcodeConfidence` |
| Doc | `PageNumber`, `PageSide`, `BookName`, `ChapterNumber`, `DocumentNumber`, `SegmentNumber` |
| Skew | `DeskewStatus`, `SkewOriginalAngle`, `SkewFinalAngle`, `SkewConfidence` |
| Calidad | `SpecklesRemoved`, `FormConfidence`, `MagData`, `MagType` |
| Otros | `EndorsedText`, `IccProfile`, `LastSegment`, `ImageMerged`, `PaperCount`, `Frame`, `PixelFlavor` |

---

## 19. Modo de escaneo vs detección de patch codes y barcodes

> **Esta sección es crítica.** El `PixelType` que elijas afecta directamente si los patch codes y barcodes se detectan correctamente, y muchas implementaciones fallan aquí.

### El hecho físico

Un **patch code** es una serie de barras negras paralelas sobre fondo blanco con un patrón de espaciado específico (Patch I, II, III, IV, VI, T). Un **barcode** es similar — alternancia binaria de marca/espacio. Las dos detecciones son **algoritmos de visión sobre imágenes binarias** (1-bit, blanco/negro).

Por eso, los firmwares de los escáneres y los drivers TWAIN ejecutan la detección **sobre un flujo bitonal**, no sobre el flujo color. Esto tiene consecuencias prácticas según el `PixelType` que pidas.

### Evidencia en el código del repo

`Spec/Kodak/kdscust.h:640-647` define un cap **custom no estándar**:

```c
// CAP_ENABLECOLORPATCHCODE
// Family:      Viper (3590)
// Type:        TWTY_BOOL
// Default:     FALSE
// Notes:       Controls recognition of the 3590 patch page.
#define CAP_ENABLECOLORPATCHCODE  0x8054
```

Kodak tuvo que **inventar un cap propietario solo para el 3590** porque en TWAIN estándar la detección de patch en color **no existe** — opera siempre sobre bitonal.

`Spec/Kodak/TWAIN_DualStream.htm` confirma: cuando `ICAP_PIXELTYPE = TWPT_RGB`, el driver desactiva automáticamente las cámaras bitonales (`/Camera_Bitonal_Top = FALSE`). Si solo recibes color, **no hay flujo bitonal y por tanto no hay patch detection**.

### Cómo afecta cada modo

| Modo (`PixelType`) | Patch detection | Barcode detection | Notas |
|---|---|---|---|
| `BlackWhite` | ✅ Nativo, óptimo | ✅ Nativo, óptimo | Modo "natural" para detección |
| `Gray` | ⚠️ Depende del driver — la mayoría binariza internamente | ⚠️ Igual | Threshold automático suele funcionar |
| `RGB` | ❌ Desactivado en muchos drivers | ⚠️ Algunos lo soportan, otros no | Sin flujo bitonal, sin detección |
| Dual-stream (color + bitonal vía DAT_FILESYSTEM) | ✅ Sobre el bitonal | ✅ Sobre el bitonal | Recibes ambos; la detección usa el bitonal y tú procesas el color |

### Tres estrategias de implementación

#### Estrategia A — Solo bitonal (la simple)

Cuando solo necesitas separar lotes con patch codes y la salida puede ser B&W:

```csharp
source.Capabilities.ICapPixelType.SetValue(PixelType.BlackWhite);
source.Capabilities.ICapXResolution.SetValue((TWFix32)200f);    // mínimo recomendado
source.Capabilities.ICapYResolution.SetValue((TWFix32)200f);
source.Capabilities.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True);
source.Capabilities.CapJobControl.SetValue(JobControl.IncludeStop);
```

Funciona en todos los drivers. Imágenes pequeñas (1-bit), detección óptima.

#### Estrategia B — Color/Gris con detección "asumida" (la pragmática)

Cuando necesitas color pero también patch codes. El driver puede o no soportarlo:

```csharp
source.Capabilities.ICapPixelType.SetValue(PixelType.RGB);
source.Capabilities.ICapXResolution.SetValue((TWFix32)200f);
source.Capabilities.ICapYResolution.SetValue((TWFix32)200f);

// Verificar primero si el driver soporta patch en este modo
var patchCap = source.Capabilities.ICapPatchCodeDetectionEnabled;
if (patchCap.IsSupported && patchCap.CanSet)
{
    var rc = patchCap.SetValue(BoolType.True);
    if (rc != ReturnCode.Success)
        Log("Driver no soporta patch en modo color — degradar a B&W o usar dual-stream");
}
```

Drivers modernos (Fujitsu fi-series, Kodak/Alaris i2000+, Brother, Canon DR) suelen soportar binarización interna y reportan patch correctamente. **Los drivers viejos fallan silenciosamente**: no devuelven error pero `ExtImageInfo[PatchCode]` siempre llega como 0.

**Validación obligatoria**: en dev, escanea un patch sheet conocido y verifica que `e.GetExtImageInfo(ExtendedImageInfo.PatchCode)` no retorna siempre 0.

#### Estrategia C — Dual-stream (la profesional, drivers Kodak / Fujitsu profesionales)

Recibes color y bitonal simultáneamente; la detección usa el bitonal, tú archivas el color:

```csharp
// Activa duplex y ambas "cámaras"
source.Capabilities.CapDuplexEnabled.SetValue(BoolType.True);

// Esto requiere DAT_FILESYSTEM, que NTwain v3 NO expone como wrapper de alto nivel.
// Hay que usar el triplet directo:
//   source.DGControl.FileSystem.ChangeDirectory("/Camera_Color_Both");
//   source.Capabilities.CapCameraEnabled.SetValue(BoolType.True);
//   source.DGControl.FileSystem.ChangeDirectory("/Camera_Bitonal_Both");
//   source.Capabilities.CapCameraEnabled.SetValue(BoolType.True);

// Patch detection sobre bitonal
source.Capabilities.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True);

// Orden de entrega: bitonal primero (para que llegue con el patch flag)
//   source.Capabilities.CapCameraOrder.SetValue(...);  // TWCM_BW_BOTH, TWCM_CL_BOTH

// En DataTransferred, detectar página side y pixel type por imagen
```

NTwain v3 no expone DAT_FILESYSTEM con un wrapper de alto nivel — hay que usar `source.DGControl` directamente o trabajar a nivel de triplet. Documentado en `Spec/Kodak/TWAIN_DualStream.htm` y `TWAIN_FileSystem.htm`.

> **Recomendación pragmática**: para apps que solo necesitan "color + patch para separar lotes", **Estrategia B con validación**. Si el driver no soporta, degradar transparentemente a Estrategia A y advertir al usuario.

### Resolución mínima recomendada

| Detección | DPI mínimo | DPI óptimo |
|-----------|-----------|------------|
| Patch code | 150 | 200–300 |
| Barcode 1D (Code 39, Code 128) | 200 | 300 |
| Barcode 2D (QR, PDF417, Data Matrix) | 300 | 400–600 |

Por debajo de 150 DPI las barras quedan demasiado finas para detección confiable. Por encima de 300 DPI no hay ganancia significativa y multiplicas tamaño de archivo.

### Configuración completa para "color + patch separation" (Estrategia B)

```csharp
private static void ConfigureColorWithPatchDetection(DataSource source)
{
    var caps = source.Capabilities;

    // Modo y resolución
    TrySet(caps.ICapXferMech,        XferMech.Native,        "XferMech");
    TrySet(caps.ICapPixelType,       PixelType.RGB,          "PixelType");
    TrySet(caps.ICapXResolution,     (TWFix32)200f,          "DPI X");
    TrySet(caps.ICapYResolution,     (TWFix32)200f,          "DPI Y");

    // ADF
    TrySet(caps.CapFeederEnabled,    BoolType.True,          "Feeder");
    TrySet(caps.CapAutoFeed,         BoolType.True,          "AutoFeed");
    TrySet(caps.CapXferCount,        -1,                     "XferCount");

    // Auto-procesos (mejora calidad de detección)
    TrySet(caps.ICapAutomaticDeskew,            BoolType.True, "Deskew");
    TrySet(caps.ICapAutomaticBorderDetection,   BoolType.True, "BorderDetect");

    // Detección de patch codes — el orden importa
    TrySet(caps.ICapPatchCodeDetectionEnabled,  BoolType.True, "PatchEnable");

    // Saber cuáles tipos detecta el driver
    if (caps.ICapSupportedPatchCodeTypes.IsSupported)
    {
        var supported = caps.ICapSupportedPatchCodeTypes.GetValues().ToList();
        Log($"Patch types soportados: {string.Join(",", supported)}");
    }

    // JobControl: IncludeStop = el patch sheet llega como página y luego se detiene el lote
    //             ExcludeStop = el patch sheet NO llega como página (filtrado por driver)
    TrySet(caps.CapJobControl,       JobControl.IncludeStop, "JobControl");

    // Verificación de soporte real (algunos drivers aceptan SetValue pero ignoran)
    var current = caps.ICapPatchCodeDetectionEnabled.GetCurrent();
    if (current != BoolType.True)
        Log("⚠️ Driver aceptó SetValue pero PatchCodeDetection no quedó activado — color puro probablemente no soportado");
}
```

### Lectura del patch en `DataTransferred`

```csharp
private void OnDataTransferred(object sender, DataTransferredEventArgs e)
{
    // ... procesar imagen ...

    PatchCode? detectedPatch = null;
    try
    {
        var info = e.GetExtImageInfo(
            ExtendedImageInfo.PatchCode,
            ExtendedImageInfo.PageSide,
            ExtendedImageInfo.PageNumber);

        var rawPatch = info[ExtendedImageInfo.PatchCode]
            ?.ReadValues<ushort>().FirstOrDefault();

        if (rawPatch.HasValue && rawPatch.Value > 0)
            detectedPatch = (PatchCode)(rawPatch.Value - 1);   // TWAIN usa 1-based; enum es 0-based
    }
    catch (Exception ex)
    {
        // Algunos drivers no implementan GetExtImageInfo aunque digan soportar patch
        Log($"GetExtImageInfo falló: {ex.Message}");
    }

    if (detectedPatch == PatchCode.Patch3)        // separador típico
        StartNewBatch();
    else if (detectedPatch.HasValue)
        Log($"Patch detectado: {detectedPatch}");
}
```

### Flag de validación durante desarrollo

Antes de publicar, ejecuta este test contra el escáner real:

```csharp
public static class PatchSupportValidator
{
    public static PatchSupportLevel Probe(DataSource source)
    {
        var caps = source.Capabilities;

        // Bitonal debería funcionar siempre
        caps.ICapPixelType.SetValue(PixelType.BlackWhite);
        if (caps.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True) != ReturnCode.Success)
            return PatchSupportLevel.None;

        // ¿En color?
        caps.ICapPixelType.SetValue(PixelType.RGB);
        var rcRgb = caps.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True);
        var actual = caps.ICapPatchCodeDetectionEnabled.GetCurrent();

        if (rcRgb == ReturnCode.Success && actual == BoolType.True)
            return PatchSupportLevel.Color;

        // Probar gris
        caps.ICapPixelType.SetValue(PixelType.Gray);
        var rcGray = caps.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True);
        if (rcGray == ReturnCode.Success && caps.ICapPatchCodeDetectionEnabled.GetCurrent() == BoolType.True)
            return PatchSupportLevel.Grayscale;

        return PatchSupportLevel.BitonalOnly;
    }

    public enum PatchSupportLevel { None, BitonalOnly, Grayscale, Color }
}
```

Documenta el resultado y úsalo para decidir el modo a configurar.

### Caso especial: barcodes en color

Los **barcodes 2D modernos** (QR, Data Matrix) suelen detectarse mejor en color/grises porque preservan los gradientes que ayudan al algoritmo a localizarlos. **Barcodes 1D** (Code 39, Code 128, EAN) están bien con bitonal o gris. Verifica con `ICapSupportedBarcodeTypes` qué soporta tu driver y prueba con muestras reales.

### Tabla de decisión rápida

| Caso de uso | PixelType recomendado | Patch | Barcode | Notas |
|-------------|----------------------|-------|---------|-------|
| Archivo OCR de documentos B&W | `BlackWhite`, 200-300 DPI | ✅ | ✅ | Mínimo overhead |
| Documentos color con separación por patch | `RGB`, 200 DPI + JobControl + validar | ⚠️ | ⚠️ | Probar driver, fallback B&W |
| Archivo color de alta calidad sin separación | `RGB`, 300 DPI | — | — | Sin caps de detección |
| Escaneo de cheques / IDs (color + barcode) | `RGB`, 300 DPI | — | ✅ | Confirmar tipo barcode soportado |
| Producción industrial Kodak/Fujitsu | Dual-stream | ✅ (bitonal) | ✅ (bitonal) | Recibe color + bitonal |
| Documentos con foto + texto | `Gray`, 300 DPI | ⚠️ | ✅ | Compromiso tamaño/calidad |

### Errores típicos por mala configuración del modo

| Síntoma | Causa probable |
|---------|----------------|
| `ICapPatchCodeDetectionEnabled.SetValue` retorna `Success` pero `GetExtImageInfo[PatchCode]` siempre 0 | Driver acepta el set pero opera solo sobre flujo bitonal que no estás recibiendo |
| Patch detection funciona en B&W pero no en color | Esperado — usa Estrategia A o C |
| `JobControl` no separa lotes | `ICapPatchCodeDetectionEnabled` no quedó realmente activado, o `JobControl.None` |
| Patch type devuelto incorrecto | Resolución muy baja (<150 DPI) o patch sheet de mala calidad |
| Solo detecta algunos tipos de patch | `ICapSupportedPatchCodeTypes` lista lo que soporta el driver; no hay garantía de los 6 |

---

## 20. Rotación: orientación, manual y automática

> Tres conceptos distintos y fáciles de confundir. Configurar mal cualquiera de los tres rota la imagen al revés o desactiva la detección por completo.

### Las tres capabilities y qué hace cada una

| Capability | Tipo | Qué hace | Cuándo usarlo |
|------------|------|----------|---------------|
| `ICapOrientation` | enum `OrientationType` | **Hint al driver** sobre cómo está físicamente colocado el papel en bandeja | Cuando sabes de antemano que cargas papel apaisado/vertical |
| `ICapRotation` | `TWFix32` (grados: 0/90/180/270) | **Rotación ortogonal explícita** post-scan, después de crop y deskew | Rotación manual fija ("siempre gira 90°") |
| `ICapAutomaticRotate` | `BoolType` | El driver **detecta el contenido y decide** la rotación | "Que el driver lo resuelva" |

> **Importante**: `ICapRotation` y `ICapAutomaticRotate` son **mutuamente exclusivos**. Según la spec Kodak (`Spec/Kodak/TWAIN_Features.htm:1483-1486`):
> - Si pones `ICapAutomaticRotate = True`, `ICapRotation` se resetea a 0.
> - Si cambias `ICapRotation`, `ICapAutomaticRotate` se desactiva automáticamente.

### Pipeline interno del escáner (orden de operaciones)

Según `Spec/Kodak/TWAIN_Features.htm:1350-1351` *("The rotation occurs after the image has been cropped and/or deskewed")*:

```
1. Captura raw del sensor
2. Crop / Border detection      (ICapAutomaticBorderDetection)
3. Deskew                       (ICapAutomaticDeskew)         ← corrige inclinación menor (1-15°)
4. Rotation                     (ICapRotation / ICapAutomaticRotate)  ← rotación ortogonal 90/180/270
5. Threshold / binarización     (si PixelType = BlackWhite)
6. Compression
7. Transfer al cliente
```

**Por qué importa el orden**: si tu hoja viene torcida y rotada 90°, el deskew corrige los grados de inclinación primero (1-15°), después la rotación ortogonal corrige la orientación (90/180/270°). Si activas solo rotation sin deskew, las hojas ligeramente torcidas se procesan inclinadas.

### `OrientationType` — los 7 valores

```csharp
public enum OrientationType : ushort
{
    Rot0       = 0,   // alias: Portrait
    Rot90      = 1,
    Rot180     = 2,
    Rot270     = 3,   // alias: Landscape
    Auto       = 4,   // driver decide automáticamente
    AutoTet    = 5,   // Auto Text Orientation (analiza texto)
    AutoPicture = 6   // Auto Picture (analiza orientación natural de imágenes)
}
```

`Spec/Kodak/TWAIN_Features.htm` confirma: **`Auto`, `AutoTet` y `AutoPicture` solo funcionan si el driver tiene "Automatic orthogonal rotation" soportada**. Si no, se ignoran o devuelven `CapBadValue`.

### Caso de uso: tu pregunta — papel vertical en bandeja apaisada

Cargas A4 portrait (8.5×11") en una bandeja que alimenta horizontal. La imagen sale rotada 90° respecto a cómo se lee. Tienes 4 caminos:

#### Opción 1 — Auto rotación (recomendado si hay texto)

```csharp
source.Capabilities.ICapAutomaticRotate.SetValue(BoolType.True);

// Combinar con deskew y border detection para mejor resultado
source.Capabilities.ICapAutomaticDeskew.SetValue(BoolType.True);
source.Capabilities.ICapAutomaticBorderDetection.SetValue(BoolType.True);
```

El driver analiza el contenido (texto, líneas) y decide si rotar 0/90/180/270°. **Funciona perfecto con documentos textuales**. **Falla** si:
- La página está casi en blanco
- Solo hay gráficos / fotos sin texto
- Resolución < 200 DPI (no hay detalle suficiente para detectar)
- El driver no soporta `ICapAutomaticRotate` (verifica con `IsSupported`)

#### Opción 2 — Rotación fija manual

Si **siempre** cargas el papel igual (ej. siempre vertical en bandeja horizontal), no necesitas detección — gira fijo:

```csharp
// Asegurarse que auto está OFF (por si quedó activo de antes)
source.Capabilities.ICapAutomaticRotate.SetValue(BoolType.False);

// Rotación ortogonal fija (TWFix32 desde float)
source.Capabilities.ICapRotation.SetValue((TWFix32)90f);   // o 180f / 270f
```

| Grados | Efecto sobre la imagen |
|--------|------------------------|
| 0 | Sin rotación |
| 90 | Gira 90° en sentido horario (right) |
| 180 | Voltea (head-to-foot) |
| 270 | Gira 90° antihorario / 90° izquierda (left) |

> **Nota**: TWAIN no estandariza el sentido (CW vs CCW). Algunos drivers usan 90° = horario, otros = antihorario. **Probar con un documento de prueba primero** y documentar el comportamiento del driver objetivo.

#### Opción 3 — Hint con `ICapOrientation`

Le dices al driver "el papel viene apaisado", y él aplica la transformación que considere correcta:

```csharp
source.Capabilities.ICapOrientation.SetValue(OrientationType.Rot90);
// El driver puede o no rotar la imagen — depende de la implementación
```

`ICapOrientation` es **menos predecible** que `ICapRotation` porque cada driver lo interpreta distinto. Algunos lo usan solo para reportar metadata; otros sí rotan. **Prefiere `ICapRotation` para rotación determinística**.

#### Opción 4 — Rotación en cliente (post-procesamiento)

Si el driver no soporta nada de lo anterior, gira en `DataTransferred` con System.Drawing:

```csharp
private void OnDataTransferred(object sender, DataTransferredEventArgs e)
{
    using var stream = e.GetNativeImageStream();
    var img = Image.FromStream(stream);

    // Rotación a la derecha 90°
    img.RotateFlip(RotateFlipType.Rotate90FlipNone);

    // Otras opciones:
    //   Rotate180FlipNone   — voltear
    //   Rotate270FlipNone   — 90° izquierda
    //   RotateNoneFlipX     — espejo horizontal
    //   RotateNoneFlipY     — espejo vertical (= flip vertical / "horizontal flip" según convención)

    Save(img);
}
```

Ventaja: control total y consistente entre drivers. Desventaja: más CPU/RAM en el cliente.

### Tabla de decisión rápida

| Escenario | Configuración recomendada |
|-----------|---------------------------|
| Documentos textuales heterogéneos (algunos vertical, otros horizontal) | `ICapAutomaticRotate = True` + `ICapAutomaticDeskew = True` |
| Siempre cargas igual (ej. siempre vertical) | `ICapRotation = 90/180/270` fijo |
| Solo fotos / sin texto | Rotación cliente (Opción 4) — auto-rotate fallaría |
| Documentos con códigos de barras / patch codes | `ICapAutomaticRotate = False` para no perder marcadores; rotar en cliente |
| Mezcla impredecible | `ICapAutomaticRotate` + fallback a rotación cliente si la confianza es baja |

### Ejemplo completo — auto-rotación con verificación

```csharp
private static void ConfigureAutoRotation(DataSource source)
{
    var caps = source.Capabilities;

    // Pre-requisitos para que auto-rotate funcione bien
    TrySet(caps.ICapAutomaticBorderDetection, BoolType.True,  "BorderDetection");
    TrySet(caps.ICapAutomaticDeskew,          BoolType.True,  "Deskew");

    // Resolución mínima
    var dpi = caps.ICapXResolution.GetCurrent();
    if ((float)dpi < 200f)
        TrySet(caps.ICapXResolution, (TWFix32)200f, "DPI X");

    // Auto-rotate
    if (caps.ICapAutomaticRotate.IsSupported && caps.ICapAutomaticRotate.CanSet)
    {
        var rc = caps.ICapAutomaticRotate.SetValue(BoolType.True);
        if (rc != ReturnCode.Success)
        {
            Log("Auto-rotate no disponible — degradar a rotación cliente");
            _useClientSideRotation = true;
        }
    }
    else
    {
        Log("Driver no soporta ICapAutomaticRotate");
        _useClientSideRotation = true;
    }
}
```

### Combinaciones que NO debes hacer

| Mal config | Por qué falla |
|-----------|---------------|
| `ICapRotation = 90` **y** `ICapAutomaticRotate = True` | El segundo set sobrescribe al primero — `ICapRotation` se resetea a 0 |
| `ICapAutomaticRotate = True` con `PixelType = RGB` y resolución 100 DPI | Puede fallar la detección — auto-rotate suele necesitar contenido reconocible y resolución decente |
| `ICapOrientation = Auto` sin verificar soporte | Drivers que no soportan auto la ignoran o devuelven `CapBadValue` |
| Rotar en cliente **y** activar auto-rotate del driver | Doble rotación → imagen al revés |
| `ICapRotation` antes de `ICapAutomaticBorderDetection` | El crop puede ocurrir DESPUÉS y dejar bordes asimétricos. Configurar caps en este orden: BorderDetection → Deskew → Rotation/AutoRotate |

### Verificar si el driver soporta auto-rotación realmente

Algunos drivers reportan `IsSupported = true` pero la rotación nunca se aplica. Test:

```csharp
public static bool ProbeAutoRotateActuallyWorks(DataSource source)
{
    var cap = source.Capabilities.ICapAutomaticRotate;
    if (!cap.IsSupported || !cap.CanSet) return false;

    var rc = cap.SetValue(BoolType.True);
    if (rc != ReturnCode.Success) return false;

    // Verificar lectura inversa
    return cap.GetCurrent() == BoolType.True;
}
```

Para validación funcional real, escanea una hoja de prueba conocidamente rotada y verifica que llega derecha.

### `ICapFlipRotation` — NO confundir

`ICapFlipRotation` (`FlipRotation` enum: `Book = 0`, `Fanfold = 1`) **no rota imágenes**. Define cómo se relaciona el reverso con el frente en duplex:

- `Book`: el reverso está orientado igual que el frente (libro abierto).
- `Fanfold`: el reverso está volteado 180° (papel continuo de impresora matricial).

Solo aplica si haces duplex y el papel viene de fuente especial (libros, fanfold). No es para tu caso de "rotar el papel vertical".

### Espejo (mirror) — caso especial

Si necesitas voltear horizontal/vertical (espejo, no rotación):

```csharp
source.Capabilities.ICapMirror.SetValue(MirrorType.Horizontal);  // o Vertical
```

Útil para escanear transparencias / negativos donde el contenido viene espejado.

### Resumen ejecutivo

1. **Auto-rotación** = `ICapAutomaticRotate = True` + buen contenido + ≥200 DPI + deskew y border detection activos.
2. **Rotación manual ortogonal** = `ICapRotation = 0/90/180/270` (TWFix32). 90° = derecha (típico, pero verificar driver).
3. **Hint físico al driver** = `ICapOrientation` (poco confiable, prefiere `ICapRotation`).
4. **Cliente** = `Bitmap.RotateFlip` en `DataTransferred` (siempre funciona, control total).
5. `ICapAutomaticRotate` y `ICapRotation` se cancelan mutuamente — no los pongas juntos.
6. La rotación interna ocurre después de crop y deskew — siempre activa esos también.
7. **`ICapFlipRotation` no es rotación** — es para duplex book/fanfold.

---

## 21. Modos de escaneo en profundidad

> El `PixelType` que eliges no es solo "blanco-y-negro vs color". Determina **bits por pixel**, **velocidad de escaneo física**, **codecs disponibles**, **caps activas/inactivas**, **calidad de detección automática** (deskew, border, patch, OCR) y **tamaño del archivo entre 24× y 96×**. Esta sección sintetiza todo el impacto.

### 21.1 — Tabla maestra de modos

| Modo (`PixelType`) | bpp | Bytes/pixel | Codecs típicos | Caso de uso típico |
|---|---|---|---|---|
| `BlackWhite` | 1 | 0.125 | CCITT G3/G4, JBIG, PackBits | Texto puro, archivo masivo, OCR rápido |
| `Gray` | 8 (a veces 4 o 16) | 1 | JPEG, LZW, PackBits, PNG | OCR de calidad, fotos B&N, documentos heterogéneos |
| `RGB` | 24 (a veces 48) | 3 | JPEG, JPEG2000, PNG, LZW | Documentos color, formularios marcados, archivo digital |
| `Palette` | 4 u 8 (indexado) | 0.5 - 1 | LZW, PackBits | Diagramas con paleta limitada (raro) |
| `CMY` | 24 | 3 | LZW | Pre-impresión (raro en escáneres modernos) |
| `CMYK` | 32 | 4 | LZW, JPEG | Pre-impresión profesional |
| `Infrared` | varía | varía | — | Detección de marcas no visibles, ocultas |

> **Comentario sobre Palette/CMY/CMYK/Infrared**: la mayoría de escáneres de oficina **no los soportan**. `ICapPixelType.GetValues()` en hardware típico devuelve solo `BlackWhite`, `Gray`, `RGB`. CMYK aparece en escáneres de producción gráfica.

### 21.2 — Tamaños reales por hoja A4 (cálculos)

Una hoja A4 mide 8.27 × 11.69 pulgadas. A distintas resoluciones, **sin comprimir**, una página single-side ocupa:

| DPI | Píxeles totales | B&W (1bpp) | Gray (8bpp) | RGB (24bpp) |
|-----|----------------|------------|-------------|-------------|
| 100 | 0.97 MP | 121 KB | 967 KB | 2.8 MB |
| 150 | 2.18 MP | 272 KB | 2.18 MB | 6.5 MB |
| **200** | **3.87 MP** | **484 KB** | **3.87 MB** | **11.6 MB** |
| **300** | **8.71 MP** | **1.09 MB** | **8.71 MB** | **26.1 MB** |
| 400 | 15.5 MP | 1.93 MB | 15.5 MB | 46.4 MB |
| 600 | 34.8 MP | 4.36 MB | 34.8 MB | 104 MB |

**Con compresión típica** (factor de reducción habitual):

| Modo + codec | Factor | A4 a 300 DPI comprimido |
|--------------|--------|-------------------------|
| B&W + CCITT G4 | ~10-50× | 30-100 KB |
| Gray + JPEG (Q=80) | ~10-15× | 600 KB - 1 MB |
| Gray + LZW | ~2-3× | 3-4 MB |
| RGB + JPEG (Q=80) | ~10-20× | 1.5-3 MB |
| RGB + JPEG2000 | ~15-30× | 1-2 MB |

> **Consecuencia operacional**: para un lote ADF de 1000 hojas a 300 DPI:
> - B&W G4: ~50 MB total — manejable en memoria, transferencia veloz.
> - RGB JPEG: ~2 GB — empieza a ser problema; usar `XferMech.File` (no Native) y procesar en streaming.

### 21.3 — Velocidad física de escaneo

Los escáneres modernos publican velocidad ppm (pages per minute). Pero esa cifra **es siempre con bitonal** a 200 DPI. La realidad:

| Modo | Velocidad relativa típica |
|------|---------------------------|
| B&W 200 DPI | 100% (referencia, ej. 80 ppm) |
| Gray 200 DPI | ~85% |
| RGB 200 DPI | ~60% |
| RGB 300 DPI | ~40% |
| RGB 600 DPI | ~15% |

Razones: el sensor CCD/CIS captura siempre 3 canales pero el firmware promedia a gris o binariza a B&W on-the-fly. La diferencia real está en el ancho de banda USB/transferencia y en si el motor de papel debe ralentizar para que el sensor lea más DPIs.

**Truco**: si tu escáner tiene "fast color mode", suele significar que escanea a su DPI nativo más cercano y luego escala — la velocidad sube pero la calidad baja.

### 21.4 — Compresiones disponibles por modo

`ICapCompression` es una capability cuyos valores válidos **dependen del PixelType actual**. Si configuras compresión antes que pixel type, el driver puede rechazar.

| Compression | B&W | Gray | RGB | Notas |
|-------------|-----|------|-----|-------|
| `None` | ✅ | ✅ | ✅ | Default — sin comprimir |
| `PackBits` | ✅ | ✅ | ✅ | RLE simple, baja eficiencia |
| `Group31D` (CCITT G3) | ✅ | ❌ | ❌ | Solo bitonal — fax 1D |
| `Group32D` | ✅ | ❌ | ❌ | Solo bitonal — fax 2D |
| `Group4` (CCITT G4) | ✅ | ❌ | ❌ | **Estándar de facto B&W** |
| `Jpeg` | ❌ | ✅ | ✅ | **Estándar para gris/color** — con pérdida |
| `Jbig` | ✅ | (rara) | ❌ | Bitonal de alta eficiencia, poco soportado |
| `Lzw` | ✅ | ✅ | ✅ | Sin pérdida, decente eficiencia |
| `Png` | ✅ | ✅ | ✅ | Sin pérdida; rara vez nativo en drivers |
| `Rle4`, `Rle8` | (paletizado) | (paletizado) | ❌ | Para BMP indexado |
| `Jpeg2000` | ❌ | ✅ | ✅ | Mejor que JPEG; soporte limitado |

**Orden recomendado**:

```csharp
// 1) PixelType primero
source.Capabilities.ICapPixelType.SetValue(PixelType.RGB);

// 2) Después compresión (ya sabe qué es válido)
source.Capabilities.ICapCompression.SetValue(CompressionType.Jpeg);

// 3) Para JPEG, calidad y subsampling
source.Capabilities.ICapJpegQuality.SetValue(80);  // 0-100
source.Capabilities.ICapJpegSubsampling.SetValue(JpegSubsampling.x422);
```

### 21.5 — Capabilities que solo aplican a un modo

#### Solo B&W (bitonal)

```csharp
ICapThreshold       // TWFix32: umbral de binarización (0-255)
ICapHalftones       // string: nombre de pattern de halftone
ICapBitOrder        // LsbFirst / MsbFirst (orden de bits)
ICapPixelFlavor     // Chocolate (0=blanco) / Vanilla (0=negro)
ICapFilter          // Drop-out: Red/Green/Blue/None — ignora ese canal al binarizar
ICapCcittKFactor    // Parámetro G3 2D
ICapTimeFill        // Bytes de relleno por fila para alinear
```

**`ICapPixelFlavor`** es sutil pero importante: define **qué pixel value representa el blanco** en el bitstream:
- `Chocolate` (default en muchos): bit 1 = negro, bit 0 = blanco.
- `Vanilla`: bit 1 = blanco, bit 0 = negro.

Si lees `NativeData` directamente y los colores salen invertidos, prueba esto. Algunos drivers lo cambian según el codec elegido sin avisar.

**`ICapFilter`** (drop-out color): útil para escanear formularios pre-impresos en color con texto a llenar a mano. Si el formulario es rojo y la letra azul, configura `FilterType.Red` y la binarización ignora todo lo rojo, dejando solo la respuesta del usuario.

```csharp
// Formulario con marcos rojos pre-impresos, queremos solo el manuscrito
source.Capabilities.ICapPixelType.SetValue(PixelType.BlackWhite);
source.Capabilities.ICapFilter.SetValue(FilterType.Red);  // descarta canal rojo
```

#### Solo Gray / RGB (continuous tone)

```csharp
ICapJpegQuality        // 0-100 (mayor = mejor calidad, archivo más grande)
ICapJpegSubsampling    // x444YCBCR, x422, x420, etc. — submuestreo cromático
ICapJpegPixelType      // pixel type para JPEG si difiere del general
ICapBitDepth           // 8 (estándar gris/color) o 16 (alta profundidad)
ICapBitDepthReduction  // método para bajar a menos bits
ICapPlanarChunky       // Chunky (RGBRGB...) o Planar (RRR..GGG..BBB..)
ICapGamma              // TWFix32: corrección gamma
ICapShadow / ICapHighlight  // TWFix32: límites de tono
```

**`ICapPlanarChunky`**: `Chunky` es el orden estándar (`RGBRGBRGB`); `Planar` separa canales (`RRRR...GGGG...BBBB`). **Casi todo el ecosistema usa Chunky**; cambiar a Planar rompe muchos viewers.

**`ICapJpegSubsampling`**: trade-off de tamaño vs calidad cromática:
- `x444` (sin subsampling): mejor calidad, archivo grande.
- `x422` (estándar JPEG): cromas reducidos a la mitad horizontal, ~33% más chico.
- `x420`: cromas a la mitad en ambos ejes, ~50% más chico, calidad aceptable para texto.
- `x411`, `x410`: más agresivos, solo para baja prioridad.

#### Aplica a todos pero con efecto distinto

```csharp
ICapBrightness   // En B&W desplaza el threshold; en gris/color ajusta luminancia
ICapContrast     // En B&W cambia la curva del threshold; en gris/color ajusta contraste lineal
```

### 21.6 — Auto-color (`ICapAutomaticColorEnabled`)

El driver detecta **por página** si es B&W puro o tiene color, y entrega el modo apropiado:

```csharp
source.Capabilities.ICapAutomaticColorEnabled.SetValue(BoolType.True);

// Modo a usar para páginas detectadas como NO-color (B&W o gris)
source.Capabilities.ICapAutomaticColorNonColorPixelType.SetValue(PixelType.BlackWhite);
```

Resultado: hojas de texto puro llegan como B&W (rápidas, pequeñas), hojas con marcas en color o fotos llegan como RGB. Cada `DataTransferred` puede traer modos diferentes — leer `e.ImageInfo.PixelType` para saber qué llegó.

**Caveats**:
- No todos los drivers lo soportan (verificar `IsSupported`).
- Aumenta el tiempo por página (el driver hace análisis adicional).
- Algunos drivers fallan con páginas poco contrastadas (gris claro detectado como color).

### 21.7 — Cómo el modo afecta a CADA proceso automático

Resumen consolidado del impacto:

| Proceso automático | Bitonal | Gray | RGB |
|--------------------|---------|------|-----|
| **Auto-deskew** (`ICapAutomaticDeskew`) | ✅ óptimo (alto contraste) | ✅ funciona bien | ⚠️ depende del driver |
| **Border detection** (`ICapAutomaticBorderDetection`) | ✅ óptimo | ✅ bueno | ⚠️ requiere fondo de bandeja contrastante |
| **Auto-rotate** (`ICapAutomaticRotate`) | ✅ óptimo (analiza patrones de texto) | ✅ funciona | ⚠️ varía mucho por driver |
| **Patch code detection** | ✅ nativo | ⚠️ binariza interno | ❌ desactivado en muchos (ver §19) |
| **Barcode 1D** | ✅ óptimo | ✅ bueno | ⚠️ depende |
| **Barcode 2D (QR, etc.)** | ⚠️ a veces falla | ✅ bueno | ✅ óptimo (gradientes) |
| **Auto-discard blank pages** | ✅ óptimo | ✅ bueno | ⚠️ páginas con leve color falsean |
| **OCR (post-proceso)** | ✅ excelente | ✅ excelente | ⚠️ tiene que binarizarse antes |
| **Imprint / endorser** (huella en papel) | OK | OK | OK (no afecta) |
| **Multifeed / detección doble alimentación** | igual | igual | igual (es ultrasonido, no imagen) |

**Lectura clave**: si tu pipeline incluye OCR, patch codes, barcodes 1D o auto-rotate de texto, **prefiere bitonal o gris**. RGB añade tamaño y a veces complica las detecciones automáticas.

### 21.8 — Bit depth y su interacción

`ICapBitDepth` define los bits efectivos por canal:

| BitDepth | Combina con | Efecto |
|----------|-------------|--------|
| 1 | `BlackWhite` | Estándar (1 bit por pixel) |
| 4 | `Gray` o `Palette` | 16 niveles — raro, casi obsoleto |
| 8 | `Gray` o `Palette` o `RGB` (8/canal) | **Estándar** — 256 niveles |
| 16 | `Gray` o `RGB` (16/canal) | Alta profundidad — fotografía profesional |
| 24 | `RGB` total | Equivalente a 8/canal |
| 32 | `CMYK` total | Equivalente a 8/canal |
| 48 | `RGB` 16/canal | Fotografía científica |

**Setear bit depth a un valor inválido para el `PixelType` actual = `BadValue`.** Configurar siempre `PixelType` antes que `BitDepth`.

### 21.9 — Recetas por caso de uso

#### Documento textual masivo para OCR / archivo

```csharp
caps.ICapPixelType.SetValue(PixelType.BlackWhite);
caps.ICapXResolution.SetValue((TWFix32)300f);
caps.ICapYResolution.SetValue((TWFix32)300f);
caps.ICapCompression.SetValue(CompressionType.Group4);
caps.ICapImageFileFormat.SetValue(FileFormat.Tiff);

// Mejorar calidad B&W
caps.ICapAutomaticDeskew.SetValue(BoolType.True);
caps.ICapAutomaticBorderDetection.SetValue(BoolType.True);
caps.ICapAutomaticRotate.SetValue(BoolType.True);

// Threshold automático suele venir bien; ajustar solo si OCR falla
// caps.ICapThreshold.SetValue((TWFix32)128f);
```

Resultado: ~50-100 KB por hoja, OCR limpio.

#### Documento heterogéneo (texto + fotos) con auto detección

```csharp
caps.ICapAutomaticColorEnabled.SetValue(BoolType.True);
caps.ICapAutomaticColorNonColorPixelType.SetValue(PixelType.Gray);
caps.ICapXResolution.SetValue((TWFix32)300f);
caps.ICapYResolution.SetValue((TWFix32)300f);
caps.ICapCompression.SetValue(CompressionType.Jpeg);
caps.ICapJpegQuality.SetValue(85);
caps.ICapJpegSubsampling.SetValue(JpegSubsampling.x422);
caps.ICapAutomaticDeskew.SetValue(BoolType.True);
caps.ICapAutomaticBorderDetection.SetValue(BoolType.True);
```

Resultado: páginas de solo texto a ~200 KB (gris JPEG), páginas con foto a ~1-2 MB.

#### Formulario pre-impreso color, capturar solo manuscrito

```csharp
caps.ICapPixelType.SetValue(PixelType.BlackWhite);
caps.ICapFilter.SetValue(FilterType.Red);   // ignorar tinta roja del formulario
caps.ICapXResolution.SetValue((TWFix32)200f);
caps.ICapCompression.SetValue(CompressionType.Group4);
caps.ICapThreshold.SetValue((TWFix32)100f);  // ajustar según contraste del manuscrito
```

#### Foto / negativo / archivo digital alta calidad

```csharp
caps.ICapPixelType.SetValue(PixelType.RGB);
caps.ICapBitDepth.SetValue(24);    // o 48 si soporta
caps.ICapXResolution.SetValue((TWFix32)600f);
caps.ICapYResolution.SetValue((TWFix32)600f);
caps.ICapCompression.SetValue(CompressionType.None);   // sin pérdida
caps.ICapImageFileFormat.SetValue(FileFormat.Tiff);
caps.ICapAutomaticDeskew.SetValue(BoolType.False);    // NO tocar foto
caps.ICapAutomaticBorderDetection.SetValue(BoolType.False);
```

#### Cheques / IDs (color + códigos)

```csharp
caps.ICapPixelType.SetValue(PixelType.RGB);
caps.ICapXResolution.SetValue((TWFix32)300f);
caps.ICapCompression.SetValue(CompressionType.Jpeg);
caps.ICapJpegQuality.SetValue(90);
caps.ICapBarcodeDetectionEnabled.SetValue(BoolType.True);
caps.ICapAutomaticDeskew.SetValue(BoolType.True);
// Ojo: patch code en RGB suele no funcionar — ver §19
```

### 21.10 — Validación runtime de soporte

No todos los drivers soportan todos los modos. Antes de configurar, descubrir lo soportado:

```csharp
public static class ScanModeProbe
{
    public static ScanModeSupport Probe(DataSource source)
    {
        var caps = source.Capabilities;

        var pixelTypes = caps.ICapPixelType.IsSupported
            ? caps.ICapPixelType.GetValues().ToList()
            : new List<PixelType>();

        var compressions = caps.ICapCompression.IsSupported
            ? caps.ICapCompression.GetValues().ToList()
            : new List<CompressionType>();

        var bitDepths = caps.ICapBitDepth.IsSupported
            ? caps.ICapBitDepth.GetValues().ToList()
            : new List<ushort>();

        var supportsAutoColor = caps.ICapAutomaticColorEnabled.IsSupported;

        return new ScanModeSupport
        {
            PixelTypes      = pixelTypes,
            Compressions    = compressions,
            BitDepths       = bitDepths,
            HasAutoColor    = supportsAutoColor,
            HasJpegQuality  = caps.ICapJpegQuality.IsSupported,
            HasFilter       = caps.ICapFilter.IsSupported,
            HasThreshold    = caps.ICapThreshold.IsSupported
        };
    }
}
```

Usa el resultado para construir tu UI dinámicamente — solo ofrecer al usuario opciones realmente soportadas.

### 21.11 — Errores típicos al configurar el modo

| Síntoma | Causa probable |
|---------|----------------|
| `SetValue(PixelType.RGB)` retorna `Failure` / `BadValue` | Driver no soporta ese modo o estás fuera del state 4 |
| Imagen RGB sale como gris | `ICapBitDepth` quedó en 8 (gris) — setearlo a 24 después del PixelType |
| Imagen B&W sale invertida (negro→blanco) | `ICapPixelFlavor` cambió — probar `Chocolate` o `Vanilla` |
| `Compression.Group4` da `BadValue` en RGB | G4 solo es válido en bitonal — orden: PixelType → Compression |
| JPEG quality no afecta nada | Driver ignora la cap o estás en modo bitonal sin JPEG |
| `AutomaticColorEnabled` activado pero todas las páginas llegan RGB | Umbral de detección del driver muy bajo / páginas con leve tinte |
| Velocidad cae mucho al cambiar a RGB | Esperado — RGB exige ~3× más bandwidth |
| Archivo TIFF muy grande | No configuraste compresión: añadir CCITT G4 (B&W) o LZW (gris/color) |
| Fila desplazada / artefactos en bitstream | `ICapBitOrder` o `ICapTimeFill` mal configurados — restablecer a defaults |
| Colores de salida con tinte verde/púrpura | `ICapJpegSubsampling` muy agresivo (x410, x411) — usar x422 mínimo |

### 21.12 — Resumen ejecutivo

1. **B&W** = 1 bpp, archivos diminutos, OCR-ready, óptimo para detección automática (patch, deskew, border, auto-rotate). 96× más eficiente en bytes que RGB.
2. **Gray** = 8 bpp, mejor para fotos B&N y OCR de calidad. Sweet spot entre tamaño y fidelidad. Compatible con casi todas las detecciones.
3. **RGB** = 24 bpp, único modo color realista. Más lento, archivos 24× más grandes, algunas detecciones automáticas se desactivan.
4. **`AutomaticColorEnabled`** = lo mejor de B&W y RGB cuando el driver lo soporta y las páginas están bien contrastadas.
5. **Compresión depende del modo**: G4/JBIG → bitonal; JPEG/JPEG2000 → gris/color; LZW/PNG → todos.
6. **Caps específicas**: `Threshold/Halftones/Filter/PixelFlavor` = solo bitonal; `JpegQuality/Subsampling/Gamma/Shadow/Highlight` = solo gris/color.
7. **Orden de configuración crítico**: `PixelType` → `BitDepth` → `Compression` → `JpegQuality/Subsampling`.
8. **A más DPI o más bpp = más lento, más datos, más memoria, transferencia más lenta**. Usar `XferMech.File` para lotes RGB grandes.
9. **Validar runtime** con `GetValues()` qué soporta el driver — no asumir.

---

## 22. Testing y mocking

`TwainSession` implementa `ITwainSession` (interfaz pública). `IDataSource` es internal, lo que limita el mocking directo.

### Estrategia recomendada — wrapper de aplicación

```csharp
public interface IScannerService
{
    IReadOnlyList<ScannerInfo> ListScanners();
    Task<IList<ScannedPage>> ScanAsync(ScannerInfo scanner, ScanProfile profile, CancellationToken ct);
}

// Implementación real envuelve TwainSession
public class TwainScannerService : IScannerService { /* ... */ }

// Mock para tests
public class FakeScannerService : IScannerService
{
    public IReadOnlyList<ScannerInfo> ListScanners() =>
        new[] { new ScannerInfo("Test Scanner", 1) };
    public Task<IList<ScannedPage>> ScanAsync(...) =>
        Task.FromResult<IList<ScannedPage>>(new[] { TestPage() });
}
```

### Pruebas de integración con escáner real

- Necesitas hardware o un driver TWAIN simulator (existe `TWAIN Working Group Sample DS`).
- En CI/CD: skip los tests de integración (`[Trait("Category", "RequiresScanner")]`) y solo correrlos en máquina dev con hardware.

---

## 23. Deployment checklist

- [ ] Decidir arquitectura: **x86** por defecto si no controlas qué driver tendrá el cliente.
- [ ] Agregar `<PlatformTarget>x86</PlatformTarget>` (o x64 si confirmaste DSM2).
- [ ] Incluir `app.manifest` con `dpiAware=false`.
- [ ] Strong-named: ✅ NTwain.dll viene firmado.
- [ ] Implementar adaptador `ILog` si tu app usa Serilog/NLog.
- [ ] Registrar `TwainSession` como **singleton** en DI.
- [ ] Asignar `session.SynchronizationContext = SynchronizationContext.Current` en el thread UI antes de `Open()`, o usar `MessageLoopHook` apropiado.
- [ ] Manejar `TransferError` con mensajes amigables para `PaperJam`, `NoMedia`, `CheckDeviceOnline`.
- [ ] Cierre correcto en `SourceDisabled` y `Dispose` (con `ForceStepDown` como red de seguridad).
- [ ] Documentar al usuario final: "Requires TWAIN driver to be installed separately."
- [ ] **NO** distribuir `TWAINDSM.dll` (Windows 7+ lo trae); si tu cliente usa drivers legacy puros, documenta descarga desde sourceforge.net/projects/twain-dsm.
- [ ] Probar en máquina con escáner real antes de release. **Los simuladores no detectan todas las quirks de drivers reales.**
- [ ] Si tu app tiene auto-update: `TwainSession` debe cerrarse limpiamente antes de reemplazar binarios — `NTwain.dll` puede quedar lockeado por la DSM.

---

## 24. Limitaciones conocidas de v3

| Limitación | Workaround |
|-----------|------------|
| No es async-first; todo es event-based | Envolver con `TaskCompletionSource` (§14) |
| Solo soporta TWAIN 2.2/2.3 (no 2.5) | v4 lo añadirá; en v3 no hay paridad con caps nuevas |
| macOS / Linux solo en Mono (legacy) | No usar en producción cross-platform |
| No genera PDF nativamente | Driver lo hace si soporta `FileFormat.Pdf`, o usar PdfSharp |
| `ITwainSession` cubre lo público pero `IDataSource` es internal | Crear tu propio wrapper (`IScannerService`) |
| Multi-source concurrente no es soportado | TWAIN DSM es global por proceso; serializar |
| Algunos drivers ignoran `NoUI` y muestran su UI igual | Aceptarlo o cambiar a otro driver |
| `PendingTransferCount = -1` en algunos drivers | No mostrar total exacto, solo "Página N…" |
| Strings ANSI vs UTF-8 (TWAIN viejo es ANSI) | NTwain ya maneja conversión vía `StatusUtf8` cuando aplica |

---

## Apéndice — Mapeo rápido constante TWAIN → propiedad NTwain

| TWAIN constant | NTwain property |
|----------------|-----------------|
| `ICAP_PIXELTYPE` | `Capabilities.ICapPixelType` |
| `ICAP_XRESOLUTION` | `Capabilities.ICapXResolution` |
| `ICAP_YRESOLUTION` | `Capabilities.ICapYResolution` |
| `ICAP_BRIGHTNESS` | `Capabilities.ICapBrightness` |
| `ICAP_CONTRAST` | `Capabilities.ICapContrast` |
| `ICAP_ROTATION` | `Capabilities.ICapRotation` |
| `ICAP_SUPPORTEDSIZES` | `Capabilities.ICapSupportedSizes` |
| `ICAP_AUTOMATICDESKEW` | `Capabilities.ICapAutomaticDeskew` |
| `ICAP_AUTOMATICBORDERDETECTION` | `Capabilities.ICapAutomaticBorderDetection` |
| `ICAP_AUTOMATICROTATE` | `Capabilities.ICapAutomaticRotate` |
| `ICAP_COMPRESSION` | `Capabilities.ICapCompression` |
| `ICAP_XFERMECH` | `Capabilities.ICapXferMech` |
| `ICAP_PATCHCODEDETECTIONENABLED` | `Capabilities.ICapPatchCodeDetectionEnabled` |
| `ICAP_BARCODEDETECTIONENABLED` | `Capabilities.ICapBarcodeDetectionEnabled` |
| `ICAP_IMAGEFILEFORMAT` | `Capabilities.ICapImageFileFormat` |
| `ICAP_JPEGQUALITY` | `Capabilities.ICapJpegQuality` |
| `ICAP_FRAMES` | `Capabilities.ICapFrames` |
| `CAP_FEEDERENABLED` | `Capabilities.CapFeederEnabled` |
| `CAP_AUTOFEED` | `Capabilities.CapAutoFeed` |
| `CAP_DUPLEXENABLED` | `Capabilities.CapDuplexEnabled` |
| `CAP_XFERCOUNT` | `Capabilities.CapXferCount` |
| `CAP_JOBCONTROL` | `Capabilities.CapJobControl` |
| `CAP_CUSTOMDSDATA` | `Capabilities.CapCustomDSData` (raw) o `DataSource.Settings` (helper) |
| `CAP_DEVICEONLINE` | `Capabilities.CapDeviceOnline` |
| `CAP_PRINTERENABLED` | `Capabilities.CapPrinterEnabled` |

---

**Documento hermano**: `NTWAIN_V3_GUIDE.md` cubre el flujo paso a paso con un ejemplo end-to-end. Este documento es la referencia exhaustiva para situaciones específicas.
