// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: ClientConnection
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

// Importa tipos de direcciones y puntos finales de red.
using System.Net;
// Importa los tipos de sockets (TcpClient, UdpClient, NetworkStream).
using System.Net.Sockets;
// Importa la codificación de texto (UTF-8 para las tramas JSON).
using System.Text;
// Importa las definiciones del protocolo (MessageEnvelope, FrameCodec, ProtocolDefaults).
using Fedo.StageLnk.Protocol;

namespace Fedo.StageLnk.Client;

/// <summary>
/// Conexión del cliente con el servidor: canal TCP para sincronización y canal
/// UDP (puerto efímero) para telemetría y recepción de órdenes de show. Gestiona
/// la reconexión inicial, los bucles de lectura y el aviso de desconexión.
/// </summary>
// Implementa IAsyncDisposable para liberar los sockets y bucles de forma asíncrona.
public sealed class ClientConnection : IAsyncDisposable
{
    // Nombre de host del servidor al que conectar.
    private readonly string _host;
    // Puerto TCP usado para el canal de sincronización.
    private readonly int _tcpPort;
    // Puerto UDP usado para telemetría y órdenes de show.
    private readonly int _udpPort;

    // Cliente TCP actual (null cuando no hay conexión activa).
    private TcpClient? _tcp;
    // Flujo de red del socket TCP para leer y escribir tramas.
    private NetworkStream? _stream;
    // Cliente UDP con puerto efímero para recibir telemetría.
    private UdpClient? _udp;
    // Token de cancelación compartido para detener ambos bucles.
    private CancellationTokenSource? _cts;
    // Tarea del bucle de lectura TCP.
    private Task? _tcpLoop;
    // Tarea del bucle de recepción UDP.
    private Task? _udpLoop;

    // Evento que publica mensajes completos recibidos por TCP.
    public event Action<MessageEnvelope>? TcpMessageReceived;
    // Evento que publica mensajes completos recibidos por UDP.
    public event Action<MessageEnvelope>? UdpMessageReceived;
    // Evento que avisa de que la conexión TCP se perdió.
    public event Action? Disconnected;
    // Evento de registro de mensajes de diagnóstico.
    public event Action<string>? Log;

    // Expone el punto final local del socket UDP (puerto efímero asignado).
    public IPEndPoint? LocalUdpEndpoint => _udp?.Client.LocalEndPoint as IPEndPoint;

    // Constructor: guarda host y puertos, usando por defecto los del protocolo.
    public ClientConnection(string host, int tcpPort = ProtocolDefaults.TcpPort, int udpPort = ProtocolDefaults.UdpPort)
    {
        _host = host;
        _tcpPort = tcpPort;
        _udpPort = udpPort;
    }

    // Conecta con el servidor creando sockets y lanzando los bucles de lectura.
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Cierra cualquier conexión anterior antes de volver a conectar.
        await TeardownAsync();

        // Nuevo token de cancelación para esta conexión.
        _cts = new CancellationTokenSource();
        // Crea un cliente TCP nuevo para esta conexión.
        _tcp = new TcpClient();

        // Guarda el último error de intento (null si alguno tuvo éxito).
        Exception? last = null;
        // Reintenta la conexión TCP hasta 10 veces.
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                // Intenta conectar con el servidor en host y puerto TCP.
                await _tcp.ConnectAsync(_host, _tcpPort, ct);
                // Limpia el error si la conexión fue exitosa.
                last = null;
                // Sale del bucle al lograr conectar.
                break;
            }
            catch (Exception ex) when (attempt < 10)
            {
                // Registra el fallo para reintentar en la siguiente iteración.
                last = ex;
                // Informa del reintento en curso.
                Log?.Invoke($"Reintentando conexión ({attempt}/10)...");
                // Descarta el cliente TCP fallido.
                _tcp.Dispose();
                // Crea un cliente TCP nuevo para el siguiente intento.
                _tcp = new TcpClient();
                // Espera un segundo antes de reintentar.
                await Task.Delay(1000, ct);
            }
        }

        // Si todos los intentos fallaron, propaga el último error.
        if (last is not null)
            throw last!;

        // Obtiene el flujo de red para leer y escribir en el socket TCP.
        _stream = _tcp.GetStream();
        // Abre un socket UDP con puerto efímero asignado por el sistema.
        _udp = new UdpClient(0);

        // Lanza el bucle de lectura TCP en segundo plano.
        _tcpLoop = TcpLoopAsync(_cts.Token);
        // Lanza el bucle de recepción UDP en segundo plano.
        _udpLoop = UdpLoopAsync(_cts.Token);
        // Registra la conexión establecida con sus puntos finales.
        Log?.Invoke($"Conectado a {_host}:{_tcpPort} (UDP local {_udp.Client.LocalEndPoint})");
    }

    /// <summary>
    /// Cierra los sockets y bucles de una conexión anterior (si los hay) para poder
    /// volver a conectar. Es seguro llamarlo sobre una conexión nunca establecida.
    /// </summary>
    // Cancela y espera los bucles y libera los recursos de la conexión previa.
    private async Task TeardownAsync()
    {
        // Solicita la cancelación de los bucles activos.
        _cts?.Cancel();
        try
        {
            // Espera a que termine el bucle TCP si estaba corriendo.
            if (_tcpLoop is not null) await _tcpLoop;
            // Espera a que termine el bucle UDP si estaba corriendo.
            if (_udpLoop is not null) await _udpLoop;
        }
        catch
        {
            // Ignora las excepciones de cancelación al desmontar.
        }
        // Libera el cliente UDP.
        _udp?.Dispose();
        // Libera el cliente TCP.
        _tcp?.Dispose();
        // Libera el token de cancelación.
        _cts?.Dispose();
        // Pone a null la referencia al cliente TCP.
        _tcp = null;
        // Pone a null la referencia al flujo de red.
        _stream = null;
        // Pone a null la referencia al cliente UDP.
        _udp = null;
        // Pone a null la tarea del bucle TCP.
        _tcpLoop = null;
        // Pone a null la tarea del bucle UDP.
        _udpLoop = null;
        // Pone a null el token de cancelación.
        _cts = null;
    }

    // Bucle que lee tramas TCP hasta que se cancela o se cierra la conexión.
    private async Task TcpLoopAsync(CancellationToken ct)
    {
        try
        {
            // Se mantiene leyendo mientras no se pida cancelación.
            while (!ct.IsCancellationRequested)
            {
                // Lee la siguiente trama TCP (null indica cierre del remoto).
                var message = await FrameCodec.ReadTcpAsync(_stream!, ct);
                // Si el remoto cerró, sale del bucle.
                if (message is null)
                    break;
                // Publica el mensaje recibido a los suscriptores.
                TcpMessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // La cancelación es un cierre esperado, no se registra.
        }
        catch (Exception ex)
        {
            // Registra el error que cortó el bucle TCP.
            Log?.Invoke($"Conexión TCP terminada: {ex.Message}");
        }
        finally
        {
            // Al terminar, invalida el flujo de red.
            _stream = null;
            // Avisa a los suscriptores de la desconexión.
            Disconnected?.Invoke();
        }
    }

    // Bucle que recibe datagramas UDP hasta que se solicita cancelación.
    private async Task UdpLoopAsync(CancellationToken ct)
    {
        try
        {
            // Se mantiene recibiendo mientras no se pida cancelación.
            while (!ct.IsCancellationRequested)
            {
                // Recibe el siguiente datagrama UDP.
                var result = await _udp!.ReceiveAsync(ct);
                // Convierte la carga útil a texto usando UTF-8.
                string json = Encoding.UTF8.GetString(result.Buffer);
                // Deserializa el texto JSON en un mensaje del protocolo.
                var message = MessageEnvelope.Deserialize(json);
                // Si la deserialización fue válida, publica el mensaje.
                if (message is not null)
                    UdpMessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // La cancelación es un cierre esperado, no se registra.
        }
        catch (Exception ex)
        {
            // Registra el error que cortó el bucle UDP.
            Log?.Invoke($"UDP terminado: {ex.Message}");
        }
    }

    // Envía un mensaje completo por el canal TCP.
    public async Task SendTcpAsync(MessageEnvelope envelope)
    {
        // Si no hay flujo activo, descarta el envío.
        if (_stream is null)
            return;
        // Codifica el mensaje como trama TCP.
        var frame = FrameCodec.EncodeTcp(envelope);
        // Escribe la trama en el flujo de red.
        await _stream.WriteAsync(frame);
        // Fuerza el vaciado del flujo para que salga de inmediato.
        await _stream.FlushAsync();
    }

    // Envía un mensaje completo por el canal UDP al servidor.
    public async Task SendUdpAsync(MessageEnvelope envelope)
    {
        // Si no hay socket UDP activo, descarta el envío.
        if (_udp is null)
            return;
        // Codifica el mensaje como payload UDP.
        var bytes = FrameCodec.EncodeUdp(envelope);
        // Envía los bytes al host y puerto UDP configurados.
        await _udp.SendAsync(bytes, bytes.Length, _host, _udpPort);
    }
    // Libera asíncronamente los recursos de la conexión.
    public async ValueTask DisposeAsync()
        => await TeardownAsync();
}
