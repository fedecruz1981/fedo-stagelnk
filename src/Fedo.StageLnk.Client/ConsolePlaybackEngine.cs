// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: ConsolePlaybackEngine
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

// Importa el protocolo (MediaAsset, MediaKind, Cue, IPlaybackEngine).
using Fedo.StageLnk.Protocol;

namespace Fedo.StageLnk.Client;

/// <summary>
/// Motor de reproducción de respaldo que solo registra los comandos en la
/// consola. Se usa en plataformas sin NAudio/ffplay o como referencia; el motor
/// real es <see cref="HybridEngine"/>.
/// </summary>
// Implementa IPlaybackEngine, el contrato común de los motores de reproducción.
public sealed class ConsolePlaybackEngine : IPlaybackEngine
{
    // Directorio base donde se buscan los archivos de los assets.
    private string _mediaRoot = "";

    // Evento para registrar mensajes de actividad del motor.
    public event Action<string>? Log;

    // Nombre descriptivo del motor (stub temporal sin render real).
    public string Name => "ConsoleStub (FFmpeg/OpenGL pendiente)";

    // Indica si el motor considera que hay una reproducción en curso.
    private bool _playing;

    // Devuelve FPS medidos simulados: 30 si reproduce, 0 en reposo.
    public double MeasuredFps => _playing ? 30.0 : 0;

    // Guarda el directorio raíz de medios al inicializar el motor.
    public void Initialize(string mediaRoot) => _mediaRoot = mediaRoot;

    // Reproduce un asset de la biblioteca: resuelve la ruta y registra la acción.
    public void Play(MediaAsset asset, double speed = 1.0, DateTimeOffset? scheduledAtUtc = null)
    {
        // Compone la ruta local combinando la raíz y la ruta relativa del asset.
        string path = Path.Combine(_mediaRoot, asset.RelativePath);
        // Registra el comando PLAY con los metadatos del asset.
        Log?.Invoke($"PLAY [{asset.Kind}] {asset.Name} ({asset.Width}x{asset.Height}, {asset.DurationSeconds}s) velocidad {speed}");
        // Si el archivo no existe en local, se avisa en el registro.
        if (!File.Exists(path))
            Log?.Invoke($"  (aviso) archivo no encontrado localmente: {path}");
        // Marca el motor como en reproducción.
        _playing = true;
    }

    // Reproduce un archivo con ruta absoluta y registra la acción.
    public void PlayFile(string absolutePath, MediaKind kind, double speed = 1.0)
    {
        // Registra el comando PLAYFILE con la ruta y el tipo de medio.
        Log?.Invoke($"PLAYFILE [{kind}] {absolutePath} (velocidad {speed})");
        // Si el archivo no existe, se avisa en el registro.
        if (!File.Exists(absolutePath))
            Log?.Invoke($"  (aviso) archivo no encontrado localmente: {absolutePath}");
        // Marca el motor como en reproducción.
        _playing = true;
    }

    // Reproduce una cue: registra la cue y sus capas ordenadas por profundidad.
    public void PlayCue(Cue cue, string mediaRoot)
    {
        // Registra la cue con número, nombre, tipo y fades configurados.
        Log?.Invoke($"CUE {cue.Number:000} '{cue.Name}' [{cue.Kind}] fadeIn={cue.FadeIn} fadeOut={cue.FadeOut}");
        // Recorre las capas de la cue ordenadas de mayor a menor ZOrder.
        foreach (var layer in cue.Layers.OrderByDescending(l => l.ZOrder))
            // Registra cada capa con su Z, media (id truncado) y opacidad.
            Log?.Invoke($"  capa z={layer.ZOrder} media={layer.MediaId?[..Math.Min(8, layer.MediaId.Length)]} opacidad={layer.Opacity}");
    }

    // Detiene la reproducción y registra el comando STOP.
    public void Stop() { _playing = false; Log?.Invoke("STOP"); }
    // Pausa la reproducción y registra el comando PAUSA.
    public void Pause() { _playing = false; Log?.Invoke("PAUSA"); }
    // Reanuda la reproducción y registra el comando REANUDAR.
    public void Resume() { _playing = true; Log?.Invoke("REANUDAR"); }
    // Congela el fotograma actual y registra el comando FREEZE.
    public void Freeze() => Log?.Invoke("FREEZE");

    // Registra un fade con su duración y dirección (a negro o desde negro).
    public void Fade(double seconds, bool toBlack)
        => Log?.Invoke($"FADE {(toBlack ? "a negro" : "desde negro")} {seconds:F1}s");

    // Apaga la salida y registra el comando BLACK OUT.
    public void BlackOut() => Log?.Invoke("BLACK OUT");

    // Avanza al siguiente elemento y registra el comando NEXT.
    public void Next() => Log?.Invoke("NEXT");
}
