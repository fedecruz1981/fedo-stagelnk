// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: TextSignTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Client;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>Pruebas del generador de carteles de texto 3D (requieren ffmpeg).</summary>
public sealed class TextSignTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el renderizador genera un PNG y lo reutiliza desde la caché
    public void Render_GeneratesPngAndCaches()
    {
        // Si ffmpeg no está disponible el test se omite
        if (!TestUtils.FfmpegAvailable)
            return;

        // Crea un directorio temporal para los PNG
        string dir = TestUtils.NewTempDir();
        try
        {
            // Crea el renderizador de carteles de texto
            var renderer = new TextSignRenderer();

            // Genera el primer cartel y devuelve su ruta
            string path1 = renderer.Render("Bienvenidos", "#FFD700", "#0A1A2A", 96, dir);
            // Genera el mismo cartel de nuevo (debe servirse de la caché)
            string path2 = renderer.Render("Bienvenidos", "#FFD700", "#0A1A2A", 96, dir);

            // El PNG debe haberse creado en disco
            Assert.True(File.Exists(path1), "El cartel PNG no se generó");
            // Ambas llamadas deben devolver la misma ruta (caché)
            Assert.Equal(path1, path2);
            // El PNG no debe ser sospechosamente pequeño
            Assert.True(new FileInfo(path1).Length > 1000, "El cartel PNG es sospechosamente pequeño");

            // Abre el PNG para inspeccionar su cabecera
            using var fs = File.OpenRead(path1);
            // Buffer para los primeros 8 bytes de la firma
            byte[] signature = new byte[8];
            // Lee los primeros 8 bytes de la firma del archivo
            Assert.Equal(8, fs.Read(signature, 0, 8));
            // Verifica la firma PNG (0x89 'P' 'N' 'G')
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, signature[..4]);
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }
}
