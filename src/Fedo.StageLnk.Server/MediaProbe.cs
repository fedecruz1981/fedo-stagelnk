// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: MediaProbe
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics; // Proporciona el lanzamiento de procesos (ffprobe)
using System.Text.Json; // Proporciona el análisis de JSON (JsonDocument)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>Metadatos extraídos de un archivo de medio con ffprobe.</summary>
public sealed class MediaInfo // Metadatos de un archivo de medio
{
    public int DurationSeconds { get; init; } // Duración del medio en segundos
    public int Width { get; init; } // Anchura de la imagen en píxeles
    public int Height { get; init; } // Altura de la imagen en píxeles
}

/// <summary>
/// Sondea archivos de medio con ffprobe (si está disponible en el sistema o vía
/// la variable de entorno FEDO_FFPROBE) para obtener duración y resolución.
/// </summary>
public static class MediaProbe // Utilidades de sondeo de medios con ffprobe
{
    private static readonly object Gate = new(); // Candado para serializar la localización de ffprobe
    private static string? _ffprobePath; // Ruta del ejecutable de ffprobe (una vez resuelta)
    private static bool _checked; // Indica si ya se intentó localizar ffprobe

    public static bool IsAvailable // Indica si ffprobe está disponible en el sistema
    {
        get // Getter de la propiedad
        {
            Resolve(); // Intenta localizar ffprobe si aún no se hizo
            return _ffprobePath is not null; // Disponible si se encontró el ejecutable
        }
    }

    private static void Resolve() // Localiza ffprobe (variable de entorno o PATH) una única vez
    {
        if (_checked) // Si ya se intentó localizar antes
            return; // No repite la búsqueda

        lock (Gate) // Serializa el bloque entre hilos
        {
            if (_checked) // Doble comprobación dentro del candado
                return; // Otro hilo ya lo resolvió

            var env = Environment.GetEnvironmentVariable("FEDO_FFPROBE"); // Lee la ruta de la variable de entorno
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) // Si la variable no está vacía y el archivo existe
                _ffprobePath = env; // Usa esa ruta explícita

            if (_ffprobePath is null) // Si aún no hay ruta conocida
            {
                try // Intenta detectar ffprobe en el PATH del sistema
                {
                    // Lanza ffprobe con la opción -version para comprobar si existe
                    using var proc = Process.Start(new ProcessStartInfo("ffprobe", "-version")
                    {
                        UseShellExecute = false, // No usa el shell del sistema
                        CreateNoWindow = true, // No crea ventana de consola
                        RedirectStandardOutput = true, // Captura la salida estándar
                        RedirectStandardError = true // Captura la salida de error
                    });
                    if (proc is not null) // Si el proceso se lanzó correctamente
                    {
                        proc.StandardOutput.ReadToEnd(); // Consume la salida para evitar bloqueos de buffer
                        proc.WaitForExit(5000); // Espera hasta 5 segundos a que termine
                        if (proc.ExitCode == 0) // Si terminó con éxito
                            _ffprobePath = "ffprobe"; // Usa el comando por su nombre en el PATH
                    }
                }
                catch // Si el lanzamiento falló (ffprobe no está instalado)
                {
                    _ffprobePath = null; // Confirma que no hay ffprobe disponible
                }
            }

            _checked = true; // Marca la resolución como ya realizada
        }
    }

    public static MediaInfo? Probe(string filePath) // Sondea un archivo y devuelve sus metadatos
    {
        if (!IsAvailable || string.IsNullOrEmpty(_ffprobePath)) // Si no hay ffprobe disponible
            return null; // No puede sondear

        try // Ejecuta ffprobe y captura posibles errores
        {
            var psi = new ProcessStartInfo(_ffprobePath!) // Prepara la línea de comandos de ffprobe
            {
                UseShellExecute = false, // No usa el shell
                CreateNoWindow = true, // Sin ventana de consola
                RedirectStandardOutput = true, // Captura el JSON de salida
                RedirectStandardError = true // Captura los errores
            };
            psi.ArgumentList.Add("-v"); // Añade la opción de verbosidad
            psi.ArgumentList.Add("error"); // Solo muestra errores
            psi.ArgumentList.Add("-print_format"); // Añade la opción de formato de salida
            psi.ArgumentList.Add("json"); // Pide la salida en JSON
            psi.ArgumentList.Add("-show_format"); // Incluye los datos del contenedor (formato)
            psi.ArgumentList.Add("-show_streams"); // Incluye los datos de los streams
            psi.ArgumentList.Add(filePath); // Añade la ruta del archivo a sondear

            using var proc = Process.Start(psi); // Ejecuta ffprobe
            if (proc is null) // Si no se pudo lanzar el proceso
                return null; // Devuelve null

            string output = proc.StandardOutput.ReadToEnd(); // Lee la salida JSON completa
            string error = proc.StandardError.ReadToEnd(); // Lee la salida de error
            proc.WaitForExit(15000); // Espera hasta 15 segundos a que termine
            if (proc.ExitCode != 0) // Si ffprobe terminó con error
                return null; // Devuelve null

            return Parse(output); // Analiza el JSON de salida
        }
        catch // Ante cualquier excepción inesperada
        {
            return null; // Devuelve null sin propagar el error
        }
    }

    private static MediaInfo? Parse(string json) // Analiza el JSON de ffprobe para extraer duración y resolución
    {
        try // Protege contra JSON mal formado
        {
            using var doc = JsonDocument.Parse(json); // Analiza el JSON en un documento
            var root = doc.RootElement; // Accede al elemento raíz del documento

            double duration = 0; // Duración extraída (0 si no aparece)
            if (root.TryGetProperty("format", out var format) && // Busca la sección "format"
                format.TryGetProperty("duration", out var durationElement)) // Y dentro la propiedad "duration"
            {
                if (durationElement.ValueKind == JsonValueKind.String) // Si la duración viene como texto
                    // Convierte el texto a double con cultura invariante (ignora el resultado si falla)
                    _ = double.TryParse(durationElement.GetString(), System.Globalization.CultureInfo.InvariantCulture, out duration);
                else if (durationElement.ValueKind == JsonValueKind.Number) // Si la duración viene como número
                    duration = durationElement.GetDouble(); // La lee directamente como double
            }

            int width = 0, height = 0; // Resolución inicializada a cero
            if (root.TryGetProperty("streams", out var streams)) // Busca la sección "streams"
            {
                foreach (var stream in streams.EnumerateArray()) // Recorre cada stream del archivo
                {
                    if (!stream.TryGetProperty("codec_type", out var codecType) || // Si el stream no declara tipo
                        !string.Equals(codecType.GetString(), "video", StringComparison.OrdinalIgnoreCase)) // O no es de vídeo
                        continue; // Salta al siguiente stream

                    if (stream.TryGetProperty("width", out var w)) // Si el stream tiene anchura
                        width = w.GetInt32(); // Lee la anchura
                    if (stream.TryGetProperty("height", out var h)) // Si el stream tiene altura
                        height = h.GetInt32(); // Lee la altura
                    break; // Usa el primer stream de vídeo encontrado
                }
            }

            return new MediaInfo // Construye el objeto de metadatos
            {
                DurationSeconds = (int)Math.Round(duration), // Duración redondeada a segundos enteros
                Width = width, // Anchura en píxeles
                Height = height // Altura en píxeles
            };
        }
        catch // Si el JSON no es válido
        {
            return null; // Devuelve null
        }
    }
}
