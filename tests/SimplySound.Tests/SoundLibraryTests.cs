using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using VRSoundboard;
using Xunit;

namespace SimplySound.Tests;

public sealed class SoundLibraryTests
{
    [Fact]
    public async Task FailedImportDoesNotLeaveAnInMemorySoundOrPartialFiles()
    {
        var root = CreateRoot();
        var sourcePath = Path.Combine(Path.GetTempPath(), $"SimplySound-import-{Guid.NewGuid():N}.wav");
        try
        {
            var storage = new Storage(root);
            storage.Initialize();
            var library = new SoundLibrary(storage);
            BreakDatabase(storage);
            CreateSilentWave(sourcePath);
            await using var source = File.OpenRead(sourcePath);

            await Assert.ThrowsAsync<SqliteException>(() => library.ImportAsync(source, "test.wav"));

            Assert.Empty(library.All);
            Assert.Empty(Directory.EnumerateFiles(storage.SoundsPath));
            Assert.Empty(Directory.EnumerateFiles(storage.CachePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedBulkDeleteKeepsTheSoundAndItsAssetsAvailable()
    {
        var root = CreateRoot();
        try
        {
            var storage = new Storage(root);
            storage.Initialize();
            var sound = CreateSound(0);
            storage.Save(sound);
            var sourceAsset = Path.Combine(storage.SoundsPath, sound.Id + ".wav");
            var cacheAsset = Path.Combine(storage.CachePath, sound.StoredFilename);
            File.WriteAllBytes(sourceAsset, [1]);
            File.WriteAllBytes(cacheAsset, [1]);
            var library = new SoundLibrary(storage);
            BreakDatabase(storage);

            Assert.Throws<SqliteException>(() => library.Delete(sound.Id));

            Assert.Equal(sound.Id, Assert.Single(library.All).Id);
            Assert.True(File.Exists(sourceAsset));
            Assert.True(File.Exists(cacheAsset));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SuccessfulBulkDeleteCommitsDatabaseAndOrderTogether()
    {
        var root = CreateRoot();
        try
        {
            var storage = new Storage(root);
            storage.Initialize();
            var sounds = new[] { CreateSound(0), CreateSound(1), CreateSound(2) };
            foreach (var sound in sounds) storage.Save(sound);
            var library = new SoundLibrary(storage);

            library.DeleteMany([sounds[0].Id, sounds[1].Id]);

            var remaining = Assert.Single(library.All);
            Assert.Equal(sounds[2].Id, remaining.Id);
            Assert.Equal(0, remaining.SortOrder);
            var persisted = Assert.Single(storage.LoadSounds());
            Assert.Equal(remaining.Id, persisted.Id);
            Assert.Equal(0, persisted.SortOrder);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedOrderPersistenceRollsBackTheWholeBulkDelete()
    {
        var root = CreateRoot();
        try
        {
            var storage = new Storage(root);
            storage.Initialize();
            var sounds = new[] { CreateSound(0), CreateSound(1) };
            foreach (var sound in sounds) storage.Save(sound);
            var library = new SoundLibrary(storage);
            using (var db = new SqliteConnection($"Data Source={Path.Combine(storage.Root, "database", "library.db")};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = $"CREATE TRIGGER fail_reorder BEFORE UPDATE ON sounds WHEN OLD.id='{sounds[1].Id}' BEGIN SELECT RAISE(ABORT, 'forced reorder failure'); END;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<SqliteException>(() => library.Delete(sounds[0].Id));

            Assert.Equal(sounds.Select(sound => sound.Id), library.All.Select(sound => sound.Id));
            Assert.Equal(new[] { 0, 1 }, library.All.Select(sound => sound.SortOrder));
            Assert.Equal(sounds.Select(sound => sound.Id), storage.LoadSounds().Select(sound => sound.Id));
            Assert.Equal(new[] { 0, 1 }, storage.LoadSounds().Select(sound => sound.SortOrder));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReorderCommitsTheEntireLibraryOrderAtomically()
    {
        var root = CreateRoot();
        try
        {
            var storage = new Storage(root);
            storage.Initialize();
            var sounds = new[] { CreateSound(0), CreateSound(1), CreateSound(2) };
            foreach (var sound in sounds) storage.Save(sound);
            var library = new SoundLibrary(storage);
            var expected = new[] { sounds[2].Id, sounds[0].Id, sounds[1].Id };

            library.Reorder(expected);

            Assert.Equal(expected, library.All.Select(sound => sound.Id));
            Assert.Equal(new[] { 0, 1, 2 }, library.All.Select(sound => sound.SortOrder));
            Assert.Equal(expected, new SoundLibrary(storage).All.Select(sound => sound.Id));
            Assert.Equal(new[] { 0, 1, 2 }, storage.LoadSounds().Select(sound => sound.SortOrder));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HotkeyAssignmentsReturnOnlyAssignedSoundsInLibraryOrder()
    {
        var root = CreateRoot();
        try
        {
            var storage = new Storage(root);
            storage.Initialize();
            var sounds = new[] { CreateSound(0), CreateSound(1), CreateSound(2) };
            sounds[0].Hotkey = "Control+Alt+A";
            sounds[2].Hotkey = "Control+Alt+C";
            foreach (var sound in sounds) storage.Save(sound);
            var library = new SoundLibrary(storage);

            var assignments = library.HotkeyAssignments;
            Assert.Same(assignments, library.HotkeyAssignments);
            Assert.Equal(
                [
                    new SoundHotkeyAssignment(sounds[0].Id, sounds[0].Name, "Control+Alt+A"),
                    new SoundHotkeyAssignment(sounds[2].Id, sounds[2].Name, "Control+Alt+C"),
                ],
                assignments);
            using (var payload = JsonDocument.Parse(JsonSerializer.Serialize(assignments, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
                Assert.Equal(new[] { "id", "name", "hotkey" }, payload.RootElement[0].EnumerateObject().Select(property => property.Name));

            library.Update(sounds[2].Id, sound => sound.Name = "Renamed sound");
            var renamed = library.HotkeyAssignments;
            Assert.NotSame(assignments, renamed);
            Assert.Equal("Renamed sound", renamed[1].Name);

            library.Reorder([sounds[2].Id, sounds[1].Id, sounds[0].Id]);
            Assert.Equal(
                [sounds[2].Id, sounds[0].Id],
                library.HotkeyAssignments.Select(assignment => assignment.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedReorderLeavesMemoryAndDatabaseInTheOriginalOrder()
    {
        var root = CreateRoot();
        try
        {
            var storage = new Storage(root);
            storage.Initialize();
            var sounds = new[] { CreateSound(0), CreateSound(1), CreateSound(2) };
            foreach (var sound in sounds) storage.Save(sound);
            var library = new SoundLibrary(storage);
            using (var db = new SqliteConnection($"Data Source={Path.Combine(storage.Root, "database", "library.db")};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = $"CREATE TRIGGER fail_reorder BEFORE UPDATE ON sounds WHEN OLD.id='{sounds[1].Id}' BEGIN SELECT RAISE(ABORT, 'forced reorder failure'); END;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<SqliteException>(() => library.Reorder([sounds[2].Id, sounds[1].Id, sounds[0].Id]));

            Assert.Equal(sounds.Select(sound => sound.Id), library.All.Select(sound => sound.Id));
            Assert.Equal(new[] { 0, 1, 2 }, library.All.Select(sound => sound.SortOrder));
            Assert.Equal(sounds.Select(sound => sound.Id), storage.LoadSounds().Select(sound => sound.Id));
            Assert.Equal(new[] { 0, 1, 2 }, storage.LoadSounds().Select(sound => sound.SortOrder));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot() => Path.Combine(Path.GetTempPath(), "SimplySound-library-test-" + Guid.NewGuid().ToString("N"));

    private static Sound CreateSound(int order) => new()
    {
        SortOrder = order,
        Name = $"Sound {order}",
        SourceFilename = $"source-{order}.wav",
        StoredFilename = Guid.NewGuid() + ".wav",
        SourceDurationSeconds = .1,
    };

    private static void BreakDatabase(Storage storage)
    {
        SqliteConnection.ClearAllPools();
        var database = Path.Combine(storage.Root, "database", "library.db");
        if (File.Exists(database)) File.Delete(database);
        Directory.CreateDirectory(database);
    }

    private static void CreateSilentWave(string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        var silence = new byte[4800];
        writer.Write(silence, 0, silence.Length);
    }
}
