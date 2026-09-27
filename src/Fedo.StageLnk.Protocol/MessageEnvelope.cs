// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: MessageEnvelope
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Text.Json; // serialización/deserialización JSON del sobre y sus payloads
using System.Text.Json.Serialization; // opciones de serialización JSON (camelCase, ignorar null)

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Sobre de mensaje que envuelve cualquier comunicación: versión del protocolo,
/// tipo de mensaje, identidad del remitente, número de secuencia y el payload
/// serializado en JSON (camelCase). Los mensajes vacíos (sin payload) se usan
/// para señales como stop, pausa o pong.
/// </summary>
public sealed record MessageEnvelope // sobre que envuelve cualquier mensaje del protocolo
{
    public int ProtocolVersion { get; init; } = ProtocolDefaults.ProtocolVersion; // versión del protocolo (la vigente por defecto)
    public MessageType Type { get; init; } = MessageType.Unknown; // tipo de mensaje (Desconocido por defecto)
    public Guid SenderId { get; init; } = Guid.Empty; // identidad del remitente del mensaje
    public long Sequence { get; init; } // número de secuencia del mensaje
    public string? Json { get; init; } // payload serializado en JSON (null si el mensaje va vacío)

    private static readonly JsonSerializerOptions Options = new() // opciones compartidas de serialización JSON
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // propiedades serializadas en camelCase
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull // no escribe propiedades con valor null
    };

    public static MessageEnvelope Create<T>(MessageType type, Guid senderId, long sequence, T payload) // crea un sobre con payload tipado
        => new() // instancia el sobre...
        {
            Type = type, // ...con el tipo de mensaje
            SenderId = senderId, // ...el remitente
            Sequence = sequence, // ...la secuencia
            // serializa el payload a JSON, o deja null si el payload es null
            Json = payload is null ? null : JsonSerializer.Serialize(payload, Options)
        };

    public static MessageEnvelope Create(MessageType type, Guid senderId, long sequence) // crea un sobre vacío (sin payload)
        => new() { Type = type, SenderId = senderId, Sequence = sequence }; // sobre sin payload con tipo, remitente y secuencia

    public T? Payload<T>() // deserializa el payload al tipo genérico T
        => Json is null ? default : JsonSerializer.Deserialize<T>(Json, Options); // sin JSON devuelve default; si no, deserializa

    public string Serialize() // serializa el sobre completo a JSON
        => JsonSerializer.Serialize(this, Options); // convierte el sobre en texto JSON

    public static MessageEnvelope? Deserialize(string json) // deserializa un sobre desde texto JSON
        => JsonSerializer.Deserialize<MessageEnvelope>(json, Options); // convierte el texto JSON en sobre (o null si falla)
}
