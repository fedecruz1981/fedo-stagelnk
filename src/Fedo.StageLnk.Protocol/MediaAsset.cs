// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: MediaAsset
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Tipo de medio audiovisual: vídeo, imagen, audio o efecto visual.
/// Determina qué motor de reproducción usa el cliente.
/// </summary>
public enum MediaKind // tipo de medio audiovisual
{
    Video, // vídeo
    Image, // imagen
    Audio, // audio
    VisualFx // efecto visual
}

/// <summary>
/// Un archivo de la biblioteca de medios: identificador (ruta relativa o hash),
/// nombre para los shows, tipo, tamaño y metadatos opcionales (duración y
/// resolución). El SHA-256 permite verificar la integridad tras la sincronización.
/// </summary>
public sealed record MediaAsset // registro inmutable que representa un archivo de la biblioteca de medios
{
    public required string Id { get; init; } // identificador del medio (ruta relativa o hash)
    public required string Name { get; init; } // nombre del medio para los shows (obligatorio)
    public MediaKind Kind { get; init; } // tipo de medio (determina el motor de reproducción)
    public required string RelativePath { get; init; } // ruta relativa del archivo (obligatoria)
    public long Size { get; init; } // tamaño del archivo en bytes
    public string Sha256 { get; init; } = ""; // hash SHA-256 para verificar la integridad
    public int DurationSeconds { get; init; } // duración en segundos (metadato opcional)
    public int Width { get; init; } // ancho en píxeles (metadato opcional)
    public int Height { get; init; } // alto en píxeles (metadato opcional)

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) // extensiones de vídeo reconocidas (comparación sin distinguir mayúsculas)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm" // extensiones de vídeo soportadas
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) // extensiones de imagen reconocidas (comparación sin distinguir mayúsculas)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" // extensiones de imagen soportadas
    };

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) // extensiones de audio reconocidas (comparación sin distinguir mayúsculas)
    {
        ".mp3", ".wav", ".flac", ".ogg" // extensiones de audio soportadas
    };

    /// <summary>Resuelve el tipo de medio a partir de la extensión del archivo.</summary>
    public static MediaKind? KindForExtension(string extension) // devuelve el tipo de medio para una extensión dada
    {
        if (VideoExtensions.Contains(extension)) return MediaKind.Video; // si es extensión de vídeo, tipo Video
        if (ImageExtensions.Contains(extension)) return MediaKind.Image; // si es extensión de imagen, tipo Image
        if (AudioExtensions.Contains(extension)) return MediaKind.Audio; // si es extensión de audio, tipo Audio
        return null; // extensión desconocida: devuelve null
    }
}
