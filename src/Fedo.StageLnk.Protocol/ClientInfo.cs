// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: ClientInfo
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Estados de ciclo de vida de un cliente: desde que se conecta (Connecting),
/// pasa por la sincronización de archivos (Syncing), queda listo para el show
/// (Ready), reproduce una cue (Playing) o se detiene (Stopped).
/// </summary>
public enum ClientStatus // estados de ciclo de vida de un cliente
{
    Disconnected, // sin conexión (estado inicial)
    Connecting, // estableciendo la conexión
    Syncing, // sincronizando los archivos de medios
    Ready, // listo para el show
    Playing, // reproduciendo una cue
    Stopped, // detenido tras la reproducción
    Error // condición de error
}

/// <summary>
/// Información básica de un cliente conectado: identidad, nombre de sala,
/// versión del software, estado actual y momento del último contacto.
/// </summary>
public sealed record ClientInfo // registro inmutable con la información básica del cliente
{
    public Guid Id { get; init; } = Guid.NewGuid(); // identificador único del cliente (nuevo GUID por defecto)
    public required string Name { get; init; } // nombre de la sala o del cliente (obligatorio)
    public string Version { get; init; } = "1.0"; // versión del software del cliente
    public ClientStatus Status { get; init; } = ClientStatus.Disconnected; // estado de ciclo de vida actual
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow; // momento del último contacto (UTC)
}
