// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: ProjectFile
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Text.Json; // Proporciona la serialización JSON (JsonSerializer)
using System.Text.Json.Serialization; // Proporciona opciones de serialización (JsonIgnoreCondition)
using Fedo.StageLnk.Protocol; // Importa los tipos del protocolo compartido (MediaAsset, Cue)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Proyecto de show completo (.fsl): nombre, versión, raíz de la biblioteca,
/// manifiesto de medios y cue list. Es la foto de un espectáculo que puede
/// reabrirse en el servidor.
/// </summary>
public sealed class StageProject // Representa un proyecto de show completo
{
    public string Name { get; set; } = "Sin título"; // Nombre del proyecto con valor por defecto
    public string Version { get; set; } = "1.0"; // Versión del proyecto
    public string? LibraryRoot { get; set; } // Ruta raíz de la biblioteca de medios
    public DateTimeOffset SavedUtc { get; set; } // Fecha y hora de guardado en UTC
    public long ManifestVersion { get; set; } // Versión del manifiesto guardada
    public List<MediaAsset> Media { get; set; } = new(); // Lista de medios del proyecto
    public List<Cue> Cues { get; set; } = new(); // Lista de cues del proyecto
}

/// <summary>Biblioteca de cues reutilizable (.fslcue), sin medios asociados.</summary>
public sealed class CueLibrary // Biblioteca de cues reutilizable
{
    public string Name { get; set; } = "Cue Library"; // Nombre de la biblioteca de cues
    public string Version { get; set; } = "1.0"; // Versión de la biblioteca
    public DateTimeOffset SavedUtc { get; set; } // Fecha y hora de guardado en UTC
    public List<Cue> Cues { get; set; } = new(); // Lista de cues de la biblioteca
}

/// <summary>
/// Persistencia de proyectos y cue libraries en JSON (camelCase, indentado),
/// distinguidos por su extensión: .fsl para proyectos completos y .fslcue
/// para bibliotecas de cues.
/// </summary>
public static class ProjectFile // Persistencia de proyectos y cue libraries en JSON
{
    private static readonly JsonSerializerOptions Options = new() // Opciones de serialización JSON compartidas
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Nombres de propiedad en camelCase
        WriteIndented = true, // JSON indentado para legibilidad
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull // Omite propiedades null al serializar
    };

    public static void SaveProject(string path, StageProject project) // Guarda un proyecto en disco
    {
        project.SavedUtc = DateTimeOffset.UtcNow; // Registra el momento actual como guardado
        File.WriteAllText(path, JsonSerializer.Serialize(project, Options)); // Serializa y escribe el archivo JSON
    }

    public static StageProject? LoadProject(string path) // Carga un proyecto desde disco
        => JsonSerializer.Deserialize<StageProject>(File.ReadAllText(path), Options); // Lee y deserializa el JSON

    public static void SaveCueLibrary(string path, CueLibrary library) // Guarda una biblioteca de cues
    {
        library.SavedUtc = DateTimeOffset.UtcNow; // Registra el momento del guardado
        File.WriteAllText(path, JsonSerializer.Serialize(library, Options)); // Serializa y escribe el archivo JSON
    }

    public static CueLibrary? LoadCueLibrary(string path) // Carga una biblioteca de cues desde disco
        => JsonSerializer.Deserialize<CueLibrary>(File.ReadAllText(path), Options); // Lee y deserializa el JSON

    public static bool IsFullProject(string path) // Indica si la extensión corresponde a un proyecto completo
        => string.Equals(Path.GetExtension(path), ".fsl", StringComparison.OrdinalIgnoreCase); // Compara la extensión con ".fsl"

    public static bool IsCueLibrary(string path) // Indica si la extensión corresponde a una cue library
        => string.Equals(Path.GetExtension(path), ".fslcue", StringComparison.OrdinalIgnoreCase); // Compara la extensión con ".fslcue"
}
