// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: FileReceiver
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

// Importa el proveedor de hash criptográfico SHA-256.
using System.Security.Cryptography;

namespace Fedo.StageLnk.Client;

/// <summary>
/// Receptor de un archivo entrante durante la sincronización: escribe los trozos
/// en un archivo temporal mientras calcula el SHA-256 en vuelo; al completarlo
/// compara el tamaño esperado para validar la transferencia. Soporta reanudar
/// una descarga ya empezada (resume): si el temporal existe, reabre en modo
/// append y re-hashea el contenido previo para que el checksum final sea válido.
/// </summary>
// Implementa IDisposable para liberar el stream y el hash al terminar.
public sealed class FileReceiver : IDisposable
{
    // Ruta del archivo temporal donde se acumula la descarga.
    private readonly string _tempPath;
    // Objeto SHA-256 que se alimenta con cada trozo recibido.
    private readonly SHA256 _sha = SHA256.Create();
    // Stream de escritura al archivo temporal.
    private readonly FileStream _stream;
    // Bytes acumulados recibidos hasta el momento.
    private long _received;
    // Tamaño total esperado para validar la transferencia.
    private readonly long _expected;

    // Identificador del medio que se está recibiendo.
    public string MediaId { get; }
    // Expone la ruta del archivo temporal en uso.
    public string TempPath => _tempPath;
    // Bytes ya acumulados (útil para reanudar peticiones HTTP con Range).
    public long Received => _received;

    // Constructor: prepara el archivo temporal y el stream de escritura.
    public FileReceiver(string mediaId, string tempPath, long expectedSize, bool resume = false)
    {
        MediaId = mediaId; // Guarda el identificador del medio que se recibe.
        _tempPath = tempPath; // Guarda la ruta del archivo temporal.
        _expected = expectedSize; // Guarda el tamaño total esperado del archivo.
        // Crea el directorio destino si no existe.
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        if (resume && File.Exists(tempPath)) // Si se reanuda un temporal con contenido...
        {
            // Hashea el contenido previo ANTES de abrir el stream de escritura para
            // no mantener el archivo abierto dos veces (FileShare.None lo impide).
            _received = new FileInfo(tempPath).Length; // ...se toma el tamaño ya recibido...
            using var existing = File.OpenRead(tempPath); // ...se lee el contenido previo...
            var buffer = new byte[81920]; // búfer de lectura
            while (true) // lee por bloques hasta el final
            {
                int read = existing.Read(buffer, 0, buffer.Length); // lee un bloque
                if (read == 0) // si no queda contenido...
                    break; // ...termina el re-hasheo
                _sha.TransformBlock(buffer, 0, read, null, 0); // alimenta el hash con el bloque
            }
        }

        // Decide el modo de apertura: append si se reanuda, crear si es una descarga nueva.
        _stream = resume && File.Exists(tempPath)
            ? new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None) // modo append para continuar la descarga
            : new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None); // modo crear para una descarga nueva
        if (resume && _stream.Length > 0) // Si se reanuda un temporal con contenido...
            _stream.Position = _stream.Length; // ...se sitúa el puntero al final para continuar.
    }

    // Escribe un trozo de datos recibido en el archivo y lo hashea.
    public void Append(byte[] data)
    {
        // Escribe el trozo completo en el archivo temporal.
        _stream.Write(data, 0, data.Length);
        // Alimenta el hash SHA-256 con los bytes del trozo.
        _sha.TransformBlock(data, 0, data.Length, null, 0);
        // Acumula el tamaño recibido.
        _received += data.Length;
    }

    // Finaliza la recepción: cierra el stream, obtiene el hash y valida el tamaño.
    public bool TryComplete(out string sha256)
    {
        // Vuelca los buffers pendientes al disco.
        _stream.Flush();
        // Cierra el stream de escritura.
        _stream.Dispose();
        // Marca el final de la entrada de datos del hash.
        _sha.TransformFinalBlock([], 0, 0);
        // Convierte el hash a hexadecimal en minúsculas.
        sha256 = Convert.ToHexString(_sha.Hash!).ToLowerInvariant();
        // Devuelve si el tamaño recibido coincide con el esperado.
        return _received == _expected;
    }

    // Libera los recursos del hash y del stream.
    public void Dispose()
    {
        _sha.Dispose(); // Libera el objeto SHA-256.
        _stream.Dispose(); // Libera el stream de escritura.
    }
}
