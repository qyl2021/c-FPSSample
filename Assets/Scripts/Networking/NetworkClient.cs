using System;
using System.Collections.Generic;
using UnityEngine;
using NetworkCompression;
using UnityEngine.Profiling;

public interface ISnapshotConsumer
{
    void ProcessEntitySpawn(int serverTime, int id, ushort typeId);
    void ProcessEntityUpdate(int serverTime, int id, ref NetworkReader reader);
    void ProcessEntityDespawn(int serverTime, int id);
}

public interface INetworkClientCallbacks : INetworkCallbacks
{
    void OnMapUpdate(ref NetworkReader data);
    void ProcessSnapshot(int serverTime);
}

public class NetworkClient
{
    [ConfigVar(Name = "client.debug", DefaultValue = "0", Description = "Enable debug printing of client handshake etc.", Flags = ConfigVar.Flags.None)]
    public static ConfigVar clientDebug;

    [ConfigVar(Name = "client.blockin", DefaultValue = "0", Description = "Cut all incoming network traffic.", Flags = ConfigVar.Flags.None)]
    public static ConfigVar clientBlockIn;
    [ConfigVar(Name = "client.blockout", DefaultValue = "0", Description = "Cut all outgoing network traffic.", Flags = ConfigVar.Flags.None)]
    public static ConfigVar clientBlockOut;

    [ConfigVar(Name = "client.verifyprotocol", DefaultValue = "1", Description = "Verify protocol match when connecting to server.", Flags = ConfigVar.Flags.None)]
    public static ConfigVar clientVerifyProtocol;

    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected
    }

    // Sent from client to server when changed
    public class ClientConfig
    {
        public int serverUpdateRate;
        public int serverUpdateSendRate;
    }

    public class Counters : NetworkConnectionCounters
    {
        public int snapshotsIn;             // Total number of snapshots received
        public int fullSnapshotsIn;         // Number of snapshots without a baseline
        public int commandsOut;             // Number of command messages sent
        public int[] packageContentStats;   // Breakdown of bits spent in last package
        public int packageContentStatsPackageSequence;  // incrementing when packageContentStats is filled
    }

    public Counters counters
    {
        get { return m_Connection != null ? m_Connection.counters : null; }
    }

    public bool isConnected
    {
        get { return m_Connection != null && m_Connection.connectionState == ConnectionState.Connected; }
    }

    public ConnectionState connectionState
    {
        get { return m_Connection != null ? m_Connection.connectionState : ConnectionState.Disconnected; }
    }

    public ClientConfig clientConfig;

    public int clientId { get { return m_Connection != null ? m_Connection.clientId : -1; } }
    public int serverTime { get { return m_Connection != null ? m_Connection.serverTime : -1; } }
    public int serverTickRate { get { return m_Connection != null ? m_Connection.serverTickRate : 60; } }
    public int lastAcknowlegdedCommandTime { get { return m_Connection != null ? m_Connection.lastAcknowlegdedCommandTime : -1; } }

    public float serverSimTime { get { return m_Connection != null ? m_Connection.serverSimTime : 0.0f; } }
    public int rtt { get { return m_Connection != null ? m_Connection.rtt : 0; } }
    public float timeSinceSnapshot { get { return m_Connection != null ? NetworkUtils.stopwatch.ElapsedMilliseconds - m_Connection.snapshotReceivedTime : -1; } }

    public delegate void DataGenerator(ref NetworkWriter data);
    public delegate void MapUpdateProcessor(ref NetworkReader data);
    public delegate void EntitySpawnProcessor(int id, ushort typeId);
    public delegate void EntityDespawnProcessor(int id);
    public delegate void EntityUpdateProcessor(int id, ref NetworkReader data);

    public NetworkClient(INetworkTransport transport)
    {
        m_Transport = transport;
        clientConfig = new ClientConfig();
    }

    public void Shutdown()
    {
        // TODO (ulfj) : Send disconnect?

        Disconnect();
    }

    internal void UpdateClientConfig()
    {
        Profiler.BeginSample("NetworkClient.UpdateClientConfig");

        clientConfig.serverUpdateRate = ClientGameLoop.clientUpdateRate.IntValue;
        clientConfig.serverUpdateSendRate = ClientGameLoop.clientUpdateSendRate.IntValue;
        if (m_Connection != null)
            m_Connection.ClientConfigChanged();

        Profiler.EndSample();
    }

    public bool Connect(string endpoint)
    {
        if (m_Connection != null)
        {
            GameDebug.Log("Must be disconnected before reconnecting");
            return false;
        }

        string address;
        var port = 0;

        if (endpoint.Contains(":"))
        {
            int.TryParse(endpoint.AfterLast(":"), out port);
            address = endpoint.BeforeFirst(":");
        }
        else
            address = endpoint;

        if (port == 0)
            port = NetworkConfig.defaultServerPort;

        // Resolve in case we got a hostname
        var resolvedAddress = System.Net.Dns.GetHostAddresses(address);
        foreach (var r in resolvedAddress)
        {
            if (r.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // Pick first ipv4
                address = r.ToString();
                break;
            }
        }

        var connectionId = m_Transport.Connect(address, port);
        if (connectionId == -1)
        {
            GameDebug.Log("Connect failed");
            return false;
        }

        m_Connection = new ClientConnection(connectionId, m_Transport, clientConfig);

        return true;
    }

    public void Disconnect()
    {
        if (m_Connection == null)
            return;

        m_Transport.Disconnect(m_Connection.connectionId);
    }

    public void QueueCommand(int time, DataGenerator generator)
    {
        if (m_Connection == null)
            return;

        m_Connection.QueueCommand(time, generator);
    }

    public void QueueEvent(ushort typeId, bool reliable, NetworkEventGenerator generator)
    {
        if (m_Connection == null)
            return;

        var e = NetworkEvent.Serialize(typeId, reliable, m_EventTypesOut, generator);
        m_Connection.QueueEvent(e);
        e.Release();
    }

    public void ProcessMapUpdate(INetworkClientCallbacks processor)
    {
        if (m_Connection == null)
            return;

        m_Connection.ProcessMapUpdate(processor);
    }

    public void Update(INetworkClientCallbacks loop)
    {
        Profiler.BeginSample("NetworkClient.Update");

        m_Transport.Update();

        // Debug tracking of outstanding events
        if(NetworkConfig.netDebug.IntValue > 1 && m_Connection != null)
        {
            var outstandingPackages = m_Connection.outstandingPackages;
            for (var i = 0; i < outstandingPackages.m_Elements.Length; i++)
            {
                if(outstandingPackages.m_Sequences[i] != -1 && outstandingPackages.m_Elements[i].events.Count > 0)
                    GameDebug.Log("Outstanding Package: " + i + " (idx), " + outstandingPackages.m_Sequences[i] + " (seq), " + outstandingPackages.m_Elements[i].events.Count + " (numevs), " + ((GameNetworkEvents.EventType)outstandingPackages.m_Elements[i].events[0].type.typeId) + " (ev0)");
            }
        }

        TransportEvent e = new TransportEvent();
        while (m_Transport.NextEvent(ref e))
        {
            switch (e.type)
            {
                case TransportEvent.Type.Connect:
                    OnConnect(e.connectionId);
                    break;
                case TransportEvent.Type.Disconnect:
                    OnDisconnect(e.connectionId);
                    break;
                case TransportEvent.Type.Data:
                    OnData(e.connectionId, e.data, e.dataSize, loop);
                    break;
            }
        }

        if(m_Connection != null)
            m_Connection.ProcessMapUpdate(loop);

        Profiler.EndSample();
    }

    public void SendData()
    {
        Profiler.BeginSample("NetworkClient.SendData");

        if (m_Connection == null || m_Connection.connectionState == ConnectionState.Disconnected)
            return;

#pragma warning disable 0162 // unreachable code
        switch (NetworkConfig.ioStreamType)
        {
            case NetworkCompression.IOStreamType.Raw:
                m_Connection.SendPackage<RawOutputStream>();
                break;
            case NetworkCompression.IOStreamType.Huffman:
                m_Connection.SendPackage<HuffmanOutputStream>();
                break;
            case NetworkCompression.IOStreamType.Rans:
                m_Connection.SendPackage<RansOutputStream>();
                break;
            default:
                GameDebug.Assert(false);
        }
#pragma warning restore

        Profiler.EndSample();
    }

    void OnConnect(int connectionId)
    {
        if (m_Connection != null && m_Connection.connectionId == connectionId)
            GameDebug.Assert(connectionState == ConnectionState.Connecting);
    }

    void OnDisconnect(int connectionId)
    {
        if (m_Connection != null && m_Connection.connectionId == connectionId)
        {
            if (m_Connection.connectionState == ConnectionState.Connected)
            {
                GameDebug.Log("Disconnected from server");
                GameDebug.Log(string.Format("Last package sent : {0} . Last package received {1} {2} ms ago",
                    m_Connection.outSequence,
                    m_Connection.inSequence,
                    NetworkUtils.stopwatch.ElapsedMilliseconds - m_Connection.inSequenceTime));
            }
            else if (m_Connection.connectionState == ConnectionState.Connecting)
                GameDebug.Log("Server never replied when trying to connect ... disconnecting");

            m_Connection.Reset();
            m_Connection = null;
        }
    }

    void OnData(int connectionId, byte[] data, int size, INetworkClientCallbacks loop)
    {
        if (clientBlockIn.IntValue > 0)
            return;

        // SHould these be asserts?
        if (m_Connection != null && m_Connection.connectionId == connectionId)
        {
#pragma warning disable 0162 // unreached code
            switch (NetworkConfig.ioStreamType)
            {
                case NetworkCompression.IOStreamType.Raw:
                    {
                        m_Connection.ReadPackage<RawInputStream>(data, size, m_Connection.compressionModel, loop);
                        break;
                    }
                case NetworkCompression.IOStreamType.Huffman:
                    {
                        m_Connection.ReadPackage<HuffmanInputStream>(data, size, m_Connection.compressionModel, loop);
                        break;
                    }
                case NetworkCompression.IOStreamType.Rans:
                    {
                        m_Connection.ReadPackage<RansInputStream>(data, size, m_Connection.compressionModel, loop);
                        break;
                    }
                default:
                    GameDebug.Assert(false);
                    break;
            }
#pragma warning restore
        }
    }

    class ClientPackageInfo : PackageInfo
    {
        public int commandTime;
        public int commandSequence;

        public override void Reset()
        {
            base.Reset();
            commandTime = 0;
            commandSequence = 0;
        }
    }

    class ClientConnection : NetworkConnection<NetworkClient.Counters, ClientPackageInfo>
    {
        public long snapshotReceivedTime;               // Time we received the last snapshot
        public float serverSimTime;                     // Server simulation time (actualy time spent doing simulation regardless of tickrate)

        ClientConfig clientConfig;

        public ClientConnection(int connectionId, INetworkTransport transport, ClientConfig clientConfig) : base(connectionId, transport)
        {
            this.clientConfig = clientConfig;
        }

        public override void Reset()
        {
            base.Reset();
            serverTime = 0;
            entities.Clear();
            spawns.Clear();
            despawns.Clear();
            updates.Clear();
        }

        public void ClientConfigChanged()
        {
            sendClientConfig = true;
        }

        public void QueueCommand(int time, DataGenerator generator)
        {
            var generateSchema = (commandSchema == null);
            if (generateSchema)
                commandSchema = new NetworkSchema(NetworkConfig.networkClientQueueCommandSchemaId);

            var info = commandsOut.Acquire(++commandSequence);

            info.time = time;
            var writer = new NetworkWriter(info.data, commandSchema, generateSchema);
            generator(ref writer);
            writer.Flush();
        }

        public void ProcessMapUpdate(INetworkClientCallbacks loop)
        {
            if (mapInfo.mapSequence > 0 && !mapInfo.processed)
            {
                var reader = new NetworkReader(mapInfo.data, mapInfo.schema);
                loop.OnMapUpdate(ref reader);
                mapInfo.processed = true;
            }
        }

        public void ProcessSnapshot(ISnapshotConsumer consumer)
        {
            // Should we prune list for stuff that is also in despawns?
            foreach (var id in spawns)
            {
                GameDebug.Assert(entities[id].type != null, "Processing spawn of id {0} but type is null", id);
                consumer.ProcessEntitySpawn(serverTime, id, entities[id].type.typeId);
            }

            spawns.Clear();

            foreach (var id in updates)
            {
                var info = entities[id];
                GameDebug.Assert(info.type != null, "Processing update of id {0} but type is null", id);
                var reader = new NetworkReader(info.lastUpdate, info.type.schema);
                consumer.ProcessEntityUpdate(serverTime, id, ref reader);
            }

            updates.Clear();

            foreach (var id in despawns)
                consumer.ProcessEntityDespawn(serverTime, id);

            despawns.Clear();
        }

        public void ReadPackage<TInputStream>(byte[] packageData, int packageSize, NetworkCompressionModel compressionModel, INetworkClientCallbacks loop) where TInputStream : struct, NetworkCompression.IInputStream
        {
            counters.bytesIn += packageSize;

            NetworkMessage content;
            byte[] assembledData;
            int assembledSize;
            int headerSize;
            var packageSequence = ProcessPackageHeader(packageData, packageSize, out content, out assembledData, out assembledSize, out headerSize);

            // The package was dropped (duplicate or too old) so bail out here
            if (packageSequence == 0)
                return;

            var input = default(TInputStream); //  new TInputStream(); due to bug new generates garbage here
            input.Initialize(compressionModel, assembledData, headerSize);

            if ((content & NetworkMessage.ClientInfo) != 0)
                ReadClientInfo(ref input);

            if ((content & NetworkMessage.MapInfo) != 0)
                ReadMapInfo(ref input);

            // The package was received out of order but older than the last map reset, 
            // so we ignore the remaining data
            if (mapInfo.ackSequence == 0 || packageSequence < mapInfo.ackSequence)
                return;

            if ((content & NetworkMessage.Snapshot) != 0)
            {
                ReadSnapshot(packageSequence, ref input);
                loop.ProcessSnapshot(serverTime);

                // Make sure the callback actually picked up the snapshot data. It is important that
                // every snapshot gets processed by the game so that the spawns, despawns and updates lists
                // does not end up containing stuff from different snapshots
                GameDebug.Assert(spawns.Count == 0 && despawns.Count == 0 && updates.Count == 0, "Game did not consume snapshots");
            }

            if ((content & NetworkMessage.Events) != 0)
                ReadEvents(ref input, loop);

            counters.packageContentStatsPackageSequence = packageSequence;
        }

        public void SendPackage<TOutputStream>() where TOutputStream : struct, NetworkCompression.IOutputStream
        {
            // We don't start sending updates before we have received at 
            // least one content package from the server

            var rawOutputStream = new BitOutputStream(m_PackageBuffer);

            if (inSequence == 0 || !CanSendPackage(ref rawOutputStream))
                return;

            // Only if there is anything to send
            // TODO (petera) should we send empty packages at a low frequency?
            if (sendClientConfig == false && commandSequence > 0 && commandSequence <= lastSentCommandSeq && eventsOut.Count == 0)
                return;

            ClientPackageInfo info;
            BeginSendPackage(ref rawOutputStream, out info);

            int endOfHeaderPos = rawOutputStream.Align();
            var output = default(TOutputStream); //  new TOutputStream(); due to bug new generate garbage here
            output.Initialize(NetworkCompressionModel.DefaultModel, m_PackageBuffer, endOfHeaderPos, null);


            if (sendClientConfig)
                WriteClientConfig(ref output);

            if (commandSequence > 0)
            {
                lastSentCommandSeq = commandSequence;
                WriteCommands(info, ref output);
            }

            WriteEvents(info, ref output);
            int compressedSize = output.Flush();
            rawOutputStream.SkipBytes(compressedSize);

            CompleteSendPackage(info, ref rawOutputStream);
        }

        byte[] protocolId = new byte[128];
        byte[] modelData = new byte[32];
        void ReadClientInfo<TInputStream>(ref TInputStream input) where TInputStream : NetworkCompression.IInputStream
        {
            var newClientId = (int)input.ReadRawBits(8);
            serverTickRate = (int)input.ReadRawBits(8); // TODO (petera) remove this from here. This should only be handshake code. tickrate updated by other means like configvar
            var length = (int)input.ReadRawBits(8);
            input.ReadRawBytes(protocolId, 0, length);

            int modelSize = (int)input.ReadRawBits(16);
            if (modelSize < modelData.Length)
                modelData = new byte[modelSize];
            for (int i = 0; i < modelSize; i++)
                modelData[i] = (byte)input.ReadRawBits(8);

            if (connectionState == ConnectionState.Connected)
            {
                GameDebug.Log("Ignoring ClientInfo as we are already connected");
                return;
            }

            var ourProtocol = NetworkConfig.protocolVersion;
            var serverProtocol = NetworkConfig.encoding.GetString(protocolId, 0, length);
            GameDebug.Log("Client protocol id: " + ourProtocol);
            GameDebug.Log("Server protocol id: " + serverProtocol);
            if (ourProtocol.AfterLast(".") != serverProtocol.AfterLast("."))
            {
                if (clientVerifyProtocol.IntValue > 0)
                {
                    GameDebug.LogError("Protocol mismatch. Server is: " + serverProtocol + " and we are: " + ourProtocol);
                    connectionState = ConnectionState.Disconnected;
                    return;
                }
                GameDebug.Log("Ignoring protocol difference client.verifyprotocol is 0");
            }

            GameDebug.Assert(clientId == -1 || newClientId == clientId, "Repeated client info didn't match existing client id");

            compressionModel = new NetworkCompressionModel(modelData);

            clientId = newClientId;

            connectionState = ConnectionState.Connected;

            if (clientDebug.IntValue > 0)
            {
                GameDebug.Log(string.Format("ReadClientInfo: clientId {0}  serverTickRate {1}", newClientId, serverTickRate));
            }
        }

        void ReadMapInfo<TInputStream>(ref TInputStream input) where TInputStream : NetworkCompression.IInputStream
        {
            //input.SetStatsType(NetworkCompressionReader.Type.MapInfo);
            var mapSequence = (ushort)input.ReadRawBits(16);
            var schemaIncluded = input.ReadRawBits(1) != 0;
            if (schemaIncluded)
            {
                mapInfo.schema = NetworkSchema.ReadSchema(ref input);   // might override previous definition
            }

            if (mapSequence > mapInfo.mapSequence)
            {
                mapInfo.mapSequence = mapSequence;
                mapInfo.ackSequence = inSequence;
                mapInfo.processed = false;
                NetworkSchema.CopyFieldsToBuffer(mapInfo.schema, ref input, mapInfo.data);
                Reset();
            }
            else
                NetworkSchema.SkipFields(mapInfo.schema, ref input);
        }

        byte[] tempSnapshotBuffer = new byte[NetworkConfig.maxEntitySnapshotDataSize];
        void ReadSnapshot<TInputStream>(int sequence, ref TInputStream input) where TInputStream : NetworkCompression.IInputStream
        {
            //input.SetStatsType(NetworkCompressionReader.Type.SnapshotSchema);
            counters.snapshotsIn++;

            // Snapshot may be delta compressed against one or more baselines
            // Baselines are indicated by sequence number of the package it was in
            var baseSequence = (int)input.ReadPackedIntDelta(sequence - 1, NetworkConfig.baseSequenceContext);

            bool enableNetworkPrediction = input.ReadRawBits(1) != 0;
            bool enableHashing = input.ReadRawBits(1) != 0;

            int baseSequence1 = 0;
            int baseSequence2 = 0;
            if (enableNetworkPrediction)
            {
                baseSequence1 = (int)input.ReadPackedIntDelta(baseSequence - 1, NetworkConfig.baseSequence1Context);
                baseSequence2 = (int)input.ReadPackedIntDelta(baseSequence1 - 1, NetworkConfig.baseSequence2Context);
            }

            if (clientDebug.IntValue > 2)
            {
                if (enableNetworkPrediction)
                    GameDebug.Log((baseSequence > 0 ? "Snap [BL]" : "Snap [  ]") + "(" + sequence + ")  " + baseSequence + " - " + baseSequence1 + " - " + baseSequence2);
                else
                    GameDebug.Log((baseSequence > 0 ? "Snap [BL]" : "Snap [  ]") + "(" + sequence + ")  " + baseSequence);
            }

            if (baseSequence == 0)
                counters.fullSnapshotsIn++;

            GameDebug.Assert(baseSequence == 0 ||
                (sequence > baseSequence && sequence - baseSequence < NetworkConfig.snapshotDeltaCacheSize), "Attempting snapshot encoding with invalid baseline: {0}:{1}", sequence, baseSequence);

            var snapshotInfo = snapshots.Acquire(sequence);
            snapshotInfo.serverTime = (int)input.ReadPackedIntDelta(baseSequence != 0 ? snapshots[baseSequence].serverTime : 0, NetworkConfig.serverTimeContext);

            var temp = (int)input.ReadRawBits(8);
            serverSimTime = temp * 0.1f;

            // Only update time if received in-order.. 
            // TODO consider dropping out of order snapshots
            // TODO detecting out-of-order on pack sequences
            if (snapshotInfo.serverTime > serverTime)
            {
                serverTime = snapshotInfo.serverTime;
                snapshotReceivedTime = NetworkUtils.stopwatch.ElapsedMilliseconds;
            }
            else
            {
                GameDebug.Log(string.Format("NetworkClient. Dropping out of order snaphot. Server time:{0} snapshot time:{1}",serverTime, snapshotInfo.serverTime));
            }

            // Read schemas
            var schemaCount = input.ReadPackedUInt(NetworkConfig.schemaCountContext);
            for (int schemaIndex = 0; schemaIndex < schemaCount; ++schemaIndex)
            {
                var typeId = (ushort)input.ReadPackedUInt(NetworkConfig.schemaTypeIdContext);

                var entityType = new EntityTypeInfo() { typeId = typeId };
                entityType.schema = NetworkSchema.ReadSchema(ref input);
                entityType.baseline = new byte[NetworkConfig.maxEntitySnapshotDataSize];
                NetworkSchema.CopyFieldsToBuffer(entityType.schema, ref input, entityType.baseline);

                if (!entityTypes.ContainsKey(typeId))
                    entityTypes.Add(typeId, entityType);
            }

            // Remove any despawning entities that belong to older base sequences
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e.type == null)
                    continue;
                if (e.despawnSequence > 0 && e.despawnSequence <= baseSequence)
                    e.Reset();
            }

            // Read new spawns
            m_TempSpawnList.Clear();
            var previousId = 1;
            var spawnCount = input.ReadPackedUInt(NetworkConfig.spawnCountContext);
            for (var spawnIndex = 0; spawnIndex < spawnCount; ++spawnIndex)
            {
                var id = (int)input.ReadPackedIntDelta(previousId, NetworkConfig.idContext);
                previousId = id;

                // Register the entity
                var typeId = (ushort)input.ReadPackedUInt(NetworkConfig.spawnTypeIdContext);    //TODO: use another encoding
                GameDebug.Assert(entityTypes.ContainsKey(typeId), "Spawn request with unknown type id {0}", typeId);

                byte fieldMask = (byte)input.ReadRawBits(8);

                // Check if we already registered the entity since we can receive spawn information
                // in several snapshots before the client has ack a package containing the spawn

                // TODO (petera) need an max entity id for safety
                while (id >= entities.Count)
                    entities.Add(new EntityInfo());

                if(entities[id].type == null)
                {
                    var e = entities[id];
                    e.type = entityTypes[typeId];
                    e.fieldMask = fieldMask;
                    spawns.Add(id);
                }

                m_TempSpawnList.Add(id);
            }

            // Read despawns
            var despawnCount = input.ReadPackedUInt(NetworkConfig.despawnCountContext);
            for (var despawnIndex = 0; despawnIndex < despawnCount; ++despawnIndex)
            {
                var id = (int)input.ReadPackedIntDelta(previousId, NetworkConfig.idContext);
                previousId = id;

                // we may see despawns many times, only handle if we still have the entity
                GameDebug.Assert(id < entities.Count, "Getting despawn for id {0} but we only know about entities up to {1}", id, entities.Count);
                if(entities[id].type == null)
                    continue;

                var entity = entities[id];

                // Already in the process of being despawned. This happens with same-snapshot spawn/despawn cases
                if (entity.despawnSequence > 0)
                    continue;

                // If we are spawning and despawning in same snapshot, delay actual deletion of
                // entity as we need it around to be able to read the update part of the snapshot
                if (m_TempSpawnList.Contains(id))
                    entity.despawnSequence = sequence; // keep until baseSequence >= despawnSequence
                else
                    entity.Reset(); // otherwise remove right away; no further updates coming, not even in this snap

                // Add to despawns list so we can request despawn from game later
                GameDebug.Assert(!despawns.Contains(id), "Double despawn in same snaphot? {0}", id);
                despawns.Add(id);
            }

            // Predict all active entities
            for(var id = 0; id < entities.Count; id++)
            {
                var info = entities[id];
                if (info.type == null)
                    continue;

                // NOTE : As long as the server haven't gotten the spawn acked, it will keep sending
                // delta relative to 0, so we need to check if the entity was in the spawn list to determine
                // if the delta is relative to the last update or not

                int baseline0Time = 0;

                byte[] baseline0 = info.type.baseline;
                GameDebug.Assert(baseline0 != null, "Unable to find schema baseline for type {0}", info.type.typeId);

                if (baseSequence != 0 && !m_TempSpawnList.Contains(id))
                {
                    baseline0 = info.baselines.FindMax(baseSequence);
                    GameDebug.Assert(baseline0 != null, "Unable to find baseline for seq {0} for id {1}", baseSequence, id);
                    baseline0Time = snapshots[baseSequence].serverTime;
                }

                if (enableNetworkPrediction)
                {
                    uint num_baselines = 1; // 1 because either we have schema baseline or we have a real baseline
                    int baseline1Time = 0;
                    int baseline2Time = 0;

                    byte[] baseline1 = null;
                    byte[] baseline2 = null;
                    if (baseSequence1 != baseSequence)
                    {
                        baseline1 = info.baselines.FindMax(baseSequence1);
                        if (baseline1 != null)
                        {
                            num_baselines = 2;
                            baseline1Time = snapshots[baseSequence1].serverTime;
                        }
                        if (baseSequence2 != baseSequence1)
                        {
                            baseline2 = info.baselines.FindMax(baseSequence2);
                            if (baseline2 != null)
                            {
                                num_baselines = 3;
                                baseline2Time = snapshots[baseSequence2].serverTime;
                            }
                        }
                    }

                    // TODO (petera) are these clears needed?
                    for (int i = 0, c = info.fieldsChangedPrediction.Length; i < c; ++i)
                        info.fieldsChangedPrediction[i] = 0;
                    for (int i = 0; i < NetworkConfig.maxEntitySnapshotDataSize; i++)
                        info.prediction[i] = 0;

                    NetworkPrediction.PredictSnapshot(info.prediction, info.fieldsChangedPrediction, info.type.schema, num_baselines, (uint)baseline0Time, baseline0, (uint)baseline1Time, baseline1, (uint)baseline2Time, baseline2, (uint)snapshotInfo.serverTime, info.fieldMask);
                }
                else
                {
                    var f = info.fieldsChangedPrediction;
                    for (var i = 0; i < f.Length; ++i)
                        f[i] = 0;
                    NetworkUtils.MemCopy(baseline0, 0, info.prediction, 0, info.type.schema.GetByteSize());
                }
            }

            // Read updates
            var updateCount = input.ReadPackedUInt(NetworkConfig.updateCountContext);
            for (var updateIndex = 0; updateIndex < updateCount; ++updateIndex)
            {
                var id = (int)input.ReadPackedIntDelta(previousId, NetworkConfig.idContext);
                previousId = id;

                var info = entities[id];

                uint hash = 0;
                // Copy prediction to temp buffer as we now overwrite info.prediction with fully unpacked
                // state by applying incoming delta to prediction.
                NetworkUtils.MemCopy(info.prediction, 0, tempSnapshotBuffer, 0, info.type.schema.GetByteSize());

                DeltaReader.Read(ref input, info.type.schema, info.prediction, tempSnapshotBuffer, info.fieldsChangedPrediction, info.fieldMask, ref hash);
                if (enableHashing)
                {
                    uint hashCheck = input.ReadRawBits(32);

                    if (hash != hashCheck)
                    {
                        GameDebug.Log("Hash check fail for entity " + id);
                        if (enableNetworkPrediction)
                            GameDebug.Assert(false, "Snapshot (" + snapshotInfo.serverTime + ") " + (baseSequence > 0 ? "Snap [BL]" : "Snap [  ]") + "  " + baseSequence + " - " + baseSequence1 + " - " + baseSequence2 + ". Sche: " + schemaCount + " Spwns: " + spawnCount + " Desp: " + despawnCount + " Upd: " + updateCount);
                        else
                            GameDebug.Assert(false, "Snapshot (" + snapshotInfo.serverTime + ") " + (baseSequence > 0 ? "Snap [BL]" : "Snap [  ]") + "  " + baseSequence + ". Sche: " + schemaCount + " Spwns: " + spawnCount + " Desp: " + despawnCount + " Upd: " + updateCount);
                    }
                }
            }

            uint snapshotHash = 0; // sum of hash for all (updated or not) entity snapshots
            uint numEnts = 0;

            for(int id = 0; id < entities.Count; id++)
            {
                var info = entities[id];
                if (info.type == null)
                    continue;

                // Skip despawned that have not also been spawned in this snapshot
                if (info.despawnSequence > 0 && !spawns.Contains(id))
                    continue;

                // If just spawned or if new snapshot is different from the last we deserialized,
                // we need to deserialize. Otherwise just ignore; no reason to deserialize the same
                // values again
                int schemaSize = info.type.schema.GetByteSize();
                if (info.baselines.GetSize() == 0 || NetworkUtils.MemCmp(info.prediction, 0, info.lastUpdate, 0, schemaSize) != 0)
                {
                    var data = info.baselines.Insert(sequence);
                    NetworkUtils.MemCopy(info.prediction, 0, data, 0, schemaSize);
                    if (sequence > info.lastUpdateSequence)
                    {
                        if (!updates.Contains(id))
                            updates.Add(id);

                        NetworkUtils.MemCopy(info.prediction, 0, info.lastUpdate, 0, schemaSize);
                        info.lastUpdateSequence = sequence;
                    }
                }

                if (enableHashing && info.despawnSequence == 0)
                {
                    snapshotHash += NetworkUtils.SimpleHash(info.prediction, schemaSize);
                    numEnts++;
                }
            }


            if (clientDebug.IntValue > 1)
            {
                if (clientDebug.IntValue > 2 || spawnCount > 0 || despawnCount > 0 || schemaCount > 0 || baseSequence == 0)
                {
                    string entityIds = "";
                    for(var i = 0; i < entities.Count; i++)
                        entityIds+=entities[i].type == null ? ",-" : ("," + i);
                    string despawnIds = string.Join(",", despawns);
                    string spawnIds = string.Join(",", spawns);
                    string updateIds = string.Join(",", updates);

                    if (enableNetworkPrediction)
                        GameDebug.Log((baseSequence > 0 ? "Snap [BL]" : "Snap [  ]") + "  " + baseSequence + " - " + baseSequence1 + " - " + baseSequence2 + ". Sche: " + schemaCount + " Spwns: " + spawnCount + "(" + spawnIds + ") Desp: " + despawnCount + "(" + despawnIds + ") Upd: " + updateCount + "(" + updateIds + ")  Ents:" + entities.Count + " EntityIds:" + entityIds);
                    else
                        GameDebug.Log((baseSequence > 0 ? "Snap [BL]" : "Snap [  ]") + "  " + baseSequence + ". Sche: " + schemaCount + " Spwns: " + spawnCount + "(" + spawnIds + ") Desp: " + despawnCount + "(" + despawnIds + ") Upd: " + updateCount + "(" + updateIds + ")  Ents:" + entities.Count + " EntityIds:" + entityIds);
                }
            }

            if (enableHashing)
            {
                uint numEntsCheck = input.ReadRawBits(32);
                if (numEntsCheck != numEnts)
                {
                    GameDebug.Log("SYNC PROBLEM: server num ents: " + numEntsCheck + " us:" + numEnts);
                    GameDebug.Assert(false);
                }
            }
        }

        void WriteClientConfig<TOutputStream>(ref TOutputStream output) where TOutputStream : NetworkCompression.IOutputStream
        {
            AddMessage(NetworkMessage.ClientConfig);

            output.WriteRawBits((uint)clientConfig.serverUpdateRate, 32);
            output.WriteRawBits((uint)clientConfig.serverUpdateSendRate, 16);
            sendClientConfig = false;

            if (clientDebug.IntValue > 0)
            {
                GameDebug.Log(string.Format("WriteClientConfig: serverUpdateRate {0}    serverUpdateSendRate {1}", clientConfig.serverUpdateRate, clientConfig.serverUpdateSendRate));
            }
        }

        void WriteCommands<TOutputStream>(ClientPackageInfo packageInfo, ref TOutputStream output) where TOutputStream : NetworkCompression.IOutputStream
        {
            AddMessage(NetworkMessage.Commands);
            counters.commandsOut++;

            var includeSchema = commandSequenceAck == 0;
            output.WriteRawBits(includeSchema ? 1U : 0, 1);
            if (includeSchema)
                NetworkSchema.WriteSchema(commandSchema, ref output);

            var sequence = commandSequence;
            output.WriteRawBits(Sequence.ToUInt16(commandSequence), 16);

            packageInfo.commandSequence = commandSequence;
            packageInfo.commandTime = commandsOut[commandSequence].time;

            CommandInfo previous = defaultCommandInfo;
            CommandInfo command;
            while (commandsOut.TryGetValue(sequence, out command))
            {
                // 1 bit to tell there is a command 
                output.WriteRawBits(1, 1);
                output.WritePackedIntDelta(command.time, previous.time, NetworkConfig.commandTimeContext);
                uint hash = 0;
                DeltaWriter.Write(ref output, commandSchema, command.data, previous.data, zeroFieldsChanged, 0, ref hash);

                previous = command;
                --sequence;
            }
            output.WriteRawBits(0, 1);
        }
        byte[] zeroFieldsChanged = new byte[(NetworkConfig.maxFieldsPerSchema + 7) / 8];

        protected override void NotifyDelivered(int sequence, ClientPackageInfo info, bool madeIt)
        {
            base.NotifyDelivered(sequence, info, madeIt);
            if (madeIt)
            {
                if (info.commandSequence > commandSequenceAck)
                {
                    commandSequenceAck = info.commandSequence;
                    lastAcknowlegdedCommandTime = info.commandTime;
                }
            }
            else
            {
                // Resend user config if the package was lost
                if ((info.content & NetworkMessage.ClientConfig) != 0)
                    sendClientConfig = true;
            }
        }

        class MapInfo
        {
            public bool processed;                  // Map reset was processed by game
            public ushort mapSequence;              // map identifier to discard duplicate messages
            public int ackSequence;                 // package sequence the map was acked in (discard packages before this)
            public NetworkSchema schema;            // Schema for the map info
            public byte[] data = new byte[1024];    // Game specific map info payload
        }

        class SnapshotInfo
        {
            public int serverTime;
        }

        class CommandInfo
        {
            public int time = 0;
            public byte[] data = new byte[512];
        }

        class EntityTypeInfo
        {
            public ushort typeId;
            public NetworkSchema schema;
            public byte[] baseline;
        }

        class EntityInfo
        {
            //public int id;
            public EntityTypeInfo type;
            public int lastUpdateSequence;
            public byte[] lastUpdate = new byte[NetworkConfig.maxEntitySnapshotDataSize];
            public int despawnSequence;
            public byte fieldMask;
            public byte[] prediction = new byte[NetworkConfig.maxEntitySnapshotDataSize];
            public byte[] fieldsChangedPrediction = new byte[(NetworkConfig.maxFieldsPerSchema + 7 ) / 8];

            public void Reset()
            {
                type = null;
                lastUpdateSequence = 0;
                despawnSequence = 0;
                fieldMask = 0;
                // TODO(petera) needed?
                for (var i = 0; i < lastUpdate.Length; i++)
                    lastUpdate[i] = 0;

                baselines.Clear();
            }

            public SparseSequenceBuffer baselines = new SparseSequenceBuffer(
                NetworkConfig.snapshotDeltaCacheSize, NetworkConfig.maxEntitySnapshotDataSize);
        }

        bool sendClientConfig = true;

        public ConnectionState connectionState = ConnectionState.Connecting;
        public int clientId = -1;
        public int serverTickRate;
        public int serverTime;
        public int lastAcknowlegdedCommandTime;
        public NetworkCompressionModel compressionModel = null;

        MapInfo mapInfo = new MapInfo();

        int commandSequence;
        int lastSentCommandSeq = 0;
        int commandSequenceAck;
        NetworkSchema commandSchema;
        SequenceBuffer<CommandInfo> commandsOut = new SequenceBuffer<CommandInfo>(3, () => new CommandInfo());

        SequenceBuffer<SnapshotInfo> snapshots = new SequenceBuffer<SnapshotInfo>(NetworkConfig.snapshotDeltaCacheSize, () => new SnapshotInfo());

        Dictionary<ushort, EntityTypeInfo> entityTypes = new Dictionary<ushort, EntityTypeInfo>();
        //Dictionary<int, EntityInfo> entities = new Dictionary<int, EntityInfo>();
        List<EntityInfo> entities = new List<EntityInfo>();

        List<int> despawns = new List<int>();
        List<int> spawns = new List<int>();
        List<int> updates = new List<int>();

        List<int> m_TempSpawnList = new List<int>();

        CommandInfo defaultCommandInfo = new CommandInfo();

    }

    public void ProcessSnapshot(ISnapshotConsumer consumer)
    {
        if (m_Connection == null)
            return;

        m_Connection.ProcessSnapshot(consumer);
    }

    Dictionary<ushort, NetworkEventType> m_EventTypesOut = new Dictionary<ushort, NetworkEventType>();
    INetworkTransport m_Transport;
    ClientConnection m_Connection;
}