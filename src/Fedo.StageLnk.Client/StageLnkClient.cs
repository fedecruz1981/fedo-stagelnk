// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: StageLnkClient
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol; // Importa los tipos del protocolo (mensajes, comandos y constantes).

namespace Fedo.StageLnk.Client; // Declara el espacio de nombres del cliente de show.

/// <summary>
/// Cliente de show: se conecta al servidor, sincroniza la biblioteca de medios
/// por TCP, reporta telemetría por UDP y ejecuta las órdenes de reproducción
/// sobre el motor (híbrido por defecto en Windows). Expone la cue list local,
/// los medios en disco y el control directo de reproducción para la consola.
/// </summary>
public sealed class StageLnkClient : IAsyncDisposable // Clase sellada del cliente; implementa la liberación asíncrona de recursos.
{
    public Guid ClientId { get; } // Identificador único de sesión del cliente.
    public string Name { get; } // Nombre lógico del cliente.

    private readonly MediaStore _store; // Almacén local de medios en disco.
    private readonly IPlaybackEngine _engine; // Motor de reproducción (híbrido por defecto en Windows).
    private readonly ClientConnection _connection; // Conexión TCP/UDP con el servidor.
    private readonly SystemMetrics _metrics = new(); // Métricas del sistema (CPU, GPU, RAM, temperatura).
    private readonly TextSignRenderer _textSign = new(); // Renderizador de carteles de texto 3D vía ffmpeg.

    private readonly Dictionary<int, Cue> _cueList = new(); // Cue list local indexada por número de cue.
    private readonly Dictionary<string, FileReceiver> _receivers = new(); // Receptores de archivos en curso de sincronización.
    private readonly HashSet<string> _pendingSync = new(StringComparer.OrdinalIgnoreCase); // IDs de medios pendientes de sincronizar (sin distinguir mayúsculas).

    private long _manifestVersion = -1; // Versión del último manifiesto recibido (-1 = aún no recibido).
    private long _cueListVersion = -1; // Versión de la última cue list recibida (-1 = aún no recibida).
    private int _lastCueNumber; // Número de la última cue reproducida.
    private ClientStatus _status = ClientStatus.Connecting; // Estado actual del cliente (empieza conectándose).

    private HttpFileDownloader? _httpSync; // Descargador HTTP de la sincronización (solo si HttpBaseUrl está fijado).
    private Task? _syncHttpTask; // Tarea de sincronización HTTP en curso (fire-and-forget con guarda).

    private readonly CancellationTokenSource _lifetimeCts = new(); // CTS que cancela todos los bucles al cerrar el cliente.
    private Task? _reconnectTask; // Tarea del bucle de reconexión en curso.
    private TaskCompletionSource? _disconnectTcs; // TCS que se resuelve cuando la conexión cae.
    private bool _disposed; // Marca de que el cliente ya fue liberado.

    public event Action<string>? Log; // Evento de log (mensajes de texto).
    public event Action<WelcomeMessage>? WelcomeReceived; // Evento al recibir el saludo del servidor.
    public event Action<Cue>? CueReceived; // Evento al reproducirse una cue.
    public event Action? SyncedAndReady; // Evento cuando termina la sincronización y se está listo.
    public event Action<int, IReadOnlyList<Cue>>? CueListReceived; // Evento al recibir la cue list completa.
    public event Action<MediaManifestMessage>? ManifestReceived; // Evento al recibir el manifiesto de medios.
    public event Action<ClientStatus>? StatusChanged; // Evento al cambiar el estado del cliente.

    public ClientStatus Status // Estado actual del cliente, expuesto públicamente.
    {
        get => _status; // Devuelve el estado interno.
        private set // Asignación privada: solo el propio cliente lo modifica.
        {
            if (_status != value) // Si el estado realmente cambió...
            {
                _status = value; // ...actualiza el estado interno.
                StatusChanged?.Invoke(value); // ...y notifica el cambio a los suscriptores.
            }
        }
    }

    public IReadOnlyList<Cue> CueList => _cueList.Values.OrderBy(c => c.Number).ToList(); // Cue list local ordenada por número de cue.

    public string MediaRoot => _store.Root; // Ruta raíz donde se guardan los medios.

    public int LastCueNumber => _lastCueNumber; // Última cue reproducida.

    public string EngineName => _engine.Name; // Nombre del motor de reproducción en uso.

    public double EngineFps => _engine.MeasuredFps; // FPS medidos por el motor.

    /// <summary>
    /// URL base del servidor HTTP de streaming (p. ej. http://192.168.1.10:9003).
    /// Si se fija antes de conectar, la sincronización de medios usa descarga HTTP
    /// con Range en paralelo (bytes crudos, sin base64) en lugar del canal TCP de
    /// trozos. Si queda null, se usa el canal TCP tradicional como respaldo.
    /// </summary>
    public string? HttpBaseUrl { get; set; } // URL base HTTP para la sincronización de medios

    /// <summary>Medios conocidos del manifiesto que están en disco + archivos locales fuera del manifiesto.</summary>
    public IReadOnlyList<MediaAsset> ListLocalMedia() // Devuelve los medios locales: del manifiesto que están en disco más los archivos locales fuera del manifiesto.
    {
        // Medios del manifiesto presentes en disco, indexados por ruta relativa.
        var known = (_lastManifest?.Media ?? new List<MediaAsset>())
            .Where(_store.Has) // Filtra solo los que existen en disco.
            .ToDictionary(m => m.RelativePath, StringComparer.OrdinalIgnoreCase); // Índice por ruta relativa sin distinguir mayúsculas.
        foreach (var local in _store.ListLocal()) // Recorre los archivos locales fuera del manifiesto...
            known.TryAdd(local.RelativePath, local); // ...y los añade si no existían ya en el índice.
        return known.Values.OrderBy(m => m.RelativePath).ToList(); // Devuelve todos los medios ordenados por ruta relativa.
    }

    public void PlayLocalFile(string pathOrName, double speed = 1.0) // Reproduce un archivo local por ruta o nombre, con velocidad opcional.
    {
        string absolute; // Ruta absoluta resuelta del archivo.
        if (Path.IsPathRooted(pathOrName)) // Si la ruta indicada ya es absoluta...
        {
            absolute = pathOrName; // ...se usa tal cual.
        }
        else // En caso contrario...
        {
            // Busca el medio local por nombre o por ruta relativa.
            var asset = ListLocalMedia().FirstOrDefault(m =>
                string.Equals(m.Name, pathOrName, StringComparison.OrdinalIgnoreCase) // Coincidencia por nombre (sin distinguir mayúsculas).
                || string.Equals(m.RelativePath, pathOrName, StringComparison.OrdinalIgnoreCase)); // O por ruta relativa (sin distinguir mayúsculas).
            if (asset is null) // Si no se encontró el medio...
            {
                // Informa que el archivo no está en la biblioteca local.
                Log?.Invoke($"Archivo local no encontrado: {pathOrName}");
                return; // Aborta la reproducción.
            }
            absolute = Path.Combine(MediaRoot, asset.RelativePath); // Construye la ruta absoluta dentro de la carpeta de medios.
        }

        if (!File.Exists(absolute)) // Si el archivo no existe físicamente en disco...
        {
            // Informa que el archivo físico falta.
            Log?.Invoke($"Archivo no encontrado: {absolute}");
            return; // Aborta la reproducción.
        }

        MediaKind? kind = MediaAsset.KindForExtension(Path.GetExtension(absolute)); // Obtiene el tipo de medio según la extensión del archivo.
        if (kind is null) // Si la extensión no tiene tipo de medio soportado...
        {
            // Informa que la extensión no es soportada.
            Log?.Invoke($"Tipo de archivo no soportado: {Path.GetExtension(absolute)}");
            return; // Aborta la reproducción.
        }

        _engine.PlayFile(absolute, kind.Value, speed); // Ordena al motor reproducir el archivo.
        Status = ClientStatus.Playing; // Marca al cliente como en reproducción.
    }

    /// <summary>
    /// Genera un cartel de texto 3D (anuncio) con ffmpeg y lo muestra en pantalla.
    /// El color se expresa como "#RRGGBB"; la duración queda a cargo de quien emite
    /// el anuncio (stop/black/fade lo quitan de pantalla).
    /// </summary>
    // Genera y muestra un cartel de texto 3D; el color se expresa como "#RRGGBB".
    public Task ShowTextAsync(string text, int fontSize = 96, string color = "#FFD700", string backgroundColor = "#0A1A2A")
    {
        try // Intenta renderizar y mostrar el cartel...
        {
            string png = _textSign.Render(text, color, backgroundColor, fontSize, _store.Root); // Renderiza el cartel a PNG dentro de la carpeta de medios.
            _engine.PlayFile(png, MediaKind.Image); // Reproduce la imagen generada en pantalla.
            Status = ClientStatus.Playing; // Marca al cliente como en reproducción.
            // Informa del cartel mostrado.
            Log?.Invoke($"Cartel mostrado: '{text}'");
        }
        catch (Exception ex) // Captura errores de renderizado o de reproducción...
        {
            // Informa del fallo al mostrar el cartel.
            Log?.Invoke($"No se pudo mostrar el cartel: {ex.Message}");
        }
        return Task.CompletedTask; // Devuelve tarea ya completada (el renderizado es síncrono).
    }

    public void Stop() { _engine.Stop(); Status = ClientStatus.Stopped; } // Detiene la reproducción y marca estado detenido.
    public void Pause() { _engine.Pause(); Status = ClientStatus.Stopped; } // Pausa la reproducción y marca estado detenido.
    public void Resume() { _engine.Resume(); Status = ClientStatus.Playing; } // Reanuda la reproducción y marca estado en reproducción.
    public void Freeze() { _engine.Freeze(); Status = ClientStatus.Stopped; } // Congela la imagen y marca estado detenido.
    public void Fade(double seconds, bool toBlack) => _engine.Fade(seconds, toBlack); // Fundido de salida: a negro o a transparente en tantos segundos.
    public void BlackOut() { _engine.BlackOut(); Status = ClientStatus.Stopped; } // Apaga la pantalla a negro y marca estado detenido.

    public Task NextAsync() => NavigateAsync(+1); // Navega a la siguiente cue de la lista.
    public Task PreviousAsync() => NavigateAsync(-1); // Navega a la cue anterior de la lista.

    private async Task NavigateAsync(int direction) // Navega por la cue list en la dirección indicada (+1 siguiente, -1 anterior).
    {
        var sorted = _cueList.Keys.OrderBy(k => k).ToList(); // Números de cue ordenados ascendentemente.
        if (sorted.Count == 0) // Si la cue list está vacía...
        {
            Log?.Invoke("Sin cues en la lista local"); // Informa que no hay cues.
            return; // Aborta la navegación.
        }

        // Busca la cue destino según la dirección de navegación...
        int target = direction > 0
            ? sorted.FirstOrDefault(k => k > _lastCueNumber) // ...la primera cue mayor que la actual (siguiente).
            : sorted.LastOrDefault(k => k < _lastCueNumber); // ...o la última cue menor que la actual (anterior).

        if (target == 0) // Si no hay destino válido (se salió del rango)...
            target = direction > 0 ? sorted[0] : sorted[^1]; // ...se envuelve al primero o al último según la dirección.

        await PlayCueAsync(target); // Reproduce la cue destino.
    }

    /// <summary>Reproduce una cue localmente (usado por la consola del cliente).</summary>
    public Task PlayCueAsync(int cueNumber, double speed = 1.0, DateTimeOffset? scheduledAtUtc = null) // Reproduce una cue localmente con velocidad y horario programado opcionales.
    {
        if (!_cueList.TryGetValue(cueNumber, out var cue)) // Si la cue no existe en la lista local...
        {
            // Informa que la cue no existe.
            Log?.Invoke($"Cue {cueNumber:000} no existe en la cue list local");
            return Task.CompletedTask; // Devuelve tarea completada sin reproducir.
        }

        _lastCueNumber = cueNumber; // Registra la cue como la última reproducida.
        Status = ClientStatus.Playing; // Marca al cliente como en reproducción.

        _engine.PlayCue(cue, _store.Root); // Notifica a la consola del motor la cue activa.

        bool played = false; // Indica si al menos una capa llegó a reproducirse.
        foreach (var layer in cue.Layers.Where(l => l.MediaId is not null)) // Recorre las capas de la cue que tienen medio vinculado...
        {
            var asset = FindAsset(layer.MediaId!); // Localiza el medio correspondiente a la capa.
            if (asset is not null) // Si el medio existe...
            {
                _engine.Play(asset, speed, scheduledAtUtc); // ...se reproduce con la velocidad y el horario indicados.
                played = true; // Marca que sí se reprodujo algo.
            }
        }

        if (!played && cue.Layers.Count == 0) // Si no se reprodujo nada y la cue no tiene capas...
            TryPlayCueFallback(cue, speed, scheduledAtUtc); // ...se intenta el plan B por coincidencia de nombre/tipo.

        CueReceived?.Invoke(cue); // Notifica que la cue se reprodujo.
        return Task.CompletedTask; // Tarea completada.
    }

    /// <summary>
    /// Cues sin capas (proyectos viejos o creadas sin vincular medio): si el tipo
    /// de la cue tiene equivalente en la biblioteca, reproduce el medio cuyo
    /// nombre coincide con la cue (o el primero de ese tipo).
    /// </summary>
    private void TryPlayCueFallback(Cue cue, double speed, DateTimeOffset? scheduledAtUtc) // Reproduce por coincidencia un medio para cues sin capas.
    {
        // Tipo de medio deseado según el tipo de la cue...
        MediaKind? wantKind = cue.Kind switch
        {
            CueKind.Audio => MediaKind.Audio, // Una cue de audio pide un medio de audio.
            CueKind.Video => MediaKind.Video, // Una cue de vídeo pide un medio de vídeo.
            CueKind.Image => MediaKind.Image, // Una cue de imagen pide un medio de imagen.
            _ => null // El resto de tipos (texto, etc.) no piden medio.
        };

        if (wantKind is null) // Si la cue no necesita un medio...
        {
            if (cue.Kind == CueKind.Text) // ...y es una cue de texto...
            {
                string signText = string.IsNullOrWhiteSpace(cue.Text) ? cue.Name : cue.Text; // Usa el texto de la cue o, si está vacío, su nombre.
                if (!string.IsNullOrWhiteSpace(signText)) // Si hay texto para mostrar...
                {
                    // Informa que se mostrará un cartel de texto.
                    Log?.Invoke($"Cue {cue.Number:000} de texto: mostrando cartel '{signText}'");
                    _ = ShowTextAsync(signText); // Dispara la generación del cartel sin esperarla (fire-and-forget).
                }
            }
            return; // Termina: no hay medio que reproducir.
        }

        // Busca en el manifiesto un medio del tipo deseado...
        var media = (_lastManifest?.Media ?? new List<MediaAsset>())
            .Where(_store.Has) // ...que exista en disco...
            .Where(m => m.Kind == wantKind.Value) // ...del tipo de medio buscado...
            .OrderByDescending(m => m.Name.Equals(cue.Name, StringComparison.OrdinalIgnoreCase)) // ...priorizando los que coinciden en nombre con la cue...
            .ThenBy(m => m.Name) // ...y ordenando el resto alfabéticamente.
            .FirstOrDefault(); // Toma el primer candidato (o null si no hay ninguno).

        if (media is null) // Si no hay ningún medio que coincida...
        {
            // Informa que no hay nada que reproducir.
            Log?.Invoke($"Cue {cue.Number:000} sin capas y sin medio {wantKind.Value} en el manifiesto; nada que reproducir");
            return; // Termina sin reproducir.
        }

        // Informa que se reproduce el medio encontrado por coincidencia de nombre.
        Log?.Invoke($"Cue {cue.Number:000} sin capas: reproduciendo '{media.Name}' ({wantKind.Value}) por coincidencia de nombre");
        _engine.Play(media, speed, scheduledAtUtc); // Reproduce el medio con la velocidad y el horario indicados.
    }

    // Constructor: configura nombre, carpeta de medios y conexión con el servidor.
    public StageLnkClient(string name, string mediaRoot, string host,
        int tcpPort = ProtocolDefaults.TcpPort, int udpPort = ProtocolDefaults.UdpPort, // Puertos TCP/UDP por defecto del protocolo.
        IPlaybackEngine? engine = null) // Motor de reproducción opcional (si es null se usa el de consola).
    {
        Name = name; // Guarda el nombre del cliente.
        ClientId = Guid.NewGuid(); // Genera un identificador único de sesión.
        _store = new MediaStore(mediaRoot); // Crea el almacén de medios sobre la carpeta raíz.
        _engine = engine ?? new ConsolePlaybackEngine(); // Usa el motor recibido o crea uno de consola.
        _connection = new ClientConnection(host, tcpPort, udpPort); // Crea la conexión TCP/UDP con el servidor.

        _engine.Log += m => Log?.Invoke(m); // Reenvía el log del motor al evento público.
        _textSign.Log += m => Log?.Invoke(m); // Reenvía el log del renderizador de carteles.

        _connection.Log += m => Log?.Invoke(m); // Reenvía el log de la conexión.
        _connection.TcpMessageReceived += OnTcpMessage; // Suscribe el manejo de mensajes TCP entrantes.
        _connection.UdpMessageReceived += OnUdpMessage; // Suscribe el manejo de mensajes UDP entrantes.
        _connection.Disconnected += OnDisconnected; // Suscribe la notificación de desconexión.
    }

    public async Task StartAsync(CancellationToken ct = default) // Arranca el cliente: inicializa el motor, conecta y lanza los bucles.
    {
        _engine.Initialize(_store.Root); // Inicializa el motor sobre la carpeta de medios.
        await _connection.ConnectAsync(ct); // Conecta TCP/UDP con el servidor.
        await SendHelloAsync(); // Envía el saludo (Hello) al servidor.

        _ = ReportLoopAsync(_lifetimeCts.Token); // Lanza el bucle de reportes de telemetría (fire-and-forget).
        _reconnectTask = ReconnectLoopAsync(_lifetimeCts.Token); // Lanza el bucle de reconexión y lo guarda para poder esperarlo.
    }

    private async Task SendHelloAsync() // Envía el mensaje Hello con los datos de identidad del cliente.
    {
        // Construye el sobre del mensaje Hello...
        var hello = MessageEnvelope.Create(MessageType.Hello, ClientId, 1,
            new HelloMessage { ClientName = Name, Version = "1.0", FreeDiskMb = FreeDiskMb }); // ...con nombre, versión y espacio libre en disco.
        await _connection.SendTcpAsync(hello); // Envía el Hello por TCP.
    }

    private void OnDisconnected() // Maneja la caída de la conexión.
    {
        if (_disposed) // Si el cliente ya fue liberado...
            return; // ...se ignora la desconexión.
        Status = ClientStatus.Disconnected; // Actualiza el estado a desconectado.
        _disconnectTcs?.TrySetResult(); // Despierta el bucle de reconexión.
    }

    /// <summary>
    /// Bucle de reconexión automática: espera a que el servidor caiga y vuelve a
    /// conectar TCP/UDP, reenvía el Hello y deja que la sincronización de medios
    /// continúe (el manifiesto que reenvía el servidor rellena lo que falte).
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct) // Bucle que detecta la caída y reconecta automáticamente.
    {
        while (!ct.IsCancellationRequested) // Repite mientras no se cancele el cliente...
        {
            var tcs = new TaskCompletionSource(); // TCS que se resuelve al desconectarse.
            _disconnectTcs = tcs; // Lo expone para que OnDisconnected lo despierte.
            try // Espera a la desconexión o a la cancelación...
            {
                await tcs.Task.WaitAsync(ct); // ...bloqueando el bucle hasta que caiga la conexión.
            }
            catch (OperationCanceledException) // Si se cancela el cliente...
            {
                break; // ...sale del bucle.
            }

            Log?.Invoke("Conexión perdida. Reconectando..."); // Informa que se perdió la conexión y se reconecta.
            try // Intenta reconectar y resincronizar...
            {
                await _connection.ConnectAsync(ct); // ...reestableciendo la conexión TCP/UDP.
                _pendingSync.Clear(); // Vacía la lista de sincronizaciones pendientes.
                foreach (var receiver in _receivers.Values) // Descarta los receptores a medio terminar...
                    receiver.Dispose(); // ...liberando sus streams para permitir reanudar el .part.
                _receivers.Clear(); // Vacía el diccionario de receptores.
                await SendHelloAsync(); // Reenvía el Hello para reiniciar el protocolo.
                Log?.Invoke("Reconectado; re-sincronizando con el servidor..."); // Informa de la reconexión exitosa.
            }
            catch (OperationCanceledException) // Si la reconexión se cancela...
            {
                break; // ...sale del bucle.
            }
            catch (Exception ex) // Si la reconexión falla...
            {
                // Informa del fallo y del reintento en 3 segundos.
                Log?.Invoke($"Reconexión falló: {ex.Message}. Reintentando en 3 s...");
                try // Espera antes de reintentar...
                {
                    await Task.Delay(3000, ct); // ...durante 3 segundos (cancelable).
                }
                catch (OperationCanceledException) // Si la espera se cancela...
                {
                    break; // ...sale del bucle.
                }
            }
        }
    }

    private async void OnTcpMessage(MessageEnvelope message) // Maneja los mensajes TCP entrantes del servidor.
    {
        try // Protege el procesamiento de errores inesperados...
        {
            switch (message.Type) // Enruta el mensaje según su tipo...
            {
                case MessageType.Welcome: // Saludo del servidor: guarda versiones y notifica.
                    var welcome = message.Payload<WelcomeMessage>(); // Deserializa el payload del saludo.
                    if (welcome is not null) // Si el saludo es válido...
                    {
                        _manifestVersion = welcome.ManifestVersion; // ...guarda la versión del manifiesto del servidor.
                        _cueListVersion = welcome.CueListVersion; // ...guarda la versión de la cue list del servidor.
                        // Informa de la bienvenida del servidor.
                        Log?.Invoke($"Bienvenido: {welcome.ServerName} v{welcome.Version} ({welcome.MediaCount} medios)");
                        WelcomeReceived?.Invoke(welcome); // Notifica a los suscriptores del saludo.
                    }
                    break; // Termina el caso Welcome.

                case MessageType.MediaManifest: // Manifiesto de medios: se procesa aparte.
                    await OnManifestAsync(message); // Procesa el manifiesto (dispara la sincronización).
                    break; // Termina el caso MediaManifest.

                case MessageType.CueList: // Cue list: reemplaza la lista local.
                    var cueMessage = message.Payload<CueListMessage>(); // Deserializa el payload de la cue list.
                    if (cueMessage is not null) // Si la cue list es válida...
                    {
                        _cueList.Clear(); // ...vacía la cue list local.
                        foreach (var cue in cueMessage.Cues) // Recorre las cues recibidas...
                            _cueList[cue.Number] = cue; // ...y las indexa por número.
                        _cueListVersion = cueMessage.Version; // Guarda la versión recibida.
                        // Informa cuántas cues se recibieron.
                        Log?.Invoke($"Cue list recibida: {cueMessage.Cues.Count} cues");
                        CueListReceived?.Invoke((int)_cueListVersion, CueList); // Notifica a los suscriptores con la lista nueva.
                    }
                    break; // Termina el caso CueList.

                case MessageType.SyncChunk: // Fragmento de sincronización de un archivo.
                    await OnSyncChunkAsync(message); // Procesa el fragmento recibido.
                    break; // Termina el caso SyncChunk.
            }
        }
        catch (Exception ex) // Captura cualquier error al procesar el mensaje...
        {
            // Informa del error al procesar el mensaje.
            Log?.Invoke($"Error procesando {message.Type}: {ex.Message}");
        }
    }

    private async Task OnManifestAsync(MessageEnvelope message) // Procesa el manifiesto de medios del servidor.
    {
        var manifest = message.Payload<MediaManifestMessage>(); // Deserializa el payload del manifiesto.
        if (manifest is null) // Si el manifiesto no es válido...
            return; // ...se ignora.

        _manifestVersion = manifest.Version; // Guarda la versión del manifiesto.
        _lastManifest = manifest; // Conserva el manifiesto para búsquedas de medios.
        ManifestReceived?.Invoke(manifest); // Notifica a los suscriptores.

        var missing = _store.Missing(manifest.Media); // Calcula qué medios del manifiesto faltan en disco.
        // Informa del resumen del manifiesto.
        Log?.Invoke($"Manifiesto v{manifest.Version}: {manifest.Media.Count} archivos, faltan {missing.Count}");

        if (missing.Count == 0) // Si no falta ningún medio...
        {
            await SendReadyAsync(); // ...se envía READY directamente.
            return; // Termina el procesamiento.
        }

        Status = ClientStatus.Syncing; // Pasa a estado de sincronización.
        _pendingSync.Clear(); // Vacía la lista de pendientes.
        _pendingSync.UnionWith(missing); // Registra los medios que faltan como pendientes.

        if (!string.IsNullOrEmpty(HttpBaseUrl)) // Si está disponible el canal HTTP de sincronización...
        {
            // Evita lanzar dos sincronizaciones HTTP a la vez.
            if (_syncHttpTask is null || _syncHttpTask.IsCompleted)
            {
                // Lanza la descarga HTTP paralela en segundo plano (fire-and-forget).
                _syncHttpTask = SyncOverHttpAsync(manifest, missing, _lifetimeCts.Token);
                // Informa del arranque de la sincronización HTTP con su paralelismo.
                Log?.Invoke($"Sincronización por HTTP: {missing.Count} archivos (paralelismo {ProtocolDefaults.MaxParallelDownloads})");
            }
        }
        else // Sin HTTP disponible se usa el canal TCP de trozos...
        {
            // Construye la petición de sincronización...
            var request = MessageEnvelope.Create(MessageType.SyncRequest, ClientId, DateTime.UtcNow.Ticks,
                new SyncRequestMessage { MissingMediaIds = missing }); // ...con la lista de medios que faltan.
            await _connection.SendTcpAsync(request); // Envía la petición por TCP.
            // Informa que se solicitó la sincronización.
            Log?.Invoke($"Solicitando sincronización de {missing.Count} archivos");
        }

        // READY no bloqueante: el cliente puede empezar el show aunque falten
        // archivos (los no locales se reproducen por streaming HTTP mientras la
        // descarga continúa en segundo plano). El estado real de la descarga se
        // sigue reportando en el estado "syncing N".
        await SendReadyAsync();
    }

    /// <summary>
    /// Descarga por HTTP los medios que faltan, en paralelo, escribiendo cada
    /// archivo en su temporal (reanudando el .part si existe) y verificando el
    /// SHA-256 antes de moverlo a su destino final. Cada archivo se confirma al
    /// servidor con SyncComplete para que la telemetría muestre el progreso.
    /// </summary>
    private async Task SyncOverHttpAsync(MediaManifestMessage manifest, List<string> missing, CancellationToken ct)
    {
        try // Protege la sincronización de errores inesperados...
        {
            // Crea el descargador HTTP la primera vez que se usa.
            if (_httpSync is null)
            {
                _httpSync = new HttpFileDownloader(HttpBaseUrl!); // ...apuntando a la URL base del servidor.
                _httpSync.Log += m => Log?.Invoke(m); // Reenvía el log del descargador al evento público.
            }

            // Medios del manifiesto que faltan, indexados por ID para no repetir trabajo.
            var toDownload = manifest.Media
                .Where(m => missing.Contains(m.Id, StringComparer.OrdinalIgnoreCase)) // Filtra los que faltan...
                .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase); // ...e indexa por ID sin distinguir mayúsculas.

            // Descarga en paralelo con el paralelismo configurado del protocolo.
            await Parallel.ForEachAsync(toDownload.Values, new ParallelOptions
            {
                MaxDegreeOfParallelism = ProtocolDefaults.MaxParallelDownloads, // Límite de descargas simultáneas.
                CancellationToken = ct // Permite cancelar la sincronización al cerrar.
            }, async (asset, token) =>
            {
                try // Protege la descarga de cada archivo...
                {
                    bool ok = await _httpSync!.DownloadAsync(asset, _store.TempPathFor(asset.Id), token); // Descarga/reanuda y verifica el checksum.
                    if (ok) // Si la verificación fue correcta...
                        _store.Finalize(asset); // ...mueve el temporal a su destino definitivo.
                    // Construye la confirmación de sincronización del medio...
                    var complete = MessageEnvelope.Create(MessageType.SyncComplete, ClientId, DateTime.UtcNow.Ticks,
                        new SyncCompleteMessage { MediaId = asset.Id, Sha256 = asset.Sha256, Verified = ok }); // ...con ID, hash y resultado.
                    await _connection.SendTcpAsync(complete); // Envía la confirmación al servidor por TCP.
                    Log?.Invoke($"HTTP sync: '{asset.Name}' {(ok ? "OK" : "FALLÓ checksum")} ({asset.Size} bytes)"); // Informa del resultado del archivo.
                }
                catch (Exception ex) // Si falla la descarga de un archivo...
                {
                    Log?.Invoke($"HTTP sync: error en '{asset.Name}': {ex.Message}"); // ...informa del error...
                    await _connection.SendTcpAsync(MessageEnvelope.Create(MessageType.SyncComplete, ClientId, DateTime.UtcNow.Ticks,
                        new SyncCompleteMessage { MediaId = asset.Id, Sha256 = asset.Sha256, Verified = false })); // ...y notifica el fallo al servidor.
                }
                finally // En cualquier caso...
                {
                    _pendingSync.Remove(asset.Id); // ...quita el medio de la lista de pendientes.
                }
            });
            Log?.Invoke($"Sincronización por HTTP finalizada ({_pendingSync.Count} pendientes)"); // Informa del final de la sincronización HTTP.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) // Si se cancela...
        {
            Log?.Invoke("Sincronización por HTTP cancelada"); // ...informa de la cancelación.
        }
        catch (Exception ex) // Si falla la sincronización completa...
        {
            Log?.Invoke($"Sincronización por HTTP falló: {ex.Message}"); // ...informa del fallo.
        }
    }

    private async Task OnSyncChunkAsync(MessageEnvelope message) // Procesa un fragmento de archivo recibido del servidor.
    {
        var chunk = message.Payload<SyncChunkMessage>(); // Deserializa el payload del fragmento.
        if (chunk is null) // Si el fragmento no es válido...
            return; // ...se ignora.

        if (!_receivers.TryGetValue(chunk.MediaId, out var receiver)) // Si no hay receptor para ese medio...
        {
            receiver = new FileReceiver(chunk.MediaId, _store.TempPathFor(chunk.MediaId), chunk.TotalSize); // ...se crea uno nuevo con ruta temporal y tamaño total.
            _receivers[chunk.MediaId] = receiver; // Se guarda el receptor por ID de medio.
        }

        receiver.Append(Convert.FromBase64String(chunk.ChunkBase64)); // Decodifica el fragmento Base64 y lo escribe en el archivo temporal.

        if (chunk.IsLast) // Si es el último fragmento del archivo...
        {
            _receivers.Remove(chunk.MediaId); // ...se quita el receptor de la lista.
            bool sizeOk = receiver.TryComplete(out string sha256); // Cierra el archivo y calcula su SHA-256.

            var asset = FindAsset(chunk.MediaId); // Localiza el medio esperado en el manifiesto.
            bool verified = sizeOk && asset is not null && string.Equals(sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase); // Verifica el tamaño y que el hash coincida con el manifiesto.

            if (verified) // Si la verificación es correcta...
            {
                _store.Finalize(receiver, asset!); // ...se mueve el archivo temporal a su lugar definitivo.
                // Informa de la recepción correcta del archivo.
                Log?.Invoke($"Recibido '{asset!.Name}' OK ({asset.Size} bytes)");
            }
            else // Si la verificación falla...
            {
                // Informa del fallo de checksum con los hashes truncados a 8 caracteres.
                Log?.Invoke($"Checksum FALLÓ en {chunk.MediaId[..8]} (esperado {asset?.Sha256[..8]} != {sha256[..8]})");
            }

            receiver.Dispose(); // Libera los recursos del receptor.

            // Construye el mensaje de sincronización completada...
            var complete = MessageEnvelope.Create(MessageType.SyncComplete, ClientId, DateTime.UtcNow.Ticks,
                new SyncCompleteMessage { MediaId = chunk.MediaId, Sha256 = sha256, Verified = verified }); // ...con ID, hash y resultado de la verificación.
            await _connection.SendTcpAsync(complete); // Envía el resultado al servidor por TCP.

            _pendingSync.Remove(chunk.MediaId); // Quita el medio de la lista de pendientes.

            if (_pendingSync.Count == 0) // Si ya no queda nada por sincronizar...
                await SendReadyAsync(); // ...se envía READY.
        }
    }

    private MediaAsset? FindAsset(string mediaId) // Busca un medio por su ID en el último manifiesto.
        => _lastManifest?.Media.FirstOrDefault(m => string.Equals(m.Id, mediaId, StringComparison.OrdinalIgnoreCase)); // Devuelve el primer medio con ese ID (sin distinguir mayúsculas) o null.

    private MediaManifestMessage? _lastManifest; // Último manifiesto recibido (declarado aquí tras su uso).

    private async Task SendReadyAsync() // Envía el mensaje READY al terminar la sincronización.
    {
        Status = ClientStatus.Ready; // Marca al cliente como listo para el show.
        // Construye el mensaje READY...
        var ready = MessageEnvelope.Create(MessageType.Ready, ClientId, DateTime.UtcNow.Ticks,
            new ReadyMessage { SyncComplete = true, ReadyForShow = true }); // ...indicando sincronización completa y listo para show.
        await _connection.SendTcpAsync(ready); // Envía READY al servidor por TCP.
        Log?.Invoke("Sincronización completa: READY para show"); // Informa que el cliente está listo.
        SyncedAndReady?.Invoke(); // Notifica a los suscriptores del evento.
    }

    private async void OnUdpMessage(MessageEnvelope message) // Maneja los comandos UDP entrantes del servidor.
    {
        try // Protege el procesamiento de errores inesperados...
        {
            switch (message.Type) // Enruta el comando según su tipo...
            {
                case MessageType.PlayCue: // Orden de reproducir una cue.
                    var play = message.Payload<PlayCueCommand>(); // Deserializa el payload del comando.
                    if (play is not null) // Si el comando es válido...
                        await OnPlayCueAsync(play); // ...se reproduce la cue pedida.
                    break; // Termina el caso PlayCue.
                case MessageType.StopCue: // Orden de detener la reproducción.
                    Status = ClientStatus.Stopped; // Marca al cliente como detenido.
                    _engine.Stop(); // Detiene el motor.
                    break; // Termina el caso StopCue.
                case MessageType.Pause: // Orden de pausar.
                    _engine.Pause(); // Pausa el motor.
                    break; // Termina el caso Pause.
                case MessageType.Resume: // Orden de reanudar.
                    _engine.Resume(); // Reanuda el motor.
                    break; // Termina el caso Resume.
                case MessageType.BlackOut: // Orden de apagar la pantalla.
                    _engine.BlackOut(); // Apaga la salida a negro.
                    break; // Termina el caso BlackOut.
                case MessageType.Fade: // Orden de fundido.
                    var fade = message.Payload<FadeCommand>(); // Deserializa el payload del fundido.
                    if (fade is not null) // Si el comando es válido...
                        _engine.Fade(fade.Seconds, fade.ToBlack); // ...se ejecuta el fundido con duración y destino.
                    break; // Termina el caso Fade.
                case MessageType.Freeze: // Orden de congelar imagen.
                    _engine.Freeze(); // Congela la imagen actual.
                    break; // Termina el caso Freeze.
                case MessageType.Next: // Orden de siguiente.
                    _engine.Next(); // Avanza el motor a la siguiente entrada.
                    break; // Termina el caso Next.
                case MessageType.ShowText: // Orden de mostrar un cartel de texto.
                    var text = message.Payload<TextMessage>(); // Deserializa el payload del cartel.
                    if (text is not null) // Si el mensaje es válido...
                        await ShowTextAsync(text.Text, text.FontSize, text.Color, text.Background); // ...se genera y muestra el cartel.
                    break; // Termina el caso ShowText.
                case MessageType.Pong: // Respuesta al ping: no requiere acción.
                    break; // Termina el caso Pong.
            }
        }
        catch (Exception ex) // Captura cualquier error al procesar el comando...
        {
            // Informa del error en el comando recibido.
            Log?.Invoke($"Error en comando {message.Type}: {ex.Message}");
        }
    }

    private Task OnPlayCueAsync(PlayCueCommand play) // Delegado que reproduce la cue indicada por el comando UDP.
        => PlayCueAsync(play.CueNumber, play.Speed, play.ScheduledAtUtc); // Llama a PlayCueAsync con los parámetros del comando.

    private async Task ReportLoopAsync(CancellationToken ct) // Bucle que envía telemetría periódica al servidor.
    {
        try // Protege el bucle de errores transitorios...
        {
            while (!ct.IsCancellationRequested) // Repite mientras no se cancele el cliente...
            {
                await _connection.SendUdpAsync(MessageEnvelope.Create(MessageType.Heartbeat, ClientId, DateTime.UtcNow.Ticks)); // Envía un heartbeat para mantener viva la conexión.

                // Construye el reporte de estado del cliente...
                var report = new ClientStatusReport
                {
                    ClientId = ClientId, // ...con el ID del cliente.
                    Status = Status, // ...el estado actual.
                    Fps = _engine.MeasuredFps, // ...los FPS medidos del motor.
                    CpuUsage = _metrics.GetCpuUsagePercent(), // ...el uso de CPU en porcentaje.
                    Gpu = _metrics.GetGpuInfo().Name, // ...el nombre de la GPU.
                    GpuUsage = _metrics.GetGpuUsagePercent(), // ...el uso de GPU en porcentaje.
                    TemperatureC = _metrics.GetTemperatureC(), // ...la temperatura en grados Celsius.
                    ResolutionWidth = _metrics.GetGpuInfo().Width, // ...el ancho de la resolución de pantalla.
                    ResolutionHeight = _metrics.GetGpuInfo().Height, // ...el alto de la resolución de pantalla.
                    RamUsedMb = Environment.WorkingSet / 1024 / 1024, // ...la RAM usada del proceso en MB.
                    RamTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024, // ...la RAM total disponible en MB.
                    FreeDiskMb = FreeDiskMb, // ...el espacio libre en disco en MB.
                    LastCueNumber = _lastCueNumber, // ...la última cue reproducida.
                    SyncState = _pendingSync.Count == 0 ? "ok" : $"syncing {_pendingSync.Count}", // ...el estado de sincronización actual.
                    TimestampUtc = DateTimeOffset.UtcNow // ...y la marca de tiempo en UTC.
                };
                await _connection.SendUdpAsync(MessageEnvelope.Create(MessageType.Status, ClientId, DateTime.UtcNow.Ticks, report)); // Envía el reporte de estado por UDP.

                await Task.Delay(ProtocolDefaults.HeartbeatIntervalMs, ct); // Espera el intervalo de heartbeat configurado (cancelable).
            }
        }
        catch (OperationCanceledException) // Si se cancela el cliente, se sale en silencio.
        {
        }
        catch (Exception ex) // Si falla un reporte (error transitorio)...
        {
            // Informa del error transitorio.
            Log?.Invoke($"ReportLoop error transitorio: {ex.Message}");
            try // Espera antes de reintentar...
            {
                await Task.Delay(1000, ct); // ...durante 1 segundo (cancelable).
            }
            catch (OperationCanceledException) // Si se cancela durante la espera...
            {
            }
        }
    }

    private long FreeDiskMb // Espacio libre en el disco del almacén de medios.
    {
        get // Getter que consulta el disco...
        {
            try // Intenta obtener el espacio libre...
            {
                var drive = new DriveInfo(Path.GetPathRoot(_store.Root) ?? "C:\\"); // ...del volumen que contiene la raíz de medios.
                return drive.AvailableFreeSpace / 1024 / 1024; // Devuelve los megabytes libres.
            }
            catch // Si falla la consulta...
            {
                return 0; // ...devuelve 0.
            }
        }
    }

    public async ValueTask DisposeAsync() // Libera los recursos del cliente de forma asíncrona.
    {
        if (_disposed) // Si el cliente ya estaba liberado...
            return; // ...no hace nada y evita doble liberación.
        _disposed = true; // Marca el cliente como liberado.
        _lifetimeCts.Cancel(); // Cancela los bucles en curso.
        _disconnectTcs?.TrySetResult(); // Despierta el bucle de reconexión para que salga.
        try // Espera a que termine el bucle de reconexión...
        {
            if (_reconnectTask is not null) // Si había bucle de reconexión en curso...
                await _reconnectTask; // ...se espera su finalización.
        }
        catch // Si la espera lanza excepción...
        {
        }
        try // Libera la conexión...
        {
            await _connection.DisposeAsync(); // ...cerrando TCP/UDP.
        }
        catch // Si la liberación lanza excepción...
        {
        }
        _lifetimeCts.Dispose(); // Libera el CancellationTokenSource.
        _httpSync?.Dispose(); // Libera el cliente HTTP de la sincronización si se creó.
    }
}
