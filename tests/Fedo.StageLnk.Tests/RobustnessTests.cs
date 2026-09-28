// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: RobustnessTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fedo.StageLnk.Client;
using Fedo.StageLnk.Protocol;
using Fedo.StageLnk.Server;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>
/// Pruebas de robustez: tramas TCP incompletas, pérdida y corrupción de
/// datagramas UDP, desincronización de reloj entre servidor y cliente, y
/// caída del servidor a mitad de reproducción. Verifican que el sistema se
/// recupera y limpia recursos en vez de colgarse o lanzar excepciones.
/// </summary>
public sealed class RobustnessTests
{
    // Semilla global para generar números de puerto únicos entre tests
    private static int _portSeed;

    // Devuelve un par de puertos TCP/UDP únicos y consecutivos
    private static (int Tcp, int Udp) NextPorts()
    {
        // Incrementa la semilla de forma atómica entre hilos
        int n = Interlocked.Increment(ref _portSeed);
        // Genera el puerto TCP con base 23000 y el UDP con base 23500
        return (23000 + n, 23500 + n);
    }

    // Espera a que el cliente sincronice y quede en estado Ready
    private static async Task WaitReadyAsync(StageLnkClient client)
    {
        // Crea un TCS para esperar el evento de sincronización
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Suscribe el manejador que marca completado el TCS
        client.SyncedAndReady += () => tcs.TrySetResult();
        // Inicia la conexión del cliente
        await client.StartAsync();
        // Espera el evento con un límite de 20 segundos
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // Confirma que el cliente quedó en estado Ready
        Assert.Equal(ClientStatus.Ready, client.Status);
    }

    // Espera hasta que la condición se cumpla o expire el tiempo límite
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        // Fecha de inicio para medir el tiempo transcurrido
        DateTime start = DateTime.UtcNow;
        // Bucle mientras no se supere el tiempo límite
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            // Si la condición ya se cumple, termina con éxito
            if (condition())
                return true;
            // Pausa 50 ms antes de volver a evaluar la condición
            await Task.Delay(50);
        }
        // Última comprobación por si la condición se cumple justo al expirar
        return condition();
    }

    // =====================================================================
    // Tramas TCP incompletas
    // =====================================================================

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Una trama TCP con cabecera válida pero cuerpo incompleto devuelve null
    public async Task TcpRead_ReturnsNull_WhenFrameBodyIsTruncated()
    {
        // Construye un sobre válido para generar una trama de referencia
        var original = MessageEnvelope.Create(MessageType.Status, Guid.NewGuid(), 7);
        // Codifica el sobre como trama TCP completa
        byte[] frame = FrameCodec.EncodeTcp(original);
        // Deja el flujo con solo 10 bytes: la cabecera (4) más un cuerpo parcial
        var stream = new MemoryStream(frame, 0, 10);

        // La lectura debe agotar el flujo y devolver null, no lanzar
        MessageEnvelope? leida = await FrameCodec.ReadTcpAsync(stream, CancellationToken.None);

        // Confirma que la trama incompleta se descartó
        Assert.Null(leida);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Una trama con longitud negativa se rechaza por seguridad
    public async Task TcpRead_Throws_WhenFrameLengthIsNegative()
    {
        // Construye una cabecera con longitud negativa (-1)
        byte[] trama = new byte[8];
        // Escribe la longitud inválida en little-endian
        BinaryPrimitives.WriteInt32LittleEndian(trama, -1);
        // Envuelve la trama en un flujo de memoria
        var stream = new MemoryStream(trama);

        // La lectura debe lanzar InvalidDataException por longitud fuera de rango
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FrameCodec.ReadTcpAsync(stream, CancellationToken.None));
    }

    // =====================================================================
    // Pérdida y corrupción de paquetes UDP
    // =====================================================================

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // El servidor ignora datagramas corruptos y sigue aceptando los válidos
    public async Task UdpServer_IgnoresCorruptDatagrams_AndKeepsServing()
    {
        // Obtiene un puerto UDP libre
        var (_, udp) = NextPorts();

        // Instancia el servidor UDP de comandos
        await using var server = new UdpCommandServer(udp);
        // Contador de datagramas válidos recibidos
        int validos = 0;
        // Suscribe el manejador que cuenta los sobres válidos
        server.DatagramReceived += (_, _, _) => Interlocked.Increment(ref validos);
        // Arranca el servidor UDP
        server.Start();

        // Abre un socket UDP efímero para enviar los datagramas
        using var sender = new UdpClient();
        // Endpoint del servidor UDP en loopback
        var serverEp = new IPEndPoint(IPAddress.Loopback, udp);

        // Envía basura binaria que no es JSON válido
        byte[] basura = { 0xFF, 0xFE, 0x00, 0x01, 0x7B, 0x7B, 0x7B };
        // Manda el primer datagrama corrupto
        await sender.SendAsync(basura, serverEp);
        // Manda un datagrama de longitud cero
        await sender.SendAsync(Array.Empty<byte>(), serverEp);
        // Manda JSON truncado: llaves sin cerrar
        await sender.SendAsync(Encoding.UTF8.GetBytes("{\"Type\":"), serverEp);

        // Construye un sobre válido de estado
        var valido = MessageEnvelope.Create(MessageType.Status, Guid.NewGuid(), 1);
        // Manda el datagrama válido después del ruido
        await sender.SendAsync(FrameCodec.EncodeUdp(valido), serverEp);

        // Espera a que el servidor procese el sobre válido
        bool recibido = await WaitUntilAsync(() => Volatile.Read(ref validos) >= 1, 5000);
        // El servidor debe seguir escuchando tras los datagramas corruptos
        Assert.True(recibido, "el servidor ignoró también el paquete válido");
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // El servidor sobrevive a una ráfaga de 200 datagramas corruptos
    public async Task UdpServer_SurvivesBurstOfCorruptDatagrams()
    {
        // Obtiene un puerto UDP libre
        var (_, udp) = NextPorts();

        // Instancia el servidor UDP de comandos
        await using var server = new UdpCommandServer(udp);
        // Contador de datagramas válidos recibidos
        int validos = 0;
        // Suscribe el manejador que cuenta los sobres válidos
        server.DatagramReceived += (_, _, _) => Interlocked.Increment(ref validos);
        // Arranca el servidor UDP
        server.Start();

        // Abre un socket UDP efímero
        using var sender = new UdpClient();
        // Endpoint del servidor UDP en loopback
        var serverEp = new IPEndPoint(IPAddress.Loopback, udp);

        // Lanza 200 datagramas corruptos de tamaño variable
        for (int i = 0; i < 200; i++)
        {
            // Genera basura determinista de tamaño variable
            byte[] basura = Encoding.UTF8.GetBytes(new string('{', i % 37 + 1));
            // Manda el datagrama corrupto
            await sender.SendAsync(basura, serverEp);
            // Poca pausa para no bloquear, pero evita saturar en algunos entornos
            if (i % 10 == 0)
                await Task.Delay(1);
        }

        // Tras la ráfaga, manda un sobre válido de ping
        var valido = MessageEnvelope.Create(MessageType.Ping, Guid.NewGuid(), 1);
        // Manda el sobre válido
        await sender.SendAsync(FrameCodec.EncodeUdp(valido), serverEp);

        // Espera a que el servidor responda
        bool recibido = await WaitUntilAsync(() => Volatile.Read(ref validos) >= 1, 5000);
        // El servidor debe seguir vivo tras la ráfaga
        Assert.True(recibido, "el servidor dejó de responder tras la ráfaga de corruptos");
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Tras un datagrama perdido, los siguientes siguen llegando en orden
    public async Task UdpCommand_AfterDroppedPacket_StillDeliversInOrder()
    {
        // Obtiene un puerto UDP libre
        var (_, udp) = NextPorts();

        // Instancia el servidor UDP de comandos
        await using var server = new UdpCommandServer(udp);
        // Lista thread-safe de números de secuencia recibidos
        var recibidos = new System.Collections.Concurrent.ConcurrentQueue<long>();
        // Suscribe el manejador que acumula las secuencias recibidas
        server.DatagramReceived += (_, _, msg) => recibidos.Enqueue(msg.Sequence);
        // Arranca el servidor UDP
        server.Start();

        // Abre un socket UDP efímero
        using var sender = new UdpClient();
        // Endpoint del servidor UDP en loopback
        var serverEp = new IPEndPoint(IPAddress.Loopback, udp);

        // Construye un helper local que manda un sobre con una secuencia dada
        void Enviar(MessageType tipo, long secuencia)
            => sender.Send(FrameCodec.EncodeUdp(MessageEnvelope.Create(tipo, Guid.NewGuid(), secuencia)), serverEp);

        // Manda el primer comando con secuencia 1
        Enviar(MessageType.Status, 1);
        // Espera a que llegue el primero
        Assert.True(await WaitUntilAsync(() => recibidos.Count >= 1, 5000), "no llegó el primer comando");

        // Simula la pérdida: el segundo datagrama nunca se envía

        // Manda el tercer comando con secuencia 3, saltando el 2 perdido
        Enviar(MessageType.PlayCue, 3);
        // Espera a que llegue tras el hueco
        Assert.True(await WaitUntilAsync(() => recibidos.Count >= 2, 5000), "no llegó el comando tras la pérdida");

        // Extrae las dos secuencias recibidas
        Assert.True(recibidos.TryDequeue(out long primera));
        // La primera secuencia recibida es la 1
        Assert.Equal(1, primera);
        // Extrae la segunda secuencia recibida
        Assert.True(recibidos.TryDequeue(out long segunda));
        // El hueco de la secuencia 2 no impide entregar la 3
        Assert.Equal(3, segunda);
    }

    // =====================================================================
    // Desincronización de reloj
    // =====================================================================

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Una cue programada en el pasado (servidor adelantado) se reproduce igual
    public async Task PlayCue_ScheduledInThePast_StillReachesEngine()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        // Crea el directorio raíz temporal del cliente
        string clientRoot = TestUtils.NewTempDir();
        // Escribe un archivo de tono de prueba en la raíz del servidor
        TestUtils.WriteToneFile(serverRoot);

        // Motor de grabación del cliente
        var engine = new RecordingEngine();
        // Servidor de pruebas
        var server = new StageLnkServer(serverRoot, tcp, udp, 0);
        // Cliente de pruebas conectado a loopback
        var client = new StageLnkClient("Desfase", clientRoot, "127.0.0.1", tcp, udp, engine);

        try
        {
            // Arranca el servidor y escanea la biblioteca
            server.Start();
            // Escanea los medios del servidor
            await server.ScanLibraryAsync();
            // Añade una cue de audio sobre el medio escaneado
            server.UpsertCue(new Cue
            {
                // Número de la cue
                Number = 1,
                // Nombre de la cue
                Name = "Tono",
                // Tipo de cue: audio
                Kind = CueKind.Audio,
                // Capa que referencia el medio escaneado
                Layers = { new LayerSpec { ZOrder = 1, MediaId = server.Library.All[0].Id } }
            });
            // Espera a que el cliente sincronice
            await WaitReadyAsync(client);
            // Pausa para asegurar el endpoint UDP registrado en el servidor
            await Task.Delay(1500);

            // Programa la cue 2 segundos en el pasado: el reloj del servidor va adelantado
            var pasado = DateTimeOffset.UtcNow.AddSeconds(-2);
            // Ordena reproducir la cue con horario ya vencido
            await server.PlayCueAsync(1, pasado);

            // La cue no debe quedarse colgada por un horario pasado
            bool reproducida = await WaitUntilAsync(() => engine.PlayedCueNumbers.Count >= 1);
            // Confirma que la cue se reprodujo pese al desfase
            Assert.True(reproducida, "la cue con horario pasado no llegó al motor");
            // El horario vencido debe llegar intacto al motor
            Assert.True(engine.PlayedSchedules.TryDequeue(out DateTimeOffset? recibido));
            // La fecha programada se conserva sin errores de conversión
            Assert.NotNull(recibido);
            // El valor enviado coincide con el recibido
            Assert.Equal(pasado.ToUnixTimeMilliseconds(), recibido!.Value.ToUnixTimeMilliseconds());
            // El cliente queda en estado Playing
            Assert.Equal(ClientStatus.Playing, client.Status);
        }
        finally
        {
            // Libera cliente y servidor, y borra los temporales
            await client.DisposeAsync();
            await server.DisposeAsync();
            TestUtils.DeleteTempDir(serverRoot);
            TestUtils.DeleteTempDir(clientRoot);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Un desfase extremo de reloj no rompe la conexión ni el cliente
    public async Task PlayCue_WithExtremeClockSkew_DoesNotBreakClient()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        // Crea el directorio raíz temporal del cliente
        string clientRoot = TestUtils.NewTempDir();
        // Escribe un archivo de tono de prueba en la raíz del servidor
        TestUtils.WriteToneFile(serverRoot);

        // Motor de grabación del cliente
        var engine = new RecordingEngine();
        // Servidor de pruebas
        var server = new StageLnkServer(serverRoot, tcp, udp, 0);
        // Cliente de pruebas conectado a loopback
        var client = new StageLnkClient("Extremo", clientRoot, "127.0.0.1", tcp, udp, engine);

        try
        {
            // Arranca el servidor y escanea la biblioteca
            server.Start();
            // Escanea los medios del servidor
            await server.ScanLibraryAsync();
            // Añade una cue de audio sobre el medio escaneado
            server.UpsertCue(new Cue
            {
                // Número de la cue
                Number = 1,
                // Nombre de la cue
                Name = "Tono",
                // Tipo de cue: audio
                Kind = CueKind.Audio,
                // Capa que referencia el medio escaneado
                Layers = { new LayerSpec { ZOrder = 1, MediaId = server.Library.All[0].Id } }
            });
            // Espera a que el cliente sincronice
            await WaitReadyAsync(client);
            // Pausa para asegurar el endpoint UDP registrado en el servidor
            await Task.Delay(1500);

            // Programa la cue casi 50 años en el futuro (desfase extremo)
            var lejano = DateTimeOffset.UtcNow.AddYears(50);
            // La orden no debe lanzar excepción pese al desfase
            await server.PlayCueAsync(1, lejano);

            // El comando se difunde igual y el motor recibe el horario
            bool recibido = await WaitUntilAsync(() => engine.PlayedSchedules.Count >= 1);
            // Confirma que el comando llegó pese al desfase extremo
            Assert.True(recibido, "el comando con desfase extremo no llegó al motor");
            // Extrae el horario recibido
            Assert.True(engine.PlayedSchedules.TryDequeue(out DateTimeOffset? horario));
            // El horario lejano se conserva íntegro, sin desbordamiento
            Assert.Equal(lejano.ToUnixTimeSeconds(), horario!.Value.ToUnixTimeSeconds());
            // El cliente sigue conectado tras el comando extremo
            Assert.NotEqual(ClientStatus.Disconnected, client.Status);
        }
        finally
        {
            // Libera cliente y servidor, y borra los temporales
            await client.DisposeAsync();
            await server.DisposeAsync();
            TestUtils.DeleteTempDir(serverRoot);
            TestUtils.DeleteTempDir(clientRoot);
        }
    }

    // =====================================================================
    // Caída del servidor a mitad de reproducción
    // =====================================================================

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // El cliente detecta la caída y pasa a Disconnected durante la reproducción
    public async Task Client_GoesDisconnected_WhenServerDropsDuringPlayback()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        // Crea el directorio raíz temporal del cliente
        string clientRoot = TestUtils.NewTempDir();
        // Escribe un archivo de tono de prueba en la raíz del servidor
        TestUtils.WriteToneFile(serverRoot);

        // Motor de grabación del cliente
        var engine = new RecordingEngine();
        // Servidor de pruebas
        var server = new StageLnkServer(serverRoot, tcp, udp, 0);
        // Cliente de pruebas conectado a loopback
        var client = new StageLnkClient("Interrupcion", clientRoot, "127.0.0.1", tcp, udp, engine);

        try
        {
            // Arranca el servidor y escanea la biblioteca
            server.Start();
            // Escanea los medios del servidor
            await server.ScanLibraryAsync();
            // Añade una cue de audio sobre el medio escaneado
            server.UpsertCue(new Cue
            {
                // Número de la cue
                Number = 1,
                // Nombre de la cue
                Name = "Tono",
                // Tipo de cue: audio
                Kind = CueKind.Audio,
                // Capa que referencia el medio escaneado
                Layers = { new LayerSpec { ZOrder = 1, MediaId = server.Library.All[0].Id } }
            });
            // Espera a que el cliente sincronice
            await WaitReadyAsync(client);
            // Pausa para asegurar el endpoint UDP registrado en el servidor
            await Task.Delay(1500);

            // Empieza a reproducir la cue
            await server.PlayCueAsync(1);
            // Espera a que el motor registre la reproducción en curso
            bool reproduciendo = await WaitUntilAsync(() => engine.PlayedCueNumbers.Count >= 1);
            // Confirma que la reproducción arrancó antes de la caída
            Assert.True(reproduciendo, "el cliente no llegó a reproducir antes de la caída");

            // El servidor cae a mitad de la reproducción
            await server.DisposeAsync();

            // El cliente debe detectar la caída y pasar a Disconnected
            bool caido = await WaitUntilAsync(() => client.Status == ClientStatus.Disconnected);
            // Confirma que el cliente detectó la desconexión
            Assert.True(caido, "el cliente no detectó la caída durante la reproducción");
        }
        finally
        {
            // Libera el cliente (el servidor ya se liberó) y borra los temporales
            await client.DisposeAsync();
            TestUtils.DeleteTempDir(serverRoot);
            TestUtils.DeleteTempDir(clientRoot);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Liberar el cliente con el servidor ya caído no lanza excepción
    public async Task Client_Dispose_DoesNotThrow_WhenServerAlreadyDown()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        // Crea el directorio raíz temporal del cliente
        string clientRoot = TestUtils.NewTempDir();
        // Escribe un archivo de tono de prueba en la raíz del servidor
        TestUtils.WriteToneFile(serverRoot);

        // Motor de grabación del cliente
        var engine = new RecordingEngine();
        // Servidor de pruebas
        var server = new StageLnkServer(serverRoot, tcp, udp, 0);
        // Cliente de pruebas conectado a loopback
        var client = new StageLnkClient("Efimero", clientRoot, "127.0.0.1", tcp, udp, engine);

        try
        {
            // Arranca el servidor y escanea la biblioteca
            server.Start();
            // Escanea los medios del servidor
            await server.ScanLibraryAsync();
            // Espera a que el cliente sincronice
            await WaitReadyAsync(client);

            // El servidor cae primero
            await server.DisposeAsync();
            // Espera a que el cliente registre la desconexión
            bool caido = await WaitUntilAsync(() => client.Status == ClientStatus.Disconnected);
            // Confirma que el cliente ya está desconectado
            Assert.True(caido, "el cliente no se desconectó tras la caída");

            // Liberar el cliente con el servidor caído debe ser seguro
            await client.DisposeAsync();
        }
        finally
        {
            // Segunda liberación idempotente: no debe lanzar
            await client.DisposeAsync();
            // Borra los temporales
            TestUtils.DeleteTempDir(serverRoot);
            TestUtils.DeleteTempDir(clientRoot);
        }
    }
}
