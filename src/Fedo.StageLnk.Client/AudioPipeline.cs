// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: AudioPipeline
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

// Importa los tipos de onda (WaveFormat, ISampleProvider) de la librería NAudio.
using NAudio.Wave;

namespace Fedo.StageLnk.Client;

/// <summary>
/// Cadena de audio: aplica el volumen con rampa lineal (para fades) y alimenta el
/// detector de beats con una mezcla mono de las muestras que fluyen.
/// </summary>
// Implementa ISampleProvider para insertarse en la cadena de audio de NAudio.
internal sealed class AudioPipeline : ISampleProvider
{
    // Fuente de audio de la que se leen las muestras (el archivo reproducido).
    private readonly ISampleProvider _source;
    // Detector de beats que recibe la mezcla mono generada.
    private readonly BeatDetector _detector;
    // Número de canales del flujo de audio (1 mono, 2 estéreo, etc.).
    private readonly int _channels;
    // Volumen actual aplicado a cada muestra (avanza hacia el objetivo durante una rampa).
    private double _volume = 1.0;
    // Volumen objetivo que se alcanzará al terminar la rampa.
    private double _targetVolume = 1.0;
    // Incremento de volumen aplicado por muestra durante la rampa.
    private double _rampStep;
    // Indica si hay una rampa de volumen en curso.
    private bool _ramping;

    // Expone el formato del flujo delegando en la fuente.
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Se dispara cuando una rampa llega a volumen 0 (fade a negro completado).</summary>
    // Evento notificado al completarse un fade que llega a silencio total.
    public event Action? RampToZeroCompleted;

    // Constructor: enlaza la fuente y el detector, y captura el número de canales.
    public AudioPipeline(ISampleProvider source, BeatDetector detector)
    {
        _source = source;
        _detector = detector;
        _channels = source.WaveFormat.Channels;
    }

    // Lee muestras desde la fuente; NAudio lo llama pidiendo hasta `count` muestras en `buffer[offset..]`.
    public int Read(float[] buffer, int offset, int count)
    {
        // Solicita el bloque de muestras a la fuente subyacente.
        int read = _source.Read(buffer, offset, count);

        // Recorre cada muestra leída para aplicarle el volumen.
        for (int i = 0; i < read; i++)
        {
            // Solo ajusta el volumen si hay una rampa en curso.
            if (_ramping)
            {
                // Avanza el volumen un paso según la dirección de la rampa.
                _volume += _rampStep;
                // Comprueba si ya se alcanzó (o cruzó) el volumen objetivo.
                if ((_rampStep < 0 && _volume <= _targetVolume) ||
                    (_rampStep > 0 && _volume >= _targetVolume))
                {
                    // Fija el volumen exactamente en el objetivo.
                    _volume = _targetVolume;
                    // Da por finalizada la rampa.
                    _ramping = false;
                    // Si la rampa terminó en silencio, avisa del fade completado.
                    if (_volume <= 0)
                        // Notifica a los suscriptores que la rampa llegó a cero.
                        RampToZeroCompleted?.Invoke();
                }
            }
            // Aplica el volumen a la muestra actual y la reescribe en el buffer.
            buffer[offset + i] = (float)(buffer[offset + i] * _volume);
        }

        // Si hay muestras válidas, prepara la mezcla mono para el detector de beats.
        if (read > 0 && _channels > 0)
        {
            // Calcula cuántos frames completos (conjunto de canales) se leyeron.
            int frames = read / _channels;
            // Buffer temporal con un valor mono por frame.
            var mono = new float[frames];
            // Recorre cada frame para promediar sus canales.
            for (int f = 0; f < frames; f++)
            {
                // Acumulador de la suma de canales del frame actual.
                double sum = 0;
                // Suma los valores de todos los canales del frame.
                for (int c = 0; c < _channels; c++)
                    sum += buffer[offset + f * _channels + c];
                // Almacena el promedio de canales como muestra mono.
                mono[f] = (float)(sum / _channels);
            }
            // Envía la mezcla mono al detector de beats para su análisis.
            _detector.Feed(mono);
        }

        // Devuelve el número de muestras efectivamente escritas.
        return read;
    }

    // Inicia (o actualiza) una rampa de volumen hacia `volume` en `seconds` segundos.
    public void SetTargetVolume(double volume, double seconds)
    {
        // Acota el volumen objetivo al rango válido [0, 1].
        _targetVolume = Math.Clamp(volume, 0, 1);
        // Si la duración no es positiva, aplica el volumen de forma instantánea.
        if (seconds <= 0)
        {
            _volume = _targetVolume;
            _ramping = false;
            return;
        }
        // Calcula el incremento por muestra: variación total dividida entre muestras totales.
        _rampStep = (_targetVolume - _volume) / (seconds * _source.WaveFormat.SampleRate);
        // Activa la rampa solo si realmente hay que cambiar el volumen.
        _ramping = _rampStep != 0;
    }
}
