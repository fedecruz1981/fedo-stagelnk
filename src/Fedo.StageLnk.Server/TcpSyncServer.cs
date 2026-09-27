// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: TcpSyncServer
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Net; // Proporciona el tipo IPAddress
using System.Net.Sockets; // Proporciona TcpListener y TcpClient
using Fedo.StageLnk.Protocol; // Importa el protocolo compartido (FrameCodec, MessageEnvelope)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Servidor TCP de sincronización: acepta conexiones de clientes, recibe sus
/// mensajes (saludo, peticiones de sync, confirmaciones) y permite enviarles
/// tramas (manifiesto, cue list, trozos de archivo). Cada cliente se gestiona
/// en una tarea propia hasta que cierra la conexión.
/// </summary>
public sealed class TcpSyncServer : IAsyncDisposable // Servidor TCP con liberación asíncrona de recursos
{
    private readonly int _port; // Puerto de escucha TCP
    private TcpListener? _listener; // Listener TCP de aceptación de conexiones
    private CancellationTokenSource? _cts; // Token de cancelación del bucle de aceptación
    private Task? _acceptLoop; // Tarea del bucle de aceptación de clientes

    public event Action<ServerClientSession>? ClientAccepted; // Se dispara al aceptar un cliente
    public event Action<ServerClientSession>? ClientClosed; // Se dispara al cerrar un cliente
    public event Action<ServerClientSession, MessageEnvelope>? MessageReceived; // Se dispara al recibir un mensaje
    public event Action<string>? Log; // Evento de log (mensajes de texto)

    public TcpSyncServer(int port = ProtocolDefaults.TcpPort) // Constructor con el puerto por defecto
    {
        _port = port; // Almacena el puerto de escucha
    }

    public void Start() // Inicia el servidor TCP
    {
        _cts = new CancellationTokenSource(); // Crea el token de cancelación
        _listener = new TcpListener(IPAddress.Any, _port); // Escucha en todas las interfaces del puerto
        _listener.Start(); // Comienza a aceptar conexiones
        _acceptLoop = AcceptLoopAsync(_cts.Token); // Lanza el bucle de aceptación en segundo plano
        // Informa de que el TCP quedó listo
        Log?.Invoke($"TCP listo en el puerto {_port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct) // Bucle que acepta conexiones entrantes
    {
        while (!ct.IsCancellationRequested) // Mientras no se cancele
        {
            TcpClient tcp; // Cliente aceptado
            try // Protege la aceptación ante errores
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct); // Acepta una conexión entrante
            }
            catch (OperationCanceledException) // Si se cancela el bucle
            {
                break; // Termina el bucle
            }
            catch (SocketException) // Si falla el socket de forma transitoria
            {
                continue; // Reintenta aceptar
            }

            var session = new ServerClientSession // Crea la sesión del cliente aceptado
            {
                Id = Guid.NewGuid(), // Id provisional del cliente
                Name = "desconocido", // Nombre provisional hasta que salude
                Tcp = tcp // Asigna el canal TCP
            };

            _ = HandleClientAsync(session, ct); // Lanza la tarea que gestiona la sesión
        }
    }

    private async Task HandleClientAsync(ServerClientSession session, CancellationToken ct) // Gestiona el ciclo de vida de una sesión
    {
        var stream = session.Tcp!.GetStream(); // Obtiene el stream de la conexión
        session.Touch(); // Marca el primer contacto del cliente
        ClientAccepted?.Invoke(session); // Notifica la aceptación del cliente

        try // Protege la lectura de mensajes
        {
            while (!ct.IsCancellationRequested) // Mientras no se cancele
            {
                var message = await FrameCodec.ReadTcpAsync(stream, ct); // Lee una trama TCP del cliente
                if (message is null) // Si la conexión se cerró
                    break; // Termina la sesión

                session.Touch(); // Actualiza el último contacto

                MessageReceived?.Invoke(session, message); // Notifica el mensaje recibido
            }
        }
        catch (OperationCanceledException) // Si se cancela la lectura
        {
        }
        catch (IOException) // Si la conexión se interrumpe
        {
        }
        catch (SocketException) // Si falla el socket
        {
        }
        catch (InvalidDataException ex) // Si la trama recibida no es válida
        {
            // Informa del frame inválido recibido
            Log?.Invoke($"Cliente {session.Name}: frame inválido ({ex.Message})");
        }
        finally // Limpieza al cerrar la sesión
        {
            ClientClosed?.Invoke(session); // Notifica el cierre de la sesión
            session.Tcp?.Dispose(); // Libera el socket del cliente
            session.Tcp = null; // Desasocia el canal de la sesión
        }
    }

    public async Task SendAsync(ServerClientSession session, MessageEnvelope envelope) // Envía un sobre de mensaje por TCP
    {
        var tcp = session.Tcp; // Toma el canal TCP del cliente
        if (tcp is null) // Si el cliente no tiene canal
            return; // No puede enviar
        var frame = FrameCodec.EncodeTcp(envelope); // Codifica el sobre como trama
        await tcp.GetStream().WriteAsync(frame); // Escribe la trama en el stream
        await tcp.GetStream().FlushAsync(); // Fuerza el envío de los datos
    }

    public async ValueTask DisposeAsync() // Detiene y libera el servidor
    {
        _cts?.Cancel(); // Cancela los bucles internos
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
