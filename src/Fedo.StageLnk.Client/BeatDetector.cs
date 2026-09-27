// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: BeatDetector
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Client;

/// <summary>Golpe detectado en el audio: fuerza normalizada y momento del evento.</summary>
// Registro inmutable con los datos de un beat detectado.
public sealed record BeatInfo
{
    // Fuerza del golpe normalizada entre 0 y 1.
    public double Strength { get; init; }
    // Energía de graves observada en la ventana del golpe.
    public double BassEnergy { get; init; }
    // Energía promedio reciente de graves (línea base de comparación).
    public double AverageEnergy { get; init; }
    // Instante en UTC en que se detectó el beat.
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Espectro reducido de una ventana analizada, con el pico dominante.</summary>
// Registro inmutable con el espectro reducido listo para visualización.
public sealed record SpectrumSnapshot
{
    // Valores agregados de energía por banda del espectro reducido.
    public double[] Bins { get; init; } = Array.Empty<double>();
    // Frecuencia del bin de mayor magnitud.
    public double PeakFrequencyHz { get; init; }
    // Magnitud del pico dominante del espectro.
    public double PeakMagnitude { get; init; }
}

/// <summary>
/// Detector de beats por energía de graves: cada ventana FFT calcula la energía del
/// espectro grave (≈ 40-350 Hz) y la compara con la media reciente. Un salto por
/// encima del umbral dispara <see cref="BeatDetected"/>.
/// </summary>
// Clase pública del detector de beats basado en energía de graves.
public sealed class BeatDetector
{
    // Número de muestras por ventana de análisis FFT.
    public const int WindowSize = 1024;
    // Tamaño del historial circular de energías recientes.
    private const int HistoryCount = 43;
    // Factor mínimo (energía / media) para considerar que hubo un beat.
    private const double OnsetThreshold = 1.3;
    // Último bin considerado "grave" dentro del espectro.
    private const int MaxBassBin = 8;

    // Frecuencia de muestreo del audio de entrada (Hz).
    private readonly int _sampleRate;
    // Número de bandas del espectro reducido.
    private readonly int _spectrumBins;
    // Buffer circular con la ventana de muestras en análisis.
    private readonly double[] _window = new double[WindowSize];
    // Posición de la siguiente muestra a guardar en la ventana.
    private int _fill;
    // Historial circular de energías de graves de ventanas anteriores.
    private readonly double[] _history = new double[HistoryCount];
    // Índice de escritura en el historial circular.
    private int _historyIndex;
    // Cantidad de valores válidos acumulados en el historial.
    private int _historyCount;
    // Energía de graves de la última ventana analizada.
    private double _lastBassEnergy;

    // Evento disparado cuando se detecta un beat.
    public event Action<BeatInfo>? BeatDetected;

    /// <summary>Espectro reducido de cada ventana analizada, para visualización.</summary>
    // Evento que publica el espectro reducido tras cada análisis.
    public event Action<SpectrumSnapshot>? SpectrumReady;

    // Constructor: guarda la tasa de muestreo y el número de bandas del espectro.
    public BeatDetector(int sampleRate, int spectrumBins = 24)
    {
        _sampleRate = sampleRate;
        _spectrumBins = spectrumBins;
    }

    // Reinicia el estado interno del detector (ventana, historial y energía previa).
    public void Reset()
    {
        // Vacía la ventana de muestras pendientes.
        _fill = 0;
        // Vuelve al inicio del historial circular.
        _historyIndex = 0;
        // Marca el historial como vacío.
        _historyCount = 0;
        // Descarta la energía previa.
        _lastBassEnergy = 0;
        // Pone a cero todos los valores del buffer de ventana.
        Array.Clear(_window);
    }

    /// <summary>Alimenta muestras mono; cuando hay una ventana completa analiza el espectro.</summary>
    // Recibe muestras mono y acumula hasta completar una ventana.
    public void Feed(ReadOnlySpan<float> samples)
    {
        // Recorre cada muestra del lote recibido.
        foreach (float sample in samples)
        {
            // Guarda la muestra en la ventana y avanza el puntero.
            _window[_fill++] = sample;
            // Si la ventana está completa, se analiza y se reinicia.
            if (_fill >= WindowSize)
            {
                // Analiza la ventana llena (FFT y detección de beat).
                Analyze();
                // Reinicia el puntero de la ventana.
                _fill = 0;
                // Limpia el buffer para la siguiente ventana.
                Array.Clear(_window);
            }
        }
    }

    // Analiza la ventana completa: espectro, energía de graves y posible beat.
    private void Analyze()
    {
        // Calcula las magnitudes espectrales de la ventana mediante FFT.
        var mag = Fft.Magnitudes(_window.AsSpan());

        // Publica el espectro reducido y el pico dominante para la visualización.
        SpectrumReady?.Invoke(new SpectrumSnapshot
        {
            Bins = ReduceSpectrum(mag),
            PeakFrequencyHz = PeakFrequency(mag),
            PeakMagnitude = PeakMagnitude(mag)
        });

        // Acumulador de la energía total en la banda de graves.
        double bass = 0;
        // Suma las magnitudes de los bins que caen en el rango grave.
        for (int i = 1; i <= MaxBassBin && i < mag.Length; i++)
            bass += mag[i];

        // Si el historial aún no está lleno, solo se rellena sin detectar.
        if (_historyCount < HistoryCount)
        {
            // Registra la energía actual en la posición del historial.
            _history[_historyIndex] = bass;
            // Avanza el índice de forma circular.
            _historyIndex = (_historyIndex + 1) % HistoryCount;
            // Cuenta este valor como nuevo elemento del historial.
            _historyCount++;
            // Actualiza la última energía para la siguiente comparación.
            _lastBassEnergy = bass;
            // Sale sin evaluar beat mientras se calienta el historial.
            return;
        }

        // Acumulador para la media de energías del historial.
        double average = 0;
        // Suma todas las energías del historial.
        for (int i = 0; i < HistoryCount; i++)
            average += _history[i];
        // Calcula la energía promedio reciente.
        average /= HistoryCount;

        // Sobrescribe el valor más antiguo con la energía actual.
        _history[_historyIndex] = bass;
        // Avanza el índice de escritura de forma circular.
        _historyIndex = (_historyIndex + 1) % HistoryCount;

        // Dispara beat si la energía supera el umbral relativo y además crece.
        if (average > 0 && bass > average * OnsetThreshold && bass > _lastBassEnergy)
        {
            // Normaliza la fuerza del golpe al rango [0, 1].
            double strength = Math.Clamp(bass / average - 1.0, 0, 1);
            // Publica el evento de beat con sus métricas.
            BeatDetected?.Invoke(new BeatInfo
            {
                Strength = strength,
                BassEnergy = bass,
                AverageEnergy = average
            });
        }

        // Actualiza la energía previa para la siguiente ventana.
        _lastBassEnergy = bass;
    }

    // Calcula la frecuencia (Hz) del bin con mayor magnitud.
    private double PeakFrequency(double[] mag)
    {
        // Bin dominante, partiendo de 1 para ignorar la componente continua.
        int peakBin = 1;
        // Magnitud máxima encontrada hasta ahora.
        double peak = 0;
        // Recorre los bins de la mitad positiva del espectro.
        for (int i = 1; i < mag.Length; i++)
        {
            // Si la magnitud supera el máximo actual, se actualiza el pico.
            if (mag[i] > peak)
            {
                peak = mag[i];
                peakBin = i;
            }
        }
        // Convierte el bin ganador a frecuencia usando la tasa de muestreo.
        return (double)peakBin * _sampleRate / WindowSize;
    }

    // Devuelve el valor de magnitud más alto del espectro.
    private double PeakMagnitude(double[] mag)
    {
        // Acumulador del máximo.
        double peak = 0;
        // Recorre los bins buscando el mayor valor.
        for (int i = 1; i < mag.Length; i++)
            peak = Math.Max(peak, mag[i]);
        return peak;
    }

    // Reduce el espectro completo a un número fijo de bandas con espaciado musical.
    private double[] ReduceSpectrum(double[] mag)
    {
        // Buffer de salida con una banda por posición.
        var reduced = new double[_spectrumBins];
        // Si no hay espectro, devuelve bandas en cero.
        if (mag.Length == 0)
            return reduced;

        // Último bin útil de la mitad positiva del espectro.
        int maxBin = mag.Length - 1;
        // Agrupa los bins en _spectrumBins bandas de tamaño geométrico (log-frecuencia):
        // las bajas frecuencias (musicalmente densas) reciben más resolución que las altas.
        for (int i = 0; i < _spectrumBins; i++)
        {
            // Bin inicial de la banda i, creciendo de forma geométrica.
            int start = (int)Math.Floor(Math.Pow(maxBin, i / (double)_spectrumBins));
            // Bin final (exclusivo) de la banda i.
            int end = (int)Math.Floor(Math.Pow(maxBin, (i + 1) / (double)_spectrumBins));
            // La banda comienza en 1 para excluir la componente continua.
            start = Math.Max(start, 1);
            // Garantiza al menos un bin por banda.
            if (end <= start)
                end = start + 1;
            // Suma de magnitudes de los bins de la banda.
            double sum = 0;
            // Recorre los bins de la banda acumulando su magnitud.
            for (int b = start; b < end && b < mag.Length; b++)
                sum += mag[b];
            // Almacena la energía agregada de la banda.
            reduced[i] = sum;
        }
        return reduced;
    }

    // Convierte un índice de bin a su frecuencia central en hercios.
    public double FrequencyOfBin(int bin)
        => (double)bin * _sampleRate / WindowSize;
}
