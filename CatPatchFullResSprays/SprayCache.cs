using System;
using System.Collections.Concurrent;
using BepInEx;
using System.Threading;
using UnityEngine;

namespace CatPatchFullResSprays;

public class SprayCache : MonoBehaviour
{
    private readonly ConcurrentDictionary<string, Entry> _compressedSprayCache = [];
    private long _compressedSprayCacheSize = 0;
    
    public void Clear()
    {
        Plugin.Logger.LogInfo($"Clearing cache");
        _compressedSprayCache.Clear();
        _compressedSprayCacheSize = 0;
    }

    public bool AddOrUpdateSpray(string sprayId, int uncompressedImageLength, byte[] compressedImageBytes, out bool didReplaceEntry)
    {
        didReplaceEntry = false;
        sprayId = sprayId?.Trim().ToLowerInvariant();
        if (sprayId.IsNullOrWhiteSpace() || compressedImageBytes == null || uncompressedImageLength == 0)
        {
            Plugin.Logger.LogInfo($"Cache: Invalid sprayId {sprayId}");
            return false;
        }


        bool added;
        long oldEntryCompressedLength = 0;
        while (true)
        {
            var myEntry = new Entry(DateTime.UtcNow, uncompressedImageLength, compressedImageBytes);
            var retEntry = _compressedSprayCache.GetOrAdd(sprayId, myEntry);
            if (myEntry == retEntry)
            {
                Plugin.Logger.LogInfo($"Cache: Added new entry {sprayId}");
                added = true;
                break;
            }

            if (retEntry.UncompressedImageLength >= myEntry.UncompressedImageLength)
            {
                Plugin.Logger.LogInfo($"Cache: Duplicate/Inferior entry {sprayId}, old: {retEntry.UncompressedImageLength} new: {myEntry.UncompressedImageLength}");
                added = false;
                break;
            }

            if (_compressedSprayCache.TryUpdate(sprayId, myEntry, retEntry))
            {
                Plugin.Logger.LogInfo($"Cache: Updated entry {sprayId}, old: {retEntry.UncompressedImageLength}, new: {myEntry.UncompressedImageLength}");
                oldEntryCompressedLength = retEntry.CompressedImageBytes.Length;
                didReplaceEntry = true;
                added = true;
                break;
            }
        }

        if (!added)
            return false;

        Interlocked.Add(ref _compressedSprayCacheSize, compressedImageBytes.Length - oldEntryCompressedLength);
        Plugin.Logger.LogInfo($"Cache: CompressedSprayCacheSize: {_compressedSprayCacheSize/1024}KB");
        return true;
    }

    public bool TryGetSpray(string sprayId, out int uncompressedImageLength, out byte[] compressedImageBytes)
    {
        sprayId = sprayId?.Trim().ToLowerInvariant();
        if (sprayId.IsNullOrWhiteSpace())
        {
            Plugin.Logger.LogInfo($"Cache: TryGetSpray invalid sprayId {sprayId}");
            compressedImageBytes = null;
            uncompressedImageLength = 0;
            return false;
        }

        uncompressedImageLength = 0;
        compressedImageBytes = null;
        while (true)
        {
            _compressedSprayCache.TryGetValue(sprayId, out Entry entry);
            if (entry == null)
            {
                Plugin.Logger.LogInfo($"Cache: No entry for {sprayId}");
                break;
            }
            uncompressedImageLength = entry.UncompressedImageLength;
            compressedImageBytes = entry.CompressedImageBytes;
            var myEntry = new Entry(DateTime.UtcNow, entry.UncompressedImageLength, entry.CompressedImageBytes);
            if (_compressedSprayCache.TryUpdate(sprayId, myEntry, entry))
            {
                Plugin.Logger.LogInfo($"Cache: Got and set entry for {sprayId}");
                break;
            }
        }
        
        return compressedImageBytes != null;
    }
    
    private sealed class Entry(DateTime lastAccessTime, int uncompressedImageLength, byte[] compressedImageBytes)
    {
        public readonly DateTime LastAccessTime = lastAccessTime;
        public readonly int UncompressedImageLength = uncompressedImageLength;
        public readonly byte[] CompressedImageBytes = compressedImageBytes;
    }
}