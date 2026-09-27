// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: NaudioEngine
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using NAudio.Wave; // Tipos NAudio de reproducción de audio (WaveOutEvent, MediaFoundationReader)
using Fedo.StageLnk.Protocol; // Tipos de protocolo compartidos

namespace Fedo.StageLnk.Client; // Espacio de nombres del cliente Fedo-StageLnk

/// <summary>
/// Motor de reproducción real para audio usando NAudio (Media Foundation).
/// El audio se decodifica y suena por el dispositivo por defecto mientras el
/// análisis FFT detecta beats y publica el espectro en vivo para los efectos.
/// El vídeo/imagen siguen sin renderer visual (pendiente en v0.5).
/// </summary>
public sealed class NaudioEngine : IPlaybackEngine
{
    public string Name => "NAudio (audio real, Media Foundation)"; // Nombre descriptivo del motor
    public double MeasuredFps => 0; // El audio no muestra fotogramas, siempre 0

    public event Action<string>? Log; // Evento de log del motor
    public event Action<BeatInfo>? BeatDetected; // Evento emitido cuando se detecta un beat
    public event Action<SpectrumSnapshot>? SpectrumAvailable; // Evento con el espectro en vivo

    private readonly object _lock = new(); // Candado que serializa el acceso al estado interno
    private string _mediaRoot = ""; // Raíz de medios sincronizados
    private MediaFoundationReader? _reader; // Lector de audio vía Media Foundation
    private WaveOutEvent? _output; // Dispositivo de salida de audio
    private AudioPipeline? _pipeline; // Cadena de procesado (sample provider + rampas)
    private BeatDetector? _detector; // Detector de beats sobre el espectro
    private int _spectrumCounter; // Contador para limitar la frecuencia de eventos de espectro

    public TimeSpan? Position
    {
        get
        {
            lock (_lock) // Protege la lectura del lector
                return _reader is null ? null : _reader.CurrentTime; // Devuelve la posición actual si hay lector
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lock) // Protege la lectura del estado de salida
                return _output?.PlaybackState == PlaybackState.Playing; // Verdadero si la salida está reproduciendo
        }
    }

    public void Initialize(string mediaRoot) => _mediaRoot = mediaRoot; // Guarda la raíz de medios para reproducciones posteriores

    public void Play(MediaAsset asset, double speed = 1.0, DateTimeOffset? scheduledAtUtc = null) // Reproduce un medio del catálogo
        => PlayAudioFile(Path.Combine(_mediaRoot, asset.RelativePath), asset.Name, asset.DurationSeconds); // Delega en la reproducción por ruta con nombre y duración conocidos

    public void PlayFile(string absolutePath, MediaKind kind, double speed = 1.0)
    {
        if (kind != MediaKind.Audio) // Solo el audio es competencia de este motor
        {
            Log?.Invoke($"PLAYFILE [{kind}] {absolutePath}: el motor de audio no maneja este tipo (use el motor híbrido)"); // Avisa de que el tipo no es soportado
            return; // Sale sin reproducir
        }
        PlayAudioFile(absolutePath, Path.GetFileName(absolutePath), 0); // Reproduce el archivo con su nombre y duración desconocida
    }

    private void PlayAudioFile(string path, string displayName, int durationSeconds)
    {
        Stop(); // Detiene cualquier reproducción previa

        if (!OperatingSystem.IsWindows()) // NAudio y Media Foundation solo funcionan en Windows
        {
            Log?.Invoke($"PLAY [audio] {displayName}: motor NAudio solo disponible en Windows"); // Notifica la limitación de plataforma
            return; // Aborta la reproducción
        }

        if (!File.Exists(path)) // Comprueba que el archivo exista localmente
        {
            Log?.Invoke($"PLAY [audio] {displayName}: no encontrado localmente ({path})"); // Notifica que falta el archivo
            return; // Aborta la reproducción
        }

        try
        {
            _reader = new MediaFoundationReader(path); // Abre el lector de audio por Media Foundation
            _detector = new BeatDetector(_reader.WaveFormat.SampleRate); // Crea el detector de beats con la frecuencia de muestreo
            _detector.BeatDetected += OnBeat; // Suscribe el manejador de beats
            _detector.SpectrumReady += OnSpectrum; // Suscribe el manejador de espectro
            _pipeline = new AudioPipeline(_reader.ToSampleProvider(), _detector); // Encadena el sample provider con el detector
            _pipeline.RampToZeroCompleted += OnRampToZero; // Suscribe el evento de rampa a cero completada

            _output = new WaveOutEvent(); // Crea el dispositivo de salida
            _output.Init(_pipeline); // Inicializa la salida con la cadena de audio
            _output.Play(); // Empieza a reproducir

            string duration = durationSeconds > 0 ? $"{durationSeconds:F1}s" : _reader.TotalTime.TotalSeconds.ToString("F1") + "s"; // Usa la duración conocida o la calcula del lector
            Log?.Invoke($"PLAY [audio] {displayName} ({duration}) → {_reader.WaveFormat.SampleRate} Hz, {_reader.WaveFormat.Channels} ch"); // Notifica el inicio de la reproducción con sus parámetros
        }
        catch (Exception ex)
        {
            Log?.Invoke($"No se pudo reproducir '{displayName}': {ex.Message}"); // Notifica el fallo de reproducción
            Stop(); // Limpia el estado tras el error
        }
    }

    public void PlayCue(Protocol.Cue cue, string mediaRoot)
    {
        string layers = cue.Layers.Count == 0 // Construye la descripción de capas del cue
            ? "(sin capas)" // Mensaje cuando el cue no tiene capas
            : string.Join(", ", cue.Layers.Select(l => $"z{l.ZOrder}")); // Lista de capas con su orden Z
        Log?.Invoke($"CUE {cue.Number:000} '{cue.Name}' [{cue.Kind}] {layers}"); // Registra el cue recibido con su detalle
        if (cue.Kind is not (CueKind.Audio or CueKind.Crossfade)) // Solo audio y crossfade tienen renderer implementado
            Log?.Invoke($"  (aviso) el renderer visual de {cue.Kind} aún no existe"); // Avisa de que el tipo visual no está implementado
    }

    public void Stop()
    {
        lock (_lock) // Bloquea el estado para detener de forma atómica
        {
            if (_output is not null) // Si hay dispositivo de salida
            {
                try { _output.Stop(); } catch { } // Intenta parar la salida ignorando errores
                _output.Dispose(); // Libera el dispositivo
                _output = null; // Suelta la referencia
            }
            if (_reader is not null) // Si hay lector abierto
            {
                _reader.Dispose(); // Libera el lector
                _reader = null; // Suelta la referencia
            }
            if (_detector is not null) // Si hay detector activo
            {
                _detector.BeatDetected -= OnBeat; // Desuscribe el manejador de beats
                _detector.SpectrumReady -= OnSpectrum; // Desuscribe el manejador de espectro
                _detector = null; // Suelta la referencia
            }
            _pipeline = null; // Suelta la referencia a la cadena de audio
        }
        Log?.Invoke("STOP"); // Notifica la detención
    }

    public void Pause()
    {
        lock (_lock) // Bloquea el estado para pausar de forma atómica
        {
            if (_output?.PlaybackState == PlaybackState.Playing) // Solo pausa si está sonando
                _output.Pause(); // Pausa la salida
        }
        Log?.Invoke("PAUSA"); // Notifica la pausa
    }

    public void Resume()
    {
        lock (_lock) // Bloquea el estado para reanudar de forma atómica
        {
            if (_output?.PlaybackState == PlaybackState.Paused) // Solo reanuda si estaba en pausa
                _output.Play(); // Reanuda la salida
        }
        Log?.Invoke("REANUDAR"); // Notifica la reanudación
    }

    public void Freeze() => Pause(); // Congelar equivale a pausar en el motor de audio

    public void Fade(double seconds, bool toBlack)
    {
        lock (_lock) // Bloquea el acceso a la cadena de audio
        {
            _pipeline?.SetTargetVolume(toBlack ? 0 : 1, seconds); // Ajusta el volumen destino (0 o 1) en el tiempo dado
        }
        Log?.Invoke($"FADE {(toBlack ? "a negro" : "desde negro")} {seconds:F1}s"); // Notifica el fundido con su duración
    }

    public void BlackOut() => Stop(); // Apagar el audio equivale a detenerlo

    public void Next() => Stop(); // Saltar al siguiente cue equivale a detener la reproducción

    private void OnBeat(BeatInfo beat)
    {
        if (beat.Strength >= 0.4) // Solo se loguean los beats con fuerza suficiente
            Log?.Invoke($"♫ BEAT fuerza={beat.Strength:F2} graves={beat.BassEnergy:F1} (media={beat.AverageEnergy:F1})"); // Muestra los datos del beat detectado
        BeatDetected?.Invoke(beat); // Propaga el beat a los suscriptores externos
    }

    private void OnSpectrum(SpectrumSnapshot spectrum)
    {
        if (++_spectrumCounter % 10 == 0) // Solo se publica cada 10 espectros para limitar la carga
            SpectrumAvailable?.Invoke(spectrum); // Propaga el espectro muestreado
    }

    private void OnRampToZero()
    {
        Task.Run(() => // Ejecuta la parada en un hilo de fondo para no bloquear la señal
        {
            lock (_lock) // Bloquea el estado de salida
            {
                if (_output is not null) // Si la salida sigue existiendo
                {
                    try { _output.Stop(); } catch { } // Intenta detenerla ignorando errores
                }
            }
        });
    }
}
