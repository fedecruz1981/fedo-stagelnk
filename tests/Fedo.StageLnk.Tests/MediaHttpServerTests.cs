// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: MediaHttpServerTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Net; // Proporciona HttpClient, RangeHeaderValue y HttpStatusCode
using System.Net.Http.Headers; // Proporciona cabeceras de rango para las peticiones
using System.Net.Sockets; // Proporciona TcpClient para peticiones HTTP crudas
using System.Text; // Proporciona Encoding para construir peticiones de texto
using Fedo.StageLnk.Server; // Importa el servidor de medios (MediaHttpServer)
using Xunit; // Proporciona el atributo [Fact] de xUnit

namespace Fedo.StageLnk.Tests;

/// <summary>
/// Pruebas del servidor HTTP de streaming de medios: servicio completo, rangos
/// (parcial, abierto y sufijo), tipos MIME, y protección contra rutas fuera de
/// la biblioteca (traversal) y archivos inexistentes.
/// </summary>
public sealed class MediaHttpServerTests
{
    // Crea un directorio temporal con un archivo binario de 100 bytes de patrón conocido
    private static string CreateSampleFile()
    {
        // Ruta única del directorio de pruebas en el directorio temporal del sistema
        string dir = Path.Combine(Path.GetTempPath(), "fedo-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); // Garantiza que el directorio exista
        // Escribe los 100 bytes (valores 0..99) en el archivo de muestra
        File.WriteAllBytes(Path.Combine(dir, "muestra.bin"), Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        return dir; // Devuelve la ruta del directorio de pruebas
    }

    // Crea y arranca el servidor HTTP en un puerto efímero (0) y devuelve el puerto real
    private static (MediaHttpServer Server, int Port) StartServer(string root)
    {
        var server = new MediaHttpServer(root, 0); // Instancia el servidor con puerto automático
        server.Start(); // Arranca el listener
        return (server, server.BoundPort); // Devuelve el servidor y el puerto en uso
    }

    // Envía una petición HTTP cruda por socket y devuelve la respuesta completa como texto
    private static async Task<string> RawGetAsync(int port, string requestLine, string? extraHeaders = null)
    {
        using var client = new TcpClient(); // Cliente TCP para la conexión cruda
        await client.ConnectAsync(IPAddress.Loopback, port); // Conecta al servidor local
        await using var stream = client.GetStream(); // Obtiene el stream de la conexión
        // Construye la petición con la línea de petición y las cabeceras dadas
        string request = $"{requestLine}\r\nHost: localhost\r\nConnection: close\r\n{extraHeaders}\r\n";
        byte[] requestBytes = Encoding.ASCII.GetBytes(request); // Codifica la petición a bytes
        await stream.WriteAsync(requestBytes); // Envía la petición al servidor
        using var response = new MemoryStream(); // Buffer que acumula la respuesta
        await stream.CopyToAsync(response); // Lee la respuesta hasta que el servidor cierre
        return Encoding.ASCII.GetString(response.ToArray()); // Devuelve la respuesta como texto
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que una petición GET completa devuelve 200, el archivo entero y las cabeceras correctas
    public async Task GetFullFile_Returns200AndAllBytes()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            using var http = new HttpClient(); // Cliente HTTP de alto nivel
            // Solicita el archivo completo por su ruta relativa
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/muestra.bin");
            // El código de estado debe ser 200 OK
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // El servidor anuncia soporte de rangos por bytes
            Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
            // El tipo MIME debe corresponder a binario genérico
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            // El contenido debe ser exactamente los 100 bytes de muestra
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(), body);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un rango cerrado (bytes=a-b) devuelve 206 con el trozo exacto
    public async Task RangeRequest_Returns206WithSubset()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            using var http = new HttpClient(); // Cliente HTTP de alto nivel
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/muestra.bin"); // Petición GET al archivo
            request.Headers.Range = new RangeHeaderValue(0, 3); // Pide solo los bytes 0..3
            using var response = await http.SendAsync(request); // Envía la petición con rango
            // El código de estado debe ser 206 Partial Content
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            // El rango servido debe ser los bytes 0-3 de un total de 100
            Assert.Equal("bytes 0-3/100", response.Content.Headers.ContentRange!.ToString());
            // El cuerpo debe ser exactamente los 4 primeros bytes
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(new byte[] { 0, 1, 2, 3 }, body);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un rango abierto (bytes=a-) sirve desde el offset hasta el final
    public async Task OpenEndedRange_ReturnsTailFromOffset()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            using var http = new HttpClient(); // Cliente HTTP de alto nivel
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/muestra.bin"); // Petición GET al archivo
            request.Headers.Range = new RangeHeaderValue(96, null); // Pide desde el byte 96 hasta el final
            using var response = await http.SendAsync(request); // Envía la petición con rango
            // El código de estado debe ser 206 Partial Content
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            // El rango servido debe ser los bytes 96-99 de un total de 100
            Assert.Equal("bytes 96-99/100", response.Content.Headers.ContentRange!.ToString());
            // El cuerpo debe ser los últimos 4 bytes del archivo
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(new byte[] { 96, 97, 98, 99 }, body);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un rango sufijo (bytes=-n) sirve los últimos n bytes
    public async Task SuffixRange_ReturnsLastBytes()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            using var http = new HttpClient(); // Cliente HTTP de alto nivel
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/muestra.bin"); // Petición GET al archivo
            request.Headers.Range = new RangeHeaderValue(null, 4); // Pide los últimos 4 bytes
            using var response = await http.SendAsync(request); // Envía la petición con rango sufijo
            // El código de estado debe ser 206 Partial Content
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            // El rango servido debe ser los bytes 96-99 de un total de 100
            Assert.Equal("bytes 96-99/100", response.Content.Headers.ContentRange!.ToString());
            // El cuerpo debe ser los últimos 4 bytes del archivo
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(new byte[] { 96, 97, 98, 99 }, body);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que una petición HEAD devuelve cabeceras sin cuerpo
    public async Task HeadRequest_ReturnsHeadersWithoutBody()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            // Envía una petición HEAD cruda por socket
            string response = await RawGetAsync(port, "HEAD /muestra.bin HTTP/1.1");
            // La respuesta debe comenzar con 200 OK
            Assert.StartsWith("HTTP/1.1 200", response);
            // La respuesta debe anunciar el tamaño de 100 bytes
            Assert.Contains("Content-Length: 100", response);
            // No debe haber cuerpo en una respuesta HEAD (solo cabeceras y la línea vacía)
            Assert.EndsWith("\r\n\r\n", response);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que intentos de salir de la biblioteca (traversal) se responden con 404
    public async Task TraversalPath_Returns404()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            // Envía una petición cruda que intenta subir de nivel
            string response = await RawGetAsync(port, "GET /../secreto.txt HTTP/1.1");
            // La respuesta debe ser 404 Not Found
            Assert.StartsWith("HTTP/1.1 404", response);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que una ruta con barras invertidas y ".." también se rechaza
    public async Task BackslashTraversal_Returns404()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            // Envía una petición cruda con barras invertidas intentando subir de nivel
            string response = await RawGetAsync(port, "GET /..%5C..%5Csecreto.txt HTTP/1.1");
            // La respuesta debe ser 404 Not Found
            Assert.StartsWith("HTTP/1.1 404", response);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que un archivo inexistente dentro de la biblioteca devuelve 404
    public async Task MissingFile_Returns404()
    {
        // Prepara el directorio con el archivo de muestra
        string dir = CreateSampleFile();
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            using var http = new HttpClient(); // Cliente HTTP de alto nivel
            // Solicita un archivo que no existe
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/no-existe.mp4");
            // El código de estado debe ser 404 Not Found
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que las peticiones con espacio en el nombre (URL codificado) se sirven correctamente
    public async Task EncodedSpaceInPath_ReturnsFile()
    {
        // Ruta única del directorio de pruebas en el directorio temporal del sistema
        string dir = Path.Combine(Path.GetTempPath(), "fedo-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); // Garantiza que el directorio exista
        // Crea un archivo cuyo nombre contiene un espacio
        File.WriteAllText(Path.Combine(dir, "FX Beat.wav"), "hola");
        // Crea y arranca el servidor HTTP sobre ese directorio
        var (server, port) = StartServer(dir);
        await using (server) // Asegura la liberación del servidor al terminar
        {
            using var http = new HttpClient(); // Cliente HTTP de alto nivel
            // Solicita el archivo con el espacio codificado como %20
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/FX%20Beat.wav");
            // El código de estado debe ser 200 OK
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // El cuerpo debe contener el texto escrito
            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal("hola", body);
        }
        // Limpia el directorio temporal de pruebas
        Directory.Delete(dir, recursive: true);
    }
}
