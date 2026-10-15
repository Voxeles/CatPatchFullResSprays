using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Together;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CatPatchFullResSprays;

public sealed class NetworkController : MonoBehaviour
{
    private const int ProtocolVersion = 2;
    private const string Probe = $"{Plugin.ModGuid}.probe";
    private const string ProbeAck = $"{Plugin.ModGuid}.probe-ack";
    private const string ToHost = $"{Plugin.ModGuid}.to-host";
    private const string ToClient = $"{Plugin.ModGuid}.to-client";
    private const int MaxTransfers = 16;
    private const long TransferBudget = 64L * 1024 * 1024;
    private const int DefaultFragmentSize = 32 * 1024; // 32KiB seems like a nice balance
    private const int WindowFragments = 8; // Max fragments per peer
    private const long PeerWindowBytes = 256 * 1024; // MP mod sets a 1MiB per-peer buffer for steam transport, stay _way_ below that
    private const long TotalWindowBytes = 1024 * 1024; // For sanity, don't exceed 1MiB total for all peers
    private const int FrameFragmentLimit = 32;
    private const float OfferIdleResend = 5;
    private const float ResendTimeout = 5;
    private const float TransferIdleTimeout = 30;
    private const float TransferTimeout = 180;
    private const int MaxPreparing = 2;
    private const int MaxServesPerPeer = 2;
    private const int MaxQueued = 64;
    private const int MaxQueuedPerPeer = MaxTransfers;
    private const float QueueExpiry = 15;
    private const int MaxInspecting = 4;
    private const float DownloadIdleTimeout = 30;
    private const float DownloadTimeout = 600;

    public static NetworkController Instance { get; private set; }
    internal SprayService Service { get; private set; }
    internal CancellationToken SessionToken => _session.Token;
    internal bool IsHost => !Net.IsRunning || Net.IsServer;
    internal int HostMaxSize { get; private set; }
    internal bool HostRequiresBackend { get; private set; }

    private CancellationTokenSource _session = new();
    private TaskCompletionSource<bool> _handshake = NewCompletion<bool>();
    private int _probeAttempts;
    private readonly HashSet<ushort> _peers = [];
    private readonly Dictionary<ushort, string> _peerSessions = [];
    private string _helloToken = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Outgoing> _outgoing = [];
    private readonly Dictionary<string, Incoming> _incoming = [];
    private readonly Dictionary<string, Download> _downloads = [];
    private readonly Dictionary<string, Receipt> _receipts = [];
    private readonly HashSet<string> _preparing = [];
    private readonly List<Request> _queue = [];
    private readonly HashSet<string> _inspecting = [];
    private bool _closed;
    private float _lastProbe = float.NegativeInfinity;
    private int _pumpRoundRobinRotation;
    private int _generation;

    private enum Kind : byte
    {
        Offer, // I have this image, interested?
        Ack, // I am interested in this portion of it right now
        Fragment, // Here's a bit of that portion
        Result, // Here's what happened with this transfer
        Get, // Give me this image
        Inspect, // What do you know about this image (size)?
        Notice, // Here's what I know about this image
        Wait, // I'm busy, ask again later
    }

    private sealed class Outgoing
    {
        public ushort Peer;
        public string Token;
        public SprayImage Image;
        public readonly int FragmentSize = DefaultFragmentSize;
        public bool Accepted;
        public int Acked; // Receiver has every fragment below this
        public int Limit; // Receiver allows fragments below this
        public int NextFragmentIndex;
        public float LastAck;
        public float OfferSent = float.NegativeInfinity;
        public float RetryAt = float.NegativeInfinity;
        public int Waits;
        public float Progress = Time.realtimeSinceStartup;
        public readonly float Started = Time.realtimeSinceStartup;
        public readonly TaskCompletionSource<bool> Completion = NewCompletion<bool>();

        public int Count => FragmentBuffer.CountFor(Image.Bytes.Length, FragmentSize);
        public int Offset(int index) => (int)Math.Min((long)index * FragmentSize, Image.Bytes.Length);
        public int FragmentLength(int index) => Offset(index + 1) - Offset(index);
        public long InFlightBytes => Offset(NextFragmentIndex) - Offset(Acked);

        public void Ack(int receive, int limit, float now)
        {
            receive = Mathf.Clamp(receive, 0, Count);
            limit = Mathf.Clamp(limit, 0, Count);
            if (!Accepted)
            {
                Accepted = true;
                LastAck = Progress = now;
            }

            // Acks may arrive out of order, so only ever move forward
            if (receive > Acked)
            {
                Acked = receive;
                LastAck = Progress = now;
            }

            Limit = Math.Max(Limit, limit);
            NextFragmentIndex = Math.Max(NextFragmentIndex, Acked);
        }
    }

    private sealed class Incoming
    {
        public ushort Peer;
        public string Token;
        public string Id;
        public string Hash;
        public bool Backend;
        public bool Saving;
        public bool Preparing = true;
        public FragmentBuffer Buffer;
        public int Limit;
        public bool NeedsAck;
        public float Progress = Time.realtimeSinceStartup;
        public readonly float Started = Time.realtimeSinceStartup;
    }

    private sealed class Download
    {
        public string Id;
        public readonly float Started = Time.realtimeSinceStartup;
        public float LastActivity = Time.realtimeSinceStartup;
        public float NextGet = float.NegativeInfinity;
        public int Waits;
        public bool Receiving;
        public readonly TaskCompletionSource<SprayImage> Completion = NewCompletion<SprayImage>();
    }

    private sealed class Request
    {
        public ushort Peer;
        public string Token;
        public string Id;
        public int Size = -1;
        public float Seen = Time.realtimeSinceStartup;
    }

    private sealed class Receipt
    {
        public bool Success;
        public string Message;
        public readonly float Expires = Time.realtimeSinceStartup + 60;
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static string Key(ushort peer, string token)
    {
        return peer + ":" + token;
    }

    private static float Backoff(int waits)
    {
        float delay = Mathf.Min(4f, 0.25f * (1 << Mathf.Clamp(waits, 0, 5)));
        return delay * UnityEngine.Random.Range(0.75f, 1.25f);
    }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        RegisterReceivers();
        Service = new SprayService(this);
        SceneManager.sceneLoaded += SceneLoaded;
        ScavPlayer.OnPlayerJoined += PlayerJoined;
        ScavPlayer.OnPlayerLeft += PlayerLeft;
    }

    private void PlayerJoined(ScavPlayer player)
    {
        EnsureHostSettingsSet();
        if (player != null && player.IsLocal)
            _lastProbe = float.NegativeInfinity;
    }

    private void PlayerLeft(ScavPlayer player)
    {
        if (player == null)
            return;
        if (player.IsLocal)
            ResetSession();
        else ForgetPeer(player.playerId.id);
    }

    private void ForgetPeer(ushort peer)
    {
        Plugin.LogVerbose($"NetworkController: ForgetPeer: {peer}");
        _peers.Remove(peer);
        _peerSessions.Remove(peer);
        foreach (string key in _outgoing.Where(p => p.Value.Peer == peer).Select(p => p.Key).ToArray())
            FinishOutgoing(key, false);
        foreach (string key in _incoming.Where(p => p.Value.Peer == peer).Select(p => p.Key).ToArray())
            _incoming.Remove(key);
        foreach (string key in _preparing.Where(k => k.StartsWith(peer + ":", StringComparison.Ordinal)).ToArray())
            _preparing.Remove(key);
        foreach (string key in _inspecting.Where(k => k.StartsWith(peer + ":", StringComparison.Ordinal)).ToArray())
            _inspecting.Remove(key);
        _queue.RemoveAll(r => r.Peer == peer);
        foreach (string key in _receipts.Keys.Where(k => k.StartsWith(peer + ":", StringComparison.Ordinal)).ToArray())
            _receipts.Remove(key);
    }

    private bool SameSession(int generation, ushort peer, string session)
    {
        return generation == _generation && (!IsHost || _peerSessions.TryGetValue(peer, out string current) && current == session);
    }

    private void SceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Net.IsRunning)
            ResetSession();
        Service.ClearScene();
    }

    private static void RegisterReceivers()
    {
        Multiplayer.RegisterCustomServerReceiver(Probe, ReceiveProbe);
        Multiplayer.RegisterCustomClientReceiver(ProbeAck, ReceiveProbeAck);
        Multiplayer.RegisterCustomServerReceiver(ToHost, ReceiveHost);
        Multiplayer.RegisterCustomClientReceiver(ToClient, ReceiveClient);
    }

    private void EnsureHostSettingsSet()
    {
        if (!IsHost || HostMaxSize != 0)
            return;

        HostMaxSize = Plugin.Instance.ConfigMaxSpraySizeMiB.Value * 1024 * 1024;
        HostRequiresBackend = Plugin.Instance.ConfigRequireBackend.Value;
    }

    private void Update()
    {
        if (_closed)
            return;

        float now = Time.realtimeSinceStartup;
        if (!IsHost
            && ScavPlayer.LocalPlayer != null
            && !_handshake.Task.IsCompleted
            && now - _lastProbe > 1)
        {
            _lastProbe = now;
            if (_probeAttempts++ >= 5)
            {
                Plugin.LogVerbose($"NetworkController: Update: Probe failed");
                _handshake.TrySetResult(false);
            }
            else
            {
                Plugin.LogVerbose($"NetworkController: Update: Sending probe");
                var writer = Multiplayer.CreateNamedWriter(Probe);
                writer.Put(ProtocolVersion);
                writer.Put(Plugin.ModVersion);
                writer.Put(_helloToken);
                SendRaw(0, writer);
            }
        }

        if (_queue.Count > 0)
        {
            _queue.RemoveAll(r => now - r.Seen > QueueExpiry);
            PumpQueue();
        }

        foreach (var (key, item) in _outgoing.ToArray())
        {
            if (now - item.Progress > TransferIdleTimeout || now - item.Started > TransferTimeout)
            {
                Plugin.LogVerbose($"NetworkController: Update: Outgoing: timed out id={item.Image.Id}");
                FinishOutgoing(key, false);
                continue;
            }

            if (!item.Accepted)
            {
                if (now >= item.RetryAt && now - item.OfferSent >= OfferIdleResend)
                {
                    item.OfferSent = now;
                    SendOffer(item);
                }
            }
            else if (item.Acked < item.NextFragmentIndex && now - item.LastAck > ResendTimeout)
            {
                Plugin.LogVerbose($"NetworkController: Update: Outgoing: No acks, resending from {item.Acked} id={item.Image.Id}");
                item.NextFragmentIndex = item.Acked;
                item.LastAck = now;
            }
        }

        PumpFragments();

        foreach (var incoming in _incoming.Values)
        {
            if (incoming.NeedsAck && !incoming.Preparing && !incoming.Saving)
                SendAck(incoming);
        }

        foreach (var (key, item) in _incoming.ToArray())
        {
            if ((now - item.Progress > TransferIdleTimeout || now - item.Started > TransferTimeout) && !item.Saving)
            {
                Plugin.LogVerbose($"NetworkController: Update: Incoming: timed out id={key}");
                _incoming.Remove(key);
                if (!IsHost && _downloads.TryGetValue(item.Token, out var download))
                {
                    _downloads.Remove(item.Token);
                    download.Completion.TrySetResult(null);
                }

                Result(item.Peer, item.Token, false, "Image transfer timed out.");
            }
        }

        foreach (var (key, item) in _downloads.ToArray())
        {
            if (!item.Receiving && now - item.LastActivity > DownloadIdleTimeout || now - item.Started > DownloadTimeout)
            {
                Plugin.LogVerbose($"NetworkController: Update: Download: timed out id={key}");
                _downloads.Remove(key);
                _incoming.Remove(Key(0, key));
                item.Completion.TrySetResult(null);
                continue;
            }

            if (!item.Receiving && now >= item.NextGet)
            {
                Plugin.LogVerbose($"NetworkController: Update: Download: Sending get for id={item.Id}");
                item.NextGet = now + 2;
                Send(0, Kind.Get, w =>
                {
                    w.Put(key);
                    w.Put(item.Id);
                });
            }
        }

        foreach (string key in _receipts.Where(p => p.Value.Expires < now).Select(p => p.Key).ToArray())
            _receipts.Remove(key);
    }

    internal async Task<bool> EnsureHostAsync()
    {
        if (IsHost)
            return true;

        var token = SessionToken;
        Task<bool> handshake = _handshake.Task;

        await Task.WhenAny(handshake, Task.Delay(5000, token));
        token.ThrowIfCancellationRequested();

        return handshake.IsCompleted && handshake.Result;
    }

    internal async Task<bool> UploadAsync(SprayImage image)
    {
        Plugin.LogVerbose($"NetworkController: UploadAsync id={image?.Id}");
        var token = SessionToken;
        if (!await EnsureHostAsync())
            return false;
        token.ThrowIfCancellationRequested();
        
        if (image == null
            || image.Bytes.Length > HostMaxSize
            || HostRequiresBackend && !image.BackendUploaded)
            return false;
        
        if (IsHost)
        {
            Plugin.LogVerbose($"NetworkController: UploadAsync: Storing on host");
            var stored = await Service.StoreAsync(image);
            token.ThrowIfCancellationRequested();

            if (!await Service.ContainsAsync(stored.Id, stored.Bytes.Length))
                return false;
            token.ThrowIfCancellationRequested();

            Plugin.LogVerbose($"NetworkController: UploadAsync: Announcing stored image");
            Announce(stored);
            return true;
        }

        if (!CanSend(image.Bytes.Length))
            return false;
        var transfer = new Outgoing { Peer = 0, Token = Guid.NewGuid().ToString("N"), Image = image };
        _outgoing.Add(Key(0, transfer.Token), transfer);
        Plugin.LogVerbose($"NetworkController: UploadAsync: Awaiting upload");
        return await transfer.Completion.Task;
    }

    internal async Task<SprayImage> DownloadAsync(string id)
    {
        Plugin.LogVerbose($"NetworkController: DownloadAsync id={id}");
        var token = SessionToken;
        if (!await EnsureHostAsync())
            return null;
        token.ThrowIfCancellationRequested();
        
        if (IsHost)
        {
            Plugin.LogVerbose($"NetworkController: DownloadAsync: Reading from host...");
            return await Service.ReadAsync(id);
        }
        
        var existing = _downloads.Values.FirstOrDefault(d => d.Id == id);
        if (existing != null)
        {
            Plugin.LogVerbose($"NetworkController: DownloadAsync: Awaiting existing download");
            return await existing.Completion.Task;
        }
        
        if (_downloads.Count >= MaxTransfers)
        {
            Plugin.LogVerbose($"NetworkController: DownloadAsync: Too many downloads!");
            return null;
        }

        var request = Guid.NewGuid().ToString("N");
        var download = new Download { Id = id };
        _downloads[request] = download;
        Plugin.LogVerbose($"NetworkController: DownloadAsync: Awaiting download");
        return await download.Completion.Task;
    }

    internal async void Inspect(string id)
    {
        Plugin.LogVerbose($"NetworkController: Inspect id={id}");
        try
        {
            var token = SessionToken;
            if (!await EnsureHostAsync())
                return;
            token.ThrowIfCancellationRequested();

            if (!IsHost)
            {
                Plugin.LogVerbose($"NetworkController: Sending inspect request");
                Send(0, Kind.Inspect, w => w.Put(id));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning(ex.Message);
        }
    }

    private bool CanSend(int size)
    {
        return _outgoing.Count < MaxTransfers && _outgoing.Values.Sum(t => (long)t.Image.Bytes.Length) + size <= TransferBudget;
    }

    private static void ReceiveProbe(ScavPlayer player, NetDataReader reader)
    {
        var peer = player.playerId;
        Plugin.LogVerbose($"ReceiveProbe: Received probe from {peer}");
        if (Instance == null)
            return;

        try
        {
            int protocol = reader.GetInt();
            string version = reader.GetString(64);
            string session = reader.GetString(32);

            if (protocol != ProtocolVersion)
            {
                // for later
                // If not compatible, return protocol version -1
            }

            if (string.IsNullOrEmpty(version)
                || session.Length != 32
                || !SprayImage.IsValidId(session))
            {
                Plugin.LogVerbose($"ReceiveProbe: Go away, {peer}");
                return;
            }

            if (Instance._peerSessions.TryGetValue(peer.id, out string old) && old != session)
            {
                Plugin.LogVerbose($"ReceiveProbe: Forgetting {peer}");
                Instance.ForgetPeer(peer.id);
            }

            Instance._peers.Add(peer.id);
            Instance._peerSessions[peer.id] = session;
            var writer = Multiplayer.CreateNamedWriter(ProbeAck);
            writer.Put(ProtocolVersion);
            writer.Put(Plugin.ModVersion);
            writer.Put(session);
            writer.Put(Instance.HostMaxSize);
            writer.Put(Instance.HostRequiresBackend);
            Instance.SendRaw(peer.id, writer);
            Plugin.LogVerbose($"ReceiveProbe: Sent ack to {peer} with token {session}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Invalid spray handshake: " + ex.Message);
        }
    }

    private static void ReceiveProbeAck(NetDataReader reader)
    {
        Plugin.LogVerbose($"ReceiveProbeAck");
        if (Instance == null)
            return;

        try
        {
            var protocol = reader.GetInt();
            var modVersion = reader.GetString(64);

            if (protocol == -1)
            {
                var message = $"FullResSprays disabled: Your version {Plugin.ModVersion} is incompatible with host's {modVersion}";
                Con.con?.LogToConsole(message);
                SprayService.Alert(message);
                return;
            }

            if (reader.GetString(32) != Instance._helloToken)
            {
                Plugin.LogVerbose($"ReceiveProbeAck: Not my token. Expected: {Instance._helloToken}");
                return;
            }

            int limit = reader.GetInt();
            bool require = reader.GetBool();
            if (limit <= 0 || limit > SprayImage.HardByteSizeLimit)
                return;

            Instance.HostMaxSize = limit;
            Instance.HostRequiresBackend = require;
            Instance._handshake.TrySetResult(true);
            Plugin.LogVerbose($"ReceiveProbeAck: Complete with maxSize={limit} and requiresBackend={require}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Invalid spray handshake response: " + ex.Message);
        }
    }

    private static void ReceiveHost(ScavPlayer player, NetDataReader reader)
    {
        if (Instance != null && Instance._peers.Contains(player.playerId))
            Instance.Receive(player.playerId, ref reader);
    }

    private static void ReceiveClient(NetDataReader reader)
    {
        if (Instance != null && Instance._handshake.Task.IsCompletedSuccessfully && Instance._handshake.Task.Result)
            Instance.Receive(0, ref reader);
    }

    private void Receive(ushort peer, ref NetDataReader reader)
    {
        try
        {
            var kind = (Kind)reader.GetByte();
            string session = reader.GetString(32);
            if (IsHost
                    ? !_peerSessions.TryGetValue(peer, out string expectedSession) || expectedSession != session
                    : session != _helloToken)
            {
                Plugin.LogVerbose($"NetworkController: Receive: Bad session");
                return;
            }

            if (kind == Kind.Inspect)
            {
                string id = ReadId(reader);
                Plugin.LogVerbose($"NetworkController: Receive: Inspect for {id}");
                if (IsHost)
                    SendNotice(peer, id);
                return;
            }

            if (kind == Kind.Notice)
            {
                string id = ReadId(reader);
                Plugin.LogVerbose($"NetworkController: Receive: Notice for {id}");
                int size = reader.GetInt();
                if (!IsHost && size > 0 && size <= SprayImage.HardByteSizeLimit)
                    Service.CacheUpdated(id, size);
                return;
            }

            string token = reader.GetString(32);
            if (token.Length != 32 || !SprayImage.IsValidId(token))
                throw new ArgumentException("Invalid transfer token.");

            string key = Key(peer, token);
            Plugin.LogVerbose($"NetworkController: Receive: TRANSFER KEY {key}");

            switch (kind)
            {
                case Kind.Get:
                    string getId = ReadId(reader);
                    Plugin.LogVerbose($"NetworkController: Receive: Get for {getId}");
                    if (IsHost)
                        Serve(peer, token, getId);
                    break;
                case Kind.Offer:
                    string id = ReadId(reader);
                    int size = reader.GetInt();
                    string hash = reader.GetString(64);
                    bool backend = reader.GetBool();
                    int fragmentSize = reader.GetInt();
                    Plugin.LogVerbose($"NetworkController: Receive: Offer for {id} of size {size / 1024}KiB");
                    Offer(peer, token, id, size, hash, backend, fragmentSize);
                    break;
                case Kind.Ack:
                    int received = reader.GetInt();
                    int limit = reader.GetInt();
                    Plugin.LogVerbose($"NetworkController: Receive: Ack received={received} limit={limit}");
                    if (_outgoing.TryGetValue(key, out var outgoing))
                        outgoing.Ack(received, limit, Time.realtimeSinceStartup);
                    break;
                case Kind.Fragment:
                    int index = reader.GetInt();
                    Plugin.LogVerbose($"NetworkController: Receive: Fragment of index={index} size={reader.AvailableBytes / 1024}KiB");

                    if (_receipts.TryGetValue(key, out var receipt))
                    {
                        Plugin.LogVerbose($"NetworkController: Receive: Fragment: Have receipt");
                        Result(peer, token, receipt.Success, receipt.Message);
                        break;
                    }

                    if (!_incoming.TryGetValue(key, out var incoming)
                        || incoming.Saving
                        || incoming.Preparing)
                    {
                        Plugin.LogVerbose($"NetworkController: Receive: Fragment: No prior request or bad timing (null={incoming == null})");
                        break;
                    }

                    if (index >= incoming.Limit)
                    {
                        Plugin.LogVerbose($"NetworkController: Receive: Fragment: Outside granted credit ({incoming.Limit})");
                        break;
                    }

                    if (incoming.Buffer.Add(index, reader))
                    {
                        incoming.Progress = Time.realtimeSinceStartup;
                        incoming.Limit = Math.Min(incoming.Buffer.Count, incoming.Buffer.ToReceiveIndex + WindowFragments);
                    }

                    incoming.NeedsAck = true;

                    if (incoming.Buffer.IsComplete)
                    {
                        Plugin.LogVerbose($"NetworkController: Receive: Fragment: Completing");
                        // Ack now to stop the sender from resending
                        SendAck(incoming);
                        CompleteIncoming(incoming);
                    }

                    break;
                case Kind.Wait:
                    Plugin.LogVerbose($"NetworkController: Receive: Wait");
                    float waitNow = Time.realtimeSinceStartup;
                    if (_outgoing.TryGetValue(key, out var waiting) && !waiting.Accepted)
                    {
                        waiting.Progress = waitNow;
                        waiting.OfferSent = float.NegativeInfinity;
                        waiting.RetryAt = waitNow + Backoff(waiting.Waits++);
                    }

                    if (!IsHost && _downloads.TryGetValue(token, out var pending) && !pending.Receiving)
                    {
                        pending.LastActivity = waitNow;
                        pending.NextGet = waitNow + Backoff(pending.Waits++);
                    }

                    break;
                case Kind.Result:
                    bool success = reader.GetBool();
                    string message = reader.GetString(256);
                    Plugin.LogVerbose($"NetworkController: Receive: Result: {success}, {message}");
                    FinishOutgoing(key, success);
                    if (!IsHost && _downloads.TryGetValue(token, out var download) && !success)
                    {
                        _downloads.Remove(token);
                        _incoming.Remove(key);
                        download.Completion.TrySetResult(null);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Invalid spray packet: " + ex.Message);
        }
    }

    private static string ReadId(NetDataReader reader)
    {
        string id = reader.GetString(128);
        if (!SprayImage.IsValidId(id))
            throw new ArgumentException("Invalid spray ID.");
        return id;
    }

    private async void Offer(ushort peer, string token, string id, int size, string hash, bool backend, int fragmentSize)
    {
        Plugin.LogVerbose($"NetworkController: Offer: peer={peer}, id={id}, size={size / 1024}KiB");
        string key = Key(peer, token);
        int generation = _generation;
        _peerSessions.TryGetValue(peer, out string session);
        try
        {
            if (_receipts.TryGetValue(key, out var receipt))
            {
                Plugin.LogVerbose($"NetworkController: Offer: Have receipt");
                Result(peer, token, receipt.Success, receipt.Message);
                return;
            }

            if (_incoming.TryGetValue(key, out var existing))
            {
                Plugin.LogVerbose($"NetworkController: Offer: existing...");
                if (!existing.Saving && !existing.Preparing)
                    SendAck(existing); // The sender missed our Ack
                return;
            }

            if (_preparing.Contains(key))
                return;
            if (!IsHost && (!_downloads.TryGetValue(token, out var expected) || expected.Id != id))
                return;
            if (!IsHost)
                _downloads[token].LastActivity = Time.realtimeSinceStartup;

            if (size <= 0
                || size > SprayImage.HardByteSizeLimit
                || fragmentSize is < FragmentBuffer.MinFragmentSize or > FragmentBuffer.MaxFragmentSize
                || hash.Length != 64
                || !SprayImage.IsValidId(hash)
                || id.StartsWith("custom-", StringComparison.Ordinal) && (id != "custom-" + hash || backend))
            {
                throw new ArgumentException("Invalid image metadata.");
            }

            if (IsHost && (size > HostMaxSize || HostRequiresBackend && !backend))
            {
                Plugin.LogVerbose($"NetworkController: Offer: Rejecting");
                Result(peer, token, false, "Host image policy rejected the upload.");
                return;
            }

            if (_preparing.Count >= MaxTransfers
                || _incoming.Count >= MaxTransfers
                || _incoming.Values.Count(t => t.Peer == peer) >= 2
                || _incoming.Values.Sum(t => (long)t.Buffer.Bytes.Length) + size > TransferBudget)
            {
                Plugin.LogVerbose($"NetworkController: Offer: Busy, asking to wait");
                SendWait(peer, token);
                return;
            }

            var incoming = new Incoming
            {
                Peer = peer, 
                Token = token, 
                Id = id, 
                Hash = hash, 
                Backend = backend, 
                Buffer = new FragmentBuffer(size, fragmentSize)
            };
            _incoming[key] = incoming;

            Plugin.LogVerbose($"NetworkController: Offer: Reading cache");
            var cached = await Service.ReadAsync(id);
            if (!SameSession(generation, peer, session) || !_incoming.ContainsKey(key))
                return;

            if (IsHost && cached != null && cached.Bytes.Length >= size)
            {
                Plugin.LogVerbose($"NetworkController: Offer: Already available, sending cache update");
                _incoming.Remove(key);
                RememberReceipt(peer, token, true, "Already available.");
                Announce(cached);
                return;
            }

            if (!IsHost)
                _downloads[token].Receiving = true;
            incoming.Preparing = false;
            incoming.Limit = Math.Min(incoming.Buffer.Count, WindowFragments);
            SendAck(incoming);
            Plugin.LogVerbose($"NetworkController: Offer: Accepted with limit={incoming.Limit}");
        }
        catch (Exception ex)
        {
            if (!SameSession(generation, peer, session))
                return;
            _incoming.Remove(key);
            Result(peer, token, false, ex.Message);
        }
    }

    private async void CompleteIncoming(Incoming incoming)
    {
        Plugin.LogVerbose($"NetworkController: CompleteIncoming: {incoming.Id}, {incoming.Buffer.Bytes.Length / 1024}KiB");
        incoming.Saving = true;
        string key = Key(incoming.Peer, incoming.Token);
        int generation = _generation;
        _peerSessions.TryGetValue(incoming.Peer, out string session);
        try
        {
            Plugin.LogVerbose($"NetworkController: CompleteIncoming: Creating image");
            var image = await Task.Run(() =>
            {
                var value = new SprayImage(incoming.Id, incoming.Buffer.Bytes, incoming.Backend);
                if (value.Hash != incoming.Hash)
                    throw new ArgumentException("Image checksum mismatch.");
                return value;
            });
            if (!SameSession(generation, incoming.Peer, session))
                return;

            Plugin.LogVerbose($"NetworkController: CompleteIncoming: Saving image");
            var stored = await Service.StoreAsync(image);
            if (!SameSession(generation, incoming.Peer, session))
                return;

            if (IsHost && !await Service.ContainsAsync(stored.Id, stored.Bytes.Length))
                throw new InvalidOperationException("Image does not fit the host caches.");
            if (!SameSession(generation, incoming.Peer, session))
                return;

            Plugin.LogVerbose($"NetworkController: CompleteIncoming: Storing receipt");
            _incoming.Remove(key);
            RememberReceipt(incoming.Peer, incoming.Token, true, "Stored.");
            if (IsHost)
            {
                Plugin.LogVerbose($"NetworkController: CompleteIncoming: Announcing");
                Announce(stored);
            }
            else if (_downloads.TryGetValue(incoming.Token, out var download))
            {
                Plugin.LogVerbose($"NetworkController: CompleteIncoming: Remove from downloads");
                _downloads.Remove(incoming.Token);
                download.Completion.TrySetResult(stored);
            }
        }
        catch (Exception ex)
        {
            if (!SameSession(generation, incoming.Peer, session))
                return;

            _incoming.Remove(key);
            RememberReceipt(incoming.Peer, incoming.Token, false, ex.Message);
            if (_downloads.TryGetValue(incoming.Token, out var download))
            {
                _downloads.Remove(incoming.Token);
                download.Completion.TrySetResult(null);
            }
        }
    }

    private void Serve(ushort peer, string token, string id)
    {
        Plugin.LogVerbose($"NetworkController: Serve: peer={peer}, id={id}");
        string key = Key(peer, token);
        if (_outgoing.ContainsKey(key) || _preparing.Contains(key))
            return;

        var request = _queue.Find(r => r.Peer == peer && r.Token == token);
        if (request == null)
        {
            if (_queue.Count >= MaxQueued || _queue.Count(r => r.Peer == peer) >= MaxQueuedPerPeer)
            {
                Plugin.LogVerbose($"NetworkController: Serve: Queue full");
                SendWait(peer, token);
                return;
            }

            request = new Request { Peer = peer, Token = token, Id = id };
            _queue.Add(request);
        }

        request.Seen = Time.realtimeSinceStartup;
        PumpQueue();
        if (_queue.Contains(request))
        {
            Plugin.LogVerbose($"NetworkController: Serve: Queued at {_queue.IndexOf(request)}");
            SendWait(peer, token);
        }
    }

    private int ServesFor(ushort peer)
    {
        string prefix = peer + ":";
        return _outgoing.Values.Count(t => t.Peer == peer) + _preparing.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    private void PumpQueue()
    {
        for (int i = 0; i < _queue.Count;)
        {
            if (_preparing.Count >= MaxPreparing || _outgoing.Count + _preparing.Count >= MaxTransfers)
                break;

            var request = _queue[i];
            if (ServesFor(request.Peer) >= MaxServesPerPeer || request.Size >= 0 && !CanSend(request.Size))
            {
                i++;
                continue;
            }

            _queue.RemoveAt(i);
            StartServe(request);
        }
    }

    private async void StartServe(Request request)
    {
        ushort peer = request.Peer;
        string token = request.Token;
        string key = Key(peer, token);
        _preparing.Add(key);
        int generation = _generation;
        _peerSessions.TryGetValue(peer, out string session);
        try
        {
            Plugin.LogVerbose($"NetworkController: Serve: Reading image");
            var image = await Service.ReadAsync(request.Id);
            if (!SameSession(generation, peer, session))
                return;

            Plugin.LogVerbose($"NetworkController: Serve: Image check");
            if (image == null)
            {
                Result(peer, token, false, "Image is unavailable on the host.");
                return;
            }

            if (!CanSend(image.Bytes.Length))
            {
                Plugin.LogVerbose($"NetworkController: Serve: Over budget, requeueing");
                request.Size = image.Bytes.Length;
                request.Seen = Time.realtimeSinceStartup;
                _queue.Insert(0, request);
                SendWait(peer, token);
                return;
            }

            Plugin.LogVerbose($"NetworkController: Serve: Adding to outgoing");
            _outgoing[key] = new Outgoing { Peer = peer, Token = token, Image = image };
        }
        catch (Exception ex)
        {
            if (SameSession(generation, peer, session))
                Result(peer, token, false, ex.Message);
        }
        finally
        {
            if (SameSession(generation, peer, session))
                _preparing.Remove(key);
        }
    }

    private void SendOffer(Outgoing item)
    {
        Plugin.LogVerbose($"NetworkController: SendOffer: token={item.Token} id={item.Image.Id} size={item.Image.Bytes.Length / 1024}KiB");
        try
        {
            Send(item.Peer, Kind.Offer, w =>
            {
                w.Put(item.Token);
                w.Put(item.Image.Id);
                w.Put(item.Image.Bytes.Length);
                w.Put(item.Image.Hash);
                w.Put(item.Image.BackendUploaded);
                w.Put(item.FragmentSize);
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Spray offer failed: " + ex.Message);
        }
    }

    private void SendAck(Incoming incoming)
    {
        incoming.NeedsAck = false;
        Send(incoming.Peer, Kind.Ack, w =>
        {
            w.Put(incoming.Token);
            w.Put(incoming.Buffer.ToReceiveIndex);
            w.Put(incoming.Limit);
        });
    }

    private void PumpFragments()
    {
        List<Outgoing> transfers = null;
        var peerInFlight = new Dictionary<ushort, long>();
        long totalInFlight = 0;
        foreach (var item in _outgoing.Values)
        {
            totalInFlight += item.InFlightBytes;
            peerInFlight[item.Peer] = peerInFlight.GetValueOrDefault(item.Peer) + item.InFlightBytes;
            if (item.Accepted && item.NextFragmentIndex < item.Limit)
                (transfers ??= []).Add(item);
        }

        if (transfers == null)
            return;

        int start = _pumpRoundRobinRotation++ % transfers.Count;
        _pumpRoundRobinRotation &= int.MaxValue;
        int sent = 0;
        bool any = true;
        while (any && sent < FrameFragmentLimit)
        {
            any = false;
            for (int i = 0; i < transfers.Count && sent < FrameFragmentLimit; i++)
            {
                var item = transfers[(start + i) % transfers.Count];
                if (item.NextFragmentIndex >= item.Limit)
                    continue;

                int length = item.FragmentLength(item.NextFragmentIndex);
                long peerBytes = peerInFlight[item.Peer];
                if (peerBytes > 0 && peerBytes + length > PeerWindowBytes
                    || totalInFlight > 0 && totalInFlight + length > TotalWindowBytes)
                    continue;

                SendFragment(item, item.NextFragmentIndex);
                item.NextFragmentIndex++;
                peerInFlight[item.Peer] = peerBytes + length;
                totalInFlight += length;
                sent++;
                any = true;
            }
        }
    }

    private void SendFragment(Outgoing item, int index)
    {
        Plugin.LogVerbose($"NetworkController: SendFragment: token={item.Token} id={item.Image.Id} index={index}");
        try
        {
            int offset = item.Offset(index);
            int length = item.FragmentLength(index);
            Send(item.Peer, Kind.Fragment, w =>
            {
                w.Put(item.Token);
                w.Put(index);
                w.Put(item.Image.Bytes, offset, length);
            });
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Spray send failed: " + ex.Message);
        }
    }

    private void FinishOutgoing(string key, bool success)
    {
        Plugin.LogVerbose($"NetworkController: FinishOutgoing");
        if (!_outgoing.TryGetValue(key, out var item))
            return;
        _outgoing.Remove(key);
        item.Completion.TrySetResult(success);
    }

    private void RememberReceipt(ushort peer, string token, bool success, string message)
    {
        Plugin.LogVerbose($"NetworkController: RememberReceipt");
        if (_receipts.Count >= 256)
            _receipts.Remove(_receipts.Keys.First());
        _receipts[Key(peer, token)] = new Receipt { Success = success, Message = message };
        Result(peer, token, success, message);
    }

    private void SendWait(ushort peer, string token)
    {
        Send(peer, Kind.Wait, w => w.Put(token));
    }

    private void Result(ushort peer, string token, bool success, string message)
    {
        message ??= "";
        if (message.Length > 256)
            message = message.Substring(0, 256);
        Send(peer, Kind.Result, w =>
        {
            w.Put(token);
            w.Put(success);
            w.Put(message);
        });
    }

    private async void SendNotice(ushort peer, string id)
    {
        Plugin.LogVerbose($"NetworkController: SendNotice peer={peer}, id={id}");
        int generation = _generation;
        string key = peer + ":" + id;
        if (_inspecting.Count >= MaxInspecting || !_inspecting.Add(key))
            return;

        _peerSessions.TryGetValue(peer, out string session);
        try
        {
            var image = await Service.ReadAsync(id);
            if (SameSession(generation, peer, session) && image != null)
            {
                Notice(peer, image);
                Plugin.LogVerbose($"NetworkController: SendNotice: sent");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning(ex.Message);
        }
        finally
        {
            if (SameSession(generation, peer, session))
                _inspecting.Remove(key);
        }
    }

    private void Notice(ushort peer, SprayImage image)
    {
        Plugin.LogVerbose($"NetworkController: Notice: peer={peer} id={image.Id} size={image.Bytes.Length / 1024}KiB");
        Send(peer, Kind.Notice, w =>
        {
            w.Put(image.Id);
            w.Put(image.Bytes.Length);
        });
    }

    private void Announce(SprayImage image)
    {
        Plugin.LogVerbose($"NetworkController: Announce: id={image.Id} size={image.Bytes.Length / 1024}KiB");
        try
        {
            Service.ApplyImage(image);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("Spray refresh failed: " + ex.Message);
        }

        if (!Net.IsRunning)
            return;

        foreach (ushort peer in _peers.ToArray())
        {
            if (ServerMain.AllClientIds.Any(id => id.id == peer))
            {
                try
                {
                    Notice(peer, image);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning("Spray notification failed: " + ex.Message);
                }
            }
            else
            {
                ForgetPeer(peer);
            }
        }
    }

    private void Send(ushort peer, Kind kind, Action<NetDataWriter> write)
    {
        var writer = Multiplayer.CreateNamedWriter(IsHost ? ToClient : ToHost);
        if (IsHost && !_peerSessions.ContainsKey(peer))
            return;

        writer.Put((byte)kind);
        writer.Put(IsHost ? _peerSessions[peer] : _helloToken);

        write(writer);
        SendRaw(peer, writer);
    }

    private void SendRaw(ushort peer, NetDataWriter writer)
    {
        if (IsHost)
            Net.Server_SendTo(DeliveryMethod.ReliableUnordered, writer, peer);
        else
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
    }

    public void ResetSession()
    {
        Plugin.LogVerbose($"NetworkController: ResetSession");
        _generation++;
        _session.Cancel();
        _session.Dispose();
        _session = new CancellationTokenSource();
        foreach (var item in _outgoing.Values)
            item.Completion.TrySetCanceled();
        foreach (var item in _downloads.Values)
            item.Completion.TrySetCanceled();
        _handshake.TrySetResult(false);
        _handshake = NewCompletion<bool>();
        _outgoing.Clear();
        _incoming.Clear();
        _downloads.Clear();
        _receipts.Clear();
        _preparing.Clear();
        _queue.Clear();
        _inspecting.Clear();
        _peers.Clear();
        _peerSessions.Clear();
        _helloToken = Guid.NewGuid().ToString("N");
        _lastProbe = float.NegativeInfinity;
        _probeAttempts = 0;
        HostMaxSize = 0;
        HostRequiresBackend = false;
        Service?.ResetSession();
    }

    private void OnApplicationQuit()
    {
        Close();
    }

    private void OnDestroy()
    {
        Close();
        SceneManager.sceneLoaded -= SceneLoaded;
        ScavPlayer.OnPlayerJoined -= PlayerJoined;
        ScavPlayer.OnPlayerLeft -= PlayerLeft;
        if (Instance == this)
            Instance = null;
    }

    private void Close()
    {
        Plugin.LogVerbose($"NetworkController: Close");
        if (_closed)
            return;

        _closed = true;
        ResetSession();
        _session.Cancel();
        Service?.Close(Plugin.Instance != null && Plugin.Instance.ConfigClearDiskCacheOnExit.Value);
    }
}
