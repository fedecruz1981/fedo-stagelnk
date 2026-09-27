// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: ClientStatusReport
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Telemetría en vivo que cada cliente envía al servidor por UDP una vez por
/// segundo: estado del motor (FPS reales), uso de CPU/GPU/RAM, resolución de
/// pantalla, temperatura y estado de la sincronización. Es la base del panel
/// de control del operador.
/// </summary>
public sealed record ClientStatusReport // registro inmutable con la telemetría en vivo del cliente
{
    public Guid ClientId { get; init; } // identificador del cliente que envía el informe
    public ClientStatus Status { get; init; } = ClientStatus.Connecting; // estado actual (Conectando por defecto)
    public double Fps { get; init; } // fotogramas por segundo reales del motor
    public string Gpu { get; init; } = ""; // nombre de la GPU en uso
    public double GpuUsage { get; init; } // uso de GPU en porcentaje
    public double CpuUsage { get; init; } // uso de CPU en porcentaje
    public long RamUsedMb { get; init; } // memoria RAM usada en megabytes
    public long RamTotalMb { get; init; } // memoria RAM total en megabytes
    public int ResolutionWidth { get; init; } // ancho de la resolución de salida en píxeles
    public int ResolutionHeight { get; init; } // alto de la resolución de salida en píxeles
    public double TemperatureC { get; init; } // temperatura en grados Celsius
    public long FreeDiskMb { get; init; } // espacio libre en disco en megabytes
    public int LastCueNumber { get; init; } // número de la última cue reproducida
    public string SyncState { get; init; } = ""; // estado de la sincronización de medios
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow; // momento de generación del informe (UTC)
}
