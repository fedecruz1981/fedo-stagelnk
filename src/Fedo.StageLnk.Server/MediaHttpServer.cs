// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: MediaHttpServer
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Net; // Proporciona IPAddress e IPEndPoint para el listener
using System.Net.Sockets; // Proporciona TcpListener y TcpClient
using System.Text; // Proporciona StringBuilder y ASCII para construir respuestas
using Fedo.StageLnk.Protocol; // Importa el protocolo compartido (puerto HTTP por defecto)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Servidor HTTP mínimo para streaming de medios por LAN: sirve los archivos de
/// la biblioteca con soporte de peticiones de rango (Range), de modo que el
/// cliente puede reproducir con ffplay directamente desde la URL sin esperar a
/// descargar el archivo completo. Implementado sobre TcpListener para evitar el
/// ACL de URLs de HttpListener (no requiere permisos de administrador).
/// </summary>
public sealed class MediaHttpServer : IAsyncDisposable // Servidor HTTP con liberación asíncrona de recursos
{
    // Raíz de la biblioteca de la que se sirven los archivos
    private readonly string _root;
    // Puerto de escucha HTTP
    private readonly int _port;
    // Listener TCP que acepta las conexiones HTTP
    private TcpListener? _listener;
    // Token de cancelación del bucle de aceptación
    private CancellationTokenSource? _cts;
    // Tarea del bucle de aceptación en segundo plano
    private Task? _acceptLoop;
    // Conexiones activas para poder cerrarlas al detener el servidor
    private readonly HashSet<TcpClient> _clients = new();
    // Candado que protege el conjunto de conexiones activas
    private readonly object _clientsLock = new();

    // Evento de log (mensajes de texto)
    public event Action<string>? Log;

    /// <summary>Puerto real en el que quedó escuchando (útil cuando se pasa 0 en pruebas).</summary>
    public int BoundPort { get; private set; }

    // Constructor: raíz de la biblioteca y puerto de escucha
    public MediaHttpServer(string root, int port = ProtocolDefaults.HttpPort)
    {
        _root = root; // Almacena la raíz de la biblioteca
        _port = port; // Almacena el puerto de escucha
    }

    // Inicia el listener y el bucle de aceptación
    public void Start()
    {
        _cts = new CancellationTokenSource(); // Crea el token de cancelación
        _listener = new TcpListener(IPAddress.Any, _port); // Escucha en todas las interfaces del puerto
        _listener.Start(); // Comienza a aceptar conexiones
        // Expone el puerto real asignado (el mismo si se pasó uno fijo)
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token); // Lanza el bucle de aceptación en segundo plano
        // Informa de que el HTTP quedó listo
        Log?.Invoke($"HTTP (streaming) listo en el puerto {BoundPort}");
    }

    // Bucle que acepta conexiones entrantes y lanza una tarea por cada una
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) // Mientras no se cancele
        {
            TcpClient client; // Cliente aceptado
            try // Protege la aceptación ante errores
            {
                client = await _listener!.AcceptTcpClientAsync(ct); // Acepta una conexión entrante
            }
            catch (OperationCanceledException) // Si se cancela el bucle
            {
                break; // Termina el bucle
            }
            catch (SocketException) // Si falla el socket de forma transitoria
            {
                continue; // Reintenta aceptar
            }

            lock (_clientsLock) // Protege el registro de la conexión
                _clients.Add(client); // Añade el cliente a las conexiones activas

            _ = HandleConnectionAsync(client, ct); // Lanza la tarea que atiende la petición
        }
    }

    // Atiende una petición HTTP completa y cierra la conexión al terminar
    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try // Protege el procesamiento de la petición
        {
            // Tiempo máximo de espera para la cabecera de la petición
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10)); // Aborta si el cliente tarda demasiado en enviarla

            var stream = client.GetStream(); // Obtiene el stream de red del cliente
            // Buffer de lectura para la cabecera HTTP (GET/HEAD no lleva cuerpo)
            var buffer = new byte[8192];
            // Lee la cabecera de la petición de una vez
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
            if (read <= 0) // Si el cliente cerró sin enviar nada
                return; // Termina la conexión

            var request = ParseRequest(buffer, read); // Interpreta método, ruta y cabeceras
            if (request is null) // Si la petición no es válida
            {
                // Responde 400 con el cuerpo como bytes UTF-8
                await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad Request"), null, 0, -1, ct);
                return; // Termina la conexión
            }

            // Solo se atienden GET y HEAD (lectura de medios)
            bool isHead = request.Value.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            if (!isHead && !request.Value.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                // Método no soportado: responde 405
                await WriteResponseAsync(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Method Not Allowed"), null, 0, -1, ct);
                return; // Termina la conexión
            }

            // Sanea la ruta pedida y la resuelve contra la raíz de la biblioteca
            var file = ResolveFile(request.Value.Path);
            if (file is null) // Si la ruta no es válida o el archivo no existe
            {
                // Responde 404 (no encontrado / acceso denegado)
                await WriteResponseAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), null, 0, -1, ct);
                return; // Termina la conexión
            }

            // Tamaño total del archivo servido
            long total = file.Length;
            // Rango solicitado (todo el archivo si no viene cabecera Range)
            var range = ParseRange(request.Value.Headers.GetValueOrDefault("range"), total);
            if (range is null) // Si el rango no se puede satisfacer
            {
                // Responde 416 (rango no satisfacible)
                await WriteResponseAsync(stream, 416, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Range Not Satisfiable"), null, 0, -1, ct);
                return; // Termina la conexión
            }

            // Tipo MIME del archivo según su extensión
            string contentType = ContentTypeFor(file.Extension);
            // Envía la respuesta con el archivo (completo o el trozo del rango)
            await WriteResponseAsync(stream, 200, contentType, null, file, range.Value.Start, range.Value.End, ct, isHead, range.Value.Partial);
            // Informa del archivo servido por streaming
            Log?.Invoke($"Stream: GET {request.Value.Path} [{range.Value.Start}-{range.Value.End}/{total}] a {client.Client.RemoteEndPoint}");
        }
        catch (OperationCanceledException) // Si se cancela la petición (timeout o cierre)
        {
        }
        catch (IOException) // Si la conexión se interrumpe
        {
        }
        catch (SocketException) // Si falla el socket
        {
        }
        catch (Exception ex) // Ante cualquier otro error
        {
            // Informa del error de streaming
            Log?.Invoke($"Stream: error ({ex.Message})");
        }
        finally // Limpieza al terminar la conexión
        {
            lock (_clientsLock) // Protege el registro de la conexión
                _clients.Remove(client); // Quita el cliente de las conexiones activas
            client.Dispose(); // Libera el socket del cliente
        }
    }

    // Interpreta la línea de petición y las cabeceras desde el buffer recibido
    private static (string Method, string Path, Dictionary<string, string> Headers)? ParseRequest(byte[] buffer, int length)
    {
        // Convierte el buffer a texto con mapeo 1:1 de bytes (preserva rutas percent-encoded con acentos)
        string text = Encoding.Latin1.GetString(buffer, 0, length);
        // Divide por líneas terminadas en CRLF
        string[] lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) // Si no hay líneas
            return null; // Petición inválida

        string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries); // Método, ruta y versión
        if (requestLine.Length < 2) // Si falta el método o la ruta
            return null; // Petición inválida

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Cabeceras por nombre (sin distinción de mayúsculas)
        foreach (string line in lines.Skip(1)) // Recorre las líneas de cabecera
        {
            int colon = line.IndexOf(':'); // Posición del separador nombre:valor
            if (colon <= 0) // Si no hay separador válido
                continue; // Salta la línea
            string name = line[..colon].Trim(); // Nombre de la cabecera
            string value = line[(colon + 1)..].Trim(); // Valor de la cabecera
            headers[name] = value; // Almacena la cabecera
        }

        // Devuelve la petición interpretada
        return (requestLine[0], requestLine[1], headers);
    }

    // Sanea la ruta pedida y la resuelve dentro de la raíz de la biblioteca
    private FileInfo? ResolveFile(string rawPath)
    {
        // Quita la barra inicial si existe
        string relative = rawPath.StartsWith('/') ? rawPath[1..] : rawPath;
        try // Protege la decodificación de la URL
        {
            // Decodifica los caracteres escapados (%20, %C3%A1, etc.)
            relative = Uri.UnescapeDataString(relative);
        }
        catch // Si la ruta tiene escapes inválidos
        {
            return null; // Se rechaza la petición
        }

        relative = relative.Replace('\\', '/'); // Normaliza las barras invertidas a normales
        // Divide en segmentos para rechazar intentos de salida del directorio
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Si algún segmento intenta subir de nivel
        if (segments.Any(s => s == ".."))
            return null; // Se rechaza la petición

        // Ruta absoluta y normalizada del archivo solicitado
        string full = Path.GetFullPath(Path.Combine(_root, string.Join(Path.DirectorySeparatorChar, segments)));
        // Ruta absoluta y normalizada de la raíz de la biblioteca
        string rootFull = Path.GetFullPath(_root);
        // Si la ruta cae fuera de la raíz (protección contra traversal)
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return null; // Se rechaza la petición

        var file = new FileInfo(full); // Información del archivo resuelto
        // Devuelve el archivo solo si existe y no es un directorio
        return file.Exists && (file.Attributes & FileAttributes.Directory) == 0 ? file : null;
    }

    // Interpreta una cabecera Range en formato bytes=a-b, bytes=a- o bytes=-b
    private static (long Start, long End, bool Partial)? ParseRange(string? rangeHeader, long total)
    {
        if (total <= 0) // Si el archivo está vacío
            return null; // No hay rango posible

        if (string.IsNullOrWhiteSpace(rangeHeader)) // Si no hay cabecera Range
            // Devuelve el archivo completo (inicio 0, fin total-1, sin 206)
            return (0, total - 1, false);

        // Recorta el prefijo "bytes=" y los espacios
        string spec = rangeHeader.Trim();
        if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) // Si no es del formato esperado
            return null; // Rango inválido
        spec = spec[6..].Trim(); // Quita el prefijo "bytes="
        if (spec.Contains(',')) // Si hay varios rangos en la misma petición
            return null; // Solo se soporta un rango por petición

        int dash = spec.IndexOf('-'); // Posición del guion que separa inicio y fin
        if (dash < 0) // Si no hay guion
            return null; // Formato inválido

        string startText = spec[..dash].Trim(); // Parte de inicio del rango
        string endText = spec[(dash + 1)..].Trim(); // Parte de fin del rango

        if (startText.Length == 0) // Rango en formato "-suffix" (últimos N bytes)
        {
            if (!long.TryParse(endText, out long suffix) || suffix <= 0) // Si el sufijo no es válido
                return null; // Rango inválido
            long suffixStart = Math.Max(0, total - suffix); // Calcula el inicio para cubrir los últimos N bytes
            // Devuelve el rango de los últimos N bytes como respuesta parcial
            return (suffixStart, total - 1, true);
        }

        if (!long.TryParse(startText, out long start)) // Si el inicio no es numérico
            return null; // Rango inválido
        if (start >= total) // Si el inicio supera el tamaño del archivo
            return null; // Rango no satisfacible (416)

        long end; // Fin del rango
        if (endText.Length == 0) // Formato "a-" (hasta el final del archivo)
            end = total - 1; // El fin es el último byte
        else if (!long.TryParse(endText, out end)) // Si el fin no es numérico
            return null; // Rango inválido
        else // Rango cerrado "a-b"
            end = Math.Min(end, total - 1); // Recorta el fin al último byte disponible

        if (end < start) // Si el fin es menor que el inicio
            return null; // Rango inválido

        // Devuelve el rango solicitado como respuesta parcial
        return (start, end, true);
    }

    // Envía la respuesta HTTP (estado, cabeceras y cuerpo) y opcionalmente el archivo
    private static async Task WriteResponseAsync(Stream stream, int status, string contentType, byte[]? body,
        FileInfo? file, long rangeStart, long rangeEnd, CancellationToken ct,
        bool isHead = false, bool partial = false)
    {
        long length; // Longitud del cuerpo a enviar
        if (body is not null) // Si la respuesta es un mensaje de texto corto
            length = body.Length; // El largo es el del mensaje
        else // Si la respuesta incluye un archivo
            length = rangeEnd - rangeStart + 1; // El largo es el del trozo servido

        // Línea de estado según si es respuesta parcial o completa
        string statusLine = status == 206 || partial ? "HTTP/1.1 206 Partial Content" : $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}";
        var headers = new StringBuilder(); // Acumula las cabeceras de respuesta
        headers.Append(statusLine).Append("\r\n"); // Añade la línea de estado
        headers.Append("Content-Type: ").Append(contentType).Append("\r\n"); // Tipo MIME del contenido
        headers.Append("Content-Length: ").Append(length).Append("\r\n"); // Largo exacto del cuerpo
        headers.Append("Accept-Ranges: bytes\r\n"); // Anuncia soporte de rangos por bytes
        if (partial && file is not null) // Si se sirve un trozo parcial
        {
            // Cabecera que describe el rango servido sobre el total
            headers.Append("Content-Range: bytes ").Append(rangeStart).Append('-').Append(rangeEnd)
                   .Append('/').Append(file.Length).Append("\r\n");
        }
        headers.Append("Connection: close\r\n\r\n"); // Cierra la conexión tras la respuesta

        byte[] headBytes = Encoding.ASCII.GetBytes(headers.ToString()); // Convierte las cabeceras a bytes
        await stream.WriteAsync(headBytes, ct); // Envía la línea de estado y las cabeceras

        if (isHead || length == 0) // Si es HEAD o no hay cuerpo
            return; // Termina sin enviar cuerpo

        if (body is not null) // Si la respuesta es un mensaje de texto
        {
            await stream.WriteAsync(body, ct); // Envía el mensaje
            return; // Termina la respuesta
        }

        // Abre el archivo para enviar el trozo pedido
        using var fs = new FileStream(file!.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        fs.Seek(rangeStart, SeekOrigin.Begin); // Salta al inicio del rango solicitado
        long remaining = length; // Bytes que quedan por enviar
        var buffer = new byte[65536]; // Buffer de copia de 64 KiB
        while (remaining > 0) // Mientras queden bytes por enviar
        {
            int toRead = (int)Math.Min(buffer.Length, remaining); // Cuántos bytes leer en esta iteración
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct); // Lee un bloque del archivo
            if (read == 0) // Si se acabó el archivo antes de lo esperado
                break; // Sale del bucle
            await stream.WriteAsync(buffer.AsMemory(0, read), ct); // Envía el bloque al cliente
            remaining -= read; // Descuenta los bytes enviados
        }
    }

    // Devuelve el tipo MIME según la extensión del archivo
    private static string ContentTypeFor(string extension)
    {
        return extension.ToLowerInvariant() switch // Compara la extensión en minúsculas
        {
            ".mp4" => "video/mp4", // MPEG-4
            ".mkv" => "video/x-matroska", // Matroska
            ".mov" => "video/quicktime", // QuickTime
            ".wav" => "audio/wav", // WAV
            ".mp3" => "audio/mpeg", // MP3
            ".ogg" => "audio/ogg", // Ogg Vorbis
            ".flac" => "audio/flac", // FLAC
            ".png" => "image/png", // PNG
            ".jpg" => "image/jpeg", // JPEG
            ".jpeg" => "image/jpeg", // JPEG alternativo
            ".bmp" => "image/bmp", // BMP
            ".gif" => "image/gif", // GIF
            ".webp" => "image/webp", // WebP
            _ => "application/octet-stream" // Otros formatos (descarga binaria)
        };
    }

    // Detiene el servidor y libera sus recursos
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel(); // Cancela los bucles internos
        lock (_clientsLock) // Protege el cierre de conexiones activas
        {
            foreach (var client in _clients) // Recorre las conexiones en curso
                client.Dispose(); // Cierra cada socket para desbloquear sus lecturas
            _clients.Clear(); // Vacía el conjunto de conexiones
        }
        try // Protege la espera del bucle de aceptación
        {
            if (_acceptLoop is not null) // Si el bucle está en marcha
                await _acceptLoop; // Espera a que termine
        }
        catch // Si el bucle terminó con error
        {
        }
        _listener?.Stop(); // Detiene el listener
        _cts?.Dispose(); // Libera el token de cancelación
    }
}
