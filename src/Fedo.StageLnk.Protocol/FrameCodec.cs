// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: FrameCodec
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Buffers.Binary; // primitivas de lectura/escritura binaria (int32 little-endian)
using System.Text; // codificación de texto (Encoding.UTF8)

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Codificador de tramas del protocolo. Por TCP se antepone un prefijo de 4
/// bytes (int32 little-endian) con la longitud del payload JSON; por UDP el
/// datagrama es directamente el JSON del sobre (limitado al tamaño máximo de
/// datagrama). Incluye la lectura fiable de tramas con límite de tamaño.
/// </summary>
public static class FrameCodec // codificador/decodificador de tramas del protocolo
{
    /// <summary>Límite de seguridad para una trama TCP (64 MiB).</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024; // 64 MiB máximo permitido por trama TCP

    /// <summary>Envuelve el sobre en una trama TCP con prefijo de longitud.</summary>
    public static byte[] EncodeTcp(MessageEnvelope message) // codifica un sobre como trama TCP con prefijo de longitud
    {
        byte[] payload = Encoding.UTF8.GetBytes(message.Serialize()); // serializa el sobre a JSON UTF-8 y lo pasa a bytes
        var frame = new byte[4 + payload.Length]; // reserva 4 bytes de cabecera más el tamaño del payload
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length); // escribe la longitud del payload (int32 LE) en la cabecera
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length); // copia el payload a la trama a partir del offset 4
        return frame; // devuelve la trama completa (cabecera + payload)
    }

    /// <summary>Codifica el sobre como datagrama UDP (JSON UTF-8 sin prefijo).</summary>
    public static byte[] EncodeUdp(MessageEnvelope message) // codifica un sobre como datagrama UDP
        => Encoding.UTF8.GetBytes(message.Serialize()); // el datagrama es directamente el JSON UTF-8 del sobre, sin prefijo

    /// <summary>
    /// Lee una trama TCP completa (prefijo de longitud + cuerpo). Devuelve null
    /// si el flujo se cierra antes de completar una trama válida.
    /// </summary>
    public static async Task<MessageEnvelope?> ReadTcpAsync(Stream stream, CancellationToken ct) // lee una trama TCP completa de forma asíncrona
    {
        var header = new byte[4]; // buffer para el prefijo de longitud (4 bytes)
        if (!await ReadExactlyAsync(stream, header, 4, ct)) // lee el prefijo completo; si el flujo se cierra...
            return null; // ...no hay trama válida y devuelve null

        int length = BinaryPrimitives.ReadInt32LittleEndian(header); // interpreta la longitud del payload (int32 LE)
        if (length <= 0 || length > MaxFrameBytes) // valida que la longitud sea positiva y no exceda el límite de seguridad
            throw new InvalidDataException($"Frame length out of range: {length}"); // lanza error si la longitud está fuera de rango

        var body = new byte[length]; // buffer del tamaño exacto del payload
        if (!await ReadExactlyAsync(stream, body, length, ct)) // lee el cuerpo completo; si el flujo se cierra...
            return null; // ...la trama quedó incompleta y devuelve null

        string json = Encoding.UTF8.GetString(body); // convierte el cuerpo a texto JSON (UTF-8)
        return MessageEnvelope.Deserialize(json); // deserializa el texto JSON a un sobre y lo devuelve
    }

    /// <summary>Lee exactamente <paramref name="count"/> bytes o devuelve false al cerrar el flujo.</summary>
    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct) // lee exactamente N bytes o false si se cierra el flujo
    {
        int offset = 0; // posición actual dentro del buffer
        while (offset < count) // mientras queden bytes por leer
        {
            // lee el siguiente trozo disponible en la posición actual
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) // si la lectura devuelve 0, el flujo está cerrado
                return false; // aborta devolviendo false
            offset += read; // avanza el offset con los bytes leídos
        }
        return true; // se leyeron exactamente count bytes
    }
}
