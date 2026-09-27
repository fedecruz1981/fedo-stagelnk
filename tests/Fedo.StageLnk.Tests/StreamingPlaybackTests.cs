// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: StreamingPlaybackTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Client; // Importa el motor híbrido y el renderer de vídeo
using Fedo.StageLnk.Protocol; // Importa MediaAsset y MediaKind
using Xunit; // Proporciona el atributo [Fact] de xUnit

namespace Fedo.StageLnk.Tests;

/// <summary>
/// Pruebas del motor híbrido cuando un medio no está sincronizado todavía:
/// decide entre reproducción local, streaming por HTTP (con y sin FX) y el
/// caso sin streaming disponible. Se verifican las decisiones y las URLs
/// construidas mediante los mensajes de log del motor.
/// </summary>
public sealed class StreamingPlaybackTests
{
    // Crea un motor híbrido listo para reproducir, con captura de log
    private static (HybridEngine Engine, List<string> Logs) CreateEngine(string mediaRoot)
    {
        // Crea el motor híbrido
        var engine = new HybridEngine();
        // Lista que acumula los mensajes de log del motor
        var logs = new List<string>();
        // Captura cada mensaje de log emitido por el motor
        engine.Log += m => logs.Add(m);
        // Inicializa el motor con la raíz de medios dada (directorio temporal vacío)
        engine.Initialize(mediaRoot);
        // Devuelve el motor y su lista de logs
        return (engine, logs);
    }

    // Crea un activo de vídeo con una ruta relativa que incluye subdirectorio y espacios
    private static MediaAsset NewVideoAsset()
    {
        // Activo de vídeo con ruta anidada y nombre con espacios
        return new MediaAsset
        {
            // Identificador del medio
            Id = "vid-1",
            // Nombre visible del medio
            Name = "Intro video",
            // Tipo de medio: vídeo
            Kind = MediaKind.Video,
            // Ruta relativa con subcarpeta y espacios
            RelativePath = @"videos\Intro video.mp4"
        };
    }

    // Crea un activo de audio con ruta simple
    private static MediaAsset NewAudioAsset()
    {
        // Activo de audio con ruta simple
        return new MediaAsset
        {
            // Identificador del medio
            Id = "aud-1",
            // Nombre visible del medio
            Name = "Tono",
            // Tipo de medio: audio
            Kind = MediaKind.Audio,
            // Ruta relativa simple
            RelativePath = "tono.wav"
        };
    }

    [Fact]
    public void Play_VideoMissing_StreamsOverHttp()
    {
        // Directorio temporal vacío (ningún medio en disco)
        string root = TestUtils.NewTempDir();
        // Crea el motor con captura de log
        var (engine, logs) = CreateEngine(root);
        try
        {
            // Configura la URL base del servidor de streaming
            engine.StreamBaseUrl = "http://127.0.0.1:9003";
            // Pide reproducir un vídeo que no existe en disco
            engine.Play(NewVideoAsset());
            // La decisión debe ser streaming por HTTP con la URL escapada correctamente
            Assert.Contains(logs, m => m.Contains("streaming por HTTP") && m.Contains("http://127.0.0.1:9003/videos/Intro%20video.mp4"));
        }
        finally
        {
            // Detiene el motor (mata cualquier ffplay lanzado) en todos los casos
            engine.Stop();
        }
    }

    [Fact]
    public void Play_AudioMissing_StreamsOverHttp_WithoutFx()
    {
        // Directorio temporal vacío (ningún medio en disco)
        string root = TestUtils.NewTempDir();
        // Crea el motor con captura de log
        var (engine, logs) = CreateEngine(root);
        try
        {
            // Configura la URL base del servidor de streaming
            engine.StreamBaseUrl = "http://127.0.0.1:9003";
            // Pide reproducir un audio que no existe en disco
            engine.Play(NewAudioAsset());
            // El audio por streaming se anuncia explícitamente como sin FX, con su URL
            Assert.Contains(logs, m => m.Contains("audio por streaming (sin FX)") && m.Contains("http://127.0.0.1:9003/tono.wav"));
        }
        finally
        {
            // Detiene el motor en todos los casos
            engine.Stop();
        }
    }

    [Fact]
    public void Play_Missing_WithoutStreamBaseUrl_LogsFallback()
    {
        // Directorio temporal vacío (ningún medio en disco)
        string root = TestUtils.NewTempDir();
        // Crea el motor sin URL de streaming (simula una LAN sin servidor HTTP)
        var (engine, logs) = CreateEngine(root);
        try
        {
            // Pide reproducir un vídeo inexistente sin streaming configurado
            engine.Play(NewVideoAsset());
            // Debe informar que el medio no está local y no hay streaming disponible
            Assert.Contains(logs, m => m.Contains("no local y sin streaming disponible"));
        }
        finally
        {
            // Detiene el motor en todos los casos
            engine.Stop();
        }
    }

    [Fact]
    public void Play_LocalVideo_DoesNotUseStreaming()
    {
        // Crea un directorio temporal
        string root = TestUtils.NewTempDir();
        // Escribe el vídeo local en la ruta que coincide con el activo (subcarpeta incluida)
        string full = Path.Combine(root, "videos", "Intro video.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!); // Garantiza la subcarpeta
        File.WriteAllBytes(full, new byte[] { 1, 2, 3 }); // Archivo de relleno
        // Crea el motor con captura de log
        var (engine, logs) = CreateEngine(root);
        try
        {
            // Configura una URL de streaming aunque el medio exista localmente
            engine.StreamBaseUrl = "http://127.0.0.1:9003";
            // Pide reproducir el vídeo que sí existe en disco
            engine.Play(NewVideoAsset());
            // Nunca debe usar streaming cuando el archivo local existe
            Assert.DoesNotContain(logs, m => m.Contains("streaming"));
        }
        finally
        {
            // Detiene el motor en todos los casos
            engine.Stop();
        }
    }
}
