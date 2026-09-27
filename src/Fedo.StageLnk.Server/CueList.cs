// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: CueList
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol; // Importa los tipos del protocolo compartido (Cue)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Colección ordenada de cues del show (por número), con una versión que se
/// incrementa en cada modificación para que los clientes detecten cambios.
/// Facilita la navegación secuencial next/previous.
/// </summary>
public sealed class CueList // Colección de cues del show ordenada por número
{
    private readonly SortedDictionary<int, Cue> _cues = new(); // Diccionario ordenado de cues por número
    private long _version; // Versión que se incrementa en cada modificación

    public long Version => _version; // Expone la versión actual de la cue list

    public IReadOnlyList<Cue> Cues // Expone las cues como lista de solo lectura
        => _cues.Values.ToList(); // Convierte los valores del diccionario en una lista

    public Cue? Find(int number) // Busca una cue por su número
        => _cues.TryGetValue(number, out var cue) ? cue : null; // Devuelve la cue si existe o null si no

    public Cue? NextAfter(int number) // Devuelve la primera cue con número mayor al dado
    {
        foreach (var cue in _cues.Values) // Recorre las cues en orden ascendente
            if (cue.Number > number) // Si el número de la cue supera al buscado
                return cue; // Devuelve esa cue
        return null; // No hay cue siguiente
    }

    public Cue? PreviousBefore(int number) // Devuelve la última cue con número menor al dado
    {
        Cue? previous = null; // Candidata a cue anterior
        foreach (var cue in _cues.Values) // Recorre las cues en orden ascendente
        {
            if (cue.Number >= number) // Si la cue alcanza o supera el número buscado
                break; // Detiene la búsqueda
            previous = cue; // Memoriza la cue como candidata
        }
        return previous; // Devuelve la cue anterior encontrada
    }

    public void ReplaceAll(IEnumerable<Cue> cues) // Reemplaza toda la lista con nuevas cues
    {
        _cues.Clear(); // Vacía el diccionario
        foreach (var cue in cues) // Recorre las cues de entrada
            _cues[cue.Number] = cue; // Inserta cada cue por su número
        _version++; // Incrementa la versión para avisar del cambio
    }

    public void Upsert(Cue cue) // Inserta o actualiza una cue
    {
        _cues[cue.Number] = cue; // Guarda la cue por su número (sobrescribe si existe)
        _version++; // Incrementa la versión
    }

    public void Remove(int number) // Elimina una cue por su número
    {
        if (_cues.Remove(number)) // Si la cue existía y se eliminó
            _version++; // Incrementa la versión
    }

    public void Clear() // Elimina todas las cues
    {
        _cues.Clear(); // Vacía el diccionario
        _version++; // Incrementa la versión
    }
}
