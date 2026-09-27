// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: StageLnkServer
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Collections.Concurrent; // Proporciona colecciones seguras para multihilo (ConcurrentDictionary)
using System.Net; // Proporciona tipos de red como IPEndPoint
using Fedo.StageLnk.Protocol; // Importa el protocolo compartido (mensajes y tipos de comando)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>
/// Orquestador del lado servidor: une el canal TCP de sincronización y el UDP de
/// comandos, gestiona la biblioteca de medios y la cue list, atiende el registro
/// y la sincronización de cada cliente, y difunde las órdenes de show (play,
/// stop, fade, texto, navegación next/prev). También vigila la salud de los
/// clientes por heartbeat.
/// </summary>
public sealed class StageLnkServer : IAsyncDisposable // Orquestador del servidor, con liberación asíncrona de recursos
{
    public Guid ServerId { get; } = Guid.NewGuid(); // Identificador único del servidor

    public MediaLibrary Library { get; } // Biblioteca de medios del servidor
    public CueList Cues { get; } = new(); // Lista de cues del show
    public string LibraryRoot { get; } // Ruta raíz de la biblioteca de medios
    public int BoundHttpPort => _http.BoundPort; // Puerto real del servidor HTTP (si se pidió efímero, el asignado por el sistema)

    private readonly TcpSyncServer _tcp; // Servidor TCP de sincronización
    private readonly UdpCommandServer _udp; // Servidor UDP de comandos
    private readonly MediaHttpServer _http; // Servidor HTTP de streaming de medios
    private readonly ConcurrentDictionary<Guid, ServerClientSession> _clients = new(); // Clientes conectados por Id
    private readonly HashSet<Guid> _inactiveClients = new(); // Clientes marcados como inactivos por heartbeat
    private readonly CancellationTokenSource _cts = new(); // Token de cancelación para detener tareas internas
    private Task? _monitor; // Tarea del bucle de monitorización de heartbeats

    public event Action<string>? Log; // Evento de log (mensajes de texto)
    public event Action<ServerClientSession>? ClientConnected; // Se dispara al conectar un cliente
    public event Action<Guid>? ClientDisconnected; // Se dispara al desconectar un cliente
    public event Action<ServerClientSession, ClientStatusReport>? ClientStatusUpdated; // Se dispara al recibir un reporte de estado
    public event Action<ServerClientSession>? ClientReady; // Se dispara cuando un cliente queda listo para el show

    public IReadOnlyCollection<ServerClientSession> Clients => _clients.Values.ToList(); // Lista de clientes conectados

    public StageLnkServer(string libraryRoot, int tcpPort = ProtocolDefaults.TcpPort, int udpPort = ProtocolDefaults.UdpPort, int httpPort = ProtocolDefaults.HttpPort) // Constructor: raíz de biblioteca y puertos TCP/UDP/HTTP
    {
        LibraryRoot = libraryRoot; // Almacena la raíz de la biblioteca
        Library = new MediaLibrary(); // Crea la biblioteca vacía
        _tcp = new TcpSyncServer(tcpPort); // Instancia el servidor TCP
        _udp = new UdpCommandServer(udpPort); // Instancia el servidor UDP
        _http = new MediaHttpServer(libraryRoot, httpPort); // Instancia el servidor HTTP de streaming

        _tcp.Log += m => Log?.Invoke(m); // Reenvía los logs de TCP al log del orquestador
        _udp.Log += m => Log?.Invoke(m); // Reenvía los logs de UDP al log del orquestador
        _http.Log += m => Log?.Invoke(m); // Reenvía los logs de HTTP al log del orquestador
        _tcp.ClientAccepted += OnTcpAccepted; // Enlaza el alta de cliente TCP
        _tcp.ClientClosed += OnTcpClosed; // Enlaza el cierre de cliente TCP
        _tcp.MessageReceived += OnTcpMessage; // Enlaza los mensajes TCP entrantes
        _udp.DatagramReceived += OnUdpDatagram; // Enlaza los datagramas UDP entrantes
    }

    public void Start() // Arranca los servidores y la monitorización
    {
        _tcp.Start(); // Inicia el servidor TCP
        _udp.Start(); // Inicia el servidor UDP
        _http.Start(); // Inicia el servidor HTTP de streaming
        _monitor = MonitorAsync(_cts.Token); // Lanza el bucle de monitorización de heartbeats
        // Informa del arranque con la ruta de la biblioteca
        Log?.Invoke($"Fedo-StageLnk Server v1.0 iniciado. Biblioteca: {LibraryRoot}");
    }

    /// <summary>Escanea la biblioteca, actualiza el manifiesto y lo difunde con la cue list.</summary>
    public async Task ScanLibraryAsync(IProgress<string>? progress = null, CancellationToken ct = default) // Escanea la biblioteca y difunde manifiesto y cues
    {
        Log?.Invoke("Escaneando biblioteca..."); // Informa del inicio del escaneo
        var assets = new LibraryScanner().Scan(LibraryRoot, progress); // Escanea la biblioteca y obtiene los medios
        Library.ReplaceAll(assets); // Actualiza la biblioteca con los medios encontrados
        // Informa del resultado del escaneo
        Log?.Invoke($"Biblioteca lista: {assets.Count} archivos (versión {Library.Version})");
        await BroadcastManifestAsync(ct); // Difunde el manifiesto a los clientes
        await BroadcastCueListAsync(ct); // Difunde la cue list a los clientes
    }

    public void UpsertCue(Cue cue) // Inserta o actualiza una cue en la lista
    {
        Cues.Upsert(cue); // Delega en la cue list
        // Informa de la cue actualizada
        Log?.Invoke($"Cue {cue.Number:000} '{cue.Name}' actualizado");
    }

    public async Task SaveProjectAsync(string path) // Guarda el proyecto actual en un archivo
    {
        var project = new StageProject // Construye la foto del proyecto
        {
            Name = Path.GetFileNameWithoutExtension(path), // Nombre del proyecto desde el archivo
            LibraryRoot = LibraryRoot, // Guarda la raíz de la biblioteca
            ManifestVersion = Library.Version, // Versión del manifiesto
            Media = Library.All.ToList(), // Copia los medios actuales
            Cues = Cues.Cues.ToList() // Copia las cues actuales
        };
        ProjectFile.SaveProject(path, project); // Persiste el proyecto en JSON
        // Informa del guardado con el número de cues y medios
        Log?.Invoke($"Proyecto guardado: {path} ({project.Cues.Count} cues, {project.Media.Count} medios)");
        await Task.CompletedTask; // Completa la tarea (método asíncrono por API)
    }

    public async Task LoadProjectAsync(string path) // Carga un proyecto desde un archivo
    {
        var project = ProjectFile.LoadProject(path); // Deserializa el proyecto
        if (project is null) // Si no se pudo leer
        {
            // Informa del fallo de lectura
            Log?.Invoke($"No se pudo leer el proyecto: {path}");
            return; // Aborta la carga
        }

        if (project.Media.Count > 0) // Si el proyecto trae medios
        {
            Library.ReplaceAll(project.Media); // Sustituye la biblioteca por los medios del proyecto
            await BroadcastManifestAsync(); // Difunde el nuevo manifiesto
        }

        Cues.ReplaceAll(project.Cues); // Sustituye la cue list por la del proyecto
        // Informa de la carga del proyecto
        Log?.Invoke($"Proyecto cargado: {path} ({project.Cues.Count} cues, {project.Media.Count} medios)");
        await BroadcastCueListAsync(); // Difunde la nueva cue list
    }

    public async Task SaveCueLibraryAsync(string path) // Guarda la cue list como biblioteca reutilizable
    {
        var library = new CueLibrary // Construye la biblioteca de cues
        {
            Name = Path.GetFileNameWithoutExtension(path), // Nombre desde el archivo
            Cues = Cues.Cues.ToList() // Copia las cues actuales
        };
        ProjectFile.SaveCueLibrary(path, library); // Persiste la biblioteca en JSON
        // Informa del guardado de la biblioteca
        Log?.Invoke($"Cue library guardada: {path} ({library.Cues.Count} cues)");
        await Task.CompletedTask; // Completa la tarea (método asíncrono por API)
    }

    public async Task LoadCueLibraryAsync(string path) // Carga una biblioteca de cues desde un archivo
    {
        var library = ProjectFile.LoadCueLibrary(path); // Deserializa la biblioteca
        if (library is null) // Si no se pudo leer
        {
            // Informa del fallo de lectura
            Log?.Invoke($"No se pudo leer la cue library: {path}");
            return; // Aborta la carga
        }

        Cues.ReplaceAll(library.Cues); // Sustituye la cue list por la de la biblioteca
        // Informa de la carga de la biblioteca
        Log?.Invoke($"Cue library cargada: {path} ({library.Cues.Count} cues)");
        await BroadcastCueListAsync(); // Difunde la nueva cue list
    }

    public Cue? NextCue(int afterNumber) => Cues.NextAfter(afterNumber); // Devuelve la cue siguiente al número dado

    /// <summary>Difunde la cue list completa a todos los clientes por TCP.</summary>
    public async Task BroadcastCueListAsync(CancellationToken ct = default) // Difunde la cue list a todos los clientes por TCP
    {
        var envelope = MessageEnvelope.Create( // Construye el sobre con la cue list
            MessageType.CueList, ServerId, DateTime.UtcNow.Ticks, // Tipo, id del servidor y marca de tiempo
            new CueListMessage { Version = Cues.Version, Cues = Cues.Cues.ToList() }); // Contenido: versión y lista de cues
        foreach (var session in _clients.Values) // Recorre todos los clientes conectados
        {
            session.NextSendSequence++; // Incrementa la secuencia de envío del cliente
            var tagged = envelope with { Sequence = session.NextSendSequence }; // Reetiqueta el sobre con la secuencia del cliente
            await _tcp.SendAsync(session, tagged); // Envía la trama por TCP
        }
    }

    private async Task BroadcastManifestAsync(CancellationToken ct = default) // Difunde el manifiesto de medios por TCP
    {
        var envelope = MessageEnvelope.Create( // Construye el sobre con el manifiesto
            MessageType.MediaManifest, ServerId, DateTime.UtcNow.Ticks, // Tipo, id del servidor y marca de tiempo
            new MediaManifestMessage { Version = Library.Version, Media = Library.All.ToList() }); // Contenido: versión y medios
        foreach (var session in _clients.Values) // Recorre los clientes conectados
        {
            session.NextSendSequence++; // Incrementa la secuencia de envío del cliente
            var tagged = envelope with { Sequence = session.NextSendSequence }; // Reetiqueta el sobre con la secuencia del cliente
            await _tcp.SendAsync(session, tagged); // Envía la trama por TCP
        }
    }

    private void OnTcpAccepted(ServerClientSession session) // Registra un cliente TCP recién aceptado
    {
        _clients[session.Id] = session; // Añade el cliente al diccionario
        // Informa de la conexión del cliente
        Log?.Invoke($"Cliente conectado (TCP): {session.Name} [{session.Id}]");
    }

    private void OnTcpClosed(ServerClientSession session) // Maneja el cierre de la conexión de un cliente TCP
    {
        if (_clients.TryRemove(session.Id, out _)) // Si el cliente estaba registrado y se elimina
        {
            _udp.Unregister(session.Id); // Libera su endpoint UDP
            // Informa de la desconexión del cliente
            Log?.Invoke($"Cliente desconectado: {session.Name} [{session.Id}]");
            ClientDisconnected?.Invoke(session.Id); // Notifica la desconexión a los suscriptores
        }
    }

    private async void OnTcpMessage(ServerClientSession session, MessageEnvelope message) // Enruta los mensajes TCP entrantes
    {
        try // Protege el manejo de mensajes ante errores
        {
            switch (message.Type) // Despacha según el tipo de mensaje
            {
                case MessageType.Hello: // Saludo inicial del cliente
                    await OnHelloAsync(session, message); // Procesa el registro del cliente
                    break; // Sale del switch
                case MessageType.SyncRequest: // Petición de archivos faltantes
                    await OnSyncRequestAsync(session, message); // Envía los trozos solicitados
                    break; // Sale del switch
                case MessageType.SyncComplete: // Confirmación de sincronización
                    await OnSyncCompleteAsync(session, message); // Registra el medio sincronizado
                    break; // Sale del switch
                case MessageType.Ready: // Cliente listo para el show
                    OnReadyAsync(session, message); // Actualiza el estado de preparación
                    break; // Sale del switch
                case MessageType.Ping: // Comprobación de latencia
                    // Responde con un Pong por TCP
                    await _tcp.SendAsync(session, MessageEnvelope.Create(MessageType.Pong, ServerId, ++session.NextSendSequence));
                    break; // Sale del switch
            }
        }
        catch (Exception ex) // Si algo falla al procesar el mensaje
        {
            // Registra el error con el tipo de mensaje y el cliente
            Log?.Invoke($"Error manejando mensaje {message.Type} de {session.Name}: {ex.Message}");
        }
    }

    private async Task OnHelloAsync(ServerClientSession session, MessageEnvelope message) // Procesa el saludo y registro del cliente
    {
        var hello = message.Payload<HelloMessage>(); // Extrae el payload de saludo
        if (hello is not null) // Si el saludo trae datos
        {
            session.Name = hello.ClientName; // Toma el nombre enviado por el cliente
            session.Version = hello.Version; // Toma la versión del cliente
        }

        Guid previousId = session.Id; // Recuerda el id provisional asignado al conectar
        if (message.SenderId != Guid.Empty && message.SenderId != previousId) // Si el cliente se identifica con otro id
        {
            session.Id = message.SenderId; // Adopta el id enviado por el cliente
            _clients.TryRemove(previousId, out _); // Elimina la entrada con el id provisional
            _clients[message.SenderId] = session; // Reinscribe al cliente con su id definitivo
        }

        session.ReadyForShow = false; // Un cliente que se saluda aún no está listo

        var welcome = MessageEnvelope.Create( // Construye el mensaje de bienvenida
            MessageType.Welcome, ServerId, ++session.NextSendSequence, // Tipo, id del servidor y secuencia
            new WelcomeMessage // Contenido del mensaje de bienvenida
            {
                ServerName = "Fedo-StageLnk Server", // Nombre del servidor
                Version = "1.0", // Versión del servidor
                AssignedClientId = session.Id, // Id asignado al cliente
                ManifestVersion = Library.Version, // Versión actual del manifiesto
                CueListVersion = Cues.Version, // Versión actual de la cue list
                MediaCount = Library.All.Count // Número de medios disponibles
            });
        await _tcp.SendAsync(session, welcome); // Envía la bienvenida por TCP

        await BroadcastManifestAsync(); // Difunde el manifiesto actual
        await BroadcastCueListAsync(); // Difunde la cue list actual

        // Informa del registro del cliente
        Log?.Invoke($"Cliente registrado: {session.Name} v{session.Version} [{session.Id}]");
        ClientConnected?.Invoke(session); // Notifica la conexión a los suscriptores
    }

    private async Task OnSyncRequestAsync(ServerClientSession session, MessageEnvelope message) // Atiende la petición de archivos faltantes
    {
        var request = message.Payload<SyncRequestMessage>(); // Extrae la petición de sincronización
        if (request is null) // Si no hay petición válida
            return; // Sale sin hacer nada

        var missing = request.MissingMediaIds; // Ids de los medios que faltan al cliente
        // Informa de los archivos solicitados por el cliente
        Log?.Invoke($"Cliente {session.Name}: solicita {missing.Count} archivos faltantes");

        foreach (var mediaId in missing) // Recorre los medios solicitados
        {
            var asset = Library.Find(mediaId); // Busca el medio en la biblioteca
            if (asset is null) // Si el medio no existe
                continue; // Salta al siguiente

            string path = Library.ResolveLocalPath(asset, LibraryRoot); // Resuelve la ruta física del archivo
            if (!File.Exists(path)) // Si el archivo no está en disco
                continue; // Salta al siguiente

            var chunks = Chunk(path, ProtocolDefaults.ChunkSize); // Divide el archivo en trozos
            foreach (var chunk in chunks) // Recorre los trozos del archivo
            {
                session.NextSendSequence++; // Incrementa la secuencia del cliente
                var chunkMessage = MessageEnvelope.Create( // Construye el mensaje de trozo
                    MessageType.SyncChunk, ServerId, session.NextSendSequence, // Tipo, id del servidor y secuencia
                    new SyncChunkMessage // Contenido del trozo
                    {
                        MediaId = mediaId, // Id del medio al que pertenece
                        Offset = chunk.Offset, // Desplazamiento dentro del archivo
                        TotalSize = chunk.Total, // Tamaño total del archivo
                        ChunkBase64 = Convert.ToBase64String(chunk.Data), // Datos del trozo en Base64
                        IsLast = chunk.IsLast // Indica si es el último trozo
                    });
                await _tcp.SendAsync(session, chunkMessage); // Envía el trozo por TCP
            }

            // Informa del envío completo del archivo
            Log?.Invoke($"Enviado '{asset.Name}' ({asset.Size} bytes) a {session.Name}");
        }
    }

    private async Task OnSyncCompleteAsync(ServerClientSession session, MessageEnvelope message) // Procesa la confirmación de sincronización
    {
        var done = message.Payload<SyncCompleteMessage>(); // Extrae la confirmación del cliente
        if (done is null) // Si no hay datos
            return; // Sale sin hacer nada

        if (done.Verified) // Si el checksum del cliente fue correcto
            session.SyncedMediaIds.Add(done.MediaId); // Registra el medio como sincronizado

        // Informa del resultado del sync (OK o checksum fallido)
        Log?.Invoke($"Cliente {session.Name}: '{done.MediaId[..8]}' sync {(done.Verified ? "OK" : "FALLÓ checksum")}");
        // Responde con un Pong para confirmar la recepción
        await _tcp.SendAsync(session, MessageEnvelope.Create(MessageType.Pong, ServerId, ++session.NextSendSequence));
    }

    private void OnReadyAsync(ServerClientSession session, MessageEnvelope message) // Procesa el estado de preparación del cliente
    {
        var ready = message.Payload<ReadyMessage>(); // Extrae el mensaje de preparación
        session.ReadyForShow = ready?.ReadyForShow ?? false; // Aplica el valor recibido (false si no llega)
        // Informa del estado de preparación del cliente
        Log?.Invoke($"Cliente {session.Name}: {(session.ReadyForShow ? "READY para show" : "no listo")}");
        if (session.ReadyForShow) // Si el cliente quedó listo
            ClientReady?.Invoke(session); // Notifica la preparación a los suscriptores
    }

    private async void OnUdpDatagram(Guid senderId, IPEndPoint endpoint, MessageEnvelope message) // Procesa los datagramas UDP entrantes
    {
        if (senderId == Guid.Empty) // Si el remitente no se identifica
            return; // Ignora el datagrama

        if (!_clients.TryGetValue(senderId, out var session)) // Si el remitente no es un cliente registrado
            return; // Ignora el datagrama

        session.Touch(); // Actualiza el último contacto del cliente
        _udp.Register(senderId, endpoint); // Asocia el endpoint UDP al cliente
        if (_inactiveClients.Remove(session.Id)) // Si el cliente estaba inactivo y se recupera
            // Informa de la recuperación del heartbeat
            Log?.Invoke($"Cliente {session.Name}: heartbeat restablecido");

        switch (message.Type) // Despacha según el tipo de mensaje UDP
        {
            case MessageType.Heartbeat: // Latido de vida
                break; // Solo refresca el contacto
            case MessageType.Status: // Reporte de telemetría
                var report = message.Payload<ClientStatusReport>(); // Extrae el reporte de estado
                if (report is not null) // Si hay reporte
                {
                    session.LastReport = report; // Guarda el último reporte
                    ClientStatusUpdated?.Invoke(session, report); // Notifica el reporte a los suscriptores
                }
                break; // Sale del switch
            case MessageType.Ping: // Comprobación de latencia
                // Responde con un Pong por UDP
                await _udp.SendToAsync(senderId, MessageEnvelope.Create(MessageType.Pong, ServerId, ++session.NextSendSequence));
                break; // Sale del switch
        }
    }

    /// <summary>Envía una orden UDP a un único cliente (comandos per-client).</summary>
    public async Task SendCommandToAsync(Guid clientId, MessageEnvelope envelope) // Envía un comando UDP a un cliente concreto
    {
        if (_clients.TryGetValue(clientId, out var session)) // Si el cliente existe
        {
            session.NextSendSequence++; // Incrementa la secuencia del cliente
            // Envía el sobre reetiquetado con la secuencia y el id del servidor
            await _udp.SendToAsync(clientId, envelope with { Sequence = session.NextSendSequence, SenderId = ServerId });
        }
    }

    /// <summary>Difunde una orden UDP a todos los clientes conectados.</summary>
    public async Task BroadcastCommandAsync(MessageEnvelope envelope) // Difunde un comando UDP a todos los clientes
    {
        var tagged = envelope with { SenderId = ServerId }; // Reetiqueta el sobre con el id del servidor
        await _udp.BroadcastAsync(tagged); // Envía el sobre por broadcast UDP
    }

    public int CurrentCueNumber { get; private set; } // Número de la cue en reproducción (base de next/prev)

    /// <summary>
    /// Ordena reproducir una cue a todos los clientes. Actualiza el número de cue
    /// actual del servidor, base de la navegación next/prev.
    /// </summary>
    public async Task PlayCueAsync(int cueNumber, DateTimeOffset? scheduled = null, double speed = 1.0) // Ordena reproducir una cue a todos los clientes
    {
        var cmd = new PlayCueCommand { CueNumber = cueNumber, ScheduledAtUtc = scheduled, Speed = speed }; // Construye el comando de reproducción
        // Informa del play de la cue
        Log?.Invoke($"PLAY cue {cueNumber:000}");
        CurrentCueNumber = cueNumber; // Actualiza la cue en reproducción
        // Difunde el comando de play por UDP
        await BroadcastCommandAsync(MessageEnvelope.Create(MessageType.PlayCue, ServerId, DateTime.UtcNow.Ticks, cmd));
    }

    /// <summary>Ordena reproducir una cue solo a un cliente concreto (por nombre).</summary>
    public async Task PlayCueToAsync(string clientName, int cueNumber) // Reproduce una cue solo en el cliente que coincida por nombre
    {
        var session = _clients.Values.FirstOrDefault(c => // Busca el primer cliente cuyo nombre coincida
            string.Equals(c.Name, clientName, StringComparison.OrdinalIgnoreCase) || // Coincidencia exacta ignorando mayúsculas
            c.Name.Contains(clientName, StringComparison.OrdinalIgnoreCase)); // O coincidencia parcial
        if (session is null) // Si no se encontró cliente
        {
            // Informa de que no existe el cliente buscado
            Log?.Invoke($"PLAYTO: no se encontró cliente '{clientName}'");
            return; // Sale sin enviar
        }

        var cmd = new PlayCueCommand { CueNumber = cueNumber }; // Construye el comando de reproducción
        // Informa del envío dirigido al cliente
        Log?.Invoke($"PLAY cue {cueNumber:000} SOLO a {session.Name}");
        // Envía el comando solo a ese cliente
        await SendCommandToAsync(session.Id, MessageEnvelope.Create(MessageType.PlayCue, ServerId, DateTime.UtcNow.Ticks, cmd));
    }

    /// <summary>Avanza a la siguiente cue de la lista (navegación secuencial).</summary>
    public async Task NextAsync() // Navega a la cue siguiente
    {
        var next = Cues.NextAfter(CurrentCueNumber); // Busca la cue siguiente a la actual
        if (next is null) // Si no hay cue siguiente
        {
            Log?.Invoke("NEXT: no hay cue siguiente"); // Informa de que no hay siguiente
            return; // Sale sin reproducir
        }
        // Informa de la navegación a la cue siguiente
        Log?.Invoke($"NEXT -> cue {next.Number:000} '{next.Name}'");
        await PlayCueAsync(next.Number); // Reproduce la cue encontrada
    }

    /// <summary>Retrocede a la cue anterior de la lista (navegación secuencial).</summary>
    public async Task PreviousAsync() // Navega a la cue anterior
    {
        var previous = Cues.PreviousBefore(CurrentCueNumber); // Busca la cue anterior a la actual
        if (previous is null) // Si no hay cue anterior
        {
            Log?.Invoke("PREV: no hay cue anterior"); // Informa de que no hay anterior
            return; // Sale sin reproducir
        }
        // Informa de la navegación a la cue anterior
        Log?.Invoke($"PREV -> cue {previous.Number:000} '{previous.Name}'");
        await PlayCueAsync(previous.Number); // Reproduce la cue encontrada
    }

    public Task StopAsync() // Ordena detener la reproducción
    {
        Log?.Invoke("STOP"); // Informa del stop
        return BroadcastCommandAsync(MessageEnvelope.Create(MessageType.StopCue, ServerId, DateTime.UtcNow.Ticks)); // Difunde el comando de stop
    }

    public Task PauseAsync() // Ordena pausar la reproducción
    {
        Log?.Invoke("PAUSA"); // Informa de la pausa
        return BroadcastCommandAsync(MessageEnvelope.Create(MessageType.Pause, ServerId, DateTime.UtcNow.Ticks)); // Difunde el comando de pausa
    }

    public Task ResumeAsync() // Ordena reanudar la reproducción
    {
        Log?.Invoke("REANUDAR"); // Informa de la reanudación
        return BroadcastCommandAsync(MessageEnvelope.Create(MessageType.Resume, ServerId, DateTime.UtcNow.Ticks)); // Difunde el comando de reanudar
    }

    public Task BlackOutAsync() // Ordena apagar la salida (blackout)
    {
        Log?.Invoke("BLACK"); // Informa del blackout
        return BroadcastCommandAsync(MessageEnvelope.Create(MessageType.BlackOut, ServerId, DateTime.UtcNow.Ticks)); // Difunde el comando de blackout
    }

    public Task FadeAsync(double seconds, bool toBlack) // Ordena un fundido con duración y dirección
    {
        var cmd = new FadeCommand { Seconds = seconds, ToBlack = toBlack }; // Construye el comando de fade
        // Informa del fundido con su dirección y duración
        Log?.Invoke($"FADE {(toBlack ? "a negro" : "desde negro")} {seconds:F1}s");
        return BroadcastCommandAsync(MessageEnvelope.Create(MessageType.Fade, ServerId, DateTime.UtcNow.Ticks, cmd)); // Difunde el comando de fade
    }

    /// <summary>
    /// Envía un cartel de texto 3D (anuncio) a todos los clientes para que lo muestren
    /// en pantalla. El color se expresa como "#RRGGBB".
    /// </summary>
    public Task BroadcastTextAsync(string text, int fontSize = 96, string color = "#FFD700", string background = "#0A1A2A") // Difunde un cartel de texto a todos los clientes
    {
        var cmd = new TextMessage { Text = text, FontSize = fontSize, Color = color, Background = background }; // Construye el mensaje de texto
        // Informa del texto difundido
        Log?.Invoke($"TEXTO: '{text}'");
        return BroadcastCommandAsync(MessageEnvelope.Create(MessageType.ShowText, ServerId, DateTime.UtcNow.Ticks, cmd)); // Difunde el comando de mostrar texto
    }

    public Task FreezeAsync() // Ordena congelar la imagen
    {
        Log?.Invoke("FREEZE"); // Informa del freeze
        return BroadcastCommandAsync(MessageEnvelope.Create(MessageType.Freeze, ServerId, DateTime.UtcNow.Ticks)); // Difunde el comando de congelar
    }

    /// <summary>Divide un archivo en trozos de tamaño fijo para su transferencia.</summary>
    private static IEnumerable<(long Offset, long Total, bool IsLast, byte[] Data)> Chunk(string path, int chunkSize) // Genera los trozos del archivo
    {
        long total = new FileInfo(path).Length; // Tamaño total del archivo en bytes
        long offset = 0; // Desplazamiento acumulado de lectura
        using var stream = File.OpenRead(path); // Abre el archivo en modo lectura
        var buffer = new byte[chunkSize]; // Buffer temporal del tamaño de cada trozo
        while (true) // Bucle infinito hasta agotar el archivo
        {
            int read = stream.Read(buffer, 0, buffer.Length); // Lee un bloque del archivo
            if (read == 0) // Si no quedan más bytes
                yield break; // Termina la generación de trozos

            // Usa el buffer completo o solo la porción leída en el último trozo
            var data = read == buffer.Length ? buffer : buffer[..read];
            offset += read; // Acumula los bytes leídos
            // Devuelve el trozo con su offset, el total, la marca de último y los datos
            yield return (offset - read, total, offset >= total, data);
        }
    }

    private async Task MonitorAsync(CancellationToken ct) // Bucle de monitorización de heartbeats
    {
        while (!ct.IsCancellationRequested) // Mientras no se cancele el token
        {
            await Task.Delay(ProtocolDefaults.HeartbeatIntervalMs, ct); // Espera el intervalo de heartbeat
            var now = DateTimeOffset.UtcNow; // Marca de tiempo actual
            foreach (var session in _clients.Values) // Recorre los clientes conectados
            {
                // Si ha pasado más tiempo que el timeout sin recibir heartbeat
                if (now - session.LastSeenUtc > TimeSpan.FromMilliseconds(ProtocolDefaults.HeartbeatTimeoutMs))
                {
                    if (_inactiveClients.Add(session.Id)) // Si aún no estaba marcado inactivo
                        // Informa del cliente marcado como inactivo
                        Log?.Invoke($"Cliente {session.Name}: sin heartbeat, marcando inactivo");
                }
                else // Si el cliente sigue dentro del tiempo de vida
                {
                    _inactiveClients.Remove(session.Id); // Lo quita de la lista de inactivos
                }
            }
        }
    }

    public async ValueTask DisposeAsync() // Libera los recursos del servidor de forma asíncrona
    {
        _cts.Cancel(); // Cancela las tareas internas
        await _tcp.DisposeAsync(); // Detiene y libera el servidor TCP
        await _udp.DisposeAsync(); // Detiene y libera el servidor UDP
        await _http.DisposeAsync(); // Detiene y libera el servidor HTTP de streaming
        _cts.Dispose(); // Libera el token de cancelación
    }
}
