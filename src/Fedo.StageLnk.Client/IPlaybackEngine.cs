// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: IPlaybackEngine
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol; // Tipos de protocolo compartidos usados en el contrato

namespace Fedo.StageLnk.Client; // Espacio de nombres del cliente Fedo-StageLnk

/// <summary>
/// Contrato de un motor de reproducción. Permite intercambiar implementaciones:
/// el motor real de audio/vídeo (NAudio + ffplay), el motor híbrido de Windows
/// o el stub de consola de respaldo. Todos los comandos de show se resuelven a
/// estos métodos.
/// </summary>
public interface IPlaybackEngine
{
    string Name { get; } // Nombre descriptivo de la implementación

    /// <summary>Fotogramas por segundo que el motor está mostrando realmente (0 si está parado).</summary>
    double MeasuredFps { get; } // FPS reales que el motor está mostrando (0 si está parado)

    event Action<string>? Log; // Evento para emitir mensajes de log del motor

    void Initialize(string mediaRoot); // Prepara el motor con la raíz de medios
    void Play(MediaAsset asset, double speed = 1.0, DateTimeOffset? scheduledAtUtc = null); // Reproduce un medio del catálogo
    void PlayFile(string absolutePath, MediaKind kind, double speed = 1.0); // Reproduce un archivo por su ruta absoluta
    void PlayCue(Cue cue, string mediaRoot); // Procesa un cue completo de la lista de reproducción
    void Stop(); // Detiene la reproducción actual
    void Pause(); // Pausa la reproducción actual
    void Resume(); // Reanuda la reproducción pausada
    void Freeze(); // Congela el medio activo (equivalente a pausa)
    void Fade(double seconds, bool toBlack); // Fundido a negro o desde negro durante N segundos
    void BlackOut(); // Apaga inmediatamente toda salida
    void Next(); // Salta a la siguiente entrada
}
