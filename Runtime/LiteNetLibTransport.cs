using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using UnityEngine;

using Mirror.LNLTransport;

namespace Mirror
{
    [DisallowMultipleComponent]
    public class LiteNetLibTransport : Transport
    {
        public const string Scheme = "litenet";

        [Header("Config")]
        public ushort port = 8888;
        public int updateTime = 15;
        public int disconnectTimeout = 5000;
        public bool ipv6Enabled;

        [Tooltip("Enable NAT hole punching")]
        public bool natPunchEnabled;

        [Tooltip("Maximum connection attempts before client stops and call disconnect event.")]
        public int maxConnectAttempts = 10;

        [Tooltip("Caps the number of messages the server will process per tick. Allows LateUpdate to finish to let the reset of unity contiue incase more messages arrive before they are processed")]
        public int serverMaxMessagesPerTick = 10000;

        [Tooltip("Caps the number of messages the client will process per tick. Allows LateUpdate to finish to let the reset of unity contiue incase more messages arrive before they are processed")]
        public int clientMaxMessagesPerTick = 1000;

        [Tooltip("Uses index in list to map to DeliveryMethod. eg channel 0 => DeliveryMethod.ReliableOrdered")]
        public List<DeliveryMethod> channels = new List<DeliveryMethod>()
        {
            DeliveryMethod.ReliableOrdered,
            DeliveryMethod.Unreliable
        };

        [Tooltip("Key that client most give server in order to connect, this is handled automatically by the transport.")]
        public string connectKey = "MIRROR_LITENETLIB";

        Client client;
        Server server;

        private void OnValidate()
        {
            Debug.Assert(channels.Distinct().Count() == channels.Count, "LiteNetLibTransport: channels should only use each DeliveryMethod");
            Debug.Assert(channels.Count > 0, "LiteNetLibTransport: There should be atleast 1 channel");
        }

        void Awake()
        {
            Debug.Log("LiteNetLibTransport initialized!");
        }

        public override bool Available()
        {
            // all except WebGL
            return Application.platform != RuntimePlatform.WebGLPlayer;
        }

        #region CLIENT

        /// <summary>
        /// Client message recieved while Transport was disabled
        /// </summary>
        readonly ConcurrentQueue<ClientDataMessage> clientDisabledQueue = new();

        private void CreateClient(ushort port)
        {
            client = new Client()
            {
                port = port,
                updateTime = updateTime,
                disconnectTimeout = disconnectTimeout,
                natPunchEnabled = natPunchEnabled
            };
                              
            client.onConnected += OnClientConnected.Invoke;
            client.onData += Client_onData;
            client.onDisconnected += OnClientDisconnected.Invoke;
        }

        private void Client_onData(ArraySegment<byte> data, DeliveryMethod deliveryMethod)
        {
            int channel = channels.IndexOf(deliveryMethod);

            if (enabled)
                OnClientDataReceived.Invoke(data, channel);
            else
                clientDisabledQueue.Enqueue(new ClientDataMessage(data, channel));
        }

        public override bool ClientConnected() => client != null && client.Connected;

        public override void ClientConnect(string address)
        {
            if (client != null)
            {
                Debug.LogWarning("Can't start client as one was already connected");
                return;
            }

            CreateClient(port);
            client.Connect(address, maxConnectAttempts, ipv6Enabled, connectKey);
        }

        public override void ClientConnect(Uri uri)
        {
            if (uri.Scheme != Scheme)
                throw new ArgumentException($"Invalid uri {uri}, use {Scheme}://host:port instead", nameof(uri));

            int serverPort = uri.IsDefaultPort ? port : uri.Port;
            CreateClient((ushort) serverPort);
            client.Connect(uri.Host, maxConnectAttempts, ipv6Enabled, connectKey);
        }

        public override void ClientSend(ArraySegment<byte> segment, int channelId)
        {
            if (client == null || !client.Connected)
            {
                Debug.LogWarning("Can't send when client is not connected");
                return;
            }

            DeliveryMethod deliveryMethod = channels[channelId];
            client.Send(deliveryMethod, segment);
        }

        public override void ClientDisconnect()
        {
            if (client != null)
            {
                // remove events before calling disconnect so stop loops within mirror
                client.onConnected -= OnClientConnected.Invoke;
                client.onData -= Client_onData;
                client.onDisconnected -= OnClientDisconnected.Invoke;

                client.Disconnect();
                client = null;
            }
        }

        public override void ClientEarlyUpdate()
        {
            if (!enabled) return;

            ProcessClientQueue();

            client?.LNL_Update();
        }

        private void ProcessClientQueue()
        {
            for (int i = 0; i < clientMaxMessagesPerTick; ++i)
            {
                if (!enabled) return;

                if (clientDisabledQueue.TryPeek(out ClientDataMessage data))
                {
                    OnClientDataReceived.Invoke(data.data, data.channel);
                    clientDisabledQueue.TryDequeue(out _);
                }
            }
        }

        #endregion

        #region SERVER

        /// <summary>
        /// Server message recieved while Transport was disabled
        /// </summary>
        readonly ConcurrentQueue<ServerDataMessage> serverDisabledQueue = new();

        public override bool ServerActive() => server != null;

        public override void ServerStart()
        {
            if (server != null)
            {
                Debug.LogWarning("Can't start server as one was already active");
                return;
            }

            server = new Server()
            {
                port = port,
                updateTime = updateTime,
                disconnectTimeout = disconnectTimeout,
                acceptConnectKey = connectKey,
                natPunchEnabled = natPunchEnabled
            };

            server.onConnected += OnServerConnected.Invoke;
            server.onData += Server_onData;
            server.onDisconnected += OnServerDisconnected.Invoke;

            server.Start();
        }

        private void Server_onData(int clientId, ArraySegment<byte> data, DeliveryMethod deliveryMethod)
        {
            int channel = channels.IndexOf(deliveryMethod);

            if (enabled)
                OnServerDataReceived.Invoke(clientId, data, channel);
            else
                serverDisabledQueue.Enqueue(new ServerDataMessage(clientId, data, channel));
        }

        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId)
        {
            if (server == null)
            {
                Debug.LogWarning("Can't send when Server is not active");
                return;
            }

            DeliveryMethod deliveryMethod = channels[channelId];
            server.SendOne(connectionId, deliveryMethod, segment);
        }

        public override void ServerDisconnect(int connectionId)
        {
            if (server == null)
            {
                Debug.LogWarning("Can't disconnect when Server is not active");
                return;
            }

            server.Disconnect(connectionId);
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            return server?.GetClientAddress(connectionId);
        }

        public override void ServerStop()
        {
            if (server != null)
            {
                server.onConnected -= OnServerConnected.Invoke;
                server.onData -= Server_onData;
                server.onDisconnected -= OnServerDisconnected.Invoke;

                server.Stop();
                server = null;
            }
            else
            {
                Debug.LogWarning("Can't stop server as no server was active");
            }
        }

        public override void ServerEarlyUpdate()
        {
            if(!enabled) return;

            ProcessServerQueue();

            server?.LNL_Update();
        }

        public override Uri ServerUri()
        {
            return server?.GetUri();
        }

        private void ProcessServerQueue()
        {
            for (int i = 0; i < serverMaxMessagesPerTick; ++i)
            {
                if (!enabled) return;

                if (serverDisabledQueue.TryPeek(out ServerDataMessage data))
                {
                    OnServerDataReceived.Invoke(data.clientId, data.data, data.channel);
                    serverDisabledQueue.TryDequeue(out _);
                }
            }
        }

        #endregion

        public override void Shutdown()
        {
            Debug.Log("LiteNetLibTransport Shutdown");
            client?.Disconnect();
            server?.Stop();
        }

        public override int GetMaxPacketSize(int channelId = Channels.Reliable)
        {
            // LiteNetLib NetPeer construct calls SetMTU(0), which sets it to
            // NetConstants.PossibleMtu[0] which is 576-68.
            // (bigger values will cause TooBigPacketException even on loopback)
            //
            // see also: https://github.com/RevenantX/LiteNetLib/issues/388
            return 576; // NetConstants.PossibleMtu[0]; // Sealed away. Bleh. :(
        }

        public override string ToString()
        {
            if (server != null)
            {
                // printing server.listener.LocalEndpoint causes an Exception
                // in UWP + Unity 2019:
                //   Exception thrown at 0x00007FF9755DA388 in UWF.exe:
                //   Microsoft C++ exception: Il2CppExceptionWrapper at memory
                //   location 0x000000E15A0FCDD0. SocketException: An address
                //   incompatible with the requested protocol was used at
                //   System.Net.Sockets.Socket.get_LocalEndPoint ()
                // so let's use the regular port instead.
                return "LiteNetLib Server port: " + port;
            }
            else if (client != null)
            {
                if (client.Connected)
                {
                    return "LiteNetLib Client ip: " + client.RemoteEndPoint;
                }
                else
                {
                    return "LiteNetLib Connecting...";
                }
            }
            return "LiteNetLib (inactive/disconnected)";
        }
    }
}
