# Claude session memory snapshot

Snapshot de los archivos de memoria persistente de Claude Code asociados a este repo. Sirve como respaldo y como referencia documental del contexto que Claude Code recuerda entre sesiones.

## Ubicación operativa real

Para que Claude Code los lea automáticamente en futuras sesiones deben estar en:

```
C:\Users\Windows\.claude\projects\C--Users-Windows-Documents-git-ntwain\memory\
```

Si clonas este repo en otra máquina y quieres que Claude Code los use, copia los archivos de `claude-memory/` (excluyendo este README) a la ruta equivalente para esa máquina:

```
%USERPROFILE%\.claude\projects\<encoded-project-path>\memory\
```

donde `<encoded-project-path>` es el path absoluto del repo con `:` y `\` reemplazados por `-`.

## Setup en una PC nueva

Tras clonar el repo, ejecutar este one-liner en **PowerShell** desde la raíz del repo:

```powershell
$enc = (Resolve-Path .).Path -replace '[\\:]', '-'
$dst = "$env:USERPROFILE\.claude\projects\$enc\memory"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item claude-memory\*.md $dst -Exclude README.md
Write-Host "Memorias copiadas a: $dst"
```

Qué hace:

1. Calcula el path codificado de Claude Code (sustituye `\` y `:` por `-` sobre el path absoluto del directorio actual).
2. Crea el directorio destino si no existe.
3. Copia `MEMORY.md` + las 3 memorias (excluye este README).
4. Imprime la ruta final para verificar.

Ejemplo: si clonas a `C:\dev\ntwain-reload`, el destino será:

```
C:\Users\<usuario>\.claude\projects\C--dev-ntwain-reload\memory\
```

Después abre Claude Code en esa carpeta. Primer prompt sugerido:

```
lee Fi6800Scanner/SESSION_HANDOFF.md y dime en qué fase estamos para continuar
```

## Archivos

| Archivo | Tipo | Propósito |
|---------|------|-----------|
| `MEMORY.md` | índice | Lista de las memorias activas con descripción de una línea |
| `user_profile.md` | user | Perfil del desarrollador (preferencias, hardware, estilo) |
| `project_fi6800_scanner.md` | project | Estado del proyecto Fi6800Scanner y dónde retomar |
| `reference_ntwain_v3_quirks.md` | reference | API quirks de NTwain v3 descubiertos al implementar |

## Aviso de privacidad

`user_profile.md` contiene información personal (nombre, email del desarrollador). Considerar antes de hacer público este repo o forks de él.
