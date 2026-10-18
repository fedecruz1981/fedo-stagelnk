# Licencias de terceros

**Fedo-StageLnk** es software propio, distribuido bajo licencia MIT (ver [`LICENSE`](./LICENSE)).
Este documento detalla los componentes de terceros que usa y con qué licencia se distribuye cada
uno.

> Documento informativo; no es asesoramiento legal.

## 1. Incluidos en las releases

Las releases (`cliente.zip`, `panel.zip`, `servidor.zip`) contienen **solo ejecutables propios**
compilados con `dotnet publish --self-contained false`: no incluyen el runtime de .NET ni
bibliotecas de terceros empaquetadas aparte (van dentro del ejecutable o se resuelven desde el
runtime).

### Dependencias de producto

| Componente | Versión | Licencia | Uso |
|---|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 2.3.0 | MIT | Motor de audio híbrido (WASAPI, streaming) |
| [System.Management](https://www.nuget.org/packages/System.Management) | 10.0.10 | MIT | Diagnóstico del cliente (WMI: CPU, RAM, GPU) |
| [Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET) | 2.23.0 | MIT | Render del visualizador (OpenGL) |
| [Silk.NET.Glfw](https://github.com/dotnet/Silk.NET) | 2.23.0 | MIT | Ventana y contexto GL |
| [Silk.NET.Maths](https://github.com/dotnet/Silk.NET) | 2.23.0 | MIT | Matemática de vectores/matrices |

### Dependencias de pruebas (solo para compilar y testear)

| Componente | Versión | Licencia |
|---|---|---|
| [xUnit.net](https://xunit.net) (`xunit`, `xunit.runner.visualstudio`) | 2.5.3 | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | 17.8.0 | MIT |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 6.0.0 | MIT |

## 2. Requeridos en el sistema (no se redistribuyen)

| Componente | Licencia | Notas |
|---|---|---|
| [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | MIT | Windows x64. Las releases son *framework-dependent*: el usuario instala el runtime (necesario también para el panel WPF) |
| [FFmpeg](https://ffmpeg.org) (`ffprobe`, `ffmpeg`, `ffplay`) | LGPL-2.1+ o GPL-2.0+ según cómo esté compilado | **Opcional y no empaquetado**: se instala aparte (`winget install Gyan.FFmpeg`). Se usa para medir duración/resolución, generar la biblioteca demo y como renderer de video/imagen |

Sin FFmpeg, el sistema sigue funcionando: reproduce audio con NAudio y muestra metadatos vacíos.

FFmpeg no se distribuye con este proyecto, así que no hay obligaciones de LGPL/GPL que cumplir.
Si algún día se empaqueta un binario de FFmpeg dentro de las releases, sí las habría (avisos de
copyright, oferta de código fuente en las compilaciones GPL, etc.).

## 3. Cómo regenerar esta lista

```powershell
# .NET: lista de paquetes usados por la solución
dotnet list Fedo-StageLnk.sln package

# Licencia de cada paquete (desde el caché local de NuGet)
Get-ChildItem "$env:USERPROFILE\.nuget\packages" -Directory |
  ForEach-Object { $ns = Get-ChildItem $_.FullName -Recurse -Filter *.nuspec | Select-Object -First 1;
    if ($ns) { $x = [xml](Get-Content $ns.FullName -Raw);
      "{0} -> {1}" -f $_.Name, $x.package.metadata.license } }
```

Última verificación: 2026-10-17.
