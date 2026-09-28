// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: TestUtils
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using Fedo.StageLnk.Client;
using Fedo.StageLnk.Protocol;

namespace Fedo.StageLnk.Tests;

/// <summary>
/// Utilidades compartidas por los tests: directorios temporales, generación de
/// tonos WAV y detección de herramientas externas (ffprobe/ffmpeg).
/// </summary>
internal static class TestUtils
{
    // Crea un directorio temporal nuevo con nombre único (GUID)
    public static string NewTempDir()
    {
        // Construye la ruta bajo %TEMP%\fsl-tests con un GUID sin guiones
        string dir = Path.Combine(Path.GetTempPath(), "fsl-tests", Guid.NewGuid().ToString("N"));
        // Crea el directorio en disco
        Directory.CreateDirectory(dir);
        // Devuelve la ruta del directorio creado
        return dir;
    }

    // Elimina un directorio temporal y su contenido si existe
    public static void DeleteTempDir(string dir)
    {
        try
        {
            // Solo borra si el directorio existe
            if (Directory.Exists(dir))
                // Borra el directorio de forma recursiva
                Directory.Delete(dir, true);
        }
        catch
        {
            // archivos en uso (por ejemplo, el dispositivo de audio); se ignora
        }
    }

    // Genera un archivo WAV PCM de 16 bits con una onda senoidal
    public static byte[] GenerateToneWav(double frequency = 440.0, double seconds = 3.0)
    {
        // Frecuencia de muestreo fija de 44100 Hz
        int sampleRate = 44100;
        // Número total de muestras según la duración pedida
        int sampleCount = (int)(sampleRate * seconds);
        // Profundidad de bits por muestra
        const short bitsPerSample = 16;
        // Bytes por muestra (16 bits = 2 bytes)
        int blockAlign = bitsPerSample / 8;
        // Tamaño del bloque de datos PCM en bytes
        int dataSize = sampleCount * blockAlign;
        // Tasa de bytes por segundo del flujo
        int byteRate = sampleRate * blockAlign;

        // Flujo en memoria para construir el WAV
        using var ms = new MemoryStream();
        // Escritor binario sobre el flujo en memoria
        using var w = new BinaryWriter(ms);
        // Escribe la cabecera RIFF
        w.Write("RIFF"u8);
        // Escribe el tamaño total del chunk RIFF
        w.Write(36 + dataSize);
        // Escribe el identificador de formato WAVE
        w.Write("WAVE"u8);
        // Inicia el chunk de formato "fmt "
        w.Write("fmt "u8);
        // Escribe el tamaño del chunk de formato
        w.Write(16);
        // Códec PCM (1) sin compresión
        w.Write((short)1);
        // Número de canales: 1 (mono)
        w.Write((short)1);
        // Frecuencia de muestreo
        w.Write(sampleRate);
        // Tasa de bytes por segundo
        w.Write(byteRate);
        // Bytes por bloque de muestra
        w.Write((short)blockAlign);
        // Bits por muestra
        w.Write(bitsPerSample);
        // Inicia el chunk de datos "data"
        w.Write("data"u8);
        // Escribe el tamaño de los datos PCM
        w.Write(dataSize);

        // Amplitud máxima de la señal (50% del rango de 16 bits)
        double amplitude = short.MaxValue * 0.5;
        // Genera cada muestra del tono
        for (int i = 0; i < sampleCount; i++)
        {
            // Aplica fundidos de entrada y salida para evitar clics
            double envelope = Math.Min(1.0, Math.Min(i / (sampleRate * 0.05), (sampleCount - i) / (sampleRate * 0.05)));
            // Calcula la muestra senoidal escalada por la envolvente
            double sample = amplitude * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            // Escribe la muestra como entero con signo de 16 bits
            w.Write((short)sample);
        }

        // Devuelve el WAV completo en bytes
        return ms.ToArray();
    }

    // Escribe un archivo WAV de tono dentro de un directorio
    public static string WriteToneFile(string dir, string name = "Tono de prueba.wav")
    {
        // Asegura que el directorio destino exista
        Directory.CreateDirectory(dir);
        // Construye la ruta completa del archivo
        string path = Path.Combine(dir, name);
        // Escribe el WAV generado en la ruta
        File.WriteAllBytes(path, GenerateToneWav());
        // Devuelve la ruta del archivo escrito
        return path;
    }

    // Indica si la herramienta ffprobe está disponible en el sistema
    public static bool FfprobeAvailable
    {
        get
        {
            try
            {
                // Configura el proceso "ffprobe -version"
                var psi = new ProcessStartInfo("ffprobe", "-version")
                {
                    // Ejecuta sin pasar por el shell
                    UseShellExecute = false,
                    // No muestra ventana de consola
                    CreateNoWindow = true,
                    // Captura la salida estándar
                    RedirectStandardOutput = true,
                    // Captura la salida de error
                    RedirectStandardError = true
                };
                // Lanza el proceso ffprobe
                using var proc = Process.Start(psi);
                // Si no se pudo iniciar, ffprobe no está disponible
                if (proc is null)
                    return false;
                // Consume la salida estándar para evitar bloqueos del proceso
                proc.StandardOutput.ReadToEnd();
                // Espera a que termine con un límite de 5 segundos
                proc.WaitForExit(5000);
                // Disponible si el proceso termina con código 0
                return proc.ExitCode == 0;
            }
            catch
            {
                // Ante cualquier error se asume que no está disponible
                return false;
            }
        }
    }

    // Indica si la herramienta ffmpeg está disponible en el sistema
    public static bool FfmpegAvailable
    {
        get
        {
            try
            {
                // Configura el proceso "ffmpeg -version"
                var psi = new ProcessStartInfo("ffmpeg", "-version")
                {
                    // Ejecuta sin pasar por el shell
                    UseShellExecute = false,
                    // No muestra ventana de consola
                    CreateNoWindow = true,
                    // Captura la salida estándar
                    RedirectStandardOutput = true,
                    // Captura la salida de error
                    RedirectStandardError = true
                };
                // Lanza el proceso ffmpeg
                using var proc = Process.Start(psi);
                // Si no se pudo iniciar, ffmpeg no está disponible
                if (proc is null)
                    return false;
                // Consume la salida estándar para evitar bloqueos del proceso
                proc.StandardOutput.ReadToEnd();
                // Espera a que termine con un límite de 5 segundos
                proc.WaitForExit(5000);
                // Disponible si el proceso termina con código 0
                return proc.ExitCode == 0;
            }
            catch
            {
                // Ante cualquier error se asume que no está disponible
                return false;
            }
        }
    }
}

/// <summary>Motor de reproducción que graba las llamadas para verificarlas en los tests.</summary>
internal sealed class RecordingEngine : IPlaybackEngine
{
    // Nombre que identifica a este motor de prueba
    public string Name => "RecordingEngine";
    // FPS medidos (fijos a 0 en las pruebas)
    public double MeasuredFps => 0;

    // Evento de log que se ignora en las pruebas
    public event Action<string>? Log { add { } remove { } }
    // Evento al reproducir una cue
    public event Action<Cue>? CuePlayed;
    // Evento al reproducir un medio
    public event Action<MediaAsset>? MediaPlayed;
    // Evento al detener la reproducción
    public event Action? StopCalled;

    // Cola con los números de cue reproducidos
    public ConcurrentQueue<int> PlayedCueNumbers { get; } = new();
    // Cola con los nombres de medio reproducidos
    public ConcurrentQueue<string> PlayedMediaNames { get; } = new();
    // Cola con los horarios programados recibidos en cada medio reproducido
    public ConcurrentQueue<DateTimeOffset?> PlayedSchedules { get; } = new();

    // Inicializa el motor (no hace nada en pruebas)
    public void Initialize(string mediaRoot) { }

    // Reproduce un medio con velocidad y programación opcionales
    public void Play(MediaAsset asset, double speed = 1.0, DateTimeOffset? scheduledAtUtc = null)
    {
        // Registra el nombre del medio reproducido
        PlayedMediaNames.Enqueue(asset.Name);
        // Registra el horario programado que recibió el motor
        PlayedSchedules.Enqueue(scheduledAtUtc);
        // Dispara el evento de medio reproducido
        MediaPlayed?.Invoke(asset);
    }

    // Reproduce un archivo a partir de su ruta absoluta
    public void PlayFile(string absolutePath, MediaKind kind, double speed = 1.0)
    {
        // Registra el nombre del archivo reproducido
        PlayedMediaNames.Enqueue(Path.GetFileName(absolutePath));
        // Dispara el evento construyendo un activo desde la ruta
        MediaPlayed?.Invoke(new MediaAsset
        {
            // Id igual a la ruta absoluta
            Id = absolutePath,
            // Nombre del archivo sin extensión
            Name = Path.GetFileNameWithoutExtension(absolutePath),
            // Tipo de medio recibido
            Kind = kind,
            // Ruta relativa igual a la absoluta
            RelativePath = absolutePath
        });
    }

    // Reproduce una cue completa con su raíz de medios
    public void PlayCue(Cue cue, string mediaRoot)
    {
        // Registra el número de la cue reproducida
        PlayedCueNumbers.Enqueue(cue.Number);
        // Dispara el evento de cue reproducida
        CuePlayed?.Invoke(cue);
    }

    // Detiene la reproducción actual
    public void Stop() => StopCalled?.Invoke();
    // Pausa la reproducción (no hace nada en pruebas)
    public void Pause() { }
    // Reanuda la reproducción (no hace nada en pruebas)
    public void Resume() { }
    // Congela la salida (no hace nada en pruebas)
    public void Freeze() { }
    // Aplica un fundido (no hace nada en pruebas)
    public void Fade(double seconds, bool toBlack) { }
    // Apaga la señal a negro (no hace nada en pruebas)
    public void BlackOut() { }
    // Avanza a la siguiente cue (no hace nada en pruebas)
    public void Next() { }
}
