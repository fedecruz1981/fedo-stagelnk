// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: HttpSyncTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics;
using System.Security.Cryptography;
using Fedo.StageLnk.Client;
using Fedo.StageLnk.Protocol;
using Fedo.StageLnk.Server;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>
/// Pruebas del canal de sincronización por HTTP (v0.8): reanudación del
/// receptor (FileReceiver resume), descarga con Range, verificación SHA-256,
/// fallo ante archivo inexistente y sincronización real del cliente por HTTP.
/// </summary>
public sealed class HttpSyncTests
{
    // Devuelve el SHA-256 (hex) del contenido dado en bytes.
    private static string Sha256Of(byte[] data) // Calcula el hash en memoria
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(); // Hash a hex minúsculas

    // Crea un archivo de muestra con contenido determinista y devuelve su SHA-256.
    private static (byte[] Data, string Sha) CreateSample(int bytes) // Genera bytes de patrón conocido
    {
        var data = new byte[bytes]; // Búfer del tamaño pedido
        for (int i = 0; i < data.Length; i++) // Rellena con un patrón determinista...
            data[i] = (byte)(i % 251); // ...de valores 0..250
        return (data, Sha256Of(data)); // Devuelve el contenido y su hash
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que FileReceiver en modo resume hashea el contenido previo + lo añadido
    public void FileReceiver_Resume_HashesExistingPlusAppended()
    {
        // Crea el directorio temporal del test
        string dir = TestUtils.NewTempDir();
        try
        {
            // Contenido de ejemplo de 100 bytes
            byte[] full = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
            // Ruta del archivo temporal de descarga
            string temp = Path.Combine(dir, "m1.part");
            // Guarda la primera mitad del archivo como si ya se hubiera descargado
            File.WriteAllBytes(temp, full[..40]);

            // Abre el receptor reanudando desde los 40 bytes ya escritos
            using (var receiver = new FileReceiver("m1", temp, full.Length, resume: true))
            {
                // Comprueba que detecta los bytes ya recibidos
                Assert.Equal(40, receiver.Received);
                // Escribe el resto de bytes para completar el archivo
                receiver.Append(full[40..]);
            }

            // Reabre en modo resume para verificar el hash del archivo completo
            using (var receiver = new FileReceiver("m1", temp, full.Length, resume: true))
            {
                // Finaliza la recepción y obtiene el hash
                bool ok = receiver.TryComplete(out string sha);
                // El tamaño debe coincidir y el hash debe ser el del contenido completo
                Assert.True(ok, "el tamaño completo no se validó");
                Assert.Equal(Sha256Of(full), sha); // El hash re-hasheado debe ser el esperado.
            }
        }
        finally
        {
            // Limpia el directorio temporal
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Prepara un servidor HTTP sobre un directorio con un archivo y devuelve servidor + datos
    private static async Task<(MediaHttpServer Server, byte[] Data, string Sha, string Dir)> StartServerWithSampleAsync(string fileName, int size)
    {
        // Directorio raíz del servidor
        string dir = TestUtils.NewTempDir();
        // Genera el contenido determinista
        var (data, sha) = CreateSample(size);
        // Escribe el archivo en la raíz
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), data);
        // Crea y arranca el servidor HTTP en puerto efímero
        var server = new MediaHttpServer(dir, 0);
        server.Start(); // Arranca el listener
        // Devuelve el servidor (con su puerto real), los datos, el hash y el directorio
        return (server, data, sha, dir);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la descarga completa por HTTP verifica el SHA y entrega el contenido exacto
    public async Task Downloader_FullDownload_VerifiesShaAndContent()
    {
        // Directorio temporal para los temporales del cliente
        string clientDir = TestUtils.NewTempDir();
        // Crea el archivo de muestra y arranca el servidor HTTP
        var (server, data, sha, dir) = await StartServerWithSampleAsync("clip.wav", 200_000);
        await using (server)
        {
            // Activo con los datos esperados del servidor
            var asset = new MediaAsset { Id = "m1", Name = "Clip", Kind = MediaKind.Audio, RelativePath = "clip.wav", Size = data.Length, Sha256 = sha };
            // Ruta del temporal de descarga
            string temp = Path.Combine(clientDir, "m1.part");
            // Descargador apuntando al servidor de streaming
            using var downloader = new HttpFileDownloader($"http://127.0.0.1:{server.BoundPort}");
            // Descarga y verifica el archivo
            bool ok = await downloader.DownloadAsync(asset, temp, CancellationToken.None);
            // La descarga debe completarse y verificarse
            Assert.True(ok, "la descarga no se completó");
            // El contenido del temporal debe ser exactamente el original
            Assert.Equal(data, await File.ReadAllBytesAsync(temp));
        }
        // Limpia los directorios temporales
        TestUtils.DeleteTempDir(clientDir); // Borra la carpeta temporal del cliente.
        TestUtils.DeleteTempDir(dir); // Borra la carpeta temporal del servidor.
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la descarga reanuda un .part a medio bajar usando HTTP Range
    public async Task Downloader_ResumesPartialPart()
    {
        // Directorio temporal para los temporales del cliente
        string clientDir = TestUtils.NewTempDir();
        // Crea el archivo de muestra y arranca el servidor HTTP
        var (server, data, sha, dir) = await StartServerWithSampleAsync("clip.wav", 200_000);
        await using (server)
        {
            // Activo con los datos esperados del servidor
            var asset = new MediaAsset { Id = "m1", Name = "Clip", Kind = MediaKind.Audio, RelativePath = "clip.wav", Size = data.Length, Sha256 = sha };
            // Ruta del temporal de descarga
            string temp = Path.Combine(clientDir, "m1.part");
            // Simula una descarga cortada: solo se recibió el primer tercio
            File.WriteAllBytes(temp, data[..(data.Length / 3)]);

            // Descargador apuntando al servidor de streaming
            using var downloader = new HttpFileDownloader($"http://127.0.0.1:{server.BoundPort}");
            // Reanuda la descarga desde el último byte recibido
            bool ok = await downloader.DownloadAsync(asset, temp, CancellationToken.None);
            // La descarga reanudada debe completarse y verificarse
            Assert.True(ok, "la descarga reanudada no se completó");
            // El contenido final debe ser el original completo
            Assert.Equal(data, await File.ReadAllBytesAsync(temp));
        }
        // Limpia los directorios temporales
        TestUtils.DeleteTempDir(clientDir); // Borra la carpeta temporal del cliente.
        TestUtils.DeleteTempDir(dir); // Borra la carpeta temporal del servidor.
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un SHA esperado incorrecto hace fallar la verificación
    public async Task Downloader_RejectsWrongHash()
    {
        // Directorio temporal para los temporales del cliente
        string clientDir = TestUtils.NewTempDir();
        // Crea el archivo de muestra y arranca el servidor HTTP
        var (server, data, _, dir) = await StartServerWithSampleAsync("clip.wav", 200_000);
        await using (server)
        {
            // Activo con un hash deliberadamente erróneo
            var asset = new MediaAsset { Id = "m1", Name = "Clip", Kind = MediaKind.Audio, RelativePath = "clip.wav", Size = data.Length, Sha256 = "hash-incorrecto" };
            // Ruta del temporal de descarga
            string temp = Path.Combine(clientDir, "m1.part");
            // Descargador apuntando al servidor de streaming
            using var downloader = new HttpFileDownloader($"http://127.0.0.1:{server.BoundPort}");
            // La descarga física funciona pero la verificación debe fallar
            bool ok = await downloader.DownloadAsync(asset, temp, CancellationToken.None);
            // El resultado debe ser falso por checksum
            Assert.False(ok, "la verificación de checksum debía fallar");
        }
        // Limpia los directorios temporales
        TestUtils.DeleteTempDir(clientDir); // Borra la carpeta temporal del cliente.
        TestUtils.DeleteTempDir(dir); // Borra la carpeta temporal del servidor.
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un archivo inexistente en el servidor no se reintenta y devuelve falso
    public async Task Downloader_FailsFast_WhenFileMissing()
    {
        // Directorio temporal para los temporales del cliente
        string clientDir = TestUtils.NewTempDir();
        // Crea el archivo de muestra y arranca el servidor HTTP
        var (server, _, _, dir) = await StartServerWithSampleAsync("clip.wav", 200_000);
        await using (server)
        {
            // Activo que apunta a un archivo inexistente en el servidor
            var asset = new MediaAsset { Id = "m1", Name = "Clip", Kind = MediaKind.Audio, RelativePath = "no-existe.wav", Size = 100, Sha256 = "x" };
            // Ruta del temporal de descarga
            string temp = Path.Combine(clientDir, "m1.part");
            // Descargador apuntando al servidor de streaming
            using var downloader = new HttpFileDownloader($"http://127.0.0.1:{server.BoundPort}");
            // La descarga debe fallar de forma inmediata (404, sin reintentos largos)
            bool ok = await downloader.DownloadAsync(asset, temp, CancellationToken.None);
            // El resultado debe ser falso
            Assert.False(ok, "la descarga debía fallar con archivo inexistente");
            // No debe quedar ningún temporal creado
            Assert.False(File.Exists(temp), "no debe quedar un temporal de un archivo inexistente");
        }
        // Limpia los directorios temporales
        TestUtils.DeleteTempDir(clientDir); // Borra la carpeta temporal del cliente.
        TestUtils.DeleteTempDir(dir); // Borra la carpeta temporal del servidor.
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el cliente sincroniza por HTTP y queda READY con el medio en disco
    public async Task Client_SyncsOverHttp_AndGoesReady()
    {
        // Directorios raíz del servidor y del cliente
        string serverRoot = TestUtils.NewTempDir(); // Directorio raíz del servidor.
        string clientRoot = TestUtils.NewTempDir(); // Directorio raíz del cliente.
        // Crea el tono WAV de prueba en el servidor
        TestUtils.WriteToneFile(serverRoot);

        // Servidor con puerto HTTP efímero para evitar colisiones
        var server = new StageLnkServer(serverRoot, 19101, 19601, 0);
        server.Start(); // Arranca listeners TCP/UDP/HTTP
        await server.ScanLibraryAsync(); // Escanea la biblioteca
        await using (server)
        {
            // Un único medio debe detectarse
            var asset = Assert.Single(server.Library.All);

            // Cliente que sincroniza por el canal HTTP del servidor
            var client = new StageLnkClient("HTTP", clientRoot, "127.0.0.1", 19101, 19601, new RecordingEngine());
            // Apunta la sincronización al servidor HTTP real (puerto efímero)
            client.HttpBaseUrl = $"http://127.0.0.1:{server.BoundHttpPort}";
            await using (client)
            {
                await client.StartAsync(); // Conecta y dispara la sincronización

                // El READY es no bloqueante: hay que esperar a que la descarga
                // HTTP termine y el servidor registre el medio sincronizado.
                string dest = Path.Combine(clientRoot, asset.RelativePath); // Ruta destino del medio
                var session = Assert.Single(server.Clients); // La sesión del cliente ya registrada
                bool done = await WaitUntilAsync(() => // Espera hasta que la condición se cumpla
                    File.Exists(dest) // El medio llegó a disco...
                    && new FileInfo(dest).Length == asset.Size // ...con el tamaño correcto...
                    && session.SyncedMediaIds.Contains(asset.Id), // ...y el servidor lo registró.
                    15000); // 15 segundos de margen
                Assert.True(done, "la sincronización por HTTP no completó la descarga y su registro"); // Confirma que se completó el ciclo.

                // El medio debe estar en disco con el tamaño correcto
                Assert.Equal(asset.Size, new FileInfo(dest).Length);
                // El servidor debe registrar el medio como sincronizado
                Assert.Contains(asset.Id, session.SyncedMediaIds);
                // El cliente debe quedar en estado Ready
                Assert.Equal(ClientStatus.Ready, client.Status);
            }
        }
        // Limpia los directorios temporales
        TestUtils.DeleteTempDir(serverRoot); // Borra la carpeta temporal del servidor.
        TestUtils.DeleteTempDir(clientRoot); // Borra la carpeta temporal del cliente.
    }

    // Espera hasta que la condición se cumpla o expire el tiempo límite
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        // Cronómetro para medir el tiempo transcurrido
        var sw = Stopwatch.StartNew();
        // Bucle mientras no se supere el tiempo límite
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            // Si la condición ya se cumple, termina con éxito
            if (condition())
                return true; // La condición se cumplió: termina con éxito.
            // Pausa 50 ms antes de volver a evaluar la condición
            await Task.Delay(50);
        }
        // Última comprobación por si se cumple justo al expirar
        return condition();
    }
}
