# Protocolo Fedo-StageLnk

Versión de protocolo: 1 — nombre: `fedo-stagelnk`.

## Puertos

| Puerto | Uso               | Transporte          |
|--------|-------------------|---------------------|
| 9001   | Sync              | TCP (fiable)        |
| 9002   | Comandos          | UDP (baja latencia) |
| 9003   | Streaming HTTP    | TCP (GET/HEAD+Range)|

El puerto 9003 es un servidor HTTP mínimo (`MediaHttpServer`) que sirve los archivos de
la biblioteca del servidor con soporte de rangos (`Range: bytes=...` → `206 Partial Content`).
ffplay puede reproducir directamente una URL `http://host:9003/<ruta>`, de modo que un
cliente reproduce un medio **aunque todavía no esté sincronizado**. El servidor HTTP no
acepta rutas fuera de la biblioteca (traversal protegida) y expone MIME por extensión.

## Formato de mensaje

Todo mensaje es un envelope JSON (`MessageEnvelope`) con campos en camelCase:

```json
{
  "protocolVersion": 1,
  "type": 1,
  "senderId": "3f4a...-guid",
  "sequence": 63921475,
  "json": "{\"clientName\":\"Sala\"}"
}
```

- `type` es un `MessageType` (ver tabla). `sequence` es un número creciente por remitente.
- El payload tipado viaja serializado en `json`.

### Framing TCP

Cada frame: `[4 bytes int32 LE longitud][cuerpo JSON UTF-8]`.
Límite de frame: 64 MB.

### Datagramas UDP

Un datagrama = un envelope JSON (máx. ~64 KB).

## Flujo de conexión y sincronización

Canal **preferente** (v0.8, si el cliente tiene configurado `HttpBaseUrl`):

```
Cliente                       Servidor
   |  Hello (TCP)                 |
   |------------------------------>|
   |  Welcome (TCP)                |
   |  MediaManifest (TCP)          |
   |  CueList (TCP)                |
   |<-----------------------------|
   |  GET http://host:9003/<medio> |   (bytes crudos + Range, en paralelo)
   |------------------------------>|
   |  200 / 206 (bytes)            |
   |<------------------------------|
   |  SyncComplete por archivo     |
   |------------------------------>|
   |  Ready (sync + show)          |
   |------------------------------>|
   |  Heartbeat + Status (UDP)     |
   |-------------------------------|  (cada 1 s)
```

Canal **de respaldo** (v0.1–v0.7, cuando no hay `HttpBaseUrl`): tras el manifiesto el
cliente envía `SyncRequest` con los `missing ids` y el servidor responde un `SyncChunk`
por trozo (64 KiB, base64 en v0.1) por TCP.

```
Cliente                       Servidor
   |  Hello (TCP)                 |
   |------------------------------>|
   |  Welcome (TCP)                |
   |  MediaManifest (TCP)          |
   |  CueList (TCP)                |
   |<-----------------------------|
   |  SyncRequest (missing ids)    |
   |------------------------------>|
   |  SyncChunk (uno por trozo)    |
   |<------------------------------|   (64 KiB/trozo, base64 en v0.1)
   |  SyncComplete por archivo     |
   |------------------------------>|
   |  Ready (sync + show)          |
   |------------------------------>|
```

En ambos canales el cliente envía un `SyncComplete` por cada archivo (verificado por
SHA-256) para que el servidor registre el progreso, y el `Ready` se envía de forma
**no bloqueante**: en cuanto llega el manifiesto el cliente se declara listo y la
descarga continúa en segundo plano. El estado real de la sincronización viaja en el
`ClientStatusReport` (`SyncState` = `"syncing N"` hasta completar) y el servidor no
espera a la descarga completa para permitir el show.

Durante el show el servidor envía comandos por **UDP** (PlayCue, StopCue, Pause, Resume,
BlackOut, Fade, Freeze, Next). El cliente reproduce localmente si el medio ya está en disco;
si el archivo **aún no** está sincronizado y hay streaming HTTP disponible (puerto 9003),
el cliente reproduce la URL directamente:

- Vídeo/imagen: ffplay abre `http://host:9003/<ruta>` (monitor/pantalla completa normal).
- Audio: respaldo por ffplay con la URL, **sin** análisis FFT/beats (los FX audio-reactivos
  requieren el archivo local). Si no hay archivo local ni streaming, la cue se registra sin
  salida y se reintenta la reproducción cuando el archivo llegue.

**Reconexión:** si el TCP se cae (servidor reiniciado, corte de red), el cliente detecta
la desconexión y vuelve a ejecutar el flujo desde el principio: nuevo `Hello`, nuevo
`Welcome` + `MediaManifest` + `CueList`, y vuelve a sincronizar solo lo que le falte
(reanudando los `.part` a medio descargar por HTTP). No hay estado de sesión que sobreviva
al TCP; la sincronización es idempotente por checksum.

## Tipos de mensaje

| Nº | Tipo              | Sentido              | Payload                            |
|----|-------------------|----------------------|------------------------------------|
| 1  | Hello             | C → S (TCP)          | HelloMessage                       |
| 2  | Welcome           | S → C (TCP)          | WelcomeMessage                     |
| 3  | ClientRegistered  | S → C                | —                                  |
| 4  | ClientDisconnected| S → C                | —                                  |
| 5  | MediaManifest     | S → C (TCP)          | MediaManifestMessage               |
| 6  | SyncRequest       | C → S (TCP)          | SyncRequestMessage                 |
| 7  | SyncChunk         | S → C (TCP)          | SyncChunkMessage                   |
| 8  | SyncComplete      | C → S (TCP)          | SyncCompleteMessage                |
| 9  | Ready             | C → S (TCP)          | ReadyMessage                       |
| 10 | CueList           | S → C (TCP)          | CueListMessage                     |
| 11 | PlayCue           | S → C (UDP)          | PlayCueCommand                     |
| 12 | StopCue           | S → C (UDP)          | —                                  |
| 13 | Pause             | S → C (UDP)          | —                                  |
| 14 | Resume            | S → C (UDP)          | —                                  |
| 15 | BlackOut          | S → C (UDP)          | —                                  |
| 16 | Fade              | S → C (UDP)          | FadeCommand                        |
| 17 | Freeze            | S → C (UDP)          | —                                  |
| 18 | Next              | S → C (UDP)          | —                                  |
| 19 | Status            | C → S (UDP)          | ClientStatusReport                 |
| 20 | Heartbeat         | C → S (UDP)          | —                                  |
| 21 | Ping              | bidireccional        | —                                  |
| 22 | Pong              | bidireccional        | —                                  |
| 23 | ShowText          | S → C (UDP)          | TextMessage                        |

## Identidad de archivos

Cada media se identifica por el **SHA-256 de su contenido** (`id`). El servidor calcula el
hash al escanear la biblioteca; el cliente lo verifica mientras recibe y no confirma
`SyncComplete` hasta que el checksum coincida.

## Temporización

- Heartbeat: cada 1000 ms. Timeout de inactividad: 6000 ms.
- Reintentos de conexión del cliente: 10 intentos, 1 s de separación.

## Archivos de proyecto

| Extensión | Contenido |
|-----------|-----------|
| `.fsl`    | Proyecto completo: medios (manifest) + cues + ruta de biblioteca |
| `.stg`    | Mismo formato que `.fsl`; lo usa el panel GUI para guardar proyectos nuevos |
| `.fslcue` | Cue library: solo la lista de cues |

Ambos son JSON (camelCase). Los proyectos guardan la referencia a los medios por su
SHA-256, de modo que al cargar un `.fsl`/`.stg` los medios se re-sincronizan contra la biblioteca.

## Notas de v0.3

- Los `SyncChunk` transportan el dato en base64 dentro del envelope JSON. Desde la v0.8
  el canal preferente de sincronización es HTTP (bytes crudos + Range, sin base64); el
  canal TCP de trozos queda como respaldo para clientes sin `HttpBaseUrl`.
- La duración/resolución de los medios se obtienen con `ffprobe` cuando está disponible
  (variable `FEDO_FFPROBE` permite apuntar a una ruta concreta).

## Notas de v0.4

- Los `ClientStatusReport` (UDP, cada 1 s) ahora incluyen telemetría en vivo: `CpuUsage`,
  `Gpu` (nombre vía WMI), `GpuUsage` (nvidia-smi si existe), `Fps` (del motor de
  reproducción), `TemperatureC` (sensor ACPI; `-1` si no está expuesto) y la resolución
  de pantalla del cliente.
- Comandos per-client: el servidor puede enviar un `PlayCue` a un cliente concreto
  (`SendCommandToAsync`) sin tocar el `CurrentCueNumber` global, usado por next/prev.
- `ServerClientSession` guarda el último reporte (`LastReport`) para mostrar el estado
  en vivo de cada cliente con el comando `clients`.

## Notas de v0.5

- `IPlaybackEngine` expone ahora `Log` y `MeasuredFps`. El motor real (`NaudioEngine`)
  usa NAudio/Media Foundation para reproducir audio con fades sample-accurate
  (`AudioPipeline` con rampa de volumen) y alimenta un `BeatDetector` con la mezcla mono.
- El análisis en vivo es local al cliente y **no** forma parte del protocolo de red:
  `Fft` (radix-2, ventana 1024) → energía de graves → eventos `BeatInfo`
  (fuerza normalizada) y `SpectrumSnapshot` (picos y espectro reducido). El evento se
  conserva dentro de la máquina por ahora; en el futuro puede reenviarse como FX remoto.
- El renderer visual (vídeo/imagen con FFmpeg+OpenGL) y los efectos GPU (partículas,
  shaders GLSL) quedan pendientes; los cues de vídeo/imagen se registran sin salida.

## Notas de v0.6

- Nuevo `ShowText` (UDP): el servidor envía un cartel de texto 3D a todos los clientes
  (`TextMessage` con `Text`, `FontSize`, `Color` y `Background` en "#RRGGBB"). El cliente
  genera el cartel localmente con ffmpeg drawtext (extrusión + borde + sombra,
  `TextSignRenderer`) y lo muestra en pantalla; `stop`/`black`/`fade` lo quitan.
- Comando de consola: `text <mensaje>` (servidor → broadcast; cliente → cartel local).

## Notas de v0.7

- **Streaming por LAN (HTTP + Range):** nuevo `MediaHttpServer` en el puerto 9003. GET/HEAD
  con soporte de rangos (`bytes=a-b`, `a-`, `-suffix`), `Accept-Ranges: bytes`, `206`/`200`
  según la petición, MIME por extensión y protección anti-traversal (`..`, barras invertidas
  y rutas fuera de la biblioteca → `404`). El cliente (`HybridEngine.StreamBaseUrl`) reproduce
  la URL del medio que todavía no tiene sincronizado.
- **`Ready` no bloqueante:** el cliente se declara listo para el show al recibir el manifiesto;
  la descarga (SyncChunk o HTTP) continúa en segundo plano y el progreso se reporta en el
  `ClientStatusReport`.

## Notas de v0.8

- **Sincronización por HTTP en paralelo:** cuando el cliente configura `HttpBaseUrl`
  (p. ej. `http://host:9003`), los medios que faltan se descargan por el `MediaHttpServer`
  con **GET + Range** y **bytes crudos** (sin base64, −33% de tráfico) en **paralelo**
  (`MaxParallelDownloads` = 3). Cada archivo se escribe en su `.part` dentro de `.sync`,
  se verifica con el SHA-256 del manifiesto y se mueve a su destino final al completar.
- **Reanudación (resume):** si una descarga queda a medias, el `.part` se conserva y al
  volver a conectar se reanuda desde el último byte recibido (`Range: bytes=offset-`).
  Un `.part` corrupto (mayor de lo esperado) se descarta y se reinicia.
- **Reintentos:** cada archivo reintenta hasta `SyncMaxAttempts` (3) con
  `SyncRetryDelayMs` (1,5 s); un `404` (medio inexistente) falla de inmediato.
- **Compatibilidad:** el canal TCP (`SyncRequest`/`SyncChunk`) permanece intacto como
  respaldo para clientes sin `HttpBaseUrl`. En ambos canales el cliente reporta
  `SyncComplete` por archivo y el estado `syncing N` en telemetría.
