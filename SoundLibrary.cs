using NAudio.Wave;
using System.IO;
using System.Text.RegularExpressions;

namespace VRSoundboard;

public sealed class SoundLibrary(Storage storage)
{
    private readonly object _gate = new();
    private readonly List<Sound> _sounds = storage.LoadSounds();
    public event Action<string, object?>? Changed;
    public IReadOnlyList<Sound> All { get { lock (_gate) return _sounds.OrderBy(s => s.SortOrder).Select(Clone).ToList(); } }
    public Sound? Get(Guid id) { lock (_gate) return _sounds.Where(s => s.Id == id).Select(Clone).FirstOrDefault(); }
    private static Sound Clone(Sound s) => System.Text.Json.JsonSerializer.Deserialize<Sound>(System.Text.Json.JsonSerializer.Serialize(s))!;

    public async Task<Sound> ImportAsync(Stream stream, string filename, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (ext is not (".mp3" or ".wav" or ".m4a" or ".aac" or ".wma")) throw new InvalidDataException("Supported formats: MP3, WAV, M4A, AAC and WMA.");
        var id = Guid.NewGuid();
        var sourcePath = Path.Combine(storage.SoundsPath, id + ext);
        var cachePath = Path.Combine(storage.CachePath, id + ".wav");
        try
        {
            await using (var file = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await stream.CopyToAsync(file, ct);
            double duration = 0;
            await Task.Run(() =>
            {
                using var reader = new AudioFileReader(sourcePath);
                duration = reader.TotalTime.TotalSeconds;
                if (duration <= 0 || duration > 3600) throw new InvalidDataException("Audio duration must be between 0 and 3600 seconds.");
                WaveFileWriter.CreateWaveFile16(cachePath, reader);
            }, ct);
            var name = Regex.Replace(Path.GetFileNameWithoutExtension(filename), @"[\p{C}]", "").Trim();
            var sound = new Sound { Id = id, Name = string.IsNullOrWhiteSpace(name) ? "Sound" : name[..Math.Min(name.Length, 100)], SourceFilename = Path.GetFileName(filename), StoredFilename = id + ".wav", SourceDurationSeconds = duration };
            lock (_gate) { sound.SortOrder = _sounds.Count; _sounds.Add(sound); storage.Save(sound); }
            Changed?.Invoke("sound-added", sound);
            return Clone(sound);
        }
        catch { File.Delete(sourcePath); File.Delete(cachePath); throw; }
    }

    public Sound Update(Guid id, Action<Sound> change)
    {
        Sound result;
        lock (_gate)
        {
            var index = _sounds.FindIndex(x => x.Id == id);
            if (index < 0) throw new KeyNotFoundException("Sound not found.");
            var s = Clone(_sounds[index]);
            change(s);
            s.Name = s.Name.Trim();
            if (s.Name.Length is < 1 or > 100) throw new ArgumentException("Name must contain 1 to 100 characters.");
            s.Volume = Math.Clamp(s.Volume, 0, 1);
            s.OutputGain = Math.Clamp(s.OutputGain, 0, 1);
            s.StartSeconds = Math.Clamp(s.StartSeconds, 0, Math.Max(0, s.SourceDurationSeconds - .01));
            if (s.EndSeconds is not null) s.EndSeconds = Math.Clamp(s.EndSeconds.Value, s.StartSeconds + .01, s.SourceDurationSeconds);
            if (s.Mode is not ("toggle" or "retrigger")) throw new ArgumentException("Invalid playback mode.");
            storage.Save(s); _sounds[index] = s; result = Clone(s);
        }
        Changed?.Invoke("sound-changed", result);
        return result;
    }
    public void Delete(Guid id)
    {
        DeleteMany([id]);
    }
    public void DeleteMany(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return;
        List<Sound> removed;
        lock (_gate)
        {
            if (ids.Distinct().Count() != ids.Count || ids.Any(id => _sounds.All(sound => sound.Id != id))) throw new KeyNotFoundException("One or more sounds were not found.");
            removed = _sounds.Where(sound => ids.Contains(sound.Id)).ToList();
            foreach (var sound in removed) { _sounds.Remove(sound); storage.Delete(sound.Id); }
            for (var i = 0; i < _sounds.Count; i++) { _sounds[i].SortOrder = i; storage.Save(_sounds[i]); }
        }
        foreach (var sound in removed)
        {
            File.Delete(Path.Combine(storage.SoundsPath, sound.Id + Path.GetExtension(sound.SourceFilename).ToLowerInvariant()));
            File.Delete(Path.Combine(storage.CachePath, sound.StoredFilename));
            if (sound.ImageFilename is not null) File.Delete(Path.Combine(storage.ImagesPath, sound.ImageFilename));
        }
        if (removed.Count == 1) Changed?.Invoke("sound-deleted", new { id = removed[0].Id });
        else Changed?.Invoke("sounds-deleted", new { ids = removed.Select(sound => sound.Id).ToArray() });
    }
    public void Reorder(IReadOnlyList<Guid> ids)
    {
        lock (_gate)
        {
            if (ids.Count != _sounds.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => _sounds.All(s => s.Id != id))) throw new ArgumentException("Order must contain every sound once.");
            for (var i = 0; i < ids.Count; i++) { var sound = _sounds.First(s => s.Id == ids[i]); sound.SortOrder = i; storage.Save(sound); }
        }
        Changed?.Invoke("order-changed", ids);
    }
}
