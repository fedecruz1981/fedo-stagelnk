// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: MessageType
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Tipos de mensaje que viajan por el protocolo Fedo-StageLnk. Los mensajes de
/// control (play/pause/...) se envían por UDP y los de sincronización de archivos
/// (manifiesto, trozos) por TCP. Los valores numéricos quedan fijos para
/// mantener compatibilidad con versiones anteriores del protocolo.
/// </summary>
public enum MessageType // tipos de mensaje que viajan por el protocolo
{
    Unknown = 0, // valor por defecto/desconocido (fijo en 0)

    Hello, // saludo inicial del cliente
    Welcome, // bienvenida del servidor
    ClientRegistered, // cliente registrado en el servidor
    ClientDisconnected, // cliente desconectado

    MediaManifest, // manifiesto de medios
    SyncRequest, // petición de sincronización
    SyncChunk, // trozo de archivo sincronizado
    SyncComplete, // sincronización de un medio completada
    Ready, // cliente listo para el show

    CueList, // cue list completa del show

    PlayCue, // reproducir una cue
    StopCue, // detener una cue
    Pause, // pausar la reproducción
    Resume, // reanudar la reproducción
    BlackOut, // salida a negro
    Fade, // orden de fundido
    Freeze, // congelar la imagen
    Next, // pasar a la siguiente cue

    Status, // informe de estado/telemetría
    Heartbeat, // latido de vida del cliente
    Ping, // ping de comprobación
    Pong, // respuesta al ping

    ShowText // mostrar un cartel de texto en pantalla
}
