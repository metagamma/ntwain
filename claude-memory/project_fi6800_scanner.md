---
name: Fi6800Scanner project
description: App WinForms para Fujitsu fi-6800 + PaperStream IP usando NTwain v3 + Cyotek; subdirectorio del fork ntwain
type: project
originSessionId: e9caa0f1-7fe6-48f5-94c3-d8210b7c8565
---
**Ubicación**: `C:\Users\Windows\Documents\git\ntwain\Fi6800Scanner\` (subdir dentro del fork de NTwain `metagamma/ntwain` rama `v3`).

**Estado actual**: Fases 0-3 completadas y compilando limpio (build verificado con `dotnet build`). MVP funcional con pre-flight check, capability discovery, profile applicator, escaneo básico, visor Cyotek con thumbnails y diagnóstico exportable a JSON.

**Fases pendientes**: 4 (SettingsDialog dinámico), 5 (separación por patch + badges), 6 (imprinter + recovery dialog), 7 (multi-stream Color+B&W), 8 (visor avanzado), 9 (TIFF multi-page + PDF + DSData persistence), 10 (Serilog + métricas), 11 (tests + release).

**Why**: el usuario quiere demostrar todas las features del fi-6800 vía PaperStream IP. La separación en fases es deliberada — el usuario validó el MVP antes de continuar y quiere que las fases siguientes sean **data-driven** con el JSON snapshot real del escáner físico.

**How to apply**: 
- Antes de retomar, leer `Fi6800Scanner/SESSION_HANDOFF.md` que tiene contexto exhaustivo (decisiones, API quirks descubiertos, plan de cada fase, archivos creados, troubleshooting).
- También están relevantes en `C:\Users\Windows\Documents\git\ntwain\`: `NTWAIN_V3_GUIDE.md`, `NTWAIN_V3_REFERENCE.md` (24 secciones de referencia), `FI6800_PAPERSTREAM_PLAN.md` (investigación inicial).
- Si el usuario trae el JSON del snapshot del fi-6800, usarlo para popular UI dinámica en Fase 4 (no hardcodear opciones).
- Stack: x86 + net462 + WinForms; NuGet `NTwain` 3.7.5, `CyotekImageBox` 1.3.1, `Newtonsoft.Json` 13.0.3.
