// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: E2eServerClientTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics;
using Fedo.StageLnk.Client;
using Fedo.StageLnk.Protocol;
using Fedo.StageLnk.Server;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>
/// Pruebas de integración reales: servidor TCP/UDP + cliente que se sincroniza,
/// recibe la cue list y ejecuta comandos broadcast y per-client.
/// </summary>
public sealed class E2eServerClientTests
{
    // Semilla global para generar números de puerto únicos entre tests
    private static int _portSeed;

    // Devuelve un par de puertos TCP/UDP únicos y consecutivos
    private static (int Tcp, int Udp) NextPorts()
    {
        // Incrementa la semilla de forma atómica entre hilos
        int n = Interlocked.Increment(ref _portSeed);
        // Genera el puerto TCP con base 19000 y el UDP con base 19500
        return (19000 + n, 19500 + n);
    }

    // Registro que agrupa el escenario completo de integración
    private sealed record Setup(
        // Servidor que se levanta para el test
        StageLnkServer Server,
        // Cliente conectado al servidor
        StageLnkClient Client,
        // Motor de reproducción que graba las llamadas
        RecordingEngine Engine,
        // Activo de medio usado como capa de la cue
        MediaAsset Asset,
        // Directorio raíz temporal del servidor
        string ServerRoot,
        // Directorio raíz temporal del cliente
        string ClientRoot)
        // Implementa la liberación asíncrona de recursos
        : IAsyncDisposable
    {
        // Libera todos los recursos del escenario de forma asíncrona
        public async ValueTask DisposeAsync()
        {
            // Detiene y libera el servidor
            await Server.DisposeAsync();
            // Desconecta y libera el cliente
            await Client.DisposeAsync();
            // Borra el directorio temporal del servidor
            TestUtils.DeleteTempDir(ServerRoot);
            // Borra el directorio temporal del cliente
            TestUtils.DeleteTempDir(ClientRoot);
        }
    }

    // Crea el escenario: directorios, servidor, cues, cliente y motor
    private static async Task<Setup> CreateSetupAsync(int tcp, int udp)
    {
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        // Crea el directorio raíz temporal del cliente
        string clientRoot = TestUtils.NewTempDir();
        // Escribe un archivo de tono de prueba en la raíz del servidor
        TestUtils.WriteToneFile(serverRoot);

        // Instancia el servidor con los puertos TCP/UDP asignados
        var server = new StageLnkServer(serverRoot, tcp, udp, 0);
        // Arranca los listeners TCP y UDP del servidor
        server.Start();
        // Escanea la biblioteca de medios del servidor
        await server.ScanLibraryAsync();
        // Verifica que el escáner detectó un único medio
        Assert.Single(server.Library.All);

        // Toma el activo detectado por el escáner
        MediaAsset asset = server.Library.All[0];
        // Añade la cue 1 de audio con una capa sobre el activo
        server.UpsertCue(new Cue
        {
            // Número de la cue
            Number = 1,
            // Nombre de la cue
            Name = "Tono",
            // Tipo de cue: audio
            Kind = CueKind.Audio,
            // Capa que referencia el medio escaneado
            Layers = { new LayerSpec { ZOrder = 1, MediaId = asset.Id } }
        });
        // Añade la cue 2 de tipo black
        server.UpsertCue(new Cue { Number = 2, Name = "Black", Kind = CueKind.Black });

        // Crea el motor de grabación para verificar llamadas
        var engine = new RecordingEngine();
        // Instancia el cliente conectado a localhost con los puertos dados
        var client = new StageLnkClient("E2E", clientRoot, "127.0.0.1", tcp, udp, engine);
        // Devuelve el escenario ya montado
        return new Setup(server, client, engine, asset, serverRoot, clientRoot);
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
        // Inicia un cronómetro para medir el tiempo transcurrido
        var sw = Stopwatch.StartNew();
        // Bucle mientras no se supere el tiempo límite
        while (sw.ElapsedMilliseconds < timeoutMs)
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

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el cliente sincroniza el medio y queda en estado Ready
    public async Task Client_SyncsMedia_AndGoesReady()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el escenario y lo libera al salir del bloque
        await using var setup = await CreateSetupAsync(tcp, udp);

        // Espera a que el cliente sincronice y quede Ready
        await WaitReadyAsync(setup.Client);

        // Ruta destino del medio sincronizado en el cliente
        string dest = Path.Combine(setup.ClientRoot, setup.Asset.RelativePath);
        // Verifica que el archivo de medio existe en el cliente
        Assert.True(File.Exists(dest), "el medio no fue sincronizado al cliente");
        // Compara el tamaño del archivo recibido con el del servidor
        Assert.Equal(setup.Asset.Size, new FileInfo(dest).Length);
        // El cliente debe tener las dos cues del servidor
        Assert.Equal(2, setup.Client.CueList.Count);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un play broadcast llega al motor del cliente
    public async Task Broadcast_Play_ReachesEngine()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el escenario y lo libera al salir del bloque
        await using var setup = await CreateSetupAsync(tcp, udp);

        // Espera a que el cliente esté listo
        await WaitReadyAsync(setup.Client);
        // Pausa para asegurar el endpoint UDP registrado en el servidor
        await Task.Delay(1500); // asegura el endpoint UDP registrado en el servidor

        // Ordena reproducir la cue 1 en modo broadcast
        await setup.Server.PlayCueAsync(1);

        // Espera a que el motor del cliente registre al menos un cue
        bool received = await WaitUntilAsync(() => setup.Engine.PlayedCueNumbers.Count >= 1);
        // Confirma que el cue llegó al motor
        Assert.True(received, "el cliente no ejecutó el cue 1");
        // Extrae el número de cue reproducido de la cola
        Assert.True(setup.Engine.PlayedCueNumbers.TryDequeue(out int cueNumber));
        // El cue reproducido debe ser el número 1
        Assert.Equal(1, cueNumber);

        // Espera a que el motor reciba la capa de medio
        bool mediaReceived = await WaitUntilAsync(() => setup.Engine.PlayedMediaNames.Count >= 1);
        // Confirma que la capa de medio llegó al motor
        Assert.True(mediaReceived, "el motor no recibió la capa de medio");
        // Extrae el nombre del medio reproducido de la cola
        Assert.True(setup.Engine.PlayedMediaNames.TryDequeue(out string? mediaName));
        // El medio reproducido debe ser el tono de prueba
        Assert.Equal("Tono de prueba", mediaName);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el broadcast llega a todos y el playto solo a un cliente
    public async Task MultiClient_Broadcast_AndPlayTo()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        // Crea el directorio temporal del cliente A
        string clientRootA = TestUtils.NewTempDir();
        // Crea el directorio temporal del cliente B
        string clientRootB = TestUtils.NewTempDir();
        // Escribe el tono de prueba en la raíz del servidor
        TestUtils.WriteToneFile(serverRoot);

        try
        {
            // Instancia el servidor con los puertos asignados
            var server = new StageLnkServer(serverRoot, tcp, udp, 0);
            // Arranca el servidor
            server.Start();
            // Escanea la biblioteca de medios
            await server.ScanLibraryAsync();
            // Toma el activo detectado
            MediaAsset asset = server.Library.All[0];
            // Añade la cue 1 de audio con el activo como capa
            server.UpsertCue(new Cue
            {
                // Número de la cue
                Number = 1,
                // Nombre de la cue
                Name = "Tono",
                // Tipo de cue: audio
                Kind = CueKind.Audio,
                // Capa que referencia el medio escaneado
                Layers = { new LayerSpec { ZOrder = 1, MediaId = asset.Id } }
            });
            // Añade la cue 2 de tipo black
            server.UpsertCue(new Cue { Number = 2, Name = "Black", Kind = CueKind.Black });

            // Motor de grabación del cliente A
            var engineA = new RecordingEngine();
            // Motor de grabación del cliente B
            var engineB = new RecordingEngine();
            // Cliente A "PantallaA" con su propio motor y raíz
            await using var clientA = new StageLnkClient("PantallaA", clientRootA, "127.0.0.1", tcp, udp, engineA);
            // Cliente B "PantallaB" con su propio motor y raíz
            await using var clientB = new StageLnkClient("PantallaB", clientRootB, "127.0.0.1", tcp, udp, engineB);

            // Espera a que el cliente A esté listo
            await WaitReadyAsync(clientA);
            // Espera a que el cliente B esté listo
            await WaitReadyAsync(clientB);
            // Pausa para asegurar los endpoints UDP registrados
            await Task.Delay(1500);

            // broadcast: llega a ambos
            // Ordena el play broadcast de la cue 1
            await server.PlayCueAsync(1);
            // Espera a que ambos motores registren el cue 1
            bool both = await WaitUntilAsync(() => engineA.PlayedCueNumbers.Count >= 1 && engineB.PlayedCueNumbers.Count >= 1);
            // Confirma que el broadcast llegó a ambos clientes
            Assert.True(both, "el broadcast no llegó a ambos clientes");

            // per-client: solo a PantallaB
            // Ordena el play de la cue 2 solo para PantallaB
            await server.PlayCueToAsync("PantallaB", 2);
            // Espera a que el motor de B registre el segundo cue
            bool bGotSecond = await WaitUntilAsync(() => engineB.PlayedCueNumbers.Count >= 2);
            // Confirma que el playto llegó a PantallaB
            Assert.True(bGotSecond, "el playto no llegó a PantallaB");

            // Pausa para descartar fugas (comandos que no debían llegar)
            await Task.Delay(600); // margen para descartar fugas
            // El motor A solo debe haber recibido el cue del broadcast
            Assert.Single(engineA.PlayedCueNumbers);
            // El motor B debe tener los dos cues recibidos
            Assert.Equal(2, engineB.PlayedCueNumbers.Count);
        }
        finally
        {
            // Borra el directorio temporal del servidor
            TestUtils.DeleteTempDir(serverRoot);
            // Borra el directorio temporal del cliente A
            TestUtils.DeleteTempDir(clientRootA);
            // Borra el directorio temporal del cliente B
            TestUtils.DeleteTempDir(clientRootB);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que Next y Previous avanzan y retroceden la cue actual
    public async Task NextPrev_AdvancesCurrentCue()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        try
        {
            // Instancia el servidor con los puertos asignados
            var server = new StageLnkServer(serverRoot, tcp, udp, 0);
            // Arranca el servidor
            server.Start();
            // Añade la cue 1 "A" de audio
            server.UpsertCue(new Cue { Number = 1, Name = "A", Kind = CueKind.Audio });
            // Añade la cue 2 "B" de audio
            server.UpsertCue(new Cue { Number = 2, Name = "B", Kind = CueKind.Audio });
            // Añade la cue 3 "C" de audio
            server.UpsertCue(new Cue { Number = 3, Name = "C", Kind = CueKind.Audio });

            // Reproduce la cue 1
            await server.PlayCueAsync(1);
            // La cue actual debe ser la 1
            Assert.Equal(1, server.CurrentCueNumber);

            // Avanza a la siguiente cue
            await server.NextAsync();
            // La cue actual debe ser la 2
            Assert.Equal(2, server.CurrentCueNumber);

            // Retrocede a la cue anterior
            await server.PreviousAsync();
            // La cue actual debe volver a ser la 1
            Assert.Equal(1, server.CurrentCueNumber);

            // Salta a la cue 3 (última)
            await server.PlayCueAsync(3);
            // Intenta avanzar desde la última cue
            await server.NextAsync();
            // Al no existir siguiente, la cue actual se queda en 3
            Assert.Equal(3, server.CurrentCueNumber); // no hay siguiente

            // Retrocede desde la última cue
            await server.PreviousAsync();
            // La cue actual debe ser la 2
            Assert.Equal(2, server.CurrentCueNumber);

            // Detiene y libera el servidor
            await server.DisposeAsync();
        }
        finally
        {
            // Borra el directorio temporal del servidor
            TestUtils.DeleteTempDir(serverRoot);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el cliente se reconecta solo tras reiniciar el servidor
    public async Task Client_AutoReconnects_AfterServerRestart()
    {
        // Obtiene un par de puertos libres
        var (tcp, udp) = NextPorts();
        // Crea el directorio raíz temporal del servidor
        string serverRoot = TestUtils.NewTempDir();
        // Crea el directorio raíz temporal del cliente
        string clientRoot = TestUtils.NewTempDir();
        // Escribe el tono de prueba en la raíz del servidor
        TestUtils.WriteToneFile(serverRoot);

        // Instancia el servidor con los puertos asignados
        var server = new StageLnkServer(serverRoot, tcp, udp, 0);
        // Arranca el servidor
        server.Start();
        // Escanea la biblioteca de medios
        await server.ScanLibraryAsync();

        // Crea el motor de grabación
        var engine = new RecordingEngine();
        // Crea el cliente conectado a localhost
        var client = new StageLnkClient("E2E", clientRoot, "127.0.0.1", tcp, udp, engine);
        try
        {
            // Espera a que el cliente esté listo
            await WaitReadyAsync(client);

            // suscribirse ANTES de tirar el servidor: evita la carrera de que el
            // cliente reconecte y quede Ready antes de registrar el manejador.
            // Crea un TCS para esperar la reconexión del cliente
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Registra el manejador que marca completada la reconexión
            client.SyncedAndReady += () => reconnected.TrySetResult();

            // el servidor cae: el cliente debe pasar a Disconnected
            // Detiene el servidor para simular la caída
            await server.DisposeAsync();
            // Espera a que el cliente detecte la desconexión
            bool wentDisconnected = await WaitUntilAsync(() => client.Status == ClientStatus.Disconnected);
            // Confirma que el cliente pasó a Disconnected
            Assert.True(wentDisconnected, "el cliente no detectó la caída del servidor");

            // margen para que Windows libere los puertos antes de re-vincular
            // Pausa breve para que el SO libere los puertos antes de re-vincular
            await Task.Delay(300);

            // el servidor vuelve en los mismos puertos y el cliente se reconecta solo
            // Variable para el servidor reiniciado (null si aún no arranca)
            StageLnkServer? server2 = null;
            // Guarda el error del último intento de arranque
            Exception? startError = null;
            // Reintenta arrancar el servidor hasta 40 veces o hasta que arranque
            for (int i = 0; i < 40 && server2 is null; i++)
            {
                try
                {
                    // Crea el nuevo servidor en los mismos puertos
                    server2 = new StageLnkServer(serverRoot, tcp, udp, 0);
                    // Arranca el nuevo servidor
                    server2.Start();
                    // Vuelve a escanear la biblioteca
                    await server2.ScanLibraryAsync();
                }
                catch (Exception ex)
                {
                    // Guarda el error ocurrido al intentar arrancar
                    startError = ex;
                    // Si el servidor llegó a crearse hay que liberarlo
                    if (server2 is not null)
                    {
                        // Libera el servidor creado de forma parcial
                        await server2.DisposeAsync();
                        // Lo marca como nulo para reintentar el arranque
                        server2 = null;
                    }
                    // Espera 250 ms antes del siguiente intento
                    await Task.Delay(250);
                }
            }

            // Confirma que el servidor se pudo reiniciar
            Assert.True(server2 is not null, $"no se pudo reiniciar el servidor: {startError?.Message}");

            try
            {
                // Espera el evento de reconexión con un límite de 25 s
                await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(25));
                // Verifica que el cliente volvió al estado Ready
                Assert.Equal(ClientStatus.Ready, client.Status);

                // Ruta del medio que debe seguir sincronizado
                string dest = Path.Combine(clientRoot, "Tono de prueba.wav");
                // Comprueba que el medio aún existe tras la reconexión
                Assert.True(File.Exists(dest), "el medio debe seguir sincronizado tras la reconexión");
            }
            finally
            {
                // Libera el servidor reiniciado
                await server2.DisposeAsync();
            }
        }
        finally
        {
            // Desconecta y libera el cliente
            await client.DisposeAsync();
            // Borra el directorio temporal del servidor
            TestUtils.DeleteTempDir(serverRoot);
            // Borra el directorio temporal del cliente
            TestUtils.DeleteTempDir(clientRoot);
        }
    }
}
