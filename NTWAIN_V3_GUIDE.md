# Guía de Implementación — NTwain v3

Configuración completa de un escáner usando NTwain v3 (rama `v3`, fork `metagamma/ntwain`).
Todas las referencias a archivos apuntan al código real del repositorio.

---

## Índice

1. [Modelo mental: estados TWAIN](#1-modelo-mental-estados-twain)
2. [Orden estricto de implementación](#2-orden-estricto-de-implementación)
3. [Paso 1 — Inicialización (`TwainSession`)](#paso-1--inicialización-twainsession)
4. [Paso 2 — Suscripción a eventos](#paso-2--suscripción-a-eventos)
5. [Paso 3 — Apertura del DSM (estado 2 → 3)](#paso-3--apertura-del-dsm-estado-2--3)
6. [Paso 4 — Selección y apertura del Source (estado 3 → 4)](#paso-4--selección-y-apertura-del-source-estado-3--4)
7. [Paso 5 — Configuración de capacidades (estado 4)](#paso-5--configuración-de-capacidades-estado-4)
8. [Paso 6 — Habilitación del escaneo (estado 4 → 5)](#paso-6--habilitación-del-escaneo-estado-4--5)
9. [Paso 7 — Recepción de imágenes](#paso-7--recepción-de-imágenes)
10. [Paso 8 — Cierre limpio](#paso-8--cierre-limpio)
11. [Ejemplo end-to-end](#11-ejemplo-end-to-end)
12. [Errores comunes](#12-errores-comunes)

---

## 1. Modelo mental: estados TWAIN

NTwain (y TWAIN en general) opera como una máquina de estados estricta. **No se puede saltar pasos**; cada operación solo es válida en su estado.

| Estado | Nombre | Qué se puede hacer |
|--------|--------|--------------------|
| 1 | `DsmUnloaded` | (estado inicial, antes de construir `TwainSession`) |
| 2 | `DsmLoaded` | Tras construir la sesión; aún no se cargó el Source Manager |
| **3** | `DsmOpened` | DSM cargado: enumerar fuentes, seleccionar una |
| **4** | `SourceOpen` | Source abierto: **único momento para configurar capabilities** |
| 5 | `SourceEnabled` | Escáner habilitado, esperando trigger del usuario / ADF |
| 6 | `TransferReady` | Hay datos pendientes; el evento `TransferReady` se dispara |
| 7 | `Transferring` | Transferencia activa; tras terminar regresa a 6 o 5 |

> **Regla de oro**: las capabilities se configuran **solo en estado 4**, antes de llamar `Enable()`.
> Una vez en estado 5+, los `SetValue()` fallan o se ignoran.

Definiciones en `src/NTwain/TwainSession.cs:38-46` y `src/NTwain/State.cs`.

---

## 2. Orden estricto de implementación

```
[Construir TwainSession]
        │
[Suscribir eventos]    ← antes de Open(), para no perder StateChanged tempranos
        │
[session.Open(hook)]                       Estado 2 → 3
        │
[Enumerar fuentes y seleccionar]
        │
[source.Open()]                            Estado 3 → 4
        │
[Configurar capabilities]                  ◄── BRILLO, ROTACIÓN, MODO, ETC.
   ├── XferMech (PRIMERO siempre)
   ├── PixelType (modo de escaneo)
   ├── Resolution (X y Y)
   ├── PaperSize (SupportedSizes)
   ├── Duplex / Feeder / AutoFeed / XferCount
   ├── Brightness / Contrast
   ├── Rotation / AutoDeskew / AutoBorderDetection
   ├── Compression
   └── PatchCode / Barcode (si aplica)
        │
[source.Enable(NoUI/ShowUI, modal, hwnd)]  Estado 4 → 5 → 6/7
        │
[Procesar eventos: TransferReady, DataTransferred, TransferError]
        │
[SourceDisabled]                           ← señal de fin
        │
[source.Close()]                           Estado 4 → 3
        │
[session.Close()]                          Estado 3 → 2
```

> El orden interno de capabilities importa: `ICapXferMech` y `ICapPixelType` deben ir antes que resoluciones y otras dependientes, porque algunos drivers ajustan los rangos válidos del resto en función de estos.

---

## Paso 1 — Inicialización (`TwainSession`)

```csharp
using NTwain;
using NTwain.Data;

var appId = TWIdentity.CreateFromAssembly(
    DataGroups.Image,
    System.Reflection.Assembly.GetEntryAssembly());

var session = new TwainSession(appId);
```

`TWIdentity.CreateFromAssembly` rellena fabricante / nombre / versión leyendo los atributos del ensamblado de entrada.

### Elegir el `MessageLoopHook` correcto

| Tipo de app | Hook | Construcción |
|-------------|------|--------------|
| **WPF** | `WpfMessageLoopHook` | `new WpfMessageLoopHook(new WindowInteropHelper(this).Handle)` |
| **WinForms** | `WindowsFormsMessageLoopHook` | `new WindowsFormsMessageLoopHook(this.Handle)` |
| **Console / sin UI** | (interno) | `session.Open()` sin argumentos |

Implementaciones en `src/NTwain/MessageLoopHooks.cs` (WinForms `:90`, WPF `:159`).

> El handle debe ser el de la ventana **principal** y debe ya existir (no llamar antes de `OnSourceInitialized`/`Form_Load`).

---

## Paso 2 — Suscripción a eventos

Suscribirse **antes** de `Open()` para no perder el primer `StateChanged`.

```csharp
session.TransferReady    += OnTransferReady;     // antes de cada página
session.DataTransferred  += OnDataTransferred;   // imagen recibida
session.TransferError    += OnTransferError;     // fallo en transferencia
session.SourceDisabled   += OnSourceDisabled;    // fin de escaneo
session.StateChanged     += OnStateChanged;      // diagnóstico (opcional)
```

Definidos en `TwainSession.cs:471–491`.

> **Threading**: si NO se asigna `session.SynchronizationContext`, los eventos se disparan en el thread del DSM. Para tocar UI, envolver en `Dispatcher.BeginInvoke` (WPF) o `Control.BeginInvoke` (WinForms).

---

## Paso 3 — Apertura del DSM (estado 2 → 3)

```csharp
ReturnCode rc = session.Open(hook);   // o session.Open() en consola
if (rc != ReturnCode.Success)
    throw new InvalidOperationException($"DSM no pudo abrir: {rc}");
```

Tras esto `session.State == 3` y se pueden enumerar fuentes.

---

## Paso 4 — Selección y apertura del Source (estado 3 → 4)

```csharp
// Listar todas las fuentes instaladas
foreach (var src in session)
    Console.WriteLine(src.Name);

// Opción A: seleccionar por nombre
var source = session.FirstOrDefault(s => s.Name.Contains("Kodak"));

// Opción B: dejar que el usuario elija con el diálogo nativo del DSM
var source = session.ShowSourceSelector();   // null si se cancela

// Opción C: usar el default
var source = session.DefaultSource;

if (source == null) { /* manejo */ return; }

ReturnCode rc = source.Open();
if (rc != ReturnCode.Success)
    throw new InvalidOperationException($"Source no abrió: {rc}");
```

`DataSource.Open()` en `src/NTwain/DataSource.cs:34-45`. Pasamos a estado 4.

---

## Paso 5 — Configuración de capacidades (estado 4)

### Patrón base de cada cap

```csharp
var cap = source.Capabilities.ICapPixelType;

if (cap.IsSupported && cap.CanSet)
{
    // Inspeccionar antes de set (opcional pero recomendado)
    var soportados = cap.GetValues().ToList();
    if (soportados.Contains(PixelType.RGB))
    {
        var rc = cap.SetValue(PixelType.RGB);
    }
}
```

API expuesta por `ICapWrapper<T>` (`src/NTwain/CapWrapper.cs`):

| Miembro | Uso |
|---------|-----|
| `IsSupported` | El driver soporta esta capability |
| `CanGet` / `CanSet` / `CanReset` | Permisos en runtime |
| `GetCurrent()` | Valor actual del driver |
| `GetDefault()` | Valor por defecto |
| `GetValues()` | Lista de valores válidos |
| `SetValue(T)` | Establece (devuelve `ReturnCode`) |
| `Reset()` | Devuelve a default |

### Capacidades en orden recomendado

> Los nombres de propiedad en `Capabilities` siguen el patrón **`ICapXxx`** para image caps (`ICAP_*`) y **`CapXxx`** para caps generales (`CAP_*`).

#### A. Mecanismo de transferencia — primero siempre

```csharp
// Native: handle único, simple, ideal para 1 página
// File:   driver escribe TIFF/BMP a disco, ideal para ADF y páginas grandes
// Memory: chunks (avanzado)
source.Capabilities.ICapXferMech.SetValue(XferMech.Native);
```

Si elegiste `XferMech.File`, configuras nombre/formato dentro de `TransferReady` (ver §7).

#### B. Modo de escaneo (Pixel Type)

```csharp
source.Capabilities.ICapPixelType.SetValue(PixelType.BlackWhite); // 1-bit
source.Capabilities.ICapPixelType.SetValue(PixelType.Gray);       // 8-bit gris
source.Capabilities.ICapPixelType.SetValue(PixelType.RGB);        // color
```

#### C. Resolución (DPI)

```csharp
TWFix32 dpi = 300f;   // conversión implícita float → TWFix32
source.Capabilities.ICapXResolution.SetValue(dpi);
source.Capabilities.ICapYResolution.SetValue(dpi);
```

#### D. Tamaño de papel

```csharp
source.Capabilities.ICapSupportedSizes.SetValue(SupportedSize.A4);
// Otros: USLetter, USLegal, A3, A5, B5, etc. (enum SupportedSize)
```

#### E. ADF / Duplex / Cantidad de páginas

```csharp
// Habilitar alimentador automático
source.Capabilities.CapFeederEnabled.SetValue(BoolType.True);
source.Capabilities.CapAutoFeed.SetValue(BoolType.True);

// Doble cara
source.Capabilities.CapDuplexEnabled.SetValue(BoolType.True);

// Cantidad de páginas: -1 = todas las del feeder
source.Capabilities.CapXferCount.SetValue(-1);
```

#### F. Brillo y contraste

```csharp
// Rangos típicos: -1000 a +1000 (depende del driver, leer GetValues())
source.Capabilities.ICapBrightness.SetValue((TWFix32)50f);
source.Capabilities.ICapContrast.SetValue((TWFix32)0f);
```

#### G. Rotación y enderezado

```csharp
// Rotación manual (grados)
source.Capabilities.ICapRotation.SetValue((TWFix32)90f);

// Auto-deskew (corrige inclinación)
source.Capabilities.ICapAutomaticDeskew.SetValue(BoolType.True);

// Auto-rotate (decide orientación según contenido)
source.Capabilities.ICapAutomaticRotate.SetValue(BoolType.True);

// Detección automática de bordes
source.Capabilities.ICapAutomaticBorderDetection.SetValue(BoolType.True);
```

#### H. Compresión

```csharp
// CCITTG4 para B&W, JPEG para color, None para sin comprimir
source.Capabilities.ICapCompression.SetValue(CompressionType.CcittG4);
```

#### I. Patch code

```csharp
source.Capabilities.ICapPatchCodeDetectionEnabled.SetValue(BoolType.True);

// Tipos detectables (leer cuáles soporta el driver)
var tipos = source.Capabilities.ICapSupportedPatchCodeTypes.GetValues();

// Modo de búsqueda (qué tan agresivo)
source.Capabilities.ICapPatchCodeSearchMode.SetValue(BarCodeSearchMode.Vert);
```

Tras escanear, los patch codes se reportan dentro de **Extended Image Info** (ver §7).

#### J. Barcode

```csharp
source.Capabilities.ICapBarcodeDetectionEnabled.SetValue(BoolType.True);
var formatos = source.Capabilities.ICapSupportedBarcodeTypes.GetValues();
```

### Helper de configuración (recomendado)

Un único método que aplica caps con guardas:

```csharp
private static void TrySet<T>(ICapWrapper<T> cap, T value, string name)
{
    if (!cap.IsSupported) { Log($"{name}: no soportado"); return; }
    if (!cap.CanSet)      { Log($"{name}: solo lectura"); return; }
    var rc = cap.SetValue(value);
    if (rc != ReturnCode.Success) Log($"{name}: SetValue → {rc}");
}
```

---

## Paso 6 — Habilitación del escaneo (estado 4 → 5)

```csharp
ReturnCode rc = source.Enable(
    SourceEnableMode.NoUI,   // o ShowUI para mostrar la UI del driver
    modal: false,            // true bloquea hasta terminar (no recomendado)
    windowHandle: hwnd);     // mismo handle del MessageLoopHook
```

`DataSource.Enable()` en `src/NTwain/DataSource.cs:75-78`.

A partir de aquí **no se reconfigura nada** — solo se procesan eventos.

---

## Paso 7 — Recepción de imágenes

### Evento `TransferReady` (antes de cada página)

```csharp
private void OnTransferReady(object sender, TransferReadyEventArgs e)
{
    // Cancelar todo el lote:    e.CancelAll = true;
    // Saltar esta página:        e.SkipCurrent = true;

    // Si usaste XferMech.File, aquí defines la ruta:
    var setup = new TWSetupFileXfer
    {
        Format = FileFormat.Tiff,
        FileName = Path.Combine(carpeta, $"page_{contador++}.tif")
    };
    session.CurrentSource.DGControl.SetupFileXfer.Set(setup);
}
```

### Evento `DataTransferred` (imagen lista)

`DataTransferredEventArgs` (`src/NTwain/DataTransferredEventArgs.cs`) trae uno de estos según el `XferMech`:

| Mech | Propiedad útil | Cómo procesar |
|------|---------------|---------------|
| `Native` | `NativeData` (IntPtr) | `using var s = e.GetNativeImageStream(); var img = Image.FromStream(s);` |
| `File` | `FileDataPath` (string) | `var img = new Bitmap(e.FileDataPath);` (copiar antes de salir, es temporal) |
| `Memory` | `MemoryInfo` + `MemoryData` | Acumular chunks hasta `XferDone` |

Adicionalmente:
- `e.ImageInfo` — dimensiones, bpp, resolución real.
- `e.GetExtImageInfo(...)` — **patch codes, barcodes**, página, lado (front/back).

```csharp
private void OnDataTransferred(object sender, DataTransferredEventArgs e)
{
    Image bitmap = null;

    if (e.NativeData != IntPtr.Zero)
    {
        using var stream = e.GetNativeImageStream();
        bitmap = Image.FromStream(stream);
    }
    else if (!string.IsNullOrEmpty(e.FileDataPath))
    {
        bitmap = Image.FromFile(e.FileDataPath);
    }

    // Patch / barcode info (si están habilitados)
    var info = e.GetExtImageInfo(ExtendedImageInfo.PatchCode,
                                 ExtendedImageInfo.BarcodeText);
    var patch = info[ExtendedImageInfo.PatchCode]?.ReadValues<ushort>().FirstOrDefault();

    // Marshalling a UI
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
        Pages.Add(new ScanResult { Image = bitmap, PatchCode = patch });
    });
}
```

> **El `NativeData` se libera al salir del handler** — siempre clonar/persistir antes de retornar.

### Evento `TransferError`

```csharp
private void OnTransferError(object sender, TransferErrorEventArgs e)
{
    var rc   = e.ReturnCode;                 // Cancel, Failure, ...
    var cc   = e.SourceStatus?.ConditionCode; // PaperJam, NoMedia, ...
    var ex   = e.Exception;                  // si falló marshaling
    Log($"Transfer error: {rc} / {cc} / {ex?.Message}");
}
```

---

## Paso 8 — Cierre limpio

### En `SourceDisabled` (fin natural del lote)

```csharp
private void OnSourceDisabled(object sender, EventArgs e)
{
    if (session.State == 4) session.CurrentSource.Close();   // 4 → 3
    if (session.State == 3) session.Close();                  // 3 → 2
}
```

### Cierre forzado (Dispose / cancelación)

```csharp
public void Dispose()
{
    try
    {
        if (session.State > 4)
            session.CurrentSource?.Close();   // intenta bajar
        if (session.State > 2)
            session.Close();
    }
    catch
    {
        session.ForceStepDown(2);             // último recurso
    }
}
```

`ForceStepDown` deshace los estados intermedios sin cuidado de errores.

---

## 11. Ejemplo end-to-end

```csharp
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using NTwain;
using NTwain.Data;

public class ScannerService : IDisposable
{
    private readonly TwainSession _session;
    private readonly Window _owner;

    public event Action<Image, ushort?> PageScanned;

    public ScannerService(Window owner)
    {
        _owner = owner;
        var appId = TWIdentity.CreateFromAssembly(DataGroups.Image,
                                                  Assembly.GetEntryAssembly());
        _session = new TwainSession(appId);

        _session.TransferReady   += OnTransferReady;
        _session.DataTransferred += OnDataTransferred;
        _session.TransferError   += OnTransferError;
        _session.SourceDisabled  += OnSourceDisabled;
    }

    public void Scan(string sourceName, ScanProfile p)
    {
        var hwnd = new WindowInteropHelper(_owner).Handle;
        var hook = new WpfMessageLoopHook(hwnd);

        if (_session.Open(hook) != ReturnCode.Success)
            throw new InvalidOperationException("DSM no abrió");

        var src = _session.FirstOrDefault(s => s.Name.Contains(sourceName))
                  ?? _session.DefaultSource;
        if (src == null) throw new InvalidOperationException("Source no encontrada");

        if (src.Open() != ReturnCode.Success)
            throw new InvalidOperationException("Source no abrió");

        ApplyProfile(src, p);

        src.Enable(SourceEnableMode.NoUI, false, hwnd);
    }

    private static void ApplyProfile(DataSource src, ScanProfile p)
    {
        var caps = src.Capabilities;

        // Orden recomendado
        TrySet(caps.ICapXferMech,                 p.XferMech,        nameof(p.XferMech));
        TrySet(caps.ICapPixelType,                p.PixelType,       nameof(p.PixelType));
        TrySet(caps.ICapXResolution,              (TWFix32)p.Dpi,    "DPI X");
        TrySet(caps.ICapYResolution,              (TWFix32)p.Dpi,    "DPI Y");
        TrySet(caps.ICapSupportedSizes,           p.PaperSize,       nameof(p.PaperSize));
        TrySet(caps.CapFeederEnabled,             (BoolType)p.UseFeeder, "Feeder");
        TrySet(caps.CapAutoFeed,                  (BoolType)p.UseFeeder, "AutoFeed");
        TrySet(caps.CapDuplexEnabled,             (BoolType)p.Duplex,    "Duplex");
        TrySet(caps.CapXferCount,                 p.PageCount,       "PageCount");
        TrySet(caps.ICapBrightness,               (TWFix32)p.Brightness, "Brightness");
        TrySet(caps.ICapContrast,                 (TWFix32)p.Contrast,   "Contrast");
        TrySet(caps.ICapAutomaticDeskew,          (BoolType)p.AutoDeskew, "AutoDeskew");
        TrySet(caps.ICapAutomaticBorderDetection, (BoolType)p.AutoBorder, "AutoBorder");
        if (p.Rotation != 0)
            TrySet(caps.ICapRotation, (TWFix32)p.Rotation, "Rotation");
        if (p.PatchCodeEnabled)
            TrySet(caps.ICapPatchCodeDetectionEnabled, BoolType.True, "PatchCode");
        if (p.BarcodeEnabled)
            TrySet(caps.ICapBarcodeDetectionEnabled,  BoolType.True, "Barcode");
    }

    private static void TrySet<T>(ICapWrapper<T> cap, T value, string name)
    {
        if (cap == null || !cap.IsSupported || !cap.CanSet) return;
        cap.SetValue(value);
    }

    private void OnTransferReady(object s, TransferReadyEventArgs e) { /* opcional */ }

    private void OnDataTransferred(object s, DataTransferredEventArgs e)
    {
        Image img = null;
        if (e.NativeData != IntPtr.Zero)
            using (var stream = e.GetNativeImageStream())
                img = Image.FromStream(stream);
        else if (!string.IsNullOrEmpty(e.FileDataPath))
            img = Image.FromFile(e.FileDataPath);

        ushort? patch = null;
        try
        {
            var info = e.GetExtImageInfo(ExtendedImageInfo.PatchCode);
            patch = info[ExtendedImageInfo.PatchCode]?.ReadValues<ushort>().FirstOrDefault();
        }
        catch { /* driver puede no soportarlo */ }

        Application.Current.Dispatcher.BeginInvoke(() => PageScanned?.Invoke(img, patch));
    }

    private void OnTransferError(object s, TransferErrorEventArgs e) { /* log */ }

    private void OnSourceDisabled(object s, EventArgs e)
    {
        if (_session.State == 4) _session.CurrentSource.Close();
        if (_session.State == 3) _session.Close();
    }

    public void Dispose()
    {
        try
        {
            if (_session.State > 4) _session.CurrentSource?.Close();
            if (_session.State > 2) _session.Close();
        }
        catch { _session.ForceStepDown(2); }
    }
}

public class ScanProfile
{
    public XferMech XferMech       { get; set; } = XferMech.Native;
    public PixelType PixelType     { get; set; } = PixelType.RGB;
    public float Dpi               { get; set; } = 300f;
    public SupportedSize PaperSize { get; set; } = SupportedSize.A4;
    public bool UseFeeder          { get; set; } = true;
    public bool Duplex             { get; set; } = false;
    public int PageCount           { get; set; } = -1;
    public float Brightness        { get; set; } = 0f;
    public float Contrast          { get; set; } = 0f;
    public float Rotation          { get; set; } = 0f;
    public bool AutoDeskew         { get; set; } = true;
    public bool AutoBorder         { get; set; } = true;
    public bool PatchCodeEnabled   { get; set; } = false;
    public bool BarcodeEnabled     { get; set; } = false;
}
```

---

## 12. Errores comunes

| Síntoma | Causa probable | Fix |
|---------|----------------|-----|
| `SetValue` retorna `Failure` | Estás fuera del estado 4 (ya llamaste `Enable`) | Configurar antes de `Enable` |
| `IsSupported == false` para todo | Llamaste antes de `source.Open()` | Abrir source primero |
| Cuelgue al cerrar la sesión | `Close()` desde el thread equivocado | Llamar desde el mismo thread del `MessageLoopHook` |
| `DataTransferred` nunca llega | Faltó `MessageLoopHook` o el handle es inválido | Pasar el HWND de la ventana principal viva |
| Crash al tocar UI desde el handler | No hay `SynchronizationContext` | `Dispatcher.BeginInvoke` / `Control.BeginInvoke` |
| Imagen vacía / corrupta en Native | Saliste del handler sin clonar el stream | `Image.FromStream` antes de retornar |
| ADF solo escanea 1 página | `CapXferCount` quedó en 1 | `caps.CapXferCount.SetValue(-1)` |
| Patch code siempre `null` | Driver no expone Extended Image Info | Verificar `GetExtImageInfo` no lanza; revisar manual del scanner |
| `TWFix32` da error de tipo | Usaste `int` directo | `(TWFix32)valorFloat` con conversión implícita desde `float`/`double` |
| Solo en x64 falla la transferencia | Driver TWAIN es 32-bit | Compilar app como x86 (los drivers TWAIN suelen ser 32-bit) |

> **TWAIN x86 vs x64**: la mayoría de drivers TWAIN comerciales son 32-bit. Si tu app es AnyCPU/x64 y el driver es x86, no aparece el source. Compilar como `x86` resuelve esto en la mayoría de casos.

---

## Referencias rápidas

- `TwainSession`            — `src/NTwain/TwainSession.cs`
- `DataSource` / `Capabilities` — `src/NTwain/DataSource.cs`, `src/NTwain/Capabilities.cs`
- `CapWrapper<T>`           — `src/NTwain/CapWrapper.cs`
- `MessageLoopHook`         — `src/NTwain/MessageLoopHooks.cs`
- `DataTransferredEventArgs` — `src/NTwain/DataTransferredEventArgs.cs`
- `TWFix32`, enums          — `src/NTwain/Data/TwainTypes.cs`, `TwainTypesExtended.cs`
- Sample WPF (referencia)   — `samples/Sample.WPF/ViewModels/TwainVM.cs`
- Sample WinForms           — `samples/Sample.Winform/TestForm.cs`
- Sample Console            — `samples/Sample.Console/Program.cs`
