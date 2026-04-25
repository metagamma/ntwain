---
name: NTwain v3 API quirks
description: Sorpresas reales del API de NTwain v3 descubiertas compilando código cliente — para no tropezarse de nuevo
type: reference
originSessionId: e9caa0f1-7fe6-48f5-94c3-d8210b7c8565
---
NTwain v3 (PackageId `NTwain` 3.7.5) tiene varias decisiones de API que no son obvias y rompen el build si asumes lo intuitivo.

**DataSource público**:
- `source.IsOpen` (no hay `source.State`)
- `source.Name` / `.Manufacturer` / `.ProductFamily` / `.Version` (TWVersion struct) / `.ProtocolVersion` (System.Version)
- `Identity` es `internal` — NO accesible. Usa los wrappers anteriores.

**Capabilities con tipos contraintuitivos**:
- `ICapBitDepth` = `ICapWrapper<int>` (no ushort)
- `ICapPhysicalWidth/Height` = `IReadOnlyCapWrapper<TWFix32>` (read-only — no `SetValue`)
- `ICapJpegQuality` = `ICapWrapper<JpegQuality>` (enum: Low=-3, Medium=-2, High=-1, valores 1-100 también válidos)
- `ICapAutoDiscardBlankPages` = `ICapWrapper<BlankPage>` (enum: Disable=-2, Auto=-1) — NO BoolType
- `ICapFilter` = `ICapWrapper<FilterType>` (enum dropout colors)
- `CapPrinterIndex` = `ICapWrapper<int>`, `CapPrinterString` = `ICapWrapper<string>`

**Extended Image Info (DataTransferredEventArgs)**:
- `e.GetExtImageInfo(...)` retorna `IEnumerable<TWInfo>` — NO Dictionary indexable.
- Pattern correcto: `infos.FirstOrDefault(i => i.InfoID == ExtendedImageInfo.X)` y luego check `info.NumItems > 0`.
- `TWInfo.ReadValues()` retorna `IList<object>` no genérico — usar `Convert.ToUInt16(values[0])` o pattern match `is TWFix32 fix`.

**C# 7.3 inferencia de genéricos**:
- `TrySet<T>(ICapWrapper<T>, T, ...)` con argumento `bool ? BoolType.True : BoolType.False` puede fallar inferencia (CS0411).
- Solución: helper específico `SetBool(ICapWrapper<BoolType> cap, bool value, ...)` que evita el ternario inline.

**DoubleFeedDetection enum**:
- Valores: `Ultrasonic=0`, `ByLength=1`, `Infrared=2`. NO hay valor "None"/"Off" estándar — para "off" simplemente no setear.

**Cyotek ImageBox (CyotekImageBox 1.3.1)**:
- `imageBox.GetSelectedImage()` retorna `Image` (no `Bitmap`) pese a lo que dice el blog. Castear si necesitas Bitmap.

**Why**: estos errores no aparecen en docs de NTwain — solo se descubren compilando. Ahorran ~30 min de iteración build-error-fix.

**How to apply**: cuando escribas código cliente de NTwain v3 en este o futuros proyectos, consultar esta lista antes de compilar. Si encuentras nuevos quirks, añadirlos aquí.
