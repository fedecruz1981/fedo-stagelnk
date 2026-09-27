// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: ScannerTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Client;
using Fedo.StageLnk.Protocol;
using Fedo.StageLnk.Server;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>Pruebas del escáner de biblioteca y del almacén de medios del cliente.</summary>
public sealed class LibraryScannerTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el escáner encuentra un audio y calcula su hash y tipo
    public void Scan_FindsAudioFile_WithHashAndKind()
    {
        // Crea un directorio temporal para el escaneo
        string dir = TestUtils.NewTempDir();
        try
        {
            // Escribe un WAV de tono y devuelve su ruta
            string wav = TestUtils.WriteToneFile(dir);

            // Escanea el directorio en busca de medios
            var results = new LibraryScanner().Scan(dir);

            // Debe haber exactamente un resultado
            var asset = Assert.Single(results);
            // El archivo debe clasificarse como audio
            Assert.Equal(MediaKind.Audio, asset.Kind);
            // El nombre debe derivarse del archivo sin extensión
            Assert.Equal("Tono de prueba", asset.Name);
            // La ruta relativa debe ser el nombre del archivo
            Assert.Equal("Tono de prueba.wav", asset.RelativePath);
            // El tamaño debe coincidir con el archivo real
            Assert.Equal(new FileInfo(wav).Length, asset.Size);
            // El hash SHA-256 no debe estar vacío
            Assert.False(string.IsNullOrWhiteSpace(asset.Sha256));
            // El id del activo debe ser su hash
            Assert.Equal(asset.Sha256, asset.Id);
            // La duración debe ser mayor o igual a cero
            Assert.True(asset.DurationSeconds >= 0);
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el escáner ignora archivos con extensiones no soportadas
    public void Scan_IgnoresUnsupportedFiles()
    {
        // Crea un directorio temporal para el escaneo
        string dir = TestUtils.NewTempDir();
        try
        {
            // Escribe un archivo de texto que no es media
            File.WriteAllText(Path.Combine(dir, "notas.txt"), "no es media");
            // Escribe un archivo binario genérico
            File.WriteAllBytes(Path.Combine(dir, "datos.bin"), new byte[16]);

            // El escáner no debe detectar ningún medio
            Assert.Empty(new LibraryScanner().Scan(dir));
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que escanear un directorio inexistente devuelve una lista vacía
    public void Scan_MissingDirectory_ReturnsEmpty()
    {
        // Construye una ruta de directorio que no existe
        string missing = Path.Combine(TestUtils.NewTempDir(), "no-existe");
        // Escanear un directorio inexistente devuelve lista vacía
        Assert.Empty(new LibraryScanner().Scan(missing));
        // Limpia el directorio temporal creado por NewTempDir
        TestUtils.DeleteTempDir(Path.GetDirectoryName(missing)!);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que los archivos en subdirectorios conservan rutas relativas
    public void Scan_NestedDirectories_UsesRelativePaths()
    {
        // Crea un directorio temporal para el escaneo
        string dir = TestUtils.NewTempDir();
        try
        {
            // Crea subdirectorios anidados para el medio
            Directory.CreateDirectory(Path.Combine(dir, "videos", "escena1"));
            // Copia el tono de prueba dentro del subdirectorio anidado
            File.Copy(TestUtils.WriteToneFile(dir), Path.Combine(dir, "videos", "escena1", "clip.wav"));

            // Escanea el directorio raíz
            var results = new LibraryScanner().Scan(dir);

            // Localiza el único activo llamado "clip"
            var clip = Assert.Single(results.Where(a => a.Name == "clip"));
            // La ruta relativa debe conservar la estructura de subdirectorios
            Assert.Equal(Path.Combine("videos", "escena1", "clip.wav"), clip.RelativePath);
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }
}

public sealed class MediaProbeTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el sondeo lee la duración cuando ffprobe está disponible
    public void Probe_ReadsDuration_WhenFfprobeAvailable()
    {
        // Si ffprobe no está disponible el test se omite
        if (!TestUtils.FfprobeAvailable)
            return; // ffprobe no disponible: se omite

        // Crea un directorio temporal para el archivo
        string dir = TestUtils.NewTempDir();
        try
        {
            // Escribe el tono de prueba y obtiene su ruta
            string wav = TestUtils.WriteToneFile(dir);
            // Sondea el archivo para obtener sus metadatos
            var info = MediaProbe.Probe(wav);

            // El resultado del sondeo no debe ser nulo
            Assert.NotNull(info);
            // El tono generado debe durar 3 segundos
            Assert.Equal(3, info.DurationSeconds);
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que sondear un archivo inexistente devuelve null
    public void Probe_MissingFile_ReturnsNull()
    {
        // Si ffprobe no está disponible el test se omite
        if (!TestUtils.FfprobeAvailable)
            // Sale del test cuando no hay ffprobe
            return;

        // Crea un directorio temporal
        string dir = TestUtils.NewTempDir();
        try
        {
            // Sondear un archivo que no existe debe devolver null
            Assert.Null(MediaProbe.Probe(Path.Combine(dir, "no-existe.wav")));
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }
}

public sealed class MediaStoreTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que Missing solo reporta los activos ausentes en disco
    public void Missing_ReportsOnlyAbsent()
    {
        // Crea un directorio temporal para el almacén
        string dir = TestUtils.NewTempDir();
        try
        {
            // Crea el almacén de medios sobre el directorio temporal
            var store = new MediaStore(dir);
            // Activo que sí existe en disco
            var present = new MediaAsset { Id = "a", Name = "A", Kind = MediaKind.Audio, RelativePath = "a.wav", Size = 4, Sha256 = "a" };
            // Activo que no existe en disco
            var absent = new MediaAsset { Id = "b", Name = "B", Kind = MediaKind.Audio, RelativePath = "b.wav", Size = 4, Sha256 = "b" };

            // Crea el archivo "a.wav" en el directorio
            File.WriteAllText(Path.Combine(dir, "a.wav"), "data");

            // Pide al almacén cuáles de los activos faltan
            var missing = store.Missing(new[] { present, absent });
            // Solo debe faltar el activo "b"
            Assert.Equal(new[] { "b" }, missing);
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que Finalize mueve el archivo temporal a su ruta final
    public void Finalize_MovesTempToFinalPath()
    {
        // Crea un directorio temporal para el almacén
        string dir = TestUtils.NewTempDir();
        try
        {
            // Crea el almacén de medios sobre el directorio temporal
            var store = new MediaStore(dir);
            // Activo cuya ruta final tiene un subdirectorio
            var asset = new MediaAsset { Id = "x", Name = "X", Kind = MediaKind.Audio, RelativePath = Path.Combine("sub", "x.wav"), Size = 5, Sha256 = "x" };

            // Obtiene la ruta temporal de recepción para el activo
            string temp = store.TempPathFor("x");

            // Abre un receptor de archivo para el id "x"
            using var receiver = new FileReceiver("x", temp, 5);
            // Añade los bytes de "hello" al receptor
            receiver.Append(System.Text.Encoding.UTF8.GetBytes("hello"));
            // Comprueba si la recepción quedó completa
            bool complete = receiver.TryComplete(out _);
            // Mueve el archivo temporal a la ruta final del activo
            store.Finalize(receiver, asset);

            // La recepción debe haberse completado
            Assert.True(complete);
            // El archivo debe existir en la ruta final (sub/x.wav)
            Assert.True(File.Exists(Path.Combine(dir, "sub", "x.wav")));
            // El archivo temporal ya no debe existir
            Assert.False(File.Exists(temp));
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }
}
