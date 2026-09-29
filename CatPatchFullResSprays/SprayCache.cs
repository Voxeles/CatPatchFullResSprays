using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CatPatchFullResSprays;

internal sealed class SprayCache : IDisposable
{
    private const int MaxEntries = 8192;
    private readonly object _lock = new();
    private readonly string _directory;
    private readonly Dictionary<string, LinkedListNode<SprayImage>> _memory = new();
    private readonly LinkedList<SprayImage> _lru = new();
    private readonly Dictionary<string, DiskEntry> _disk = new();
    private readonly Dictionary<string, string> _files = new();
    private readonly long _memoryLimit;
    private readonly long _diskLimit;
    private long _memoryBytes;
    private long _diskBytes;
    private bool _disposed;

    private sealed class DiskEntry
    {
        public string Path;
        public int Length;
        public bool Broken;
        public DateTime Accessed;
    }

    public SprayCache(string directory, long memoryLimit, long diskLimit)
    {
        _directory = Path.GetFullPath(directory);
        _memoryLimit = Math.Max(0, memoryLimit);
        _diskLimit = Math.Max(0, diskLimit);

        try
        {
            Directory.CreateDirectory(_directory);

            foreach (string path in Directory.EnumerateFiles(_directory, "*.tmp"))
                TryDelete(path);

            foreach (string path in Directory.EnumerateFiles(_directory))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!SprayImage.IsValidId(id) || !IsImageExtension(Path.GetExtension(path)))
                    continue;

                try
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 0 || info.Length > SprayImage.HardByteSizeLimit)
                        throw new InvalidDataException("Invalid cache file size.");
                    var entry = new DiskEntry { Path = path, Length = (int)info.Length, Accessed = info.LastWriteTimeUtc };
                    if (_disk.TryGetValue(id, out var previous))
                    {
                        if (previous.Length >= entry.Length)
                        {
                            TryDelete(path);
                            continue;
                        }

                        if (!RemoveDisk(id))
                        {
                            TryDelete(path);
                            continue;
                        }
                    }
                    _disk[id] = entry;
                    _diskBytes += entry.Length;
                }
                catch (Exception ex) when (IsIoError(ex))
                {
                    Plugin.Logger.LogWarning(ex.Message);
                    TryDelete(path);
                }
            }

            TrimDisk();

        }
        catch (Exception ex) when (IsIoError(ex))
        {
            Plugin.Logger.LogWarning("Disk cache unavailable: " + ex.Message);
        }
    }

    private static bool IsIoError(Exception ex)
    {
        return ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException;
    }

    public long MemoryBytes
    {
        get
        {
            lock (_lock)
                return _memoryBytes;
        }
    }

    public long DiskBytes
    {
        get
        {
            lock (_lock)
                return _diskBytes;
        }
    }

    public SprayImage Get(string id)
    {
        lock (_lock)
            return _disposed ? null : GetLocked(SprayImage.Normalize(id));
    }

    public SprayImage GetFile(string key)
    {
        lock (_lock)
        {
            if (_disposed || !_files.TryGetValue(key, out string id))
                return null;
            var image = GetLocked(id);
            if (image == null)
                _files.Remove(key);
            return image;
        }
    }

    private SprayImage GetLocked(string id)
    {
        Plugin.LogVerbose($"SprayCache: Get: id={id}");
        if (_memory.TryGetValue(id, out LinkedListNode<SprayImage> node))
        {
            Plugin.LogVerbose($"SprayCache: Get: Got from memory");
            _lru.Remove(node);
            _lru.AddFirst(node);
            TouchDisk(id);
            return node.Value;
        }

        if (!_disk.TryGetValue(id, out var entry) || entry.Broken)
            return null;

        try
        {
            Plugin.LogVerbose($"SprayCache: Get: Checking disk...");
            using var stream = File.OpenRead(entry.Path);
            if (stream.Length != entry.Length)
                throw new InvalidDataException("Invalid cache file length.");

            using var reader = new BinaryReader(stream);
            byte[] bytes = reader.ReadBytes(entry.Length);
            if (bytes.Length != entry.Length)
                throw new EndOfStreamException();
            var image = new SprayImage(id, bytes, backendUploaded: !id.StartsWith("custom-", StringComparison.Ordinal));

            Plugin.LogVerbose($"SprayCache: Get: Got from disk");
            TouchDisk(id);
            AddMemory(image);
            return image;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            Plugin.Logger.LogWarning("Cache read failed: " + ex.Message);
            entry.Broken = true;
            RemoveDisk(id);
            return null;
        }
    }

    public SprayImage Put(SprayImage image)
    {
        Plugin.LogVerbose($"SprayCache: Put: {image.Id}, {image.Bytes.Length / 1024}KiB");
        lock (_lock)
        {
            if (_disposed)
                return image;

            var previous = GetLocked(image.Id);
            if (previous != null
                && previous.Bytes.Length >= image.Bytes.Length
                && (previous.BackendUploaded || !image.BackendUploaded)
                && (string.IsNullOrEmpty(image.FileKey) || image.FileKey == previous.FileKey))
            {
                Plugin.LogVerbose($"SprayCache: Put: New file isn't better than previous. Nothing to do.");
                return previous;
            }

            if (previous != null && previous.Bytes.Length >= image.Bytes.Length)
            {
                image = new SprayImage(
                    previous.Id,
                    previous.Bytes,
                    previous.BackendUploaded || image.BackendUploaded,
                    string.IsNullOrEmpty(image.FileKey) ? previous.FileKey : image.FileKey);
            }

            Plugin.LogVerbose($"SprayCache: Put: Saving image.");
            AddMemory(image);
            WriteDisk(image);
            if (_memory.ContainsKey(image.Id) || _disk.ContainsKey(image.Id))
                RememberFile(image.FileKey, image.Id);
            return image;
        }
    }

    public bool Contains(string id, int minimumSize = 1)
    {
        lock (_lock)
        {
            return !_disposed &&
                   (_memory.TryGetValue(id, out LinkedListNode<SprayImage> node) && node.Value.Bytes.Length >= minimumSize
                    || _disk.TryGetValue(id, out var disk) && !disk.Broken && disk.Length >= minimumSize);
        }
    }

    private void AddMemory(SprayImage image)
    {
        Plugin.LogVerbose($"SprayCache: AddMemory: id={image.Id}, size={image.Bytes.Length / 1024}KiB");
        if (_memory.TryGetValue(image.Id, out LinkedListNode<SprayImage> old))
        {
            _memoryBytes -= old.Value.Bytes.Length;
            _lru.Remove(old);
            _memory.Remove(image.Id);
        }

        if (image.Bytes.Length > _memoryLimit)
            return;

        _memory[image.Id] = _lru.AddFirst(image);
        _memoryBytes += image.Bytes.Length;
        while (_memoryBytes > _memoryLimit || _memory.Count > MaxEntries)
        {
            LinkedListNode<SprayImage> last = _lru.Last;
            _memoryBytes -= last.Value.Bytes.Length;
            _memory.Remove(last.Value.Id);
            _lru.RemoveLast();
        }
    }

    private void RememberFile(string key, string id)
    {
        if (string.IsNullOrEmpty(key))
            return;

        Plugin.LogVerbose($"SprayCache: RememberFile: id={id}, key={key}");
        if (_files.Count >= MaxEntries)
            _files.Remove(_files.Keys.First());
        _files[key] = id;
    }

    private static bool IsImageExtension(string extension)
    {
        return extension is ".png" or ".gif" or ".jpg" or ".webp" or ".bmp" or ".img";
    }

    private static string ImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
            bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10)
            return ".png";
        if (bytes.Length >= 6 && (Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a"))
            return ".gif";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
            return ".jpg";
        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return ".bmp";
        return ".img";
    }

    private void WriteDisk(SprayImage image)
    {
        Plugin.LogVerbose($"SprayCache: WriteDisk: id={image.Id}, size={image.Bytes.Length / 1024}KiB");
        long length = image.Bytes.Length;
        if (length > _diskLimit)
            return;

        string path = Path.Combine(_directory, image.Id + ImageExtension(image.Bytes)), temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            long replacedLength = _disk.TryGetValue(image.Id, out var replacing) ? replacing.Length : 0;
            Plugin.LogVerbose($"SprayCache: WriteDisk: Prev size {replacedLength / 1024}KiB");

            foreach (KeyValuePair<string, DiskEntry> candidate in _disk.OrderBy(p => p.Value.Accessed).ToArray())
            {
                if (_diskBytes - replacedLength + length <= _diskLimit && (_disk.Count < MaxEntries || replacing != null))
                    break;
                if (candidate.Key != image.Id)
                {
                    Plugin.LogVerbose($"SprayCache: WriteDisk: Removing {candidate.Key} to make room");
                    RemoveDisk(candidate.Key);
                }
            }

            if (_diskBytes - replacedLength + length > _diskLimit || _disk.Count >= MaxEntries && replacing == null)
            {
                Plugin.Logger.LogWarning("Disk cache cannot make space; existing files may be locked.");
                return;
            }

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(image.Bytes, 0, image.Bytes.Length);
                stream.Flush(true);
                Plugin.LogVerbose($"SprayCache: WriteDisk: Wrote");
            }

            if (replacing != null && replacing.Path != path && !RemoveDisk(image.Id))
            {
                TryDelete(temp);
                Plugin.LogVerbose($"SprayCache: WriteDisk: Deleted temp");
                return;
            }

            // Replace atomically
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);

            if (_disk.TryGetValue(image.Id, out var previous))
                _diskBytes -= previous.Length;

            _disk[image.Id] = new DiskEntry
            {
                Path = path, Length = image.Bytes.Length, Accessed = DateTime.UtcNow
            };
            _diskBytes += length;
            TrimDisk();
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            Plugin.Logger.LogWarning("Disk cache write failed: " + ex.Message);
            TryDelete(temp);
        }
    }

    private void TouchDisk(string id)
    {
        if (!_disk.TryGetValue(id, out var entry))
            return;

        entry.Accessed = DateTime.UtcNow;
        try
        {
            File.SetLastWriteTimeUtc(entry.Path, entry.Accessed);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            Plugin.Logger.LogWarning(ex.Message);
        }
    }

    private void TrimDisk()
    {
        foreach (KeyValuePair<string, DiskEntry> candidate in _disk.OrderBy(p => p.Value.Accessed).ToArray())
        {
            if (_diskBytes <= _diskLimit && _disk.Count <= MaxEntries)
                break;
            RemoveDisk(candidate.Key);
        }
    }

    private bool RemoveDisk(string id)
    {
        Plugin.LogVerbose($"SprayCache: RemoveDisk: id={id}");

        if (!_disk.TryGetValue(id, out var entry))
            return true;

        if (!TryDelete(entry.Path))
            return false;

        _diskBytes -= entry.Length;
        _disk.Remove(id);
        Plugin.LogVerbose($"SprayCache: RemoveDisk removed id={id}");
        return true;
    }

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            Plugin.LogVerbose($"SprayCache: TryDelete: deleted {path}");
            return true;
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            Plugin.Logger.LogWarning(ex.Message);
            return false;
        }
    }

    public void Close(bool clearDisk)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _memory.Clear();
            _lru.Clear();
            _files.Clear();
            _memoryBytes = 0;
            if (clearDisk)
            {
                foreach (string id in _disk.Keys.ToArray())
                    RemoveDisk(id);
            }
        }
    }

    public void Dispose()
    {
        Close(false);
    }
}
