# Fi6800Scanner — Handoff de Sesión

> **Para Claude Code en futuras sesiones**: este documento captura todo el contexto necesario para retomar el desarrollo del proyecto sin haber participado en la conversación original. Léelo completo antes de modificar código.

---

## 0. Identidad del proyecto

**Objetivo**: app WinForms en español que demuestra todas las features del escáner **Fujitsu fi-6800** vía driver **PaperStream IP TWAIN** usando **NTwain v3** como puente y **Cyotek ImageBox** como visor.

**Repo**: el proyecto vive como subdirectorio dentro de un fork de NTwain v3 (`metagamma/ntwain` rama `v3`):

```
C:\Users\Windows\Documents\git\ntwain\
├── src/NTwain/                        ← código fuente de NTwain v3 (NO modificar)
├── Spec/Kodak/                        ← docs TWAIN/Kodak (referencia)
├── samples/                           ← samples originales de NTwain
├── NTWAIN_V3_GUIDE.md                 ← guía paso a paso uso NTwain
├── NTWAIN_V3_REFERENCE.md             ← referencia exhaustiva (catálogo enums, caps, etc.)
├── FI6800_PAPERSTREAM_PLAN.md         ← plan completo del proyecto (FASE 0-11)
└── Fi6800Scanner/                     ← ← ESTE PROYECTO
    ├── Fi6800Scanner.sln
    ├── README.md
    ├── SESSION_HANDOFF.md             ← este archivo
    ├── Fi6800Scanner.Core/            ← biblioteca .NET (sin UI)
    └── Fi6800Scanner.App/             ← WinForms exe
```

**Documentos hermanos relevantes** (todos en `C:\Users\Windows\Documents\git\ntwain\`):

- **`NTWAIN_V3_GUIDE.md`** — tutorial paso a paso para usar NTwain v3 (estados, eventos, ejemplo end-to-end).
- **`NTWAIN_V3_REFERENCE.md`** — referencia densa: catálogo completo de enums, caps, ReturnCode/ConditionCode, rotación, modos de escaneo, patch/barcode interaction, etc. **24 secciones**.
- **`FI6800_PAPERSTREAM_PLAN.md`** — plan original del proyecto con investigación de fi-6800 + PaperStream + Cyotek.

Si el usuario hace preguntas sobre NTwain en general, primero buscar en estos dos primeros docs antes de explorar código.

---

## 1. Decisiones del usuario (no cambiar sin consultar)

| Decisión | Valor | Motivo |
|----------|-------|--------|
| Arquitectura | **x86** | Compatibilidad con drivers TWAIN del fi-6800 |
| Target framework | **net462** | NTwain v3 lo soporta, y tooling Win10/11 trae .NET 4.6.2 por defecto |
| Idioma de UI/docs | **Español** | El usuario habla español; mensajes y docs en español |
| Alcance | "**todo**" — todas las features del fi-6800 | El usuario quiere implementación completa |
| Tiene escáner físico | **Sí** | Validación contra hardware real es posible y esperada |
| Visor de imagen | **Cyotek ImageBox** | NuGet `CyotekImageBox` 1.3.1 |
| Persistencia config | **Newtonsoft.Json** | Más confiable que `System.Text.Json` en net462 |

---

## 2. Estado actual: Fases 0-3 completadas

### Lo que YA funciona y compila (build verificado)

```
$ dotnet build Fi6800Scanner.sln -c Debug
Build succeeded.
    0 Warning(s)  0 Error(s)
```

Ejecutable producido: `Fi6800Scanner.App\bin\x86\Debug\net462\Fi6800Scanner.App.exe`

### Funcionalidad implementada

1. **Pre-flight checker** (`Fi6800Scanner.Core.Diagnostics.PreflightChecker`)
   - Verifica arquitectura del proceso vs PaperStream IP instalado (registry HKLM\SOFTWARE\PFU\* en ambas vistas)
   - Detecta TWAINDSM.dll en System32/SysWOW64
   - Advierte si Windows 11 24H2 (build ≥26100) sin KB5055523 — bug TWAIN-bridge
   - Lee versión de PaperStream desde `Uninstall` registry

2. **Capability discovery** (`Fi6800Scanner.Core.Capabilities.ScannerProbe`)
   - Abre source filtrando por nombre que contiene "fi-6800" o "PaperStream IP" (fallback default)
   - Enumera `CAP_SUPPORTEDCAPS` para listar todas las capabilities soportadas
   - Separa los IDs custom (≥0x8000) — semántica desconocida pero IDs registrados
   - Lee runtime: PixelTypes, BitDepths, Resolución, SupportedSizes, Compressions, FileFormats, PatchCodes, Barcodes, ExtImageInfo
   - Booleans para ~25 features (Duplex, AutoColor, AutoDeskew, AutoRotate, AutoBorder, JobControl, Imprinter, etc.)
   - Detecta multi-stream heurísticamente (CapCameraEnabled + CapCameraSide)

3. **Diagnóstico UI** (`Fi6800Scanner.App.Forms.DiagnosticsDialog`)
   - Reporte legible del pre-flight + snapshot
   - Botón "Guardar JSON…" exporta el snapshot completo

4. **Profile applicator** (`Fi6800Scanner.Core.Capabilities.ProfileApplicator`)
   - Aplica un `ScanProfile` al `DataSource` en orden correcto (XferMech → Pixel/AutoColor → Resolution → Geometry → Auto-procesos → Imagen → Compresión → Detecciones → Multifeed → Imprinter)
   - Helper `SetBool` específico para `ICapWrapper<BoolType>` (evita problemas de inferencia con ternario)
   - `TrySet<T>` genérico con reporte de Applied/Skipped/Failed
   - Nota: `ICapPhysicalHeight` para long page es read-only — NO setea, marca como Skipped

5. **Escáner service** (`Fi6800Scanner.Core.Services.Fi6800ScannerService`)
   - Implementa `IScannerService` (interfaz pública para test/mock)
   - Encapsula `TwainSession` + `DataSource` + `WindowsFormsMessageLoopHook`
   - Eventos: `PageReceived`, `ProgressChanged`, `ErrorOccurred`, `BatchCompleted`
   - `OpenAndProbe(hwnd)` → snapshot
   - `ApplyProfile(profile)` → ProfileApplyResult
   - `StartScan()` / `CancelScan()` (vía `e.CancelAll = true` en TransferReady)
   - `XferMech.File`: configura `TWSetupFileXfer` en `TransferReady` con path en `%TEMP%\Fi6800Scanner\<sessionId>\page_NNNNN.tif`
   - `XferMech.Native`: convierte `e.NativeData` → `Bitmap` vía `e.GetNativeImageStream()`
   - Lee Extended Image Info: PageSide, PatchCode (1-based en TWAIN, restamos 1 al cast a enum 0-based), SkewFinalAngle, BarcodeText, BarcodeType
   - Genera thumbnail 200×280 con `InterpolationMode.HighQualityBicubic`
   - Cleanup: `source.Close() → session.Close() → ForceStepDown(2)` como fallback

6. **MainForm** (`Fi6800Scanner.App.Forms.MainForm`)
   - Toolbar: Pre-flight, Discover, Escanear, Cancelar, Limpiar, Guardar lote, Diagnóstico
   - SplitContainer: ListView de thumbnails (izquierda) + ImageViewerPanel (derecha)
   - StatusBar: estado + source + páginas + errores
   - Captura `SynchronizationContext.Current` en `Load` event para marshalling
   - Helper `Post(Action)` que hace `_uiContext.Post(...)` o `BeginInvoke`

7. **ImageViewerPanel** (`Fi6800Scanner.App.Controls.ImageViewerPanel`)
   - Cyotek `ImageBox` con SizeMode=Fit, AllowZoom=true, GridDisplayMode=Client
   - Toolbar: Fit, 1:1, Zoom +/-, Rotar L/R, Voltear, Recortar selección, Guardar como…
   - StatusBar: coordenadas pixel bajo cursor (vía `PointToImage`), zoom %, tamaño imagen
   - SelectionMode = Rectangle, evento `CropRequested` (de tipo `EventHandler<Image>` no `Bitmap` — Cyotek devuelve Image)
   - Pixel grid auto-activado al display de imagen
   - Rotación: `Bitmap.RotateFlip(RotateFlipType.RotateXXX)` directamente sobre la imagen

8. **PageThumbnailListView** (`Fi6800Scanner.App.Controls.PageThumbnailListView`)
   - ListView con LargeIcons + ImageList
   - Label: `#N (rev) [Patch] 📊` con badges según contenido
   - Tooltip muestra: tamaño, DPI, modo, patch, barcodes
   - Evento `PageSelected` ↑ MainForm

9. **DiagnosticsDialog** (`Fi6800Scanner.App.Forms.DiagnosticsDialog`)
   - Texto monospace con secciones: PRE-FLIGHT, SNAPSHOT, FEATURES, CAPS SOPORTADAS, CAPS CUSTOM
   - Botón guardar JSON

---

## 3. ⚠️ API QUIRKS de NTwain v3 descubiertos durante implementación

**Documenta esto, no te tropieces de nuevo**:

| Esperaba | Realidad | Solución |
|----------|----------|----------|
| `source.State` (int) | No existe. State está en `TwainSession.State` | Usar `source.IsOpen` |
| `source.Identity` público | Es `internal` | Usar `source.Name`, `.Manufacturer`, `.ProductFamily`, `.Version` (TWVersion struct), `.ProtocolVersion` (System.Version) |
| `ICapBitDepth = ICapWrapper<ushort>` | Es `ICapWrapper<int>` | `List<int>` en el modelo, no `List<ushort>` |
| `ICapPhysicalWidth/Height = ICapWrapper<TWFix32>` | Es `IReadOnlyCapWrapper<TWFix32>` | No se puede `SetValue`. Long page real necesita `DGControl.Capability` directo |
| `ICapJpegQuality = ICapWrapper<int>` | Es `ICapWrapper<JpegQuality>` enum (-3..-1 + valores numéricos) | Cast: `(JpegQuality)p.JpegQuality` |
| `ICapAutoDiscardBlankPages = ICapWrapper<BoolType>` | Es `ICapWrapper<BlankPage>` enum (Disable=-2, Auto=-1) | Usar `BlankPage.Auto` / `BlankPage.Disable`, no `BoolType` |
| `ICapFilter = ICapWrapper<int>` | Es `ICapWrapper<FilterType>` enum | Pasar `FilterType.Red` etc. |
| `GetExtImageInfo` retorna `Dictionary<>` indexable | Retorna `IEnumerable<TWInfo>` | Usar `infos.FirstOrDefault(i => i.InfoID == X)` y check `info.NumItems > 0` |
| `TWInfo.ReadValues<T>()` genérico | Es `ReadValues()` que devuelve `IList<object>` | `Convert.ToUInt16(values[0])` o pattern match `is TWFix32 fix` |
| `Cyotek.GetSelectedImage()` retorna `Bitmap` (según blog) | Retorna `Image` | Cast o cambiar event signature a `EventHandler<Image>` |
| Conditional `bool ? BoolType.True : BoolType.False` infiere `T` correctamente | Falla CS0411 en C# 7.3 con genérico | Helper `SetBool(cap, bool, label, report)` que evita el ternario inline |
| `DoubleFeedDetection` con valor "None"/"Off" | Solo Ultrasonic=0, ByLength=1, Infrared=2 | Para "Off" no hay flag estándar — el driver decide; reportar como Skipped |

**Nombres de cap relevantes confirmados**:
- `source.Capabilities.CapSupportedCaps` → `IReadOnlyCapWrapper<CapabilityId>` ✓
- `source.Capabilities.CapDoubleFeedDetection` → `ICapWrapper<DoubleFeedDetection>` ✓
- `source.Capabilities.CapPrinterIndex` → `ICapWrapper<int>` ✓
- `source.Capabilities.CapPrinterString` → `ICapWrapper<string>` ✓
- `source.Capabilities.CapPrinterEnabled` → `ICapWrapper<BoolType>` ✓

---

## 4. Estructura de archivos del proyecto

```
Fi6800Scanner/
├── Fi6800Scanner.sln                         x86, dos proyectos referenciados
│
├── Fi6800Scanner.Core/
│   ├── Fi6800Scanner.Core.csproj             SDK-style net462 x86
│   │                                         Refs: NTwain 3.7.5, Newtonsoft.Json 13.0.3, System.Drawing
│   │
│   ├── Models/
│   │   ├── PageSide.cs                       enum {Front, Rear}
│   │   ├── MultifeedMode.cs                  enum {Off, Ultrasonic, ByLength, Both}
│   │   ├── DetectedBarcode.cs                {Type, Text, Position, Confidence}
│   │   ├── ImprinterConfig.cs                {Enabled, Text, CounterStart, CounterStep, CounterDigits}
│   │   ├── ScannedPage.cs                    IDisposable; Id, PageNumber, Side, PixelType, dims, dpi, TempFilePath, Thumbnail, FullImage, DetectedPatch, Barcodes, SkewAngleDegrees, CapturedAt
│   │   ├── ScanProfile.cs                    perfil completo: image/resolución/duplex/auto-procesos/compresión/patch/barcode/multifeed/imprinter
│   │   └── Fi6800CapabilitySnapshot.cs       resultado del probe + Features (~30 booleans) + CustomCapDescriptor[]
│   │
│   ├── Diagnostics/
│   │   ├── PreflightResult.cs                {Success, Warnings, Errors, OsVersion, IsApp64Bit, PaperStreamX86/X64Installed, TwainDsmAvailable, ArchitectureMatch, PaperStreamVersion}
│   │   └── PreflightChecker.cs               static Run() — registry, file system, OS check
│   │
│   ├── Capabilities/
│   │   ├── ScannerProbe.cs                   public Probe(DataSource) → snapshot
│   │   └── ProfileApplicator.cs              static Apply(source, profile, snap) → ApplyReport
│   │
│   └── Services/
│       ├── IScannerService.cs                interfaz + ProfileApplyResult, BatchProgress, BatchSummary, ScanErrorInfo
│       ├── Fi6800ScannerService.cs           implementación NTwain
│       └── SnapshotJsonExporter.cs           ToJson/Save/Load con StringEnumConverter
│
└── Fi6800Scanner.App/
    ├── Fi6800Scanner.App.csproj              SDK-style net462 x86 WinExe
    │                                         Refs: System.Windows.Forms, System.Drawing, System.Configuration
    │                                         PackageRefs: CyotekImageBox 1.3.1
    │                                         ProjectRef: Core
    ├── app.manifest                          dpiAware=false (importante para legacy TWAIN)
    ├── Program.cs                            STA, Application.EnableVisualStyles, Run(MainForm)
    │
    ├── Forms/
    │   ├── MainForm.cs                       layout principal, eventos toolbar, marshalling
    │   └── DiagnosticsDialog.cs              reporte read-only + export JSON
    │
    └── Controls/
        ├── ImageViewerPanel.cs               UserControl con Cyotek ImageBox + toolbar + statusbar
        └── PageThumbnailListView.cs          UserControl con ListView + ImageList
```

---

## 5. Fases pendientes — plan detallado

### FASE 4 — SettingsDialog completo con tabs dinámicos

**Objetivo**: diálogo modal donde el usuario puede ajustar TODOS los parámetros del `ScanProfile`. Los controles deben **poblarse dinámicamente desde `Fi6800CapabilitySnapshot`** — solo mostrar opciones realmente soportadas.

**Archivos a crear**:
- `Fi6800Scanner.App/Forms/SettingsDialog.cs` (form modal con TabControl)
- Cada tab puede ser un UserControl separado en `Fi6800Scanner.App/Controls/Settings/`

**Tabs sugeridos**:

1. **Imagen**
   - PixelType combo (poblado de `snap.PixelTypes`)
   - AutoColor checkbox + AutoColorNonColorMode combo (si `snap.Features.AutoColor`)
   - BitDepth combo (de `snap.BitDepths`)
   - Brightness slider (-1000 a 1000) si soportado
   - Contrast slider si soportado
   - Threshold slider (0-255) — solo visible si PixelType=BlackWhite
   - Filter dropout combo (None/Red/Green/Blue) — solo visible si PixelType=BlackWhite
   - Mirror checkbox

2. **Resolución y tamaño**
   - DPI: slider o combo (de `snap.ResolutionValues`, fallback a Range si solo min/max)
   - PaperSize combo (de `snap.SupportedSizes`)
   - Long page checkbox + length input (mm) — habilitar si `snap.Features.LongPageDetected`
   - Custom frame: 4 inputs (Left/Top/Right/Bottom) en pulgadas o mm

3. **Duplex / feeder**
   - Duplex checkbox (si `snap.Features.Duplex`)
   - PageCount: -1 (todas) o número específico

4. **Auto-procesos**
   - AutoDeskew (si `snap.Features.AutoDeskew`)
   - AutoBorder (si `snap.Features.AutoBorder`)
   - AutoRotate radio O Rotation manual (0/90/180/270) — exclusivos
   - DiscardBlankPages (si `snap.Features.DiscardBlank`)
   - AutoLengthDetection (si `snap.Features.AutoLengthDetection`)

5. **Compresión y formato**
   - Compression combo (de `snap.Compressions`, filtrar por PixelType actual)
   - JpegQuality slider (1-100) — solo visible si Compression=Jpeg
   - JpegSubsampling combo (si `snap.Features.JpegSubsampling`)
   - FileFormat combo (de `snap.FileFormats`)
   - XferMech radio (Native / File / Memory)

6. **Patch codes** (si `snap.Features.PatchDetection`)
   - Enable checkbox
   - Lista de checkboxes con cada `PatchCode` de `snap.PatchCodes`
   - Mensaje: ⚠️ "Detección de patch suele requerir modo BlackWhite o Gray para resultados confiables"

7. **Barcodes** (si `snap.Features.BarcodeDetection`)
   - Enable checkbox
   - Lista con cada `BarcodeType` de `snap.Barcodes`
   - Search direction (BarcodeDirection: Horz/Vert/HorzVert/VertHorz)

8. **Multifeed** (si `snap.Features.DoubleFeedDetection`)
   - Mode radio: Off / Ultrasonic / ByLength / Both — habilitar según `DoubleFeedByLength` y `DoubleFeedByUltrasonic`
   - Length input (mm) si ByLength
   - Sensitivity (Low/Medium/High) si Ultrasonic
   - Response (Stop/StopAndWait/Sound/DoNotImprint)

9. **Imprinter** (si `snap.Features.Imprinter`)
   - Enabled
   - String text + token helper ({COUNTER}, {DATE}, {TIME})
   - CounterStart, CounterStep, CounterDigits
   - Position combo (Top/Bottom × Front/Rear × Before/After) — usar enum `Printer`
   - Font style + char rotation
   - **Importante**: fi-6800 solo tiene post-imprint hardware → texto NO aparece en imagen escaneada (a menos que el driver tenga "digital endorser" que estampe sobre la imagen)

10. **Custom caps** (informativo)
    - Lista de `snap.CustomCaps` — IDs detectados, marcador "Semántica desconocida (PFU no publica)"
    - Permitir al usuario editar manualmente vía `DGControl.Capability` directo (avanzado)

**Patrón de implementación**:
```csharp
// Cada control habilita/deshabilita en función del snapshot
chkAutoColor.Enabled = _snapshot.Features.AutoColor;
chkAutoColor.Text = _snapshot.Features.AutoColor ? "Auto color" : "Auto color (no soportado)";

cmbPixelType.DataSource = _snapshot.PixelTypes;
cmbPixelType.SelectedItem = _profile.PixelType;
```

**Persistencia**: botones "Cargar perfil…", "Guardar perfil…" — JSON via `Newtonsoft.Json`.

---

### FASE 5 — Separación de lotes con patch codes + badges visuales

**Objetivo**: cuando se detecta un patch code en una página, separar el lote en sub-lotes y mostrar visualmente.

**Archivos a tocar**:
- `Fi6800Scanner.Core/Models/ScannedPage.cs` — ya tiene `DetectedPatch`
- `Fi6800Scanner.Core/Models/Batch.cs` (NUEVO) — lista de `ScannedPage` + metadata (separator type, batch number)
- `Fi6800Scanner.Core/Services/BatchSplitter.cs` (NUEVO) — agrupa páginas por separadores
- `Fi6800Scanner.App/Controls/PageThumbnailListView.cs` — añadir grupos visuales (`ListViewGroup` por sub-lote)
- `Fi6800Scanner.App/Controls/PageThumbnailListView.cs` — pintar badges por encima del thumbnail (override `OnDrawItem`)

**Lógica**:
- Cuando `JobControl.IncludeStop` está activo y un patch sheet llega: el patch se incluye como página + el escaneo se detiene → en el código, todas las páginas anteriores forman un "batch" y la del patch sheet inicia un nuevo batch.
- Con `JobControl.ExcludeStop`: el patch sheet NO llega como página, solo se detiene → diferenciación menos visual.

**Badge visual**:
- Pintar pequeño icono de patch (color según tipo) en esquina del thumbnail.
- Para barcode: mostrar primer texto truncado debajo del thumbnail.

**UI**:
- Botón nuevo en toolbar: "Separar lote por patch" (toggle).
- Vista de lista agrupada (ListView con `ShowGroups = true`).

---

### FASE 6 — Imprinter UI + manejo robusto de errores

**Objetivo**: tab Imprinter funcional + UI de recuperación cuando ocurre PaperJam, NoMedia, PaperDoubleFeed, etc.

**Archivos**:
- `Fi6800Scanner.App/Forms/ErrorRecoveryDialog.cs` (NUEVO) — diálogo modal con mensaje + botones (Reintentar / Continuar / Cancelar lote / Ignorar y siguiente)
- `Fi6800Scanner.App/Forms/SettingsDialog.cs` — añadir tab Imprinter
- `Fi6800Scanner.Core/Services/Fi6800ScannerService.cs` — exponer evento `RecoverableErrorOccurred` con info detallada

**Mapeo ConditionCode → mensaje amigable**:
```csharp
PaperJam        → "Atasco. Despeja el papel y reintenta."
PaperDoubleFeed → "Doble alimentación. Verifica el papel y reintenta."
NoMedia         → "Bandeja vacía. Carga papel y reintenta."
CheckDeviceOnline → "Escáner desconectado. Verifica USB/energía."
Interlock       → "Tapa abierta. Cierra y reintenta."
DocTooLight/DocTooDark → ajustar brillo/contraste
LowMemory       → cancelar lote, reducir DPI o usar XferMech.File
```

**Flujo de recuperación**:
1. NTwain dispara `TransferError` con `e.SourceStatus.ConditionCode = PaperJam`.
2. Service emite `RecoverableErrorOccurred`.
3. MainForm muestra `ErrorRecoveryDialog`.
4. Si "Reintentar": no hacer nada, el escáner suele continuar tras el evento.
5. Si "Cancelar": llamar `_scanner.CancelScan()`.

---

### FASE 7 — Multi Image Output (multi-stream Color + Bitonal simultáneo)

**Objetivo**: aprovechar la feature del fi-6800 que emite hasta **3 imágenes por hoja física en una sola pasada**.

**Concepto**: cuando se activa, cada hoja física genera múltiples eventos `DataTransferred`. Cada uno tiene `e.ImageInfo.PixelType` distinto y, si `ExtImageInfo.PageImageNumber` está disponible, indica cuál stream es.

**Activación**: depende del driver. PaperStream IP probablemente lo expone vía caps custom (≥0x8000) — **el snapshot real lo dirá**.

**Plan**:
1. En el snapshot, detectar caps custom relacionadas (revisar etiquetas si las hay).
2. Si no hay forma estándar, usar `DAT_FILESYSTEM`:
   - `source.DGControl.FileSystem.ChangeDirectory("/Camera_Color_Both")`
   - `c.CapCameraEnabled.SetValue(BoolType.True)`
   - `source.DGControl.FileSystem.ChangeDirectory("/Camera_Bitonal_Both")`
   - `c.CapCameraEnabled.SetValue(BoolType.True)`
3. En el `ScannedPage` modelo, agrupar por `PageNumber` físico (ya tenemos field). Stream secundario va a misma `PageNumber` con distinto `PixelType`.
4. Vista UI: agrupar visualmente streams de misma hoja (badge "Color/B&W" sobre el mismo thumbnail original o thumbnails uno bajo otro).

**NTwain v3 NO expone `DAT_FILESYSTEM` con wrapper** — hay que usar `source.DGControl.FileSystem` directo o el triplet bajo nivel. Investigar si está disponible al implementar.

**Archivos**:
- `Fi6800Scanner.Core/Services/MultiStreamCoordinator.cs` (NUEVO) — agrupa streams por hoja
- `Fi6800Scanner.Core/Models/PhysicalSheet.cs` (NUEVO) — agrupa N `ScannedPage`
- `Fi6800Scanner.App/Controls/PageThumbnailListView.cs` — modo "agrupar por hoja física"

---

### FASE 8 — Cyotek viewer avanzado

**Objetivo**: mejoras al visor existente.

**Tareas**:
- Atajos de teclado: `+`/`-` zoom, `0` actual size, `f` fit, `←`/`→` página anterior/siguiente, `r` rotar, `del` borrar página
- Side-by-side: dos ImageBox cuando se selecciona una hoja con streams múltiples
- Anotaciones simples: línea, círculo, texto sobre la imagen (guardadas como overlay separado, no destructivas)
- Magnifier window: cursor con zoom 4× sobre área
- Print preview integrado

---

### FASE 9 — Persistencia: TIFF multi-página + PDF + custom DSData

**Objetivo**: guardar todo el lote como un solo archivo.

**Archivos**:
- `Fi6800Scanner.Core/Services/MultiPageTiffWriter.cs` (NUEVO)
  - Usa `Image.SaveAdd` con `EncoderValue.MultiFrame` / `FrameDimensionPage` / `Flush`
  - Soporta compresión LZW para gris/color, CCITT G4 para B&W
- `Fi6800Scanner.Core/Services/PdfWriter.cs` (NUEVO)
  - Usar `PdfSharp` (NuGet `PdfSharp` o `PdfSharpCore`) — añadir PackageRef
  - Para searchable PDF: integrar Tesseract (`Tesseract` NuGet) o usar OCR del propio driver si lo expone
- `Fi6800Scanner.Core/Services/ProfilePersister.cs` (NUEVO)
  - Guarda `ScanProfile` en JSON
  - Guarda config del driver vía `source.Settings` (DAT_CUSTOMDSDATA blob opaco)
  - Carga/lista perfiles desde `%APPDATA%\Fi6800Scanner\Profiles\`

**UI**:
- Botón "Guardar lote como TIFF…" → multi-page TIFF
- Botón "Guardar lote como PDF…" → PDF
- Submenú "Perfiles": Guardar actual / Cargar / Eliminar / Default

---

### FASE 10 — Logging + tracking robusto

**Objetivo**: observabilidad de producción.

**Archivos**:
- `Fi6800Scanner.Core/Diagnostics/SerilogAdapter.cs` (NUEVO) — implementa `NTwain.ILog` redirigiendo a Serilog
- Inicialización en `Program.cs`:
  ```csharp
  Log.Logger = new LoggerConfiguration()
      .MinimumLevel.Debug()
      .WriteTo.File(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "Fi6800Scanner", "logs", "log-.txt"),
                    rollingInterval: RollingInterval.Day)
      .CreateLogger();
  NTwain.PlatformInfo.Current.Log = new SerilogAdapter(Log.Logger);
  ```
- PackageRefs: `Serilog`, `Serilog.Sinks.File`

**Métricas a loguear**:
- Cada page received: tiempo de transferencia, tamaño, modo
- Cada error: ConditionCode + stack trace
- Throughput (ppm calculado)
- Eventos del state machine

**Crash dumps**:
- `AppDomain.CurrentDomain.UnhandledException` → log + diálogo

---

### FASE 11 — Tests + documentación + release

**Tests** (`Fi6800Scanner.Tests/`, NUEVO proyecto xUnit):
- `ProfileApplicatorTests.cs` — con un fake `IScannerService` que registra los SetValue calls
- `BatchSplitterTests.cs` — fed con páginas con y sin patch codes
- `SnapshotJsonExporterTests.cs` — round-trip
- `[Trait("Category", "RequiresScanner")]` para integration tests

**Documentación final**:
- `Fi6800Scanner/README.md` actualizar con todas las features
- `Fi6800Scanner/CHANGELOG.md` con versiones
- Screenshots
- `Fi6800Scanner/TROUBLESHOOTING.md` con casos comunes (PaperJam, no aparece source, etc.)

**Release**:
- Build Release + sign si aplica
- Instalador con `Inno Setup` o `WiX`
- README incluye instrucciones de instalación de PaperStream IP

---

## 6. Lo primero que debe hacer Claude Code en la próxima sesión

### Si el usuario llega con el JSON snapshot del fi-6800:

1. **Leer el JSON completo** — es la fuente de verdad sobre qué soporta el escáner físico.
2. Identificar:
   - ¿Qué `PixelTypes` reporta?
   - ¿Qué `Compressions` están disponibles?
   - ¿`AutoColor` está soportado? ¿`PatchDetection`? ¿`Imprinter`?
   - ¿Cuántas caps custom (`CustomCapIds`)? Listar IDs concretos.
   - ¿`ExtImageInfo` qué campos expone?
   - ¿Resolución min/max real?
3. Implementar **Fase 4 (SettingsDialog)** primero — usa el snapshot para popular controles dinámicamente.
4. Tras Fase 4, decidir con el usuario qué fase priorizar (5/6/7/9) según necesidad.

### Si el usuario llega con feedback / bugs del MVP:

1. Reproducir contra el escáner físico.
2. Logs en `%LOCALAPPDATA%\Fi6800Scanner\logs\` (cuando Fase 10 esté hecha).
3. Verificar contra **§3 API QUIRKS** — la mayoría de bugs en NTwain v3 vienen de mal entendido del API.

### Si el usuario quiere algo distinto:

Re-leer este documento + `FI6800_PAPERSTREAM_PLAN.md` + `NTWAIN_V3_REFERENCE.md` antes de cambiar arquitectura.

---

## 7. Build y ejecución

```powershell
cd C:\Users\Windows\Documents\git\ntwain\Fi6800Scanner
dotnet restore Fi6800Scanner.sln
dotnet build Fi6800Scanner.sln -c Debug
.\Fi6800Scanner.App\bin\x86\Debug\net462\Fi6800Scanner.App.exe
```

**Si el build falla**:
- Verificar `dotnet --list-sdks` — necesario al menos uno con MSBuild que entienda SDK-style + net462 (cualquier SDK 6+ sirve).
- Si NuGet no resuelve `NTwain` 3.7.5: verificar que `nuget.org` esté en sources (`dotnet nuget list source`).
- Si `Cyotek.Windows.Forms.ImageBox` no resuelve: el package NuGet correcto es `CyotekImageBox` (sin "Cyotek.Windows.Forms" prefix).

**Si no aparece el escáner en Discover**:
- Pre-flight ya da pistas (architecture mismatch, KB faltante).
- Probar en PaperStream Capture (la app de Fujitsu) primero para confirmar que el escáner sí se ve a nivel TWAIN.
- Verificar que la app se ejecuta como x86 (en Task Manager → Detalles → columna "Plataforma").

---

## 8. Convenciones del código

- **Idioma**: comentarios y mensajes UI en español; nombres de identificadores en inglés (estándar .NET).
- **C# version**: 7.3 (LangVersion 7.3) — no usar `record`, `init-only`, `nullable reference types`, `is not`, switch expressions, etc.
- **String interpolation**: OK (`$"..."`).
- **Pattern matching básico**: OK (`if (x is Foo f)`).
- **Estilo**: 4-space indent, llaves Allman (línea propia para `{`), `private static` para helpers, `var` cuando el tipo es obvio.
- **No emojis en código** salvo en strings de UI cuando es semántico (ej: `"📊"` para indicador de barcode).
- **Manejo de errores**: capturar `Exception` solo en límites (eventos, UI handlers); dejar burbujear el resto.
- **NTwain calls que pueden fallar silenciosamente**: envolver en try/catch y marcar como Skipped/Failed en `ApplyReport`.

---

## 9. Recursos de referencia (en este repo)

- `..\NTWAIN_V3_GUIDE.md` — flujo paso a paso con ejemplo end-to-end
- `..\NTWAIN_V3_REFERENCE.md` — referencia exhaustiva de NTwain (24 secciones)
- `..\FI6800_PAPERSTREAM_PLAN.md` — plan original con investigación
- `..\Spec\Kodak\` — documentación TWAIN/Kodak local (ICAP_*, CAP_*, ExtImageInfo)
- `..\src\NTwain\Capabilities.cs` — código fuente para ver tipos exactos de cada cap
- `..\src\NTwain\Data\TwainValues.cs` — enums (PixelType, JpegQuality, BlankPage, FilterType, etc.)
- `..\src\NTwain\DataTransferredEventArgs.cs` — TWInfo handling
- `..\samples\Sample.Winform\TestForm.cs` — referencia de patrón cliente WinForms

---

## 10. Información crítica del usuario

- **Email**: `dante.usr@gmail.com`
- **Idioma preferido**: español
- **Working dir**: `C:\Users\Windows\Documents\git\ntwain`
- **Hardware**: Fujitsu fi-6800 disponible para validación
- **Arquitectura preferida**: x86 + net462 (decisión confirmada)
- **OS**: Windows 11 Pro N (build snapshot al momento del handoff: 26200)

---

**Fin del handoff. Si algo no está claro, leer en este orden: este documento → FI6800_PAPERSTREAM_PLAN.md → NTWAIN_V3_REFERENCE.md → código fuente.**
