// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: MediaProbe
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics; // Proporciona el lanzamiento de procesos (ffprobe)
using System.Text.Json; // Proporciona el análisis de JSON (JsonDocument)
using Fedo.StageLnk.Shared; // Herramientas externas compartidas (ffmpeg/ffprobe/ffplay)

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

    public static bool IsAvailable // Indica si ffprobe está disponible en el sistema
    {
        get => ExternalTools.IsFfprobeAvailable; // Delega en el resolvedor compartido
    }

    public static string? FfprobePath => ExternalTools.FfprobePath; // Ruta resuelta de ffprobe

    public static MediaInfo? Probe(string filePath) // Sondea un archivo y devuelve sus metadatos
    {
        var ffprobePath = ExternalTools.FfprobePath;
        if (!ExternalTools.IsFfprobeAvailable || string.IsNullOrEmpty(ffprobePath)) // Si no hay ffprobe disponible
            return null; // No puede sondear

        try // Ejecuta ffprobe y captura posibles errores
        {
            var psi = new ProcessStartInfo(ffprobePath) // Prepara la línea de comandos de ffprobe
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
