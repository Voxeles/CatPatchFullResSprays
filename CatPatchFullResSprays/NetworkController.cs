using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using CatPatchspace;
using UnityEngine;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CatPatchFullResSprays;

public class NetworkController : MonoBehaviour
{
    private const ushort ClientProbeMsg = 48900; 
    private const ushort ServerProbeAckMsg = 48901;
    private const ushort ClientUploadMsg = 48902;
    private const ushort ClientDownloadMsg = 48903;
    private const ushort ServerDownloadResponse = 48904;
    private const ushort ServerSprayUpdateMsg = 48905;
    
    public static NetworkController Instance { get; private set; }

    private static bool _handlersRegistered = false;
    private static bool _serverHasMod = false;

    private SprayCache _sprayCache = new();
    private HashSet<string> _sentSprays = [];
    private HashSet<string> _missedSprays = [];
    private ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pendingRequests = [];

    public void Awake()
    {
        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
        NetPlayer.OnPlayerJoined += OnPlayerJoined;
    }

    public void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        NetPlayer.OnPlayerJoined -= OnPlayerJoined;
        Instance = null;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!KrokoshaScavMultiplayer.network_system_is_running && _handlersRegistered)
        {
            Instance.ResetSession();
        }
    }

    private static void OnPlayerJoined(NetPlayer player)
    {
        if (player == null)
            return;
        RegisterHandlersOnce();
        if (!player.is_local)
            return;
        // We just joined a new server, determine if it has the mod
        if (!player.is_host)
            Instance.SendProbe();
        else
            _serverHasMod = true;
    }

    public void ResetSession()
    {
        _handlersRegistered = false;
        _serverHasMod = false;
        _sprayCache.Clear();
        _sentSprays.Clear();
        _missedSprays.Clear();
        _pendingRequests.Clear();
        Plugin.Logger.LogInfo("Reset session.");
    }

    private static void RegisterHandlersOnce()
    {
        if (!KrokoshaScavMultiplayer.network_system_is_running || _handlersRegistered)
            return;
        Net.RegisterServerReceiver(ClientProbeMsg, OnServerProbeReceived);
        Net.RegisterClientReceiver(ServerProbeAckMsg, OnClientProbeAckReceived);
        Net.RegisterServerReceiver(ClientUploadMsg, OnServerSprayReceived);
        Net.RegisterServerReceiver(ClientDownloadMsg, OnServerDownloadRequest);
        Net.RegisterClientReceiver(ServerDownloadResponse, OnClientSprayReceived);
        Net.RegisterClientReceiver(ServerSprayUpdateMsg, OnClientSprayUpdate);
        _handlersRegistered = true;
    }
    
    
    
    public void SendProbe()
    {
        Plugin.Logger.LogInfo("Sending probe");
        var netDataWriter = Net.CreateWriter(ClientProbeMsg);
        Net.Client_Send(DeliveryMethod.ReliableUnordered, netDataWriter);
    }

    private static void OnServerProbeReceived(knetid id, ref NetDataReader reader)
    {
        Plugin.Logger.LogInfo($"Ack probe from {id}");
        var netDataWriter = Net.CreateWriter(ServerProbeAckMsg);
        Net.Server_SendTo(DeliveryMethod.ReliableUnordered, netDataWriter, id);
    }

    private static void OnClientProbeAckReceived(knetid id, ref NetDataReader reader)
    {
        Plugin.Logger.LogInfo("Received probe ack from server.");
        _serverHasMod = true;
    }

    
    
    public void UploadSprayToHost(string sprayId, byte[] imageBytes)
    {
        Plugin.Logger.LogInfo($"UploadSprayToHost {sprayId} (length: {imageBytes?.Length})");
        
        if (sprayId == null || (sprayId = sprayId.Trim()).Length == 0 || imageBytes == null || imageBytes.Length == 0)
            return;
        if (!_serverHasMod)
            return;
        if (_sentSprays.Contains(sprayId))
            return;
        
        var compressedImageBytes = Deflate.CompressData(imageBytes);
        if (compressedImageBytes == null)
            return;

        _sprayCache.AddOrUpdateSpray(sprayId, imageBytes.Length, compressedImageBytes, out _);

        if (Net.is_host)
            return;

        var netDataWriter = Net.CreateWriter(ClientUploadMsg);
        netDataWriter.Put(sprayId);
        netDataWriter.Put(compressedImageBytes.Length);
        netDataWriter.Put(compressedImageBytes);
        Plugin.Logger.LogInfo($"Sending spray {sprayId} data, len: {compressedImageBytes.Length}");
        Net.Client_Send(DeliveryMethod.ReliableUnordered, netDataWriter);
        _sentSprays.Add(sprayId);
    }

    private static void OnServerSprayReceived(knetid id, ref NetDataReader reader)
    {
        Plugin.Logger.LogInfo("OnServerSprayReceived");
        
        reader.Get(out string sprayId);
        if (sprayId == null || (sprayId = sprayId.Trim()).Length == 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray from {id}: No sprayId");
            return;
        }

        reader.Get(out int compressedImageBytesLength);
        if (compressedImageBytesLength <= 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray from {id}: Bad length");
            return;
        }

        var compressedImageBytes = new byte[compressedImageBytesLength];
        reader.GetBytes(compressedImageBytes, compressedImageBytesLength);

        var imageBytes = Deflate.DecompressData(compressedImageBytes);
        var recompressedimageBytes = Deflate.CompressData(imageBytes);
        if (recompressedimageBytes == null || recompressedimageBytes.Length == 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray {sprayId} from {id}: Bad image data");
            return;
        }
        if (imageBytes.Length > Plugin.Instance.ConfigMaxSpraySizeInBytes.Value)
        {
            Plugin.Logger.LogInfo($"Invalid spray {sprayId} from {id}: Image too big");
            return;
        }

        Plugin.Logger.LogInfo($"Saving spray {sprayId} (length: {imageBytes.Length}) from {id}");
        Instance._sprayCache.AddOrUpdateSpray(sprayId, imageBytes.Length, recompressedimageBytes, out bool didReplaceEntry);

        if (didReplaceEntry || Instance._missedSprays.Contains(sprayId))
        {
            Plugin.Logger.LogInfo($"New spray {sprayId} from {id} previously missed");
            SendCacheUpdate(sprayId, imageBytes.Length, recompressedimageBytes);
        }
    }

    private static void SendCacheUpdate(string sprayId, int uncompressedLength, byte[] compressedImageBytes)
    {
        Plugin.Logger.LogInfo("SendCacheUpdate");
        
        var netDataWriter = Net.CreateWriter(ServerSprayUpdateMsg);
        netDataWriter.Put(sprayId);
        netDataWriter.Put(uncompressedLength);
        netDataWriter.Put(compressedImageBytes.Length);
        netDataWriter.Put(compressedImageBytes);
        Plugin.Logger.LogInfo($"Sending updated spray {sprayId} (length: {uncompressedLength})");
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, netDataWriter, ServerMain.AllClientIds);
    }

    private static void OnClientSprayUpdate(knetid id, ref NetDataReader reader)
    {
        Plugin.Logger.LogInfo("OnClientSprayUpdate");
        
        reader.Get(out string sprayId);
        if (sprayId == null || (sprayId = sprayId.Trim()).Length == 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray from host: No sprayId");
            return;
        }

        reader.Get(out int uncompressedImageBytesLength);
        if (uncompressedImageBytesLength <= 0)
        {
            Plugin.Logger.LogInfo($"Ignoring spray from host: Bad length");
            return;
        }
        
        reader.Get(out int compressedImageBytesLength);
        if (compressedImageBytesLength <= 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray from {id}: Bad length");
            return;
        }
        
        var compressedImageBytes = new byte[compressedImageBytesLength];
        reader.GetBytes(compressedImageBytes, compressedImageBytesLength);

        var imageBytes = Deflate.DecompressData(compressedImageBytes);
        var recompressedimageBytes = Deflate.CompressData(imageBytes);
        if (recompressedimageBytes == null || recompressedimageBytes.Length == 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray {sprayId} from {id}: Bad image data");
            return;
        }

        bool added = Instance._sprayCache.AddOrUpdateSpray(sprayId, imageBytes.Length, recompressedimageBytes, out _);
        if (!added && !Net.is_host)
            return;

        var sprayController = CatPatchspace.Plugin.Instance.gameObject.GetComponent<SprayController>();
        if (sprayController == null)
        {
            Plugin.Logger.LogInfo($"No spray controller!");
            return;
        }
        var uploadQueue = sprayController._uploadQueue;
        var downloadCache = sprayController._networkController._downloadCache;
        
        lock (uploadQueue._lock)
        lock (downloadCache._lock)
        {
            downloadCache._cachedBytes[sprayId] = imageBytes;
            
            var sprayInstances = FindObjectsByType<SprayInstance>(FindObjectsSortMode.None);
            foreach (var sprayInstance in sprayInstances)
            {
                if (!sprayInstance.SprayId.Equals(sprayId))
                    continue;
                sprayInstance.ImageBytes = imageBytes;
                sprayController._visualController.TryGetOrCreateSprite(sprayId, imageBytes, out var sprite, out _);
                sprayInstance.Renderer.sprite = sprite;
            }
        }
    }


    // Called from a worker thread, OK to block
    public byte[] TryDownloadSpray(string sprayId)
    {
        Plugin.Logger.LogInfo($"TryDownloadSpray {sprayId}");
        
        if (!_serverHasMod)
            return null;
        if (sprayId == null || (sprayId = sprayId.Trim()).Length == 0)
            return null;
        
        if (_sprayCache.TryGetSpray(sprayId, out _, out byte[] compressedImageBytes))
            return Deflate.DecompressData(compressedImageBytes);

        if (Net.is_host)
        {
            _missedSprays.Add(sprayId);
            return null;
        }

        TaskCompletionSource<byte[]> tcs = new();
        _pendingRequests.TryAdd(sprayId, tcs);

        try
        {
            var netDataWriter = Net.CreateWriter(ClientDownloadMsg);
            netDataWriter.Put(sprayId);
            Plugin.Logger.LogInfo($"Sending download request for {sprayId}");
            Net.Client_Send(DeliveryMethod.ReliableUnordered, netDataWriter);

            if (tcs.Task.Wait(TimeSpan.FromSeconds(5)))
                return tcs.Task.Result;
            else
                return null;
        }
        finally
        {
            _pendingRequests.TryRemove(sprayId, out _);
        }
    }

    private static void OnServerDownloadRequest(knetid id, ref NetDataReader reader)
    {
        Plugin.Logger.LogInfo("OnServerDownloadRequest");
        
        reader.Get(out string sprayId);
        sprayId ??= "";
        sprayId = sprayId.Trim();

        if (!Instance._sprayCache.TryGetSpray(sprayId, out _, out byte[] compressedImageBytes))
            Instance._missedSprays.Add(sprayId);
        compressedImageBytes ??= [];
        
        var netDataWriter = Net.CreateWriter(ServerDownloadResponse);
        netDataWriter.Put(sprayId);
        netDataWriter.Put(compressedImageBytes.Length);
        netDataWriter.Put(compressedImageBytes);
        Plugin.Logger.LogInfo($"Sending spray {sprayId}");
        Net.Server_SendTo(DeliveryMethod.ReliableUnordered, netDataWriter, id);
    }

    private static void OnClientSprayReceived(knetid id, ref NetDataReader reader)
    {
        Plugin.Logger.LogInfo("OnClientSprayReceived");
        
        reader.Get(out string sprayId);
        if (sprayId == null || (sprayId = sprayId.Trim()).Length == 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray {sprayId} from host: No sprayId");
            return;
        }

        byte[] imageBytes = null;
        try
        {
            reader.Get(out int compressedImageBytesLength);
            if (compressedImageBytesLength <= 0)
            {
                Plugin.Logger.LogInfo($"Invalid spray {sprayId} from host: No data");
                return;
            }
        
            var compressedImageBytes = new byte[compressedImageBytesLength];
            reader.GetBytes(compressedImageBytes, compressedImageBytesLength);
        
            imageBytes = Deflate.DecompressData(compressedImageBytes);
            var recompressedImageBytes = Deflate.CompressData(imageBytes);
            if (recompressedImageBytes == null || recompressedImageBytes.Length == 0)
            {
                Plugin.Logger.LogInfo($"Invalid spray {sprayId} from host: Bad image data");
                imageBytes = null;
                return;
            }

            Plugin.Logger.LogInfo($"Saving spray {sprayId} (length: {imageBytes.Length}) from host");
            Instance._sprayCache.AddOrUpdateSpray(sprayId, imageBytes.Length, recompressedImageBytes, out _);
        }
        finally
        {
            if (Instance._pendingRequests.TryRemove(sprayId, out var tcs))
                tcs.SetResult(imageBytes);
        }
    }
}