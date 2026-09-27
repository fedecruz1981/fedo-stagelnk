// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: MediaLibrary
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol; // Importa los tipos del protocolo compartido (MediaAsset)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Índice de la biblioteca de medios del servidor, clave por Id (hash SHA-256).
/// Mantiene una versión que cambia con cada actualización para difundir el
/// manifiesto a los clientes.
/// </summary>
public sealed class MediaLibrary // Índice de la biblioteca de medios del servidor
{
    private readonly Dictionary<string, MediaAsset> _byId = new(StringComparer.OrdinalIgnoreCase); // Índice de medios por Id (hash), insensible a mayúsculas
    private long _version; // Versión que cambia con cada actualización

    public long Version => _version; // Expone la versión del manifiesto

    public IReadOnlyList<MediaAsset> All // Expone todos los medios ordenados por nombre
        => _byId.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(); // Ordena por nombre ignorando mayúsculas

    public MediaAsset? Find(string id) // Busca un medio por su Id
        => _byId.TryGetValue(id, out var asset) ? asset : null; // Devuelve el medio o null si no existe

    public void ReplaceAll(IEnumerable<MediaAsset> media) // Reemplaza toda la biblioteca
    {
        _byId.Clear(); // Vacía el índice
        foreach (var asset in media) // Recorre los medios de entrada
            _byId[asset.Id] = asset; // Indexa cada medio por su Id
        _version++; // Incrementa la versión
    }

    public string ResolveLocalPath(MediaAsset asset, string libraryRoot) // Resuelve la ruta física de un medio
        => Path.Combine(libraryRoot, asset.RelativePath); // Combina la raíz con la ruta relativa
}
