// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: HybridEngine
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol; // Tipos de protocolo compartidos (MediaAsset, Cue, etc.)

namespace Fedo.StageLnk.Client; // Espacio de nombres del cliente Fedo-StageLnk

/// <summary>
/// Motor combinado: audio real con NAudio (Media Foundation) + vídeo/imagen
/// real con ffplay en ventana propia. Cada tipo de medio se enruta al motor
/// adecuado; solo puede haber uno activo a la vez.
/// </summary>
public sealed class HybridEngine : IPlaybackEngine
{
    public string Name => "Hybrid (NAudio audio + ffplay vídeo/imagen)"; // Nombre descriptivo del motor

    public event Action<string>? Log; // Evento de log reenviado desde los motores internos
    public event Action<BeatInfo>? BeatDetected; // Evento de beat propagado desde el audio
    public event Action<SpectrumSnapshot>? SpectrumAvailable; // Evento de espectro propagado desde el audio

    private readonly NaudioEngine _audio = new(); // Motor de audio NAudio interno
    private readonly FfplayVideoRenderer _video = new(); // Renderer de vídeo ffplay interno
    private string _mediaRoot = ""; // Raíz donde residen los medios sincronizados
    private bool _audioPaused; // Recuerda si el audio quedó en pausa para el Resume posterior

    public HybridEngine()
    {
        _audio.Log += m => Log?.Invoke(m); // Reenvía los logs del audio al log propio
        _video.Log += m => Log?.Invoke(m); // Reenvía los logs del vídeo al log propio
        _audio.BeatDetected += b => BeatDetected?.Invoke(b); // Propaga los beats detectados por el audio
        _audio.SpectrumAvailable += s => SpectrumAvailable?.Invoke(s); // Propaga el espectro del audio
    }

    public double MeasuredFps => _video.MeasuredFps; // Expone los FPS medidos por el renderer de vídeo

    public bool IsVideoActive => _video.IsActive; // Indica si el renderer de vídeo está activo

    /// <summary>Monitor en el que abrir el render de vídeo/imagen (null = principal).</summary>
    public DisplayBounds? TargetMonitor
    {
        get => _video.TargetMonitor; // Devuelve el monitor configurado en el renderer de vídeo
        set => _video.TargetMonitor = value; // Propaga el monitor al renderer de vídeo
    }

    /// <summary>Si es true, el vídeo/imagen se abre a pantalla completa en su monitor.</summary>
    public bool Fullscreen
    {
        get => _video.Fullscreen; // Devuelve el flag de pantalla completa del vídeo
        set => _video.Fullscreen = value; // Propaga el flag al renderer de vídeo
    }

    /// <summary>
    /// URL base del servidor HTTP de streaming (p. ej. http://192.168.1.5:9003).
    /// Cuando un medio no existe en disco, el motor reproduce su URL directamente
    /// en vez de esperar a la descarga (vídeo/imagen con ffplay; audio también por
    /// ffplay como respaldo, aunque sin FFT audio-reactivo).
    /// </summary>
    public string? StreamBaseUrl { get; set; }

    public void Initialize(string mediaRoot)
    {
        _mediaRoot = mediaRoot; // Guarda la raíz de medios
        _audio.Initialize(mediaRoot); // Inicializa el motor de audio con la misma raíz
    }

    public void Play(MediaAsset asset, double speed = 1.0, DateTimeOffset? scheduledAtUtc = null)
    {
        _audioPaused = false; // Reinicia el estado de pausa del audio
        // Ruta física esperada del medio en el disco local
        string local = Path.Combine(_mediaRoot, asset.RelativePath);
        if (File.Exists(local)) // Si el medio ya está sincronizado en disco
        {
            if (asset.Kind == MediaKind.Audio) // Si el medio es audio
            {
                _video.Stop(); // Detiene cualquier vídeo en curso
                _audio.Play(asset, speed, scheduledAtUtc); // Reproduce el audio con la velocidad y horario dados
            }
            else // Si es vídeo o imagen
            {
                _audio.Stop(); // Detiene cualquier audio en curso
                _video.Play( // Lanza la reproducción visual con ffplay
                    local, // Ruta completa a partir de la raíz de medios
                    asset.Name, // Título de ventana con el nombre del medio
                    paused: speed <= 0, // Inicia en pausa si la velocidad no es positiva
                    loop: asset.Kind == MediaKind.Image); // Las imágenes se muestran en bucle
            }
        }
        else if (StreamBaseUrl is not null) // Si el medio no está local pero hay servidor de streaming
        {
            // URL del medio dentro del servidor HTTP (la descarga sigue en segundo plano)
            string url = BuildStreamUrl(asset.RelativePath);
            if (asset.Kind == MediaKind.Audio) // Si es audio
            {
                _video.Stop(); // Detiene cualquier vídeo en curso
                // Notifica que el audio se emite por streaming (sin FFT)
                Log?.Invoke($"Render: '{asset.Name}' no local; audio por streaming (sin FX) {url}");
                _video.Play(url, asset.Name, paused: speed <= 0, loop: false); // Reproduce el audio por ffplay desde la URL
            }
            else // Si es vídeo o imagen
            {
                _audio.Stop(); // Detiene cualquier audio en curso
                // Notifica que se reproduce por streaming desde la URL
                Log?.Invoke($"Render: '{asset.Name}' no local; streaming por HTTP {url}");
                _video.Play(url, asset.Name, paused: speed <= 0, loop: asset.Kind == MediaKind.Image); // Reproduce el medio por ffplay desde la URL
            }
        }
        else // Sin archivo local ni streaming disponible
        {
            // Notifica que el medio no se puede reproducir todavía
            Log?.Invoke($"Render: '{asset.Name}' no local y sin streaming disponible");
        }
    }

    // Construye la URL del medio dentro del servidor HTTP de streaming
    private string BuildStreamUrl(string relativePath)
    {
        // Escapa cada segmento de la ruta para formar una URL válida
        string urlPath = string.Join('/', relativePath.Split('\\', '/').Select(Uri.EscapeDataString));
        // Combina la base con la ruta escapada (sin barra final duplicada)
        return $"{StreamBaseUrl!.TrimEnd('/')}/{urlPath}";
    }

    public void PlayFile(string absolutePath, MediaKind kind, double speed = 1.0)
    {
        _audioPaused = false; // Reinicia el estado de pausa del audio
        if (kind == MediaKind.Audio) // Si se trata de un archivo de audio
        {
            _video.Stop(); // Detiene cualquier vídeo en curso
            _audio.PlayFile(absolutePath, kind, speed); // Reproduce el archivo por el motor de audio
        }
        else // Si es vídeo o imagen
        {
            _audio.Stop(); // Detiene cualquier audio en curso
            _video.Play(absolutePath, Path.GetFileName(absolutePath), paused: speed <= 0, loop: kind == MediaKind.Image); // Reproduce el archivo visual con su nombre como título
        }
    }

    public void PlayCue(Cue cue, string mediaRoot)
    {
        string layers = cue.Layers.Count == 0 // Construye la descripción de capas del cue
            ? "(sin capas)" // Mensaje cuando el cue no tiene capas
            : string.Join(", ", cue.Layers.Select(l => $"z{l.ZOrder}")); // Lista de capas con su orden Z
        Log?.Invoke($"CUE {cue.Number:000} '{cue.Name}' [{cue.Kind}] {layers}"); // Registra el cue recibido con su detalle
    }

    public void Stop()
    {
        _audio.Stop(); // Detiene el audio
        _video.Stop(); // Detiene el vídeo
        _audioPaused = false; // Limpia el estado de pausa del audio
    }

    public void Pause()
    {
        if (_audio.IsPlaying) // Si el audio está sonando
        {
            _audio.Pause(); // Pausa el audio
            _audioPaused = true; // Registra la pausa del audio
        }
        else // Si no suena el audio, pausa lo visual
        {
            _video.Pause(); // Pausa el vídeo/imagen
        }
    }

    public void Resume()
    {
        if (_audioPaused) // Si el audio estaba en pausa
        {
            _audio.Resume(); // Reanuda el audio
            _audioPaused = false; // Limpia el estado de pausa
        }
        else // Si no, reanuda lo visual
        {
            _video.Resume(); // Reanuda el vídeo/imagen
        }
    }

    public void Freeze()
    {
        if (_audio.IsPlaying) // Si el audio está sonando
            _audio.Pause(); // Lo pausa para congelarlo
        else // Si no suena el audio
            _video.Pause(); // Pausa el vídeo/imagen
    }

    public void Fade(double seconds, bool toBlack)
    {
        _audio.Fade(seconds, toBlack); // Aplica el fundido sobre el audio
        if (toBlack && _video.IsActive) // Si se funde a negro y hay vídeo activo
            _video.Stop(); // Detiene el vídeo para dejar la pantalla en negro
    }

    public void BlackOut()
    {
        _audio.Stop(); // Detiene el audio
        _video.Stop(); // Detiene el vídeo
    }

    public void Next() => Stop(); // Saltar al siguiente cue equivale a detener la reproducción
}
