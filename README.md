# Fedo-StageLnk v1.0

## Descargas

Tres ejecutables para Windows x64, framework-dependent: necesitan el runtime de
.NET 8 Desktop (el panel es WPF).

| Versión | Qué es | Tamaño |
|---------|--------|--------|
| [1.0.1](https://github.com/fedecruz1981/fedo-stagelnk/releases/tag/v1.0.1) | `fedo-stagelnk-v1.0.1-servidor.zip` — host de consola del servidor | 0,15 MB |
| [1.0.1](https://github.com/fedecruz1981/fedo-stagelnk/releases/tag/v1.0.1) | `fedo-stagelnk-v1.0.1-cliente.zip` — host de consola del cliente | 1,84 MB |
| [1.0.1](https://github.com/fedecruz1981/fedo-stagelnk/releases/tag/v1.0.1) | `fedo-stagelnk-v1.0.1-panel.zip` — panel de operador (WPF) | 0,30 MB |

[Todas las releases](https://github.com/fedecruz1981/fedo-stagelnk/releases) · [reportar un problema](https://github.com/fedecruz1981/fedo-stagelnk/issues)

Se descomprime el zip y se abre el `.exe` del zip que corresponda: `Fedo.StageLnk.Server.Host.exe`,
`Fedo.StageLnk.Client.Host.exe` o `Fedo.StageLnk.Server.Gui.exe`. Para compilar desde el
código, ver [Compilar](#compilar).

Sistema profesional cliente/servidor para reproducción multimedia distribuida en espectáculos.
Desarrollado por **Fedo-Soft** — herramientas hechas por técnicos, para técnicos.

![Panel de operador de Fedo-StageLnk abierto sobre la biblioteca de demostración, con la grilla de cues y el estado del servidor en la barra inferior](docs/captura.png)

> La captura es del binario que se publica en la release de arriba, corriendo con
> la biblioteca de ejemplo (`--demo`) que genera la propia app.

Sustituye los cables HDMI largos por una red Ethernet Gigabit: los archivos se transfieren
durante la sincronización y los comandos durante el espectáculo. **Streaming por LAN
opcional (HTTP + Range)**: si un medio aún no está sincronizado, el cliente lo reproduce
directamente desde la URL en vez de bloquear el show.

## Quickstart

Dos terminales en la misma máquina, con el SDK de .NET 8 instalado. FFmpeg es
opcional pero recomendado (`winget install Gyan.FFmpeg`).

```powershell
git clone https://github.com/fedecruz1981/fedo-stagelnk.git
cd fedo-stagelnk
dotnet build Fedo-StageLnk.sln
```

**Terminal 1 — servidor**, arrancando con una biblioteca demo de un tono:

```powershell
dotnet run --project src\Fedo.StageLnk.Server.Host -- --demo
```

**Terminal 2 — cliente**, apuntando a ese servidor:

```powershell
dotnet run --project src\Fedo.StageLnk.Client.Host -- 127.0.0.1 "Sala Principal"
```

El cliente sincroniza la biblioteca, queda `READY` y ya recibe comandos.
Desde la consola del servidor: `play 1`, `stop`, `fade 2`. Desde la del cliente:
`playfile Intro video.mp4` reproduce un archivo local sin pasar por el servidor.

Para trabajar con el panel de operador en vez de con la consola del servidor:

```powershell
dotnet run --project src\Fedo.StageLnk.Server.Gui -- --demo
```

Y para comprobar que todo está sano antes de salir a trabajar:

```powershell
dotnet test Fedo-StageLnk.sln
```

En un espectáculo real, el servidor o el panel van en la cabina y cada cliente en la máquina
que reproduce. Todo el tráfico va por la LAN: **TCP 9001** para sincronizar medios,
**UDP 9002** para comandos y **HTTP 9003** para streaming por rango.

## Estado actual (v0.8: Sync por HTTP en paralelo y reanudable)

- Biblioteca de medios con escaneo, **probe FFmpeg** (duración y resolución reales) y hash SHA-256 por archivo.
- Sincronización cliente/servidor con verificación de checksum.
- **Sync por HTTP (puerto 9003)**: los archivos que faltan se descargan con **bytes crudos +
  Range** (sin base64, −33% de tráfico) y en **paralelo** (3 archivos a la vez). Los `.part`
  a medio descargar **se reanudan** desde el último byte al reconectar, con reintentos
  (3 intentos, 1,5 s) y `404` de fallo inmediato. El canal TCP de trozos queda como respaldo.
- **Streaming HTTP por LAN (puerto 9003)**: `MediaHttpServer` con GET/HEAD y soporte de
  rangos (`Range: bytes=...` → `206`). ffplay reproduce la URL de un medio que todavía se
  está descargando en segundo plano.
- **`Ready` no bloqueante**: el cliente se declara listo al recibir el manifiesto; la
  descarga continúa en segundo plano (`SyncState = "syncing N"` en la telemetría).
- Comandos de show sobre **UDP** (puerto 9002): play, stop, pause, resume, next, prev, freeze, black, fade.
- Heartbeat y telemetría del cliente (estado, RAM, disco, último cue) cada segundo.
- Sistema de Cue List con capas (posición, escala, rotación, blend, color, opacidad...).
- **Editor de cues** desde el host (addcue, delcue, layer) y navegación next/prev automática.
- **Persistencia**: proyectos `.fsl` (medios + cues) y cue libraries `.fslcue`.
- **Multi-cliente**: varios clientes conectados a la vez, cada uno con su propia telemetría
  en vivo. Comandos broadcast a todos o **per-client** (`playto <cliente> <cue>`).
- **Estado en vivo por cliente**: CPU%, GPU (nombre y % uso), FPS reales del motor, RAM,
  resolución de pantalla, temperatura (si el sensor ACPI lo expone), disco libre.
- **Motor de audio real (NAudio / Media Foundation)**: reproduce WAV/MP3/AAC... con
  play, pausa, reanudar, stop y fades reales (`NaudioEngine`).
- **Renderer de vídeo/imagen real (ffplay)**: cada medio visual se abre en su propia
  ventana SDL (`FfplayVideoRenderer`); con `-loop` para imágenes y `-autoexit` para vídeo.
- **Motor híbrido (`HybridEngine`)**: enruta audio a NAudio y vídeo/imagen a ffplay,
  con un único motor activo a la vez y pausa/reanudar/stop coordinados. Con
  `StreamBaseUrl` configurado, si el medio no está en disco reproduce la URL del
  servidor HTTP 9003 (vídeo/imagen con ffplay; audio por ffplay sin FFT como respaldo).
- **Consola interactiva del cliente**: permite ejecutar todas las funciones desde el propio
  cliente — reproducir cues o archivos locales (vídeo/imagen/audio), navegar la cue list,
  listar medios/cues, estado, fades y esperas.
- **Análisis en vivo**: FFT de ventana propia (1024 muestras) con detección de beats por
  energía de graves y espectro reducido publicados como eventos para los efectos.
  El cliente host muestra el pico dominante y una barra de espectro en consola.
- **FX audio-reactivo (FFT)**: las cues de tipo `VisualFx` abren en el cliente una ventana
  WPF (`Fedo.StageLnk.Visualizer`) que dibuja barras espejadas del espectro en vivo, destello
  y anillo de beat, alimentadas por el mismo FFT del audio que suena. Se lanza desde el panel
  con el botón **FX FFT** o con una cue `VisualFx` con capa de audio (demo: cue 5 "FX FFT").
- **Cartel de texto 3D**: anuncios de texto generados con ffmpeg drawtext (extrusión 3D +
  borde + sombra) y mostrados en pantalla; se disparan desde el servidor (`text <mensaje>`)
  o desde la consola del cliente (`TextSignRenderer`).
- Motor pluggable (`IPlaybackEngine`): `HybridEngine`/`NaudioEngine` en Windows,
  `ConsolePlaybackEngine` de respaldo en otras plataformas.

## Arquitectura

```
                Ethernet

   FEDO-STAGELNK SERVER (PC operador / cabina)
     Biblioteca · Cue List · Sincronización · Administración
        |                       |
        +--------+--------------+
                 |
        Cliente 1               Cliente 2
      (Proyector)            (Pantalla LED)
          | HDMI                  | HDMI
          |                       |
       Pantalla               Pantalla
```

- **TCP 9001** — conexión, manifiesto y confirmaciones de sincronización (frame: 4 bytes longitud + JSON).
- **UDP 9002** — comandos en vivo, heartbeat y telemetría (datagrama JSON).
- **TCP 9003** — streaming HTTP de medios con soporte de Range. También es el canal
  preferente de **sincronización** (bytes crudos + Range, descarga paralela y reanudable);
  el canal TCP de trozos base64 queda como respaldo.
- Cada cue reproduce el medio local; si falta y hay streaming disponible, el cliente lo
  emite desde la URL `http://<servidor>:9003/<ruta>` mientras la descarga continúa.

## Repositorio

```
src/
  Fedo.StageLnk.Protocol/     Modelos y mensajes compartidos
  Fedo.StageLnk.Server/       Núcleo del servidor (biblioteca, sync, UDP/TCP)
  Fedo.StageLnk.Client/       Núcleo del cliente (store, motor, conexión)
  Fedo.StageLnk.Server.Host/  Host de consola del servidor
  Fedo.StageLnk.Client.Host/  Host de consola del cliente
  Fedo.StageLnk.Server.Gui/   Panel de operador (WPF)
  Fedo.StageLnk.Visualizer/   Visualizador FFT reactivo (WPF)
tests/
  Fedo.StageLnk.Tests/        Pruebas xUnit (71)
assets/
  fedostagelnk.ico            Icono de la aplicación
docs/
  README.md                   Este documento
  PROTOCOLO.md                Especificación del protocolo de red
```

## Requisitos

- .NET SDK 8 (ver `global.json`). El SDK 8.0.423 se instaló localmente en
  `%LOCALAPPDATA%\Microsoft\dotnet` mediante `dotnet-install.ps1`.
- **FFmpeg** (opcional pero recomendado): `winget install Gyan.FFmpeg`. Se usa `ffprobe`
  para leer duración/resolución reales, `ffmpeg` para generar la biblioteca demo y `ffplay`
  como renderer de vídeo/imagen en el cliente. Sin él, el sistema funciona con metadatos
  vacíos y audio NAudio solamente.

## Compilar

```powershell
dotnet build Fedo-StageLnk.sln
```

## Ejecutar

Servidor (crea biblioteca demo con un tono de prueba):

```powershell
dotnet run --project src\Fedo.StageLnk.Server.Host -- --demo
```

Cliente:

```powershell
dotnet run --project src\Fedo.StageLnk.Client.Host -- 127.0.0.1 "Sala Principal"
```

Con biblioteca demo local (para probar la consola sin depender del servidor):

```powershell
dotnet run --project src\Fedo.StageLnk.Client.Host -- 127.0.0.1 "Sala Principal" --demo
```

### Panel de operador (GUI WPF)

`src\Fedo.StageLnk.Server.Gui` es el panel de operador con interfaz gráfica: sistema de proyectos
(barra **PROYECTO**: Nuevo / Abrir / Guardar / Guardar como, con aviso al cerrar si hay cambios),
cue list editable, transporte (PLAY/STOP/PAUSA/REANUDAR/NEXT/PREV/FREEZE/BLACK/FADE 3s/**FX FFT**),
grid de clientes con telemetría en vivo, anuncio de cartel 3D con selector de color y log de la
sesión. Los proyectos se guardan como `.stg` (JSON: medios + cue list; también abre `.fsl`). El
botón **CARGAR MEDIOS…** importa archivos de audio/vídeo/imagen a la biblioteca del servidor: el
nuevo manifiesto se difunde y cada cliente descarga automáticamente los archivos que le faltan
(con verificación SHA-256) hasta quedar READY. Incluye una biblioteca demo (tono de audio + pista
rítmica `FX Beat` para el FFT + vídeo + logo generados automáticamente):

```powershell
dotnet run --project src\Fedo.StageLnk.Server.Gui -- --demo
```

El primer argumento que no sea un modificador es la ruta de la biblioteca de medios
(por defecto `Library\Server`). Doble clic en una cue la reproduce; los comandos de red
funcionan igual que en la consola del servidor.

### Consola del cliente

El cliente es una consola interactiva que ejecuta **todas** las funciones desde la máquina
local (además de recibir los comandos del servidor por red):

```text
play <n>             reproduce la cue n de la cue list local
playfile <ruta|nom>  reproduce un archivo local (vídeo/imagen/audio) con el motor híbrido
text <texto>         muestra un cartel de texto 3D en pantalla (anuncio)
loadfile <ruta|nom>  verifica un archivo local sin reproducirlo
media                lista los medios locales (manifiesto sincronizado + archivos en disco)
cues                 lista la cue list recibida del servidor
status               estado, motor, mediaRoot y disco libre
stop|pause|resume|freeze|black   control del motor
fade <s>             fade a negro en <s> segundos
next|prev            navega a la siguiente/anterior cue localmente
wait <s>             espera <s> segundos
help | exit
```

`playfile`/`loadfile` aceptan una ruta absoluta o un nombre relativo al `MediaRoot`.
Los vídeos/imágenes se abren en ventana propia con **ffplay**; el audio suena por el
dispositivo por defecto con **NAudio**. Al reproducir una cue o un archivo, el estado
se reporta al servidor por telemetría (FPS, último cue, etc.).

**Reconexión automática:** si el servidor se cae o la conexión TCP se interrumpe, el
cliente pasa a `Disconnected` y entra en un bucle de reconexión (reintentos cada 1 s,
con espera de 3 s entre ciclos si el servidor sigue caído). En cuanto el servidor
vuelve a estar disponible, el cliente reconecta, reenvía su Hello, re-sincroniza el
manifiesto (descargando únicamente lo que le falte, verificado por SHA-256) y vuelve a
quedar READY sin intervención humana.

También se pueden encolar comandos automáticos:

```powershell
dotnet run --project src\Fedo.StageLnk.Client.Host -- --demo --cmd-delay:6000 --cmd:"play 1" --cmd:"playfile Intro video.mp4"
```

Comandos del servidor (consola): `play <n> | stop | pause | resume | next | prev | freeze | black | fade <s> | text <texto> | current`.
Per-client: `playto <cliente> <n>` (envía a un solo cliente).
Edición y persistencia: `addcue <n> <kind> <nombre> | delcue <n> | layer <n> <z> <media> |
saveproj <file.fsl> | loadproj | savecues <file.fslcue> | loadcues | cues | media | clients | exit`.
El comando `clients` muestra una tabla con el estado en vivo de cada cliente
(CPU, GPU, FPS, RAM, resolución, temperatura, cue actual y sync).

## Pruebas

Batería automatizada con xUnit (`tests\Fedo.StageLnk.Tests`):

```powershell
dotnet test Fedo-StageLnk.sln
```

Cobertura (71 tests):
- **Protocolo**: codec TCP/UDP (roundtrip, prefijo de longitud, rechazo de frames inválidos), camelCase.
- **Núcleo**: hash SHA-256, `CueList` (next/prev/upsert/orden), `MediaLibrary`, persistencia `.fsl`/`.fslcue`.
- **Medios**: escáner (hash, kind, rutas relativas), probe ffprobe, `MediaStore`.
- **Streaming HTTP**: `MediaHttpServer` — GET completo, rangos (cerrado, abierto, sufijo),
  HEAD sin cuerpo, MIME, `404` para archivos inexistentes y protección anti-traversal.
- **Playback streaming**: `HybridEngine` — decisión local vs. HTTP según haya archivo en
  disco, URLs escapadas con espacios/subcarpetas, audio sin FX y fallback sin streaming.
- **Sync HTTP**: `HttpFileDownloader` — descarga completa con verificación SHA-256,
  reanudación de un `.part` a medio bajar (Range), rechazo de hash incorrecto y fallo
  rápido ante `404`; `FileReceiver` en modo resume con hash del contenido previo.
- **DSP**: FFT (pico del tono), detección de beats (tono estable = sin beats, pulso = beat), rampa de fade.
- **E2E real**: servidor TCP/UDP + cliente que se sincroniza (checksum) por TCP y por
  **HTTP** (puertos efímeros, sin colisiones), recibe cues, ejecuta comandos broadcast y
  `playto` per-client, y se reconecta automáticamente cuando el servidor se reinicia.

## Siguiente hoja de ruta

- v0.8 Renderer OpenGL nativo con composición de capas, blend y salida fullscreen.
- v0.8 Visual FX GPU: partículas y shaders GLSL alimentados por los eventos de beat/espectro.
- v1.0 Release. v2.0 Node editor, DMX, OSC, Art-Net, plugins.

© Fedo-Soft — Fedo-StageLnk