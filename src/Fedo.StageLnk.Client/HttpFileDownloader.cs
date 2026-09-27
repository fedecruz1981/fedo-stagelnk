// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: HttpFileDownloader
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Net; // Proporciona HttpStatusCode para distinguir respuestas 200/206.
using System.Net.Http.Headers; // Proporciona RangeHeaderValue para peticiones de rango.
using Fedo.StageLnk.Protocol; // Proporciona MediaAsset y ProtocolDefaults.

namespace Fedo.StageLnk.Client;

/// <summary>
/// Descarga un medio de la biblioteca desde el servidor HTTP de streaming
/// (puerto 9003) usando GET con Range: reanuda archivos a medio bajar desde el
/// último byte recibido y verifica el SHA-256 al completar. Al no pasar por el
/// envelope JSON/base64 del canal TCP, ahorra ~33% de tráfico y admite descarga
/// paralela de varios archivos a la vez.
/// </summary>
public sealed class HttpFileDownloader : IDisposable
{
    // Cliente HTTP compartido para todas las descargas del sincronizador.
    private readonly HttpClient _http;
    // URL base del servidor de streaming (p. ej. http://host:9003).
    private readonly string _baseUrl;

    /// <summary>Evento de log para informar del progreso de las descargas.</summary>
    public event Action<string>? Log; // Evento de log (mensajes de texto).

    // Constructor: prepara el HttpClient con la URL base del servidor.
    public HttpFileDownloader(string baseUrl)
    {
        // Recorta cualquier barra final para construir URLs limpias.
        _baseUrl = baseUrl.TrimEnd('/');
        // Crea el cliente HTTP con tiempo de espera amplio para archivos grandes.
        _http = new HttpClient
        {
            // 10 minutos máximo por petición (archivos de vídeo largos).
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    /// <summary>
    /// Descarga (o reanuda) un medio al archivo temporal indicado y verifica su
    /// checksum. Devuelve true solo si el archivo quedó completo y con el SHA-256
    /// esperado del manifiesto.
    /// </summary>
    public async Task<bool> DownloadAsync(MediaAsset asset, string tempPath, CancellationToken ct)
    {
        // Borra temporales corruptos (más grandes de lo esperado o que ya completaron mal).
        PrepareTempFile(tempPath, asset.Size);

        for (int attempt = 1; attempt <= ProtocolDefaults.SyncMaxAttempts; attempt++) // Reintenta hasta agotar los intentos...
        {
            // Determina el offset de reanudación a partir del tamaño del temporal actual.
            long offset = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
            if (offset > asset.Size) // Si el temporal es mayor que el esperado...
            {
                File.Delete(tempPath); // ...está corrupto: se borra y se empieza de cero.
                offset = 0; // ...y se reinicia la descarga desde el principio.
            }
            if (offset == asset.Size) // Si el temporal ya tiene el tamaño completo...
                return TryCompleteTemp(asset, tempPath); // ...solo falta verificar.

            try // Intenta la descarga por HTTP...
            {
                // Descarga desde el offset actual hasta el final del archivo, y
                // verifica el checksum sobre el receptor (hash calculado en vuelo).
                bool ok;
                using (var receiver = offset > 0
                    ? new FileReceiver(asset.Id, tempPath, asset.Size, resume: true) // receptor en modo append si hay bytes previos
                    : new FileReceiver(asset.Id, tempPath, asset.Size)) // o nuevo si no
                {
                    bool downloaded = await DownloadRemainingAsync(asset, receiver, offset, ct); // Descarga el tramo restante.
                    if (!downloaded) // Si la descarga falló (red, truncado)...
                    {
                        // ...espera antes de reintentar...
                        await DelayRetryAsync(ct);
                        continue; // ...y pasa al siguiente intento.
                    }

                    // Cierra el receptor, calcula el hash y valida el tamaño final.
                    bool sizeOk = receiver.TryComplete(out string sha256);
                    // La descarga es válida si el tamaño coincide y el hash es el esperado.
                    ok = sizeOk && string.Equals(sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase);
                }
                if (!ok) // Si el checksum no coincide...
                    continue; // ...reintenta (el temporal completo lo descarta en la próxima pasada).
                return true; // Descarga completa y verificada.
            }
            catch (FileNotFoundException) // Si el medio no existe en el servidor (404)...
            {
                // No tiene sentido reintentar: se descarta el temporal vacío creado antes de fallar.
                if (File.Exists(tempPath)) // Si quedó un temporal...
                    File.Delete(tempPath); // ...se elimina.
                Log?.Invoke($"Descarga '{asset.Name}': no existe en el servidor"); // ...informa del fallo definitivo...
                return false; // ...y no reintenta.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) // Si se cancela...
            {
                throw; // ...se propaga la cancelación.
            }
            catch (Exception ex) // Si falla la petición (red, timeouts)...
            {
                // Informa del error del intento...
                Log?.Invoke($"Descarga '{asset.Name}': error intento {attempt}/{ProtocolDefaults.SyncMaxAttempts} ({ex.Message})");
                await DelayRetryAsync(ct); // ...espera antes de reintentar.
            }
        }
        return false; // Se agotaron los intentos: el archivo no se pudo completar.
    }

    // Descarga el tramo restante del archivo mediante GET con Range.
    private async Task<bool> DownloadRemainingAsync(MediaAsset asset, FileReceiver receiver, long offset, CancellationToken ct)
    {
        // Construye la petición GET a la URL del medio dentro del servidor HTTP.
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/" + BuildUrlPath(asset.RelativePath));
        if (offset > 0) // Si hay bytes previos...
            request.Headers.Range = new RangeHeaderValue(offset, null); // ...pide desde el último byte recibido.
        // Envía la petición y espera solo las cabeceras para leer el cuerpo como flujo.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) // Si el medio no existe en el servidor...
            throw new FileNotFoundException($"Medio '{asset.Name}' no encontrado en {_baseUrl}"); // ...lanza el fallo definitivo.
        if (!response.IsSuccessStatusCode) // Si el servidor responde 416 u otro error...
            return false; // ...la descarga falla.
        if (offset > 0 && response.StatusCode == HttpStatusCode.OK) // Si se pidió un rango pero el servidor ignoró...
            return false; // ...se aborta: el reintento empezará desde cero (temporal corrupto).

        // Lee el cuerpo de la respuesta por bloques y lo va escribiendo en el receptor.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[ProtocolDefaults.SyncCopyBuffer]; // Búfer de copia por bloque.
        while (true) // Bucle de lectura hasta el final del cuerpo...
        {
            int read = await stream.ReadAsync(buffer, ct); // Lee un bloque del cuerpo.
            if (read == 0) // Si se llegó al final...
                break; // ...termina la copia.
            receiver.Append(buffer[..read]); // Escribe el bloque en el archivo temporal (y lo hashea).
        }

        // El archivo está completo solo si se recibieron exactamente los bytes esperados.
        return receiver.Received == asset.Size;
    }

    // Comprueba el SHA-256 de un temporal de tamaño completo y lo deja listo para finalizar.
    private bool TryCompleteTemp(MediaAsset asset, string tempPath)
    {
        // Abre el receptor en append (no añade bytes; solo re-hashea el contenido existente).
        using var receiver = new FileReceiver(asset.Id, tempPath, asset.Size, resume: true);
        // Finaliza la recepción: obtiene el hash y valida el tamaño.
        bool sizeOk = receiver.TryComplete(out string sha256);
        // Válido solo si el tamaño coincide y el hash es el esperado en el manifiesto.
        return sizeOk && string.Equals(sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Prepara el temporal: crea el directorio y descarta archivos con tamaño inválido.
    private static void PrepareTempFile(string tempPath, long expectedSize)
    {
        // Asegura que el directorio destino exista.
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        if (File.Exists(tempPath)) // Si el temporal ya existe...
        {
            long len = new FileInfo(tempPath).Length; // ...se mira su tamaño...
            if (len > expectedSize) // ...y si supera el esperado (archivo truncado o corrupto)...
                File.Delete(tempPath); // ...se borra para empezar de nuevo.
        }
    }

    // Escapa cada segmento de la ruta relativa para formar una URL válida (como el streaming).
    private static string BuildUrlPath(string relativePath)
        => string.Join('/', relativePath.Split('\\', '/').Select(Uri.EscapeDataString)); // Escapa cada segmento y los une con barras.

    // Espera el retardo configurado entre reintentos, respetando la cancelación.
    private async Task DelayRetryAsync(CancellationToken ct)
    {
        try // Intenta esperar...
        {
            await Task.Delay(ProtocolDefaults.SyncRetryDelayMs, ct); // ...el retardo de reintento.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) // Si se cancela durante la espera...
        {
            // Se propaga la cancelación hacia arriba.
        }
    }

    // Libera el HttpClient al terminar.
    public void Dispose() => _http.Dispose();
}
