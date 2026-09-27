// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: ServerClientSession
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Net; // Proporciona el tipo IPEndPoint
using System.Net.Sockets; // Proporciona el tipo TcpClient
using Fedo.StageLnk.Protocol; // Importa los tipos del protocolo compartido (ClientStatusReport)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Estado que el servidor mantiene por cada cliente conectado: identidad, canales
/// TCP/UDP, último reporte de telemetría, medios sincronizados y la secuencia de
/// mensajes salientes. El método <see cref="Touch"/> refresca el último contacto.
/// </summary>
public sealed class ServerClientSession // Estado por cliente conectado
{
    public Guid Id { get; set; } // Identificador único del cliente
    public required string Name { get; set; } // Nombre del cliente (obligatorio)
    public string Version { get; set; } = "1.0"; // Versión del cliente
    public TcpClient? Tcp { get; set; } // Canal TCP de sincronización del cliente
    public IPEndPoint? UdpEndpoint { get; set; } // Endpoint UDP del cliente (telemetría/comandos)
    public ClientStatusReport? LastReport { get; set; } // Último reporte de telemetría recibido
    public bool ReadyForShow { get; set; } // Indica si el cliente está listo para el show
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow; // Último contacto conocido
    public HashSet<string> SyncedMediaIds { get; } = new(StringComparer.OrdinalIgnoreCase); // Ids de medios ya sincronizados
    public long NextSendSequence { get; set; } // Siguiente número de secuencia para envíos

    public void Touch() => LastSeenUtc = DateTimeOffset.UtcNow; // Actualiza el último contacto al instante actual
}
