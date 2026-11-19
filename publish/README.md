# MDViewer — publish-сборки

| Папка | Размер (≈) | .NET Runtime | Windows App SDK | Назначение |
|-------|------------|--------------|-----------------|------------|
| `self-contained/` | ~237 MB | **встроен** | встроен | Запуск без установки .NET |
| `framework-dependent/` | ~161 MB | нужен **.NET Desktop Runtime 10 (x64)** | встроен | Меньший размер |

Обе сборки unpackaged **win-x64**, Release, без PDB.
Папки локализации WinUI (*.mui) оставлены только: `ru-RU`, `en-us`, `en-GB`.

`framework-dependent` — это копия рабочего `bin\...\win-x64` (не сырой `dotnet publish`):
у unpackaged WinUI `publish` часто не копирует `MDViewer.pri`, `*.xbf` и часть `Assets`, из‑за чего ломаются окно, иконки и просмотр.

На целевой машине также нужен **WebView2 Runtime** (обычно уже есть в Windows 11).

## Пересборка

```powershell
.\publish\build.ps1
```
