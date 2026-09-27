// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: ProtocolDefaults
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Valores por defecto del protocolo: nombre, versión, puertos de red,
/// temporizadores de heartbeat y tamaños máximos de trozos/datagramas.
/// </summary>
public static class ProtocolDefaults // constantes con los valores por defecto del protocolo
{
    public const string ProtocolName = "fedo-stagelnk"; // nombre del protocolo
    public const int ProtocolVersion = 1; // versión del protocolo

    public const int TcpPort = 9001; // puerto TCP para sincronización de archivos
    public const int UdpPort = 9002; // puerto UDP para mensajes de control
    public const int HttpPort = 9003; // puerto HTTP para streaming de medios (Range)

    public const int HeartbeatIntervalMs = 1000; // intervalo entre heartbeats (1000 ms = 1 s)
    public const int HeartbeatTimeoutMs = 6000; // tiempo para considerar perdido al cliente (6000 ms = 6 s)

    public const int ChunkSize = 65536; // tamaño máximo de cada trozo de archivo (64 KiB)
    public const int MaxDatagramBytes = 65507; // tamaño máximo de un datagrama UDP (64 KiB menos cabeceras IP/UDP)

    // Constantes del canal de sincronización por HTTP (v0.8)
    public const int MaxParallelDownloads = 3; // número máximo de archivos descargados a la vez
    public const int SyncMaxAttempts = 3; // intentos máximos por archivo antes de declararlo fallido
    public const int SyncRetryDelayMs = 1500; // espera entre reintentos de descarga (1.5 s)
    public const int SyncCopyBuffer = 65536; // tamaño del búfer de copia del flujo HTTP (64 KiB)

    public const string Magic = "FSL"; // firma mágica del protocolo
}
