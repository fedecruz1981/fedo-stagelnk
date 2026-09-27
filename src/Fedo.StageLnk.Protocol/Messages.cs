// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: Messages
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>Saludo inicial del cliente al conectarse (TCP).</summary>
public sealed record HelloMessage // mensaje de saludo inicial del cliente
{
    public required string ClientName { get; init; } // nombre del cliente (obligatorio)
    public string Version { get; init; } = "1.0"; // versión del software del cliente
    public long FreeDiskMb { get; init; } // espacio libre en disco del cliente en megabytes
}

/// <summary>Bienvenida del servidor con la identidad asignada y las versiones vigentes.</summary>
public sealed record WelcomeMessage // mensaje de bienvenida del servidor
{
    public required string ServerName { get; init; } // nombre del servidor (obligatorio)
    public string Version { get; init; } = "1.0"; // versión del software del servidor
    public Guid AssignedClientId { get; init; } // identidad asignada al cliente
    public long ManifestVersion { get; init; } // versión vigente del manifiesto de medios
    public long CueListVersion { get; init; } // versión vigente de la cue list
    public int MediaCount { get; init; } // número de medios en el manifiesto
}

/// <summary>Inventario de medios que debe tener cada cliente (manifiesto versionado).</summary>
public sealed record MediaManifestMessage // mensaje con el inventario de medios del show
{
    public long Version { get; init; } // versión del manifiesto (detecta cambios)
    public List<MediaAsset> Media { get; init; } = new(); // lista de medios que debe tener cada cliente
}

/// <summary>Cue list completa del show, versionada para detectar cambios.</summary>
public sealed record CueListMessage // mensaje con la cue list completa del show
{
    public long Version { get; init; } // versión de la cue list (detecta cambios)
    public List<Cue> Cues { get; init; } = new(); // lista de cues del show
}

/// <summary>Petición del cliente para recibir los medios que le faltan.</summary>
public sealed record SyncRequestMessage // mensaje de petición de medios faltantes
{
    public List<string> MissingMediaIds { get; init; } = new(); // identificadores de los medios que faltan al cliente
}

/// <summary>Confirmación de que un medio se sincronizó y su hash coincide.</summary>
public sealed record SyncCompleteMessage // mensaje de confirmación de sincronización de un medio
{
    public required string MediaId { get; init; } // identificador del medio sincronizado (obligatorio)
    public required string Sha256 { get; init; } // hash SHA-256 verificado del medio (obligatorio)
    public bool Verified { get; init; } // indica si el hash coincidió con el esperado
}

/// <summary>Trozo de archivo (hasta 64 KiB en base64) enviado durante la sincronización.</summary>
public sealed record SyncChunkMessage // mensaje con un trozo de archivo de sincronización
{
    public required string MediaId { get; init; } // identificador del medio al que pertenece el trozo (obligatorio)
    public long Offset { get; init; } // posición de inicio del trozo dentro del archivo
    public long TotalSize { get; init; } // tamaño total del archivo en bytes
    public required string ChunkBase64 { get; init; } // contenido del trozo codificado en base64 (obligatorio)
    public bool IsLast { get; init; } // indica si es el último trozo del archivo
}

/// <summary>Aviso del cliente de que terminó la sincronización y está listo para el show.</summary>
public sealed record ReadyMessage // mensaje de aviso de cliente listo
{
    public bool SyncComplete { get; init; } // indica si la sincronización de medios terminó
    public bool ReadyForShow { get; init; } // indica si el cliente está listo para el show
}

/// <summary>Orden de reproducción de una cue, con opción de arranque programado.</summary>
public sealed record PlayCueCommand // mensaje de orden de reproducción de una cue
{
    public int CueNumber { get; init; } // número de la cue a reproducir
    public DateTimeOffset? ScheduledAtUtc { get; init; } // arranque programado opcional (UTC)
    public double Speed { get; init; } = 1.0; // velocidad de reproducción (1.0 = normal)
}

/// <summary>Orden de fade (a negro o desde negro) con su duración en segundos.</summary>
public sealed record FadeCommand // mensaje de orden de fundido
{
    public double Seconds { get; init; } // duración del fade en segundos
    public bool ToBlack { get; init; } // true = fade a negro; false = fade desde negro
}

/// <summary>
/// Cartel de texto para pantalla (anuncios): el cliente genera el cartel 3D
/// y lo muestra en su salida. El color es hex "#RRGGBB".
/// </summary>
public sealed record TextMessage // mensaje con un cartel de texto para pantalla
{
    public string Text { get; init; } = ""; // texto a mostrar en el cartel
    public int FontSize { get; init; } = 96; // tamaño de fuente del cartel
    public string Color { get; init; } = "#FFD700"; // color del texto en hex "#RRGGBB"
    public string Background { get; init; } = "#0A1A2A"; // color de fondo del cartel en hex "#RRGGBB"
}
