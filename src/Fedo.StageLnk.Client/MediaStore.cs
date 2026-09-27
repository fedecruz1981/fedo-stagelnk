// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: MediaStore
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol; // Tipos de protocolo (MediaAsset) para el almacén de medios

namespace Fedo.StageLnk.Client; // Espacio de nombres del cliente Fedo-StageLnk

/// <summary>
/// Almacén de medios del cliente en disco: raíz con los archivos sincronizados,
/// directorio temporal de descargas (.sync) y área de carteles generados (.text).
/// Verifica la presencia de medios, calcula los faltantes y finaliza las
/// descargas validadas.
/// </summary>
public sealed class MediaStore
{
    public string Root { get; } // Directorio raíz con los medios sincronizados
    public string TempDir { get; } // Directorio temporal de descargas en curso

    public MediaStore(string root)
    {
        Root = root; // Fija la raíz de medios
        TempDir = Path.Combine(root, ".sync"); // El directorio temporal cuelga de la raíz (.sync)
        Directory.CreateDirectory(Root); // Garantiza que la raíz exista
        Directory.CreateDirectory(TempDir); // Garantiza que el directorio temporal exista
    }

    public string PathFor(MediaAsset asset) // Calcula la ruta física de un medio del catálogo
        => Path.Combine(Root, asset.RelativePath); // Combina la raíz con la ruta relativa del medio

    public bool Has(MediaAsset asset)
    {
        var file = new FileInfo(PathFor(asset)); // Obtiene información del archivo destino
        return file.Exists && file.Length == asset.Size; // Existe si está presente y con el tamaño esperado
    }

    public List<string> Missing(IEnumerable<MediaAsset> manifest) // Calcula los medios que faltan en disco
        => manifest.Where(a => !Has(a)).Select(a => a.Id).ToList(); // Filtra los que no están y devuelve sus IDs

    public string TempPathFor(string mediaId) // Ruta temporal de descarga para un medio
        => Path.Combine(TempDir, $"{mediaId}.part"); // Combina el temporal con el id y la extensión .part

    public void Finalize(FileReceiver receiver, MediaAsset asset)
    {
        string target = PathFor(asset); // Ruta definitiva del medio
        string? dir = Path.GetDirectoryName(target); // Directorio que contendrá el archivo
        if (!string.IsNullOrEmpty(dir)) // Si hay subdirectorio
            Directory.CreateDirectory(dir); // Lo crea antes de mover el archivo
        if (File.Exists(target)) // Si ya existe un archivo previo
            File.Delete(target); // Lo borra para sustituirlo por el nuevo
        File.Move(receiver.TempPath, target); // Mueve la descarga temporal a su destino final
    }

    /// <summary>
    /// Mueve una descarga completada (temporal) a su destino definitivo.
    /// Devuelve true solo si el temporal existía y se movió.
    /// </summary>
    public bool Finalize(MediaAsset asset)
    {
        string temp = TempPathFor(asset.Id); // Ruta de la descarga temporal
        if (!File.Exists(temp)) // Si no hay descarga temporal...
            return false; // ...no hay nada que finalizar
        string target = PathFor(asset); // Ruta definitiva del medio
        string? dir = Path.GetDirectoryName(target); // Directorio que contendrá el archivo
        if (!string.IsNullOrEmpty(dir)) // Si hay subdirectorio
            Directory.CreateDirectory(dir); // Lo crea antes de mover el archivo
        if (File.Exists(target)) // Si ya existe un archivo previo
            File.Delete(target); // Lo borra para sustituirlo por el nuevo
        File.Move(temp, target); // Mueve la descarga temporal a su destino final
        return true; // Indica que la descarga se movió con éxito
    }

    public void CleanupStaleTemps()
    {
        foreach (var file in Directory.EnumerateFiles(TempDir, "*.part")) // Recorre los temporales .part
            File.Delete(file); // Elimina cada descarga incompleta u huérfana
    }

    /// <summary>
    /// Lista los archivos de medios presentes en el directorio raíz (sin el
    /// área de sincronización). Los archivos sin extensión de medio se omiten.
    /// </summary>
    public List<MediaAsset> ListLocal()
    {
        var result = new List<MediaAsset>(); // Lista resultado de medios locales
        if (!Directory.Exists(Root)) // Si la raíz no existe
            return result; // Devuelve una lista vacía

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) // Recorre todos los archivos de la raíz
        {
            string rel = Path.GetRelativePath(Root, file); // Ruta relativa del archivo respecto a la raíz
            if (rel.StartsWith(".sync", StringComparison.OrdinalIgnoreCase)) // Omite el área de sincronización
                continue; // Salta al siguiente archivo
            if (rel.StartsWith(".text", StringComparison.OrdinalIgnoreCase)) // Omite el área de carteles
                continue; // Salta al siguiente archivo
            MediaKind? kind = MediaAsset.KindForExtension(Path.GetExtension(file)); // Deduce el tipo de medio por extensión
            if (kind is null) // Si la extensión no corresponde a un medio conocido
                continue; // Salta al siguiente archivo

            var info = new FileInfo(file); // Datos del archivo (tamaño, etc.)
            result.Add(new MediaAsset // Añade un medio a la lista resultado
            {
                Id = rel, // El id es la ruta relativa
                Name = Path.GetFileNameWithoutExtension(file), // Nombre del archivo sin extensión
                Kind = kind.Value, // Tipo de medio detectado
                RelativePath = rel, // Ruta relativa para reconstruir la ruta física
                Size = info.Length // Tamaño en bytes del archivo
            });
        }

        return result.OrderBy(m => m.RelativePath).ToList(); // Devuelve los medios ordenados por ruta
    }
}
