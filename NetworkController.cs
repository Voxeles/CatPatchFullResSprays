using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using KrokoshaCasualtiesMP;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine.SceneManagement;

namespace CatPatchFullResSprays;

public class NetworkController : MonoBehaviour
{
    private const ushort ClientProbeMsg = 48000; 
    private const ushort ServerProbeAckMsg = 48001;
    private const ushort ClientUploadMsg = 48002;
    private const ushort ClientDownloadMsg = 48003;
    private const ushort ServerDownloadResponse = 48004;
    
    public static NetworkController Instance { get; private set; }

    private static bool _handlersRegistered = false;
    private static bool _serverHasMod = false;
    private ConcurrentDictionary<string, byte[]> compressedSpraysCache = [];
    private ConcurrentDictionary<string, TaskCompletionSource<byte[]>> pendingRequests = [];

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
        compressedSpraysCache.Clear();
        Plugin.Logger.LogInfo("Reset session.");
    }

    private static void RegisterHandlersOnce()
    {
        if (!KrokoshaScavMultiplayer.network_system_is_running || _handlersRegistered)
            return;
        _handlersRegistered = true;
        Net.RegisterServerReceiver(ClientProbeMsg, OnServerProbeReceived);
        Net.RegisterClientReceiver(ServerProbeAckMsg, OnClientProbeAckReceived);
        Net.RegisterServerReceiver(ClientUploadMsg, OnServerSprayReceived);
        Net.RegisterServerReceiver(ClientDownloadMsg, OnServerDownloadRequest);
        Net.RegisterClientReceiver(ServerDownloadResponse, OnClientSprayReceived);
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
        if (compressedSpraysCache.ContainsKey(sprayId))
            return;
        
        var compressedImageBytes = Deflate.CompressData(imageBytes);
        if (compressedImageBytes == null)
            return;

        compressedSpraysCache.TryAdd(sprayId, compressedImageBytes);

        if (Net.is_host)
            return;

        var netDataWriter = Net.CreateWriter(ClientUploadMsg);
        netDataWriter.Put(sprayId);
        netDataWriter.Put(compressedImageBytes.Length);
        netDataWriter.Put(compressedImageBytes);
        Plugin.Logger.LogInfo($"Sending spray {sprayId} data, len: {compressedImageBytes.Length}");
        Net.Client_Send(DeliveryMethod.ReliableUnordered, netDataWriter);
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

        if (Instance.compressedSpraysCache.ContainsKey(sprayId))
        {
            Plugin.Logger.LogInfo($"Duplicate spray {sprayId} from {id}");
            return;
        }

        var imageData = Deflate.DecompressData(compressedImageBytes);
        var recompressedImageData = Deflate.CompressData(imageData);
        if (recompressedImageData == null || recompressedImageData.Length == 0)
        {
            Plugin.Logger.LogInfo($"Invalid spray {sprayId} from {id}: Bad image data");
            return;
        }
        if (imageData.Length > Plugin.Instance.ConfigMaxSpraySizeInBytes.Value)
        {
            Plugin.Logger.LogInfo($"Invalid spray {sprayId} from {id}: Image too big");
            return;
        }
        
        Plugin.Logger.LogInfo($"Saving spray {sprayId} (length: {imageData.Length}) from {id}");
        Instance.compressedSpraysCache.TryAdd(sprayId, compressedImageBytes);
    }
    
    
    
    public byte[] TryDownloadSpray(string sprayId)
    {
        Plugin.Logger.LogInfo($"TryDownloadSpray {sprayId}");
        
        if (!_serverHasMod)
            return null;
        if (sprayId == null || (sprayId = sprayId.Trim()).Length == 0)
            return null;
        
        if (compressedSpraysCache.TryGetValue(sprayId, out byte[] cachedCompressedImageBytes))
            return Deflate.DecompressData(cachedCompressedImageBytes);
        
        if (Net.is_host)
            return null;

        TaskCompletionSource<byte[]> tcs = new();
        pendingRequests.TryAdd(sprayId, tcs);

        try
        {
            var netDataWriter = Net.CreateWriter(ClientDownloadMsg);
            netDataWriter.Put(sprayId);
            Plugin.Logger.LogInfo($"Sending download request for {sprayId}");
            Net.Client_Send(DeliveryMethod.ReliableUnordered, netDataWriter);

            if (tcs.Task.Wait(TimeSpan.FromSeconds(10)))
                return tcs.Task.Result;
            else
                return null;
        }
        finally
        {
            pendingRequests.TryRemove(sprayId, out _);
        }
    }

    private static void OnServerDownloadRequest(knetid id, ref NetDataReader reader)
    {
        Plugin.Logger.LogInfo("OnServerDownloadRequest");
        
        reader.Get(out string sprayId);
        sprayId ??= "";
        sprayId = sprayId.Trim();

        Instance.compressedSpraysCache.TryGetValue(sprayId, out byte[] compressedImageBytes);
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

            if (Instance.compressedSpraysCache.ContainsKey(sprayId))
            {
                Plugin.Logger.LogInfo($"Duplicate spray {sprayId} from host");
                return;
            }
        
            imageBytes = Deflate.DecompressData(compressedImageBytes);
            var recompressedImageBytes = Deflate.CompressData(imageBytes);
            if (recompressedImageBytes == null || recompressedImageBytes.Length == 0)
            {
                Plugin.Logger.LogInfo($"Invalid spray {sprayId} from host: Bad image data");
                imageBytes = null;
                return;
            }
        
            Plugin.Logger.LogInfo($"Saving spray {sprayId} (length: {imageBytes.Length}) from host");
            Instance.compressedSpraysCache.TryAdd(sprayId, compressedImageBytes);
        }
        finally
        {
            if (Instance.pendingRequests.TryRemove(sprayId, out var tcs))
                tcs.SetResult(imageBytes);
        }
    }
    
}