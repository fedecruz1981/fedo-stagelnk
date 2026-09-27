// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Tests :: CoreTests
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Security.Cryptography;
using Fedo.StageLnk.Protocol;
using Fedo.StageLnk.Server;
using Xunit;

namespace Fedo.StageLnk.Tests;

/// <summary>Pruebas de utilidades del núcleo: hash, persistencia y cue list.</summary>
public sealed class HashTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el hash de un archivo vacío coincide con el vector SHA-256 conocido
    public void Sha256File_KnownVector()
    {
        // Crea un directorio temporal para aislar la prueba
        string dir = TestUtils.NewTempDir();
        try
        {
            // Construye la ruta del archivo vacío dentro del directorio temporal
            string path = Path.Combine(dir, "empty.txt");
            // Escribe un archivo vacío (0 bytes) en la ruta calculada
            File.WriteAllBytes(path, Array.Empty<byte>());

            // Calcula el hash SHA-256 del archivo vacío
            string hash = Hash.Sha256File(path);
            // Verifica el hash contra el vector conocido del SHA-256 de 0 bytes
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
        }
        finally
        {
            // Limpia el directorio temporal siempre, incluso si falla el test
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que el hash del archivo coincide con el calculado en memoria
    public void Sha256File_MatchesInMemoryHash()
    {
        // Crea un directorio temporal para aislar la prueba
        string dir = TestUtils.NewTempDir();
        try
        {
            // Construye la ruta del archivo binario dentro del directorio temporal
            string path = Path.Combine(dir, "data.bin");
            // Reserva un buffer de 512 bytes
            var data = new byte[512];
            // Rellena el buffer con bytes aleatorios
            Random.Shared.NextBytes(data);
            // Escribe el buffer aleatorio como archivo binario
            File.WriteAllBytes(path, data);

            // Calcula el hash esperado en memoria sobre los mismos bytes
            string expected = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            // Compara el hash del archivo con el hash calculado en memoria
            Assert.Equal(expected, Hash.Sha256File(path));
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }
}

public sealed class CueListTests
{
    // Factoría que crea una cue con su número y un nombre interpolado
    private static Cue Make(int number) => new() { Number = number, Name = $"Cue {number}" };

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que NextAfter devuelve la siguiente cue en orden numérico
    public void NextAfter_ReturnsNextInOrder()
    {
        // Crea una lista de cues vacía
        var list = new CueList();
        // Reemplaza el contenido con las cues 1, 3 y 5
        list.ReplaceAll(new[] { Make(1), Make(3), Make(5) });

        // Tras la 1 debe venir la 3
        Assert.Equal(3, list.NextAfter(1)!.Number);
        // Tras la 3 debe venir la 5
        Assert.Equal(5, list.NextAfter(3)!.Number);
        // Tras la 5 no existe una siguiente cue
        Assert.Null(list.NextAfter(5));
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que PreviousBefore devuelve la cue anterior en orden numérico
    public void PreviousBefore_ReturnsPreviousInOrder()
    {
        // Crea una lista de cues vacía
        var list = new CueList();
        // Reemplaza el contenido con las cues 1, 3 y 5
        list.ReplaceAll(new[] { Make(1), Make(3), Make(5) });

        // Antes de la 3 debe estar la 1
        Assert.Equal(1, list.PreviousBefore(3)!.Number);
        // Antes de la 6 (que no existe) debe estar la 5
        Assert.Equal(5, list.PreviousBefore(6)!.Number);
        // Antes de la 1 no hay una cue anterior
        Assert.Null(list.PreviousBefore(1));
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que Upsert reemplaza la cue con el mismo número sin duplicarla
    public void Upsert_ReplacesSameNumber()
    {
        // Crea una lista de cues vacía
        var list = new CueList();
        // Inserta (o actualiza) la cue 1
        list.Upsert(Make(1));
        // Inserta de nuevo la cue 1; debe reemplazar a la anterior
        list.Upsert(Make(1));

        // Comprueba que solo queda una única cue
        Assert.Single(list.Cues);
        // Verifica que la versión aumentó con cada upsert (dos en total)
        Assert.Equal(2, list.Version);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que ReplaceAll reinicia el contenido e incrementa la versión
    public void ReplaceAll_ResetsContentAndBumpsVersion()
    {
        // Crea una lista de cues vacía
        var list = new CueList();
        // Añade una cue inicial
        list.Upsert(Make(1));
        // Guarda la versión actual antes de reemplazar
        long version = list.Version;

        // Reemplaza todo el contenido por las cues 7 y 8
        list.ReplaceAll(new[] { Make(7), Make(8) });

        // Comprueba que ahora hay dos cues
        Assert.Equal(2, list.Cues.Count);
        // Verifica que la primera cue es la 7
        Assert.Equal(7, list.Cues[0].Number);
        // Confirma que la versión aumentó respecto a la guardada
        Assert.True(list.Version > version);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que Remove solo incrementa la versión si la cue existe
    public void Remove_BumpsVersionOnlyWhenPresent()
    {
        // Crea una lista de cues vacía
        var list = new CueList();
        // Añade la cue 1
        list.Upsert(Make(1));
        // Guarda la versión actual de la lista
        long v = list.Version;

        // Intenta eliminar una cue inexistente (la 2)
        list.Remove(2);
        // La versión no debe cambiar al no existir la cue
        Assert.Equal(v, list.Version);

        // Elimina la cue 1 que sí existe
        list.Remove(1);
        // La versión debe incrementar al eliminar una cue existente
        Assert.True(list.Version > v);
        // La lista debe quedar vacía
        Assert.Empty(list.Cues);
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la lista de cues se expone ordenada por número
    public void Cues_AreSortedByNumber()
    {
        // Crea una lista de cues vacía
        var list = new CueList();
        // Añade la cue 5
        list.Upsert(Make(5));
        // Añade la cue 1
        list.Upsert(Make(1));
        // Añade la cue 3
        list.Upsert(Make(3));

        // Verifica que las cues se ordenan por número al exponerlas
        Assert.Equal(new[] { 1, 3, 5 }, list.Cues.Select(c => c.Number).ToArray());
    }
}

public sealed class MediaLibraryTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que ReplaceAll carga medios y Find localiza por id sin diferenciar mayúsculas
    public void ReplaceAll_AndFind()
    {
        // Crea una biblioteca de medios vacía
        var lib = new MediaLibrary();
        // Construye un activo de audio de ejemplo
        var asset = new MediaAsset
        {
            // Identificador único del activo
            Id = "abc",
            // Nombre visible del activo
            Name = "Uno",
            // Tipo de medio: audio
            Kind = MediaKind.Audio,
            // Ruta relativa al archivo dentro de la biblioteca
            RelativePath = "uno.wav",
            // Tamaño del archivo en bytes
            Size = 10,
            // Hash SHA-256 del archivo
            Sha256 = "abc"
        };

        // Reemplaza el contenido de la biblioteca por este activo
        lib.ReplaceAll(new[] { asset });

        // La versión debe ser 1 tras el primer reemplazo
        Assert.Equal(1, lib.Version);
        // Verifica que la búsqueda ignora mayúsculas (busca por "ABC")
        Assert.Same(asset, lib.Find("ABC")); // case-insensitive
        // Confirma que también encuentra por el id exacto en minúsculas
        Assert.Same(asset, lib.Find("abc"));
        // No debe encontrar un id inexistente
        Assert.Null(lib.Find("xyz"));
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que la colección All se expone ordenada alfabéticamente por nombre
    public void All_IsSortedByName()
    {
        // Crea una biblioteca de medios vacía
        var lib = new MediaLibrary();
        // Carga tres activos con nombres en orden desordenado
        lib.ReplaceAll(new[]
        {
            // Activo "Zeta"
            NewAsset("z", "Zeta"),
            // Activo "Alfa"
            NewAsset("a", "Alfa"),
            // Activo "Milo"
            NewAsset("m", "Milo")
        });

        // Comprueba que los nombres salen ordenados: Alfa, Milo, Zeta
        Assert.Equal(new[] { "Alfa", "Milo", "Zeta" }, lib.All.Select(m => m.Name).ToArray());
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba la clasificación de tipo de medio según la extensión del archivo
    public void KindForExtension_Classifies()
    {
        // La extensión .mp4 debe clasificarse como video
        Assert.Equal(MediaKind.Video, MediaAsset.KindForExtension(".mp4"));
        // La extensión se evalúa sin distinguir mayúsculas (.MKV)
        Assert.Equal(MediaKind.Video, MediaAsset.KindForExtension(".MKV"));
        // La extensión .png debe clasificarse como imagen
        Assert.Equal(MediaKind.Image, MediaAsset.KindForExtension(".png"));
        // La extensión .wav debe clasificarse como audio
        Assert.Equal(MediaKind.Audio, MediaAsset.KindForExtension(".wav"));
        // Una extensión no soportada devuelve null
        Assert.Null(MediaAsset.KindForExtension(".txt"));
    }

    // Factoría de activos de video con id y nombre dados
    private static MediaAsset NewAsset(string id, string name)
        // Crea el activo con ruta relativa y hash derivados del id
        => new() { Id = id, Name = name, Kind = MediaKind.Video, RelativePath = $"{id}.mp4", Sha256 = id };
}

public sealed class ProjectFileTests
{
    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que guardar y cargar un proyecto conserva todos los datos
    public void ProjectRoundtrip_PreservesData()
    {
        // Crea un directorio temporal para guardar el proyecto
        string dir = TestUtils.NewTempDir();
        try
        {
            // Construye la ruta del archivo de proyecto
            string path = Path.Combine(dir, "show.fsl");
            // Crea un proyecto con datos de ejemplo
            var project = new StageProject
            {
                // Nombre del proyecto
                Name = "Show",
                // Directorio raíz de la biblioteca de medios
                LibraryRoot = dir,
                // Colección de medios del proyecto
                Media =
                {
                    // Activo de audio con sus propiedades completas
                    new MediaAsset { Id = "m1", Name = "Medio", Kind = MediaKind.Audio, RelativePath = "medio.wav", Size = 100, Sha256 = "m1" }
                },
                // Colección de cues del proyecto
                Cues =
                {
                    // Primera cue con una capa de video
                    new Cue
                    {
                        // Número de la cue
                        Number = 1,
                        // Nombre de la cue
                        Name = "Intro",
                        // Tipo de cue: video
                        Kind = CueKind.Video,
                        // Fundido de entrada en segundos
                        FadeIn = 1.0,
                        // Capas superpuestas que componen la cue
                        Layers = { new LayerSpec { ZOrder = 1, MediaId = "m1", Opacity = 0.5 } }
                    },
                    // Segunda cue de tipo black (sin capas)
                    new Cue { Number = 2, Name = "Black", Kind = CueKind.Black }
                }
            };

            // Guarda el proyecto en disco
            ProjectFile.SaveProject(path, project);
            // Carga el proyecto recién guardado
            var loaded = ProjectFile.LoadProject(path);

            // El proyecto cargado no debe ser nulo
            Assert.NotNull(loaded);
            // Se conserva el nombre del proyecto
            Assert.Equal("Show", loaded.Name);
            // Se conserva un único medio
            Assert.Single(loaded.Media);
            // Se conservan las dos cues
            Assert.Equal(2, loaded.Cues.Count);
            // Se conserva el número de la primera cue
            Assert.Equal(1, loaded.Cues[0].Number);
            // Se conserva el nombre de la primera cue
            Assert.Equal("Intro", loaded.Cues[0].Name);
            // Se conserva el fade in de la primera cue
            Assert.Equal(1.0, loaded.Cues[0].FadeIn);
            // Se conserva una única capa en la primera cue
            Assert.Single(loaded.Cues[0].Layers);
            // Se conserva el id de medio de la capa
            Assert.Equal("m1", loaded.Cues[0].Layers[0].MediaId);
            // Se conserva la opacidad de la capa
            Assert.Equal(0.5, loaded.Cues[0].Layers[0].Opacity);
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba que guardar y cargar una biblioteca de cues conserva los datos
    public void CueLibraryRoundtrip_PreservesData()
    {
        // Crea un directorio temporal para guardar la biblioteca
        string dir = TestUtils.NewTempDir();
        try
        {
            // Construye la ruta del archivo de biblioteca de cues
            string path = Path.Combine(dir, "cues.fslcue");
            // Crea una biblioteca de cues con una entrada
            var library = new CueLibrary
            {
                // Nombre de la biblioteca
                Name = "Cue Library",
                // Colección con una cue de imagen
                Cues = { new Cue { Number = 10, Name = "Logo", Kind = CueKind.Image } }
            };

            // Guarda la biblioteca en disco
            ProjectFile.SaveCueLibrary(path, library);
            // Carga la biblioteca recién guardada
            var loaded = ProjectFile.LoadCueLibrary(path);

            // La biblioteca cargada no debe ser nula
            Assert.NotNull(loaded);
            // Se conserva el nombre de la biblioteca
            Assert.Equal("Cue Library", loaded.Name);
            // Se conserva una única cue
            Assert.Single(loaded.Cues);
            // Se conserva el número de la cue
            Assert.Equal(10, loaded.Cues[0].Number);
            // Se conserva el tipo de la cue
            Assert.Equal(CueKind.Image, loaded.Cues[0].Kind);
        }
        finally
        {
            // Limpia el directorio temporal siempre
            TestUtils.DeleteTempDir(dir);
        }
    }

    // Atributo xUnit que declara el método como prueba
    [Fact]
    // Prueba la detección de tipo de archivo por extensión
    public void ExtensionDetection()
    {
        // La extensión .FSL se detecta como proyecto completo (no distingue mayúsculas)
        Assert.True(ProjectFile.IsFullProject("show.FSL"));
        // La extensión .fslcue se detecta como biblioteca de cues
        Assert.True(ProjectFile.IsCueLibrary("cues.fslcue"));
        // Un archivo .fslcue no debe considerarse proyecto completo
        Assert.False(ProjectFile.IsFullProject("cues.fslcue"));
    }
}
