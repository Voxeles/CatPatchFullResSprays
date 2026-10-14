using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using CatPatchspace;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CatPatchFullResSprays;

internal sealed class SprayService
{
    private readonly NetworkController _network;
    private readonly Task<SprayCache> _cache;
    private readonly SemaphoreSlim _httpSemaphore = new(2);
    private readonly Dictionary<string, Task<SprayImage>> _uploads = [];
    private readonly Dictionary<string, Task<SprayImage>> _downloads = [];
    private readonly Dictionary<string, int> _updates = [];
    private readonly HashSet<string> _checking = [];
    private readonly HashSet<string> _inspected = [];
    private readonly Dictionary<string, int> _renderedSizes = [];
    private readonly List<Sprite> _retiredSprites = [];
    private int _scene;

    public SprayService(NetworkController network)
    {
        _network = network;
        string directory = Path.Combine(Paths.ConfigPath, "Cat-Patch", "FullResSpraysCache");
        long memory = Plugin.Instance.ConfigMemoryCacheMiB.Value * 1024L * 1024;
        long disk = Plugin.Instance.ConfigDiskCacheMiB.Value * 1024L * 1024;
        _cache = Task.Run(() => new SprayCache(directory, memory, disk));
    }

    internal async Task<SprayImage> ReadAsync(string id)
    {
        var cache = await _cache;
        return await Task.Run(() => cache.Get(id));
    }

    internal async Task<SprayImage> StoreAsync(SprayImage image)
    {
        var cache = await _cache;
        return await Task.Run(() => cache.Put(image));
    }

    internal async Task<bool> ContainsAsync(string id, int minimumSize = 1)
    {
        var cache = await _cache;
        return await Task.Run(() => cache.Contains(id, minimumSize));
    }

    internal async void Upload(string path, Action<string, byte[], string> success, Action<string> failure)
    {
        Plugin.LogVerbose($"SprayService: Upload: {path}");

        int scene = _scene;
        var token = _network.SessionToken;
        SprayImage image;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string key = await Task.Run(() => FileKey(fullPath), token);
            token.ThrowIfCancellationRequested();

            Plugin.LogVerbose($"SprayService: Upload: key={key}");
            if (!_uploads.TryGetValue(key, out var task))
            {
                if (_uploads.Count >= 16)
                    throw new InvalidOperationException("Too many spray uploads are pending.");

                Plugin.LogVerbose($"SprayService: Upload: UploadCore");
                task = UploadCore(fullPath, key, token);
                _uploads.Add(key, task);
            }

            try
            {
                image = await task;
                Plugin.LogVerbose($"SprayService: Upload: UploadCore complete");
            }
            finally
            {
                if (_uploads.TryGetValue(key, out var active) && active == task)
                    _uploads.Remove(key);
            }

            token.ThrowIfCancellationRequested();
            if (scene != _scene)
                throw new OperationCanceledException("The scene changed before the spray was ready.");
        }
        catch (Exception ex)
        {
            try
            {
                Plugin.LogVerbose($"SprayService: Upload: Failure: {ex.Message}");
                failure?.Invoke(ex is OperationCanceledException ? "Spray cancelled because the session or scene changed." : ex.Message);
            }
            catch (Exception callbackError)
            {
                Plugin.Logger.LogError(callbackError);
            }

            return;
        }

        try
        {
            Plugin.LogVerbose($"SprayService: Upload: Calling success callback...");
            success?.Invoke(image.Id, image.Bytes, path);
            Plugin.LogVerbose($"SprayService: Upload: ...success callback done");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError(ex);
        }
    }

    private async Task<SprayImage> UploadCore(string path, string key, CancellationToken token)
    {
        var cache = await _cache;

        var image = await Task.Run(() => cache.GetFile(key), token);
        if (image != null)
            Plugin.LogVerbose($"SprayService: UploadCore: Got image {key} from cache {image.Bytes.Length / 1024}KiB");

        string backendError = null;
        if (image == null || !image.BackendUploaded)
        {
            var bytes = image?.Bytes ?? await Task.Run(() => ReadFile(path, key), token);
            Plugin.LogVerbose($"SprayService: UploadCore: Got image bytes {bytes.Length}");
            await _httpSemaphore.WaitAsync(token);
            try
            {
                image = await Task.Run(() =>
                {
                    Plugin.LogVerbose($"SprayService: UploadCore: Calling backend...");
                    bool uploaded = GarythecatBackendClient.UploadSpray(bytes, out var response, out backendError);
                    Plugin.LogVerbose($"SprayService: UploadCore: Backend responded: {response?.status}, {backendError}");
                    string id = SprayImage.Normalize(response?.spray_id);
                    uploaded = uploaded && SprayImage.IsValidId(id) && !id.StartsWith("custom-", StringComparison.Ordinal);
                    if (!uploaded)
                        id = "custom-" + SprayImage.ComputeHash(bytes);
                    return new SprayImage(id, bytes, uploaded, key);
                }, token);
            }
            finally
            {
                _httpSemaphore.Release();
            }
        }
        token.ThrowIfCancellationRequested();

        // Store now as the host may send a cache update message when we successfully upload it
        // We need the image in the cache by then, or else we'll redownload the image we just uploaded
        Plugin.LogVerbose($"SprayService: UploadCore: Storing image");
        image = await StoreAsync(image);
        token.ThrowIfCancellationRequested();

        Plugin.LogVerbose($"SprayService: UploadCore: Uploading to host");
        bool hosted = await _network.UploadAsync(image);
        token.ThrowIfCancellationRequested();
        if (!hosted && !image.BackendUploaded)
            throw new InvalidOperationException("Spray upload failed: the host did not accept the image and the HTTP backend failed. " + backendError);

        return image;
    }

    private static string FileKey(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Spray file was not found.", path);
        return SprayImage.ComputeHash(Encoding.UTF8.GetBytes(path + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length));
    }

    private static byte[] ReadFile(string path, string key)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > SprayImage.HardByteSizeLimit)
            throw new InvalidDataException($"Spray files must be between 1 byte and {SprayImage.HardByteSizeLimit / 1024 / 1024} MiB.");

        using var reader = new BinaryReader(stream);
        var bytes = reader.ReadBytes((int)stream.Length);
        if (bytes.Length != stream.Length || FileKey(path) != key)
            throw new IOException("The spray file changed while being read. Try again.");

        return bytes;
    }

    internal async void Download(string id, Action<byte[]> ready)
    {
        Plugin.LogVerbose($"SprayService: Download: Image {id}");
        id = SprayImage.Normalize(id);
        int scene = _scene;
        var token = _network.SessionToken;
        try
        {
            if (!SprayImage.IsValidId(id))
                throw new ArgumentException("Invalid spray ID.");
            if (!_downloads.TryGetValue(id, out var task))
            {
                if (_downloads.Count >= 32)
                    throw new InvalidOperationException("Too many spray downloads are pending.");
                Plugin.LogVerbose($"SprayService: Download: DownloadCore");
                task = DownloadCore(id, token);
                _downloads[id] = task;
            }

            SprayImage image;
            try
            {
                image = await task;
                Plugin.LogVerbose($"SprayService: Download: DownloadCore complete");
            }
            finally
            {
                if (_downloads.TryGetValue(id, out var active) && active == task)
                    _downloads.Remove(id);
            }
            token.ThrowIfCancellationRequested();
            if (scene != _scene)
                return;

            var preferred = await ReadAsync(id);
            Plugin.LogVerbose($"SprayService: Download: Got existing {preferred?.Bytes.Length / 1024}KiB");
            token.ThrowIfCancellationRequested();
            if (scene != _scene)
                return;
            if (preferred != null && preferred.Bytes.Length > image.Bytes.Length)
                image = preferred;

            Plugin.LogVerbose($"SprayService: Download: Invoking callback with: {image.Bytes.Length / 1024}KiB");
            ready?.Invoke(image.Bytes);
            if (_inspected.Count < 8192 && _inspected.Add(id))
                _network.Inspect(id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (scene == _scene && !token.IsCancellationRequested)
                Alert("Spray download failed: " + ex.Message);
        }
    }
    private async Task<SprayImage> DownloadCore(string id, CancellationToken token)
    {
        Plugin.LogVerbose($"SprayService: DownloadCore: id={id}");
        var image = await ReadAsync(id);
        token.ThrowIfCancellationRequested();
        Plugin.LogVerbose($"SprayService: DownloadCore: cache image {image?.Bytes.Length / 1024}KiB");
        if (image != null)
            return image;

        image = await _network.DownloadAsync(id);
        token.ThrowIfCancellationRequested();
        Plugin.LogVerbose($"SprayService: DownloadCore: network image {image?.Bytes.Length / 1024}KiB");
        if (image != null)
            return image;

        if (id.StartsWith("custom-", StringComparison.Ordinal))
            throw new IOException("This custom image is no longer available on the host.");

        await _httpSemaphore.WaitAsync(token);
        try
        {
            image = await Task.Run(() =>
            {
                Plugin.LogVerbose($"SprayService: DownloadCore: Calling backend...");
                if (!GarythecatBackendClient.DownloadSpray(id, out byte[] bytes, out string error))
                    throw new IOException(error ?? "HTTP backend unavailable.");
                return new SprayImage(id, bytes, true);
            }, token);
        }
        finally
        {
            _httpSemaphore.Release();
        }
        token.ThrowIfCancellationRequested();
        Plugin.LogVerbose($"SprayService: DownloadCore: backend image {image?.Bytes.Length / 1024}KiB");
        return await StoreAsync(image);
    }

    internal async void CacheUpdated(string id, int size)
    {
        Plugin.LogVerbose($"SprayService: CacheUpdated: id={id}, size={size / 1024}KiB");
        if (_updates.Count >= 8192 && !_updates.ContainsKey(id))
            return;

        _updates[id] = Math.Max(_updates.GetValueOrDefault(id, 0), size);
        if (!_checking.Add(id))
            return;

        var token = _network.SessionToken;
        int scene = _scene;
        try
        {
            while (_updates.TryGetValue(id, out int requested))
            {
                Plugin.LogVerbose($"SprayService: CacheUpdated: Checking...");
                var cached = await ReadAsync(id);
                token.ThrowIfCancellationRequested();
                if (scene != _scene)
                    return;

                requested = _updates[id];
                if (cached != null && cached.Bytes.Length >= requested)
                {
                    Plugin.LogVerbose($"SprayService: CacheUpdated: Got from cache of size {cached.Bytes.Length / 1024}KiB");
                    ApplyImage(cached);
                    _updates.Remove(id);
                    return;
                }

                Plugin.LogVerbose($"SprayService: CacheUpdated: Download request...");
                var image = await _network.DownloadAsync(id);
                token.ThrowIfCancellationRequested();
                if (scene != _scene)
                    return;
                
                Plugin.LogVerbose($"SprayService: CacheUpdated: Got from host of size {image?.Bytes.Length / 1024}KiB");
                if (image == null || image.Bytes.Length < requested)
                    throw new IOException("The host could not provide the announced image.");

                ApplyImage(image);
                if (_updates[id] <= image.Bytes.Length)
                {
                    _updates.Remove(id);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && scene == _scene)
                Alert("Spray update failed: " + ex.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested && scene == _scene)
            {
                _checking.Remove(id);
                _updates.Remove(id);
            }
        }
    }

    internal void PrepareSprite(SprayVisualController visual, string id, byte[] bytes)
    {
        id = SprayImage.Normalize(id);
        if (bytes == null)
            return;

        if (_renderedSizes.TryGetValue(id, out int length)
            && length < bytes.Length
            && visual._spriteCache.TryGetValue(id, out var old))
        {
            visual._spriteCache.Remove(id);
            if (old != null)
                _retiredSprites.Add(old);
        }
        Plugin.LogVerbose($"SprayService: PrepareSprite: Noted {id} of size {bytes.Length / 1024}KiB");
        _renderedSizes[id] = Math.Max(length, bytes.Length);
    }

    internal void ApplyImage(SprayImage image)
    {
        var visual = SprayNetworkController._instance?._visualController;
        if (visual == null)
            return;

        var sprays =
            visual._activeSprays
                .Where(go => go != null)
                .Select(go => go.GetComponent<SprayInstance>())
                .Where(s =>
                    s != null
                    && s.SprayId == image.Id
                    && (s.ImageBytes == null || s.ImageBytes.Length < image.Bytes.Length))
                .ToArray();
        if (sprays.Length == 0)
            return;

        PrepareSprite(visual, image.Id, image.Bytes);
        if (!visual.TryGetOrCreateSprite(image.Id, image.Bytes, out var sprite, out string error))
        {
            Alert("Could not display updated spray: " + error); return;
        }

        Plugin.LogVerbose($"SprayService: ApplyImage: Updating {sprays.Length} sprites");

        foreach (var spray in sprays)
        {
            spray.ImageBytes = image.Bytes;
            if (spray.Renderer != null)
                spray.Renderer.sprite = sprite;
            MainMenuSpraysData.RefreshSpray(spray);
        }

        ReleaseRetiredSprites(visual);
    }

    private void ReleaseRetiredSprites(SprayVisualController visual)
    {
        var used = new HashSet<Sprite>(visual._spriteCache.Values);
        foreach (var go in visual._activeSprays)
        {
            if (go != null && go.TryGetComponent<SpriteRenderer>(out var renderer))
                used.Add(renderer.sprite);
        }
        Plugin.LogVerbose($"SprayService: ReleaseRetiredSprites: Retiring {_retiredSprites.Count} sprites");
        foreach (var sprite in _retiredSprites.ToArray())
        {
            if (sprite != null && used.Contains(sprite))
                continue;
            DestroySpriteAndTexture(sprite);
            _retiredSprites.Remove(sprite);
        }
    }

    private static void DestroySpriteAndTexture(Sprite sprite)
    {
        if (sprite == null)
            return;
        Object.Destroy(sprite.texture);
        Object.Destroy(sprite);
    }

    internal void ClearScene()
    {
        Plugin.LogVerbose($"SprayService: ClearScene");
        _scene++;
        _inspected.Clear();
        _checking.Clear();
        _updates.Clear();
        MainMenuSpraysData.Reset();
    }

    internal void TrimCatPatchCaches()
    {
        Plugin.LogVerbose($"SprayService: TrimCatPatchCaches");
        _renderedSizes.Clear();

        foreach (var sprite in _retiredSprites)
            DestroySpriteAndTexture(sprite);
        _retiredSprites.Clear();

        var plugin = CatPatchspace.Plugin.Instance;
        if (plugin == null)
            return;

        var controller = plugin.GetComponent<SprayController>();
        if (controller != null)
        {
            foreach (var sprite in controller._visualController._spriteCache.Values)
                DestroySpriteAndTexture(sprite);
            controller._visualController._spriteCache.Clear();

            foreach (var texture in controller._previewTextureCache.Values)
                Object.Destroy(texture);
            controller._previewTextureCache.Clear();

            lock (controller._uploadQueue._lock)
                controller._uploadQueue._cacheByKey.Clear();

            lock (controller._networkController._downloadCache._lock)
                controller._networkController._downloadCache._cachedBytes.Clear();
        }

        var inspector = plugin.GetComponent<SprayInspector>();
        if (inspector != null)
        {
            inspector.ClosePreview();
            inspector.ClearHoverHighlight();
            foreach (var gif in inspector._gifAnimationCache.Values)
                gif?.DestroyTextures();
            inspector._gifAnimationCache.Clear();
            inspector._failedGifCacheKeys.Clear();
        }
    }

    internal void ResetSession()
    {
        Plugin.LogVerbose($"SprayService: ResetSession");
        _uploads.Clear();
        _downloads.Clear();
        _updates.Clear();
        _checking.Clear();
        _inspected.Clear();
    }

    internal static void Alert(string message)
    {
        Plugin.Logger.LogWarning(message);
        if (PlayerCamera.main != null)
            PlayerCamera.main.DoAlert(message);
    }

    internal void Close(bool clearDisk)
    {
        try
        {
            // Dispose of the cache synchronously on the main thread to prevent potential races
            // This will not deadlock as SprayCache never needs the main Unity thread
            _cache.GetAwaiter().GetResult().Close(clearDisk);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Cache shutdown failed: " + ex.Message);
        }
    }
}
