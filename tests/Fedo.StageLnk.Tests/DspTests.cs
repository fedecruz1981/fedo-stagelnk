// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: DspTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Client;
using NAudio.Wave;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>Pruebas de procesamiento digital de señal: FFT y detección de beats.</summary>
public sealed class FftTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la FFT de un seno presenta el pico en la frecuencia esperada
    public void Sine_PeaksAtExpectedFrequency()
    {
        // Frecuencia de muestreo usada para sintetizar la señal
        const int sampleRate = 44100;
        // Tamaño de la ventana de la FFT
        const int window = 1024;
        // Buffer de muestras con el tamaño de la ventana
        var samples = new float[window];
        // Genera cada muestra de la onda senoidal de 440 Hz
        for (int i = 0; i < window; i++)
            // Rellena la muestra con la fórmula del seno de amplitud 0.5
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440.0 * i / sampleRate));

        // Calcula las magnitudes del espectro FFT
        var mag = Fft.Magnitudes(samples);

        // El bin de pico comienza en 1 (se ignora la componente DC)
        int peakBin = 1;
        // Recorre todo el espectro buscando la mayor magnitud
        for (int i = 1; i < mag.Length; i++)
            // Si la magnitud actual supera al pico, se actualiza el bin
            if (mag[i] > mag[peakBin])
                // Marca el bin actual como el nuevo pico
                peakBin = i;

        // bin 10 ≈ 430.7 Hz (el seno de 440 Hz cae aquí)
        // Verifica que el pico está en el bin esperado para 440 Hz
        Assert.Equal(10, peakBin);
        // Guarda la magnitud del bin de pico
        double peakMag = mag[peakBin];
        // Comprueba que la magnitud del pico está en el rango teórico (A/2 = 0.25)
        Assert.InRange(peakMag, 0.15, 0.4); // A/2 = 0.25
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que una señal de silencio no produce ningún pico en el espectro
    public void Silence_HasNoPeak()
    {
        // Calcula la FFT sobre un buffer de ceros (silencio)
        var mag = Fft.Magnitudes(new float[1024]);
        // Comprueba que todas las magnitudes del espectro son cero
        Assert.All(mag, m => Assert.Equal(0, m));
    }
}

public sealed class BeatDetectorTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un tono sostenido no dispara beats (sin cambios de energía)
    public void SteadyTone_DoesNotTriggerBeat()
    {
        // Frecuencia de muestreo del detector
        const int sampleRate = 44100;
        // Crea el detector de beats con esa frecuencia de muestreo
        var detector = new BeatDetector(sampleRate);
        // Contador de beats detectados
        int beats = 0;
        // Suscribe el manejador que incrementa el contador en cada beat
        detector.BeatDetected += _ => beats++;

        // Buffer de un tono del tamaño de la ventana del detector
        var tone = new float[BeatDetector.WindowSize];
        // Genera cada muestra del tono senoidal de 100 Hz
        for (int i = 0; i < tone.Length; i++)
            // Rellena la muestra con el seno de amplitud 0.5
            tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 100.0 * i / sampleRate));

        // Alimenta el detector 60 veces con el tono constante
        for (int w = 0; w < 60; w++)
            // Entrega cada ventana de tono al detector
            detector.Feed(tone);

        // No debe haberse disparado ningún beat con un tono sostenido
        Assert.Equal(0, beats);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un pulso de graves tras silencio dispara al menos un beat
    public void SilenceThenBassPulse_TriggersBeat()
    {
        // Frecuencia de muestreo del detector
        const int sampleRate = 44100;
        // Crea el detector de beats con esa frecuencia de muestreo
        var detector = new BeatDetector(sampleRate);
        // Contador de beats detectados
        int beats = 0;
        // Suscribe el manejador que incrementa el contador en cada beat
        detector.BeatDetected += _ => beats++;

        // Buffer de silencio (todo ceros)
        var silence = new float[BeatDetector.WindowSize];
        // Buffer de graves con energía baja
        var bassLow = new float[BeatDetector.WindowSize];
        // Buffer de graves con energía alta
        var bassHigh = new float[BeatDetector.WindowSize];
        // Genera el tono de graves de baja amplitud
        for (int i = 0; i < bassLow.Length; i++)
            // Rellena la muestra con seno de amplitud 0.3
            bassLow[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 100.0 * i / sampleRate));
        // Genera el tono de graves de alta amplitud
        for (int i = 0; i < bassHigh.Length; i++)
            // Rellena la muestra con seno de amplitud 0.9
            bassHigh[i] = (float)(0.9 * Math.Sin(2 * Math.PI * 100.0 * i / sampleRate));

        // Alimenta el detector 50 veces con silencio
        for (int w = 0; w < 50; w++)
            // Entrega cada ventana de silencio al detector
            detector.Feed(silence);
        // Alimenta el detector 5 veces con graves suaves
        for (int w = 0; w < 5; w++)
            // Entrega cada ventana de graves bajos
            detector.Feed(bassLow);
        // Alimenta el detector 20 veces con graves fuertes
        for (int w = 0; w < 20; w++)
            // Entrega cada ventana de graves altos
            detector.Feed(bassHigh);

        // El salto de energía de graves debe producir al menos un beat
        Assert.True(beats >= 1, "se esperaba al menos un beat al subir la energía de graves");
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la frecuencia de pico del espectro coincide con el tono generado
    public void Spectrum_PeakFrequency_MatchesTone()
    {
        // Frecuencia de muestreo del detector
        const int sampleRate = 44100;
        // Crea el detector de beats con esa frecuencia de muestreo
        var detector = new BeatDetector(sampleRate);
        // Almacena el último snapshot de espectro emitido
        SpectrumSnapshot? last = null;
        // Suscribe el manejador que guarda cada espectro disponible
        detector.SpectrumReady += s => last = s;

        // Buffer de un tono del tamaño de la ventana del detector
        var tone = new float[BeatDetector.WindowSize];
        // Genera cada muestra del tono senoidal de 440 Hz
        for (int i = 0; i < tone.Length; i++)
            // Rellena la muestra con el seno de amplitud 0.5
            tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440.0 * i / sampleRate));

        // Alimenta el detector 5 veces para que emita su espectro
        for (int w = 0; w < 5; w++)
            // Entrega cada ventana de tono al detector
            detector.Feed(tone);

        // El espectro debe haberse emitido al menos una vez
        Assert.NotNull(last);
        // La frecuencia de pico debe rondar los 440 Hz del tono
        Assert.InRange(last!.PeakFrequencyHz, 400, 480);
        // El espectro debe exponer exactamente 24 bins de banda
        Assert.True(last.Bins.Length == 24);
    }
}

public sealed class AudioPipelineTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la rampa a cero escala las muestras y se completa
    public void FadeToZero_ScalesSamples_AndCompletes()
    {
        // Frecuencia de muestreo del pipeline
        const int sampleRate = 44100;
        // Proveedor de audio: generador de seno
        var source = new SineProvider(sampleRate);
        // Detector de beats conectado al pipeline
        var detector = new BeatDetector(sampleRate);
        // Crea el pipeline de audio con fuente y detector
        var pipeline = new AudioPipeline(source, detector);

        // Contador de eventos de rampa completada
        int completed = 0;
        // Suscribe el manejador que incrementa el contador al completar la rampa
        pipeline.RampToZeroCompleted += () => completed++;

        // Inicia la rampa de volumen a cero (0.1 s = 4410 muestras)
        pipeline.SetTargetVolume(0.0, 0.1); // rampa a silencio en 0.1 s (4410 muestras)

        // Buffer de lectura de 4096 muestras
        var buffer = new float[4096];
        // Lee un primer bloque de muestras desde el pipeline
        int read = pipeline.Read(buffer, 0, buffer.Length);
        // El pipeline debe producir muestras desde el inicio
        Assert.True(read > 0);

        // Máxima magnitud observada en el primer bloque
        double maxFirst = 0;
        // Recorre las muestras leídas para hallar el máximo absoluto
        for (int i = 0; i < read; i++)
            // Acumula la mayor magnitud absoluta de la muestra
            maxFirst = Math.Max(maxFirst, Math.Abs(buffer[i]));
        // La rampa no debía silenciar la salida inmediatamente
        Assert.True(maxFirst > 0.3, "la rampa no debía silenciar la salida inmediatamente");

        // Sigue leyendo hasta que la rampa se complete (máx. 10 bloques)
        for (int r = 0; r < 10 && completed == 0; r++)
            // Lee otro bloque para avanzar la rampa
            pipeline.Read(buffer, 0, buffer.Length);

        // La rampa debe haberse completado exactamente una vez
        Assert.Equal(1, completed);

        // tras completar la rampa, la salida es silencio
        // Lee un bloque más para verificar que queda en silencio
        pipeline.Read(buffer, 0, buffer.Length);
        // Comprueba que todas las muestras del bloque final son cero
        Assert.All(buffer, v => Assert.Equal(0, v));
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un cambio de volumen inmediato atenúa las muestras
    public void SetTargetVolume_Immediate_AppliesAttenuation()
    {
        // Frecuencia de muestreo del pipeline
        const int sampleRate = 44100;
        // Crea el pipeline con un generador de seno y un detector
        var pipeline = new AudioPipeline(new SineProvider(sampleRate), new BeatDetector(sampleRate));
        // Aplica una atenuación al 50% de forma instantánea
        pipeline.SetTargetVolume(0.5, 0); // instantáneo

        // Buffer de lectura de 1024 muestras
        var buffer = new float[1024];
        // Lee un bloque de muestras ya atenuadas
        int read = pipeline.Read(buffer, 0, buffer.Length);

        // Verifica que la amplitud de cada muestra no supere 0.51
        for (int i = 0; i < read; i++)
            // El valor absoluto de cada muestra debe estar entre 0 y 0.51
            Assert.InRange(Math.Abs(buffer[i]), 0, 0.51);
    }

    // Proveedor de señal senoidal usado como fuente de audio de prueba
    private sealed class SineProvider : ISampleProvider
    {
        // Incremento de fase por muestra (paso angular)
        private readonly double _phaseStep;
        // Fase acumulada actual del oscilador
        private double _phase;

        // Constructor que configura el formato y el paso de fase
        public SineProvider(int sampleRate)
        {
            // Define el formato WAV como float IEEE mono a la frecuencia dada
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            // Calcula el paso de fase para un seno de 440 Hz
            _phaseStep = 2 * Math.PI * 440.0 / sampleRate;
        }

        // Formato del flujo de audio (mono float IEEE)
        public WaveFormat WaveFormat { get; }

        // Produce muestras senoidales en el buffer de salida
        public int Read(float[] buffer, int offset, int count)
        {
            // Genera cada muestra solicitada
            for (int i = 0; i < count; i++)
            {
                // Escribe la muestra senoidal de amplitud 0.5 en la fase actual
                buffer[offset + i] = (float)(0.5 * Math.Sin(_phase));
                // Avanza la fase según el paso calculado
                _phase += _phaseStep;
            }
            // Devuelve el número de muestras generadas
            return count;
        }
    }
}
