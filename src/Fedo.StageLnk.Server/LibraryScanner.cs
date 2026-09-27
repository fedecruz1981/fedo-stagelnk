// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: LibraryScanner
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol; // Importa los tipos del protocolo compartido (MediaAsset)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Escanea un directorio buscando archivos de medio conocidos (vídeo, imagen,
/// audio), calcula su hash SHA-256 como identificador y extrae los metadatos
/// con ffprobe (duración y resolución).
/// </summary>
public sealed class LibraryScanner // Escáner de la biblioteca de medios
{
    public List<MediaAsset> Scan(string libraryRoot, IProgress<string>? progress = null) // Escanea la raíz y devuelve los medios encontrados
    {
        var results = new List<MediaAsset>(); // Lista donde se acumulan los medios encontrados
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Conjunto de extensiones admitidas (insensible a mayúsculas)
        extensions.UnionWith(MediaAsset.VideoExtensions); // Añade las extensiones de vídeo
        extensions.UnionWith(MediaAsset.ImageExtensions); // Añade las extensiones de imagen
        extensions.UnionWith(MediaAsset.AudioExtensions); // Añade las extensiones de audio

        if (!Directory.Exists(libraryRoot)) // Si la carpeta raíz no existe
            return results; // Devuelve la lista vacía

        // Recorre todos los archivos de la raíz (incluye subdirectorios)
        foreach (var file in Directory.EnumerateFiles(libraryRoot, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file); // Obtiene la extensión del archivo
            if (!extensions.Contains(ext)) // Si la extensión no está admitida
                continue; // Salta al siguiente archivo

            var kind = MediaAsset.KindForExtension(ext); // Determina el tipo de medio según la extensión
            if (kind is null) // Si el tipo no se pudo determinar
                continue; // Salta al siguiente archivo

            var info = new FileInfo(file); // Obtiene información del archivo (tamaño, etc.)
            string relative = Path.GetRelativePath(libraryRoot, file); // Calcula la ruta relativa a la raíz
            string id = Hash.Sha256File(file); // Calcula el hash SHA-256 como identificador único

            var mediaInfo = MediaProbe.Probe(file); // Sondea el archivo con ffprobe para obtener metadatos

            results.Add(new MediaAsset // Añade el medio a la lista de resultados
            {
                Id = id, // Asigna el identificador por hash
                Name = Path.GetFileNameWithoutExtension(file), // Nombre del archivo sin extensión
                Kind = kind.Value, // Tipo de medio
                RelativePath = relative, // Ruta relativa a la raíz
                Size = info.Length, // Tamaño en bytes
                Sha256 = id, // Hash SHA-256
                DurationSeconds = mediaInfo?.DurationSeconds ?? 0, // Duración en segundos (0 si no se pudo sondear)
                Width = mediaInfo?.Width ?? 0, // Anchura en píxeles
                Height = mediaInfo?.Height ?? 0 // Altura en píxeles
            });

            // Notifica el progreso del escaneo con la ruta relativa y el total acumulado
            progress?.Report($"Escaneado: {relative} ({results.Count} archivos)");
        }

        return results; // Devuelve todos los medios encontrados
    }
}
