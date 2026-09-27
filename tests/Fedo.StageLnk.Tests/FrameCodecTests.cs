// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: FrameCodecTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Text;
using Fedo.StageLnk.Protocol;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>Pruebas del codificador de tramas TCP/UDP y del sobre JSON.</summary>
public sealed class FrameCodecTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que una trama TCP conserva el payload al decodificarla
    public async Task TcpRoundtrip_PreservesPayload()
    {
        // Crea un sobre con mensaje Hello, id y secuencia de ejemplo
        var original = MessageEnvelope.Create(MessageType.Hello, Guid.NewGuid(), 42,
            // Payload con los datos del mensaje Hello
            new HelloMessage { ClientName = "TestClient", Version = "1.0", FreeDiskMb = 1234 });

        // Codifica el sobre en una trama TCP con prefijo de longitud
        byte[] frame = FrameCodec.EncodeTcp(original);

        // Abre un flujo en memoria sobre la trama codificada
        using var stream = new MemoryStream(frame);
        // Lee y decodifica la trama desde el flujo
        var decoded = await FrameCodec.ReadTcpAsync(stream, CancellationToken.None);

        // La trama decodificada no debe ser nula
        Assert.NotNull(decoded);
        // Se conserva el tipo de mensaje
        Assert.Equal(original.Type, decoded.Type);
        // Se conserva el id del emisor
        Assert.Equal(original.SenderId, decoded.SenderId);
        // Se conserva el número de secuencia
        Assert.Equal(original.Sequence, decoded.Sequence);
        // Extrae el payload tipado como HelloMessage
        var hello = decoded.Payload<HelloMessage>();
        // Se conserva el nombre del cliente
        Assert.Equal("TestClient", hello!.ClientName);
        // Se conserva el espacio libre en disco
        Assert.Equal(1234, hello.FreeDiskMb);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la trama TCP lleva un prefijo de longitud de 4 bytes
    public void TcpFrame_HasLengthPrefix()
    {
        // Crea un sobre Pong simple sin payload
        var original = MessageEnvelope.Create(MessageType.Pong, Guid.NewGuid(), 1);
        // Codifica la trama TCP
        byte[] frame = FrameCodec.EncodeTcp(original);

        // Lee la longitud declarada desde los 4 primeros bytes
        int payloadLength = BitConverter.ToInt32(frame, 0);
        // La longitud debe ser el total menos los 4 bytes del prefijo
        Assert.Equal(frame.Length - 4, payloadLength);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que una longitud cero es rechazada por el decodificador
    public async Task TcpRead_RejectsZeroLength()
    {
        // Flujo de 4 bytes cuyo prefijo indica longitud cero
        using var stream = new MemoryStream(new byte[4]);
        // Debe lanzar InvalidDataException al leer una longitud cero
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => FrameCodec.ReadTcpAsync(stream, CancellationToken.None));
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que una trama que supera el tamaño máximo es rechazada
    public async Task TcpRead_RejectsOversizedFrame()
    {
        // Flujo cuyo prefijo declara una longitud mayor al máximo permitido
        using var stream = new MemoryStream(BitConverter.GetBytes(FrameCodec.MaxFrameBytes + 1));
        // Debe lanzar InvalidDataException ante una trama demasiado grande
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => FrameCodec.ReadTcpAsync(stream, CancellationToken.None));
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un datagrama UDP se puede deserializar y conserva el payload
    public void UdpRoundtrip_Works()
    {
        // Crea un sobre de estado con CPU y FPS de ejemplo
        var original = MessageEnvelope.Create(MessageType.Status, Guid.NewGuid(), 7,
            // Payload con el reporte de estado del cliente
            new ClientStatusReport { CpuUsage = 12.5, Fps = 30 });
        // Codifica el datagrama UDP
        byte[] datagram = FrameCodec.EncodeUdp(original);

        // Deserializa el sobre desde su representación UTF-8
        var decoded = MessageEnvelope.Deserialize(Encoding.UTF8.GetString(datagram));

        // El sobre decodificado no debe ser nulo
        Assert.NotNull(decoded);
        // El tipo debe seguir siendo Status
        Assert.Equal(MessageType.Status, decoded.Type);
        // Extrae el payload como reporte de estado
        var report = decoded.Payload<ClientStatusReport>();
        // Se conserva el uso de CPU
        Assert.Equal(12.5, report!.CpuUsage);
        // Se conservan los FPS
        Assert.Equal(30, report.Fps);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la serialización JSON usa nombres de propiedades en camelCase
    public void Serialize_UsesCamelCase()
    {
        // Crea un sobre Hello mínimo
        var envelope = MessageEnvelope.Create(MessageType.Hello, Guid.NewGuid(), 1);
        // Serializa el sobre a JSON
        string json = envelope.Serialize();

        // El JSON debe contener la propiedad "senderId" en camelCase
        Assert.Contains("\"senderId\"", json);
        // El JSON debe contener la propiedad "sequence" en camelCase
        Assert.Contains("\"sequence\"", json);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un mensaje de texto se conserva al recorrer la trama TCP
    public async Task TextMessage_Roundtrip()
    {
        // Crea un sobre con mensaje de texto y su estilo
        var original = MessageEnvelope.Create(MessageType.ShowText, Guid.NewGuid(), 5,
            // Payload con texto, tamaño de fuente y colores
            new TextMessage { Text = "Bienvenidos al show", FontSize = 128, Color = "#FFD700", Background = "#0A1A2A" });

        // Codifica la trama TCP
        byte[] frame = FrameCodec.EncodeTcp(original);
        // Abre un flujo en memoria sobre la trama
        using var stream = new MemoryStream(frame);
        // Lee y decodifica la trama
        var decoded = await FrameCodec.ReadTcpAsync(stream, CancellationToken.None);

        // La trama decodificada no debe ser nula
        Assert.NotNull(decoded);
        // El tipo debe seguir siendo ShowText
        Assert.Equal(MessageType.ShowText, decoded.Type);
        // Extrae el payload como mensaje de texto
        var text = decoded.Payload<TextMessage>();
        // Se conserva el texto del cartel
        Assert.Equal("Bienvenidos al show", text!.Text);
        // Se conserva el tamaño de fuente
        Assert.Equal(128, text.FontSize);
        // Se conserva el color del texto
        Assert.Equal("#FFD700", text.Color);
        // Se conserva el color de fondo
        Assert.Equal("#0A1A2A", text.Background);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un flujo TCP vacío devuelve null sin lanzar errores
    public async Task EmptyTcpStream_ReturnsNull()
    {
        // Flujo de memoria sin bytes
        using var stream = new MemoryStream(Array.Empty<byte>());
        // Leer un flujo vacío debe devolver null
        Assert.Null(await FrameCodec.ReadTcpAsync(stream, CancellationToken.None));
    }
}
