# Manual em PDF

Gera `docs/RWVideoWall-Manual-do-operador.pdf` a partir de `src/VideoWallRW.App/Manual/ManualContent.cs`
e dos prints em `src/VideoWallRW.App/Assets/Manual`.

Requisitos: Python 3 com `reportlab`, `Pillow` e `fontTools`; fonte Inter em TTF
(`Inter-Regular/SemiBold/Bold/Italic.ttf` na pasta de trabalho) e DejaVu Sans.

```
python3 parse.py ../../src/VideoWallRW.App/Manual/ManualContent.cs manual.json
python3 build.py manual.json ../../src/VideoWallRW.App/Assets/Manual ../../docs/RWVideoWall-Manual-do-operador.pdf <versão>
```
