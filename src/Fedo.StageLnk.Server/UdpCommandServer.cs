// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: UdpCommandServer
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Collections.Concurrent; // Proporciona colecciones seguras para multihilo (ConcurrentDictionary)
using System.Net; // Proporciona el tipo IPEndPoint
using System.Net.Sockets; // Proporciona el tipo UdpClient
using System.Text; // Proporciona la codificación UTF8
using Fedo.StageLnk.Protocol; // Importa el protocolo compartido (FrameCodec, MessageEnvelope)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Servidor UDP de comandos y telemetría de baja latencia. Recibe los reportes
/// de estado de los clientes y permite enviarles órdenes de show (play, stop,
/// fade, texto...) de forma individual o por broadcast. Los endpoints se
/// asocian dinámicamente al ver el identificador del remitente.
/// </summary>
public sealed class UdpCommandServer : IAsyncDisposable // Servidor UDP con liberación asíncrona de recursos
{
    private readonly int _port; // Puerto UDP de escucha
    private UdpClient? _udp; // Cliente UDP del servidor
    private CancellationTokenSource? _cts; // Token de cancelación del bucle de recepción
    private Task? _receiveLoop; // Tarea del bucle de recepción
    private readonly ConcurrentDictionary<Guid, IPEndPoint> _endpoints = new(); // Endpoints UDP por id de cliente

    public event Action<Guid, IPEndPoint, MessageEnvelope>? DatagramReceived; // Se dispara al recibir un datagrama
    public event Action<string>? Log; // Evento de log (mensajes de texto)

    public UdpCommandServer(int port = ProtocolDefaults.UdpPort) // Constructor con el puerto por defecto
    {
        _port = port; // Almacena el puerto de escucha
    }

    public void Start() // Inicia el servidor UDP
    {
        _cts = new CancellationTokenSource(); // Crea el token de cancelación
        _udp = new UdpClient(_port); // Abre el socket UDP en el puerto
        _receiveLoop = ReceiveLoopAsync(_cts.Token); // Lanza el bucle de recepción en segundo plano
        // Informa de que el UDP quedó listo
        Log?.Invoke($"UDP listo en el puerto {_port}");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct) // Bucle que recibe los datagramas entrantes
    {
        while (!ct.IsCancellationRequested) // Mientras no se cancele
        {
            UdpReceiveResult result; // Resultado de la recepción
            try // Protege la recepción ante errores
            {
                result = await _udp!.ReceiveAsync(ct); // Espera un datagrama entrante
            }
            catch (OperationCanceledException) // Si se cancela el bucle
            {
                break; // Termina el bucle
            }
            catch (SocketException) // Si falla el socket de forma transitoria
            {
                continue; // Reintenta recibir
            }

            string json = Encoding.UTF8.GetString(result.Buffer); // Convierte los bytes recibidos a texto
            MessageEnvelope? message = null; // Inicializa el mensaje como nulo
            try // Protege la deserialización ante JSON corrupto
            {
                message = MessageEnvelope.Deserialize(json); // Deserializa el sobre de mensaje
            }
            catch
            {
                // Ignora datagramas con JSON inválido para resistir corrupción
                continue;
            }
            if (message is null) // Si el mensaje no es válido
                continue; // Ignora el datagrama

            if (message.SenderId != Guid.Empty) // Si el remitente se identifica
                _endpoints[message.SenderId] = result.RemoteEndPoint; // Asocia dinámicamente su endpoint

            DatagramReceived?.Invoke(message.SenderId, result.RemoteEndPoint, message); // Notifica el datagrama recibido
        }
    }

    public void Register(Guid clientId, IPEndPoint endpoint) // Asocia un endpoint UDP a un cliente
        => _endpoints[clientId] = endpoint; // Guarda el endpoint en el diccionario

    public void Unregister(Guid clientId) // Desasocia el endpoint de un cliente
        => _endpoints.TryRemove(clientId, out _); // Elimina su endpoint si existe

    public async Task SendToAsync(Guid clientId, MessageEnvelope envelope) // Envía un mensaje UDP a un cliente concreto
    {
        if (!_endpoints.TryGetValue(clientId, out var endpoint) || _udp is null) // Si no hay endpoint o socket
            return; // No puede enviar
        var bytes = FrameCodec.EncodeUdp(envelope); // Codifica el sobre en bytes
        await _udp.SendAsync(bytes, bytes.Length, endpoint); // Envía el datagrama al endpoint del cliente
    }

    public async Task BroadcastAsync(MessageEnvelope envelope) // Difunde un mensaje UDP a todos los clientes
    {
        if (_udp is null) // Si no hay socket
            return; // No puede enviar
        var bytes = FrameCodec.EncodeUdp(envelope); // Codifica el sobre en bytes
        foreach (var endpoint in _endpoints.Values) // Recorre los endpoints conocidos
            await _udp.SendAsync(bytes, bytes.Length, endpoint); // Envía el datagrama a cada endpoint
    }

    public async ValueTask DisposeAsync() // Detiene y libera el servidor
    {
        _cts?.Cancel(); // Cancela el bucle de recepción
        try // Protege la espera del bucle
        {
            if (_receiveLoop is not null) // Si el bucle está en marcha
                await _receiveLoop; // Espera a que termine
        }
        catch // Si el bucle terminó con error
        {
        }
        _udp?.Dispose(); // Cierra el socket UDP
        _cts?.Dispose(); // Libera el token de cancelación
    }
}
