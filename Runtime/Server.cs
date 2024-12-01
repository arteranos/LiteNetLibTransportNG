using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using UnityEngine;

namespace Mirror.LNLTransport
{
    public delegate void OnConnected(int clientId);
    public delegate void OnServerData(int clientId, ArraySegment<byte> data, DeliveryMethod deliveryMethod);
    public delegate void OnDisconnected(int clientId);

    public class Server : INatPunchTarget
    {
        private const string Scheme = "litenet";
        private const int ConnectionCapacity = 1000;

        // configuration
        internal ushort port;
        internal int updateTime;
        internal int disconnectTimeout;
        internal string acceptConnectKey;
        internal bool natPunchEnabled;

        // LiteNetLib state
        NetManager server;
        Dictionary<int, NetPeer> connections = new(ConnectionCapacity);
        NatPunchAddon natListener = new();

        public Action<INatPunchTarget, IPEndPoint> OnNeedingNatPunch 
        { 
            // The server is just waiting for connections.
            get => throw new NotSupportedException(); 
            set => throw new NotSupportedException(); 
        }

        public int LocalPort => server.LocalPort;

        public event OnConnected onConnected;
        public event OnServerData onData;
        public event OnDisconnected onDisconnected;

        /// <summary>
        /// Mirror connection Ids are 1 indexed but LiteNetLib is 0 indexed so we have to add 1 to the peer Id
        /// </summary>
        /// <param name="peerId">0 indexed id used by LiteNetLib</param>
        /// <returns>1 indexed id used by mirror</returns>
        private static int ToMirrorId(int peerId)
        {
            return peerId + 1;
        }

        /// <summary>
        /// Mirror connection Ids are 1 indexed but LiteNetLib is 0 indexed so we have to add 1 to the peer Id
        /// </summary>
        /// <param name="mirrorId">1 indexed id used by mirror</param>
        /// <returns>0 indexed id used by LiteNetLib</returns>
        private static int ToPeerId(int mirrorId)
        {
            return mirrorId - 1;
        }

        public void Start()
        {
            // not if already started
            if (server != null)
            {
                Debug.LogWarning("LiteNetLib: server already started.");
                return;
            }

            Debug.Log("LiteNet SV: starting...");

            // create server
            EventBasedNetListener listener = new();

            server = new NetManager(listener)
            {
                UpdateTime = updateTime,
                DisconnectTimeout = disconnectTimeout,
                NatPunchEnabled = natPunchEnabled
            };

            // set up events
            listener.ConnectionRequestEvent += Listener_ConnectionRequestEvent;
            listener.PeerConnectedEvent += Listener_PeerConnectedEvent;
            listener.NetworkReceiveEvent += Listener_NetworkReceiveEvent;
            listener.PeerDisconnectedEvent += Listener_PeerDisconnectedEvent;
            listener.NetworkErrorEvent += Listener_NetworkErrorEvent;

            natListener.relay = server;

            server.NatPunchModule.Init(natListener);

            // start listening
            server.Start(port);
        }

        private void Listener_ConnectionRequestEvent(ConnectionRequest request)
        {
            Debug.Log("LiteNet SV connection request");
            request.AcceptIfKey(acceptConnectKey);
        }

        private void Listener_PeerConnectedEvent(NetPeer peer)
        {
            int id = ToMirrorId(peer.Id);
            Debug.Log($"LiteNet SV client connected: {peer} id={id}");
            connections[id] = peer;
            onConnected?.Invoke(id);
        }

        private void Listener_NetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            int id = ToMirrorId(peer.Id);

            // Debug.Log($"LiteNet SV received {reader.AvailableBytes} bytes. method={deliveryMethod}");
            onData?.Invoke(id, reader.GetRemainingBytesSegment(), deliveryMethod);
            reader.Recycle();
        }

        private void Listener_PeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            int id = ToMirrorId(peer.Id);
            // this is called both when a client disconnects, and when we
            // disconnect a client.
            Debug.Log($"LiteNet SV client disconnected: {peer} info={disconnectInfo.Reason}");
            onDisconnected?.Invoke(id);
            connections.Remove(id);
        }

        private void Listener_NetworkErrorEvent(IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            Debug.LogWarning($"LiteNet SV network error: {endPoint} error={socketError}");
            // TODO should we disconnect or is it called automatically?
        }

        public void Stop()
        {
            server?.Stop();
            server = null;
            natListener.relay = null;
            natListener = null;
        }


        public bool Send(List<int> connectionIds, DeliveryMethod deliveryMethod, ArraySegment<byte> segment)
        {
            if (server == null)
            {
                Debug.LogWarning("LiteNet SV: can't send because not started yet.");
                return false;
            }

            foreach (int connectionId in connectionIds)
            {
                SendOne(connectionId, deliveryMethod, segment);
            }
            return true;
        }

        public void SendOne(int connectionId, DeliveryMethod deliveryMethod, ArraySegment<byte> segment)
        {
            if (server == null)
            {
                Debug.LogWarning("LiteNet SV: can't send because not started yet.");
                return;
            }

            if (connections.TryGetValue(connectionId, out NetPeer peer))
            {
                try
                {
                    peer.Send(segment.Array, segment.Offset, segment.Count, deliveryMethod);
                }
                catch (TooBigPacketException exception)
                {
                    { Debug.LogWarning($"LiteNet SV: send failed for connectionId={connectionId} reason={exception}"); }
                }
            }
            else
            {
                Debug.LogWarning($"LiteNet SV: invalid connectionId={connectionId}");
            }
        }

        /// <summary>
        /// Kicks player
        /// </summary>
        /// <param name="connectionId"></param>
        /// <returns></returns>
        public bool Disconnect(int connectionId)
        {
            if (server != null)
            {
                if (connections.TryGetValue(connectionId, out NetPeer peer))
                {
                    // disconnect the client.
                    // PeerDisconnectedEvent will call OnDisconnect.
                    peer.Disconnect();
                    return true;
                }
                Debug.LogWarning($"LiteNet SV: invalid connectionId={connectionId}");
                return false;
            }
            return false;
        }

        public Uri GetUri()
        {
            UriBuilder builder = new UriBuilder();
            builder.Scheme = Scheme;
            builder.Host = Dns.GetHostName();
            builder.Port = port;
            return builder.Uri;
        }

        public string GetClientAddress(int connectionId)
        {
            if (server != null)
            {
                if (connections.TryGetValue(connectionId, out NetPeer peer))
                {
                    return peer.ToString();
                }
            }
            return string.Empty;
        }

        public IPEndPoint GetClientIPEndPoint(int connectionId)
        {
            if (server != null)
            {
                if (connections.TryGetValue(connectionId, out NetPeer peer))
                {
                    return peer;
                }
            }
            return null;
        }

        public void LNL_Update()
        {
            natListener?.Poll();

            server?.NatPunchModule.PollEvents();

            server?.PollEvents();
        }

        public void InitiateNatPunch(IPEndPoint relay, string token) 
            => server?.NatPunchModule.SendNatIntroduceRequest(relay, token);

        public void Knock(IPEndPoint clientExternal)
        {
            // Not sure why, but this NAT punch needs to go all the way.
            server?.NatPunchModule.SendNatPunchPacket(clientExternal, false);
        }
    }
}
