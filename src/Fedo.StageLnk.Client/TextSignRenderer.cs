// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: TextSignRenderer
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics; // Proporciona Process y ProcessStartInfo para lanzar ffmpeg
using System.Globalization; // Analiza y formatea números con cultura invariable (colores hex)
using System.Security.Cryptography; // Proporciona SHA256 para el hash del texto del cartel
using System.Text; // Proporciona StringBuilder para construir la cadena de filtros drawtext

namespace Fedo.StageLnk.Client; // Espacio de nombres del cliente Fedo-StageLnk

/// <summary>
/// Genera carteles de texto con apariencia 3D (extrusión + borde + sombra) usando
/// ffmpeg drawtext, listos para mostrarse con el renderer ffplay. El texto se escribe
/// en un archivo propio (sin escaping de contenido) y se trabaja con rutas relativas
/// dentro de un directorio temporal para evitar el escaping de ':' y '\' en la
/// sintaxis de filtros de ffmpeg.
/// </summary>
public sealed class TextSignRenderer
{
    private const int Depth = 4; // Número de capas de extrusión 3D del texto

    private readonly object _lock = new(); // Candado que serializa la generación de carteles

    public event Action<string>? Log; // Evento de log del renderer

    public bool IsAvailable
    {
        get
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("ffmpeg", "-version") // Lanza ffmpeg para comprobar su presencia
                {
                    UseShellExecute = false, // No pasa por el shell del sistema
                    CreateNoWindow = true, // No crea ventana de consola
                    RedirectStandardOutput = true, // Captura la salida de la versión
                    RedirectStandardError = true // Captura los errores
                });
                if (proc is null) // Si no se pudo iniciar el proceso
                    return false; // ffmpeg no está disponible
                proc.StandardOutput.ReadToEnd(); // Consume la salida de la versión
                proc.WaitForExit(3000); // Espera hasta 3 segundos a que termine
                return proc.ExitCode == 0; // Disponible si finaliza sin error
            }
            catch
            {
                return false; // Ante cualquier fallo se considera no disponible
            }
        }
    }

    /// <summary>
    /// Genera (o recupera de caché) el cartel PNG y devuelve su ruta absoluta.
    /// El estilo 3D se logra apilando capas de texto en colores degradados
    /// (extrusión), con un borde oscuro y una sombra desplazada.
    /// </summary>
    public string Render(string text, string color, string backgroundColor, int fontSize, string workDir)
    {
        if (string.IsNullOrWhiteSpace(text)) // Valida que el texto no esté vacío
            throw new ArgumentException("El texto del cartel no puede estar vacío", nameof(text)); // Lanza excepción por texto inválido

        var (r, g, b) = ParseHex(color, 0xFF, 0xD7, 0x00); // Decodifica el color del texto con fallback amarillo
        var (br, bg, bb) = ParseHex(backgroundColor, 0x0A, 0x1A, 0x2A); // Decodifica el color de fondo con fallback azul oscuro

        string dir = Path.Combine(workDir, ".text"); // Directorio de carteles dentro del área de trabajo
        Directory.CreateDirectory(dir); // Garantiza que el directorio exista

        string stem = $"{HashText(text)}_{fontSize}_{ToHex(r, g, b)}_{ToHex(br, bg, bb)}"; // Clave única del cartel según contenido y estilo
        string outName = $"cartel_{stem}.png"; // Nombre del PNG generado
        string outPath = Path.Combine(dir, outName); // Ruta absoluta del PNG
        if (File.Exists(outPath)) // Si el cartel ya existe en caché
            return outPath; // Lo devuelve sin regenerarlo

        lock (_lock) // Serializa la generación para evitar renderizados simultáneos
        {
            if (File.Exists(outPath)) // Revisa la caché de nuevo dentro del candado
                return outPath; // Devuelve el cartel ya existente

            string font = LocateFont(dir) // Localiza y copia una fuente TTF del sistema
                ?? throw new InvalidOperationException("No se encontró una fuente TTF del sistema para drawtext"); // Aborta si no hay fuente disponible

            File.WriteAllText(Path.Combine(dir, "texto.txt"), text); // Escribe el texto en un archivo propio para evitar escapes

            int width = Math.Clamp(text.Length * (int)(fontSize * 0.62) + 240, 360, 2560); // Ancho del lienzo en función del texto y la fuente
            int height = Math.Clamp(fontSize + 280, 240, 1080); // Alto del lienzo en función del tamaño de fuente

            string filter = BuildFilter(fontSize, r, g, b); // Construye la cadena de filtros drawtext

            var psi = new ProcessStartInfo("ffmpeg") // Prepara el proceso ffmpeg
            {
                UseShellExecute = false, // No pasa por el shell del sistema
                CreateNoWindow = true, // No crea ventana de consola
                RedirectStandardOutput = true, // Captura la salida estándar
                RedirectStandardError = true, // Captura los errores
                WorkingDirectory = dir // Trabaja en el directorio de carteles para usar rutas relativas
            };
            psi.ArgumentList.Add("-f"); // Añade el flag de formato de entrada
            psi.ArgumentList.Add("lavfi"); // Usa el filtro virtual lavfi como fuente
            psi.ArgumentList.Add("-i"); // Añade el flag de entrada
            psi.ArgumentList.Add($"color=c=0x{ToHex(br, bg, bb)}:s={width}x{height}:d=1"); // Fuente de color de fondo con tamaño y duración de 1 s
            psi.ArgumentList.Add("-vf"); // Añade el flag de filtros de vídeo
            psi.ArgumentList.Add(filter); // Añade la cadena de filtros drawtext
            psi.ArgumentList.Add("-frames:v"); // Añade el flag de número de fotogramas
            psi.ArgumentList.Add("1"); // Genera un único fotograma
            psi.ArgumentList.Add("-y"); // Sobrescribe el archivo de salida si existe
            psi.ArgumentList.Add(outName); // Nombre relativo del PNG de salida

            string stderr; // Buffer para capturar los errores de ffmpeg
            using (var proc = Process.Start(psi)) // Lanza ffmpeg dentro del bloque using
            {
                if (proc is null) // Si el proceso no arrancó
                    throw new InvalidOperationException("No se pudo iniciar ffmpeg"); // Aborta la generación
                proc.StandardOutput.ReadToEnd(); // Consume la salida estándar
                stderr = proc.StandardError.ReadToEnd(); // Lee los errores de ffmpeg
                proc.WaitForExit(30000); // Espera hasta 30 segundos a que termine
                if (proc.ExitCode != 0) // Si ffmpeg terminó con error
                    throw new InvalidOperationException($"ffmpeg falló generando el cartel: {stderr}"); // Lanza excepción con el error capturado
            }

            Log?.Invoke($"Cartel generado: {outName} ({width}x{height})"); // Notifica el cartel generado con sus dimensiones
        }

        return outPath; // Devuelve la ruta absoluta del cartel generado
    }

    private static string BuildFilter(int fontSize, int r, int g, int b)
    {
        var sb = new StringBuilder(); // Acumulador de la cadena de filtros
        for (int i = Depth - 1; i >= 1; i--) // Recorre las capas de extrusión de atrás hacia delante
        {
            double factor = 0.30 + 0.12 * (Depth - 1 - i); // Factor de brillo decreciente según la capa
            sb.Append("drawtext=textfile=texto.txt:fontfile=font.ttf:fontsize=").Append(fontSize) // Añade una capa drawtext con el tamaño de fuente
              .Append(":fontcolor=0x").Append(ToHex((int)(r * factor), (int)(g * factor), (int)(b * factor))) // Añade el color degradado de la capa
              .Append(":x=(w-text_w)/2+").Append(i).Append(":y=(h-text_h)/2+").Append(i) // Desplaza la capa en diagonal para crear la extrusión
              .Append(','); // Separa la capa de la siguiente con una coma
        }
        sb.Append("drawtext=textfile=texto.txt:fontfile=font.ttf:fontsize=").Append(fontSize) // Añade la capa frontal con el tamaño de fuente
          .Append(":fontcolor=0x").Append(ToHex(r, g, b)) // Añade el color principal del texto
          .Append(":borderw=3:bordercolor=0x000000") // Añade un borde negro de 3 píxeles
          .Append(":shadowx=3:shadowy=3:shadowcolor=0x000000") // Añade la sombra negra desplazada
          .Append(":x=(w-text_w)/2:y=(h-text_h)/2"); // Centra el texto en el lienzo
        return sb.ToString(); // Devuelve la cadena de filtros completa
    }

    private static string? LocateFont(string dir)
    {
        string[] candidates = // Lista de fuentes TTF del sistema en orden de preferencia
        {
            @"C:\Windows\Fonts\arialbd.ttf", // Arial negrita
            @"C:\Windows\Fonts\segoeuib.ttf", // Segoe UI negrita
            @"C:\Windows\Fonts\verdanab.ttf", // Verdana negrita
            @"C:\Windows\Fonts\timesbd.ttf", // Times New Roman negrita
            @"C:\Windows\Fonts\arial.ttf" // Arial normal como respaldo
        };
        foreach (string candidate in candidates) // Recorre las fuentes candidatas
        {
            if (!File.Exists(candidate)) // Salta las fuentes que no existen
                continue; // Prueba la siguiente candidata
            string dest = Path.Combine(dir, "font.ttf"); // Ruta destino de la fuente en el directorio de trabajo
            File.Copy(candidate, dest, overwrite: true); // Copia la fuente con sobrescritura
            return dest; // Devuelve la ruta de la fuente copiada
        }
        return null; // Ninguna fuente encontrada
    }

    private static (int R, int G, int B) ParseHex(string hex, int defaultR, int defaultG, int defaultB)
    {
        string h = hex.TrimStart('#'); // Elimina la almohadilla inicial si la hay
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)) // Valida que sea un color hex de 6 dígitos
            return (defaultR, defaultG, defaultB); // Devuelve el color por defecto si es inválido
        return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF); // Descompone el hex en componentes R, G y B
    }

    private static string ToHex(int r, int g, int b) // Convierte componentes RGB a hex de 6 dígitos
        => $"{(r & 0xFF):X2}{(g & 0xFF):X2}{(b & 0xFF):X2}"; // Formatea cada componente a 2 dígitos hex en mayúsculas

    private static string HashText(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text)); // Calcula el SHA-256 del texto del cartel
        return Convert.ToHexString(hash)[..10].ToLowerInvariant(); // Devuelve los primeros 10 caracteres del hash en minúsculas
    }
}
