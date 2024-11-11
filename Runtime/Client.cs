using System;
using System.Net;
using LiteNetLib;
using UnityEngine;

namespace Mirror.LNLTransport
{
    public delegate void OnClientData(ArraySegment<byte> data, DeliveryMethod deliveryMethod);

    public class Client
    {

        // configuration
        internal ushort port;
        internal int updateTime;
        internal int disconnectTimeout;
        internal bool natPunchEnabled;

        // LiteNetLib state
        NetManager client;

        public event Action onConnected;
        public event OnClientData onData;
        public event Action onDisconnected;

        public IPEndPoint RemoteEndPoint => client.FirstPeer;

        public bool Connected { get; private set; }

        public void Connect(string address, int maxConnectAttempts, bool ipv6Enabled, string connectKey)
        {
            // not if already connected or connecting
            if (client != null)
            {
                Debug.LogWarning("LiteNet: client already connected/connecting.");
                return;
            }

            Debug.Log("LiteNet CL: connecting...");

            // create client
            EventBasedNetListener listener = new EventBasedNetListener();
            client = new NetManager(listener)
            {
                UpdateTime = updateTime,
                DisconnectTimeout = disconnectTimeout,
                MaxConnectAttempts = maxConnectAttempts,
                NatPunchEnabled = natPunchEnabled
            };

            // DualMode seems to break some addresses, so make this an option so that it can be turned on when needed
            if (ipv6Enabled)
            {
                client.IPv6Enabled = true;
            }

            // set up events
            listener.PeerConnectedEvent += Listener_PeerConnectedEvent;
            listener.NetworkReceiveEvent += Listener_NetworkReceiveEvent;
            listener.PeerDisconnectedEvent += Listener_PeerDisconnectedEvent;
            listener.NetworkErrorEvent += Listener_NetworkErrorEvent;

            // start & connect
            client.Start();
            client.Connect(address, port, connectKey);
        }

        private void Listener_PeerConnectedEvent(NetPeer peer)
        {
            Debug.Log($"LiteNet CL client connected: {peer}");
            Connected = true;
            onConnected?.Invoke();
        }

        private void Listener_NetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            // Debug.Log($"LiteNet CL received {reader.AvailableBytes} bytes. method={deliveryMethod}");
            onData?.Invoke(reader.GetRemainingBytesSegment(), deliveryMethod);
            reader.Recycle();
        }

        private void Listener_PeerDisconnectedEvent(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            // this is called when the server stopped.
            // this is not called when the client disconnected.
            Debug.Log($"LiteNet CL disconnected. info={disconnectInfo}");
            Connected = false;
            Disconnect();
        }

        private void Listener_NetworkErrorEvent(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            Debug.LogWarning($"LiteNet CL network error: {endPoint} error={socketError}");
            // TODO should we disconnect or is it called automatically?
        }


        public void Disconnect()
        {
            if (client != null)
            {
                // clean up
                client.Stop();
                client = null;
                Connected = false;

                // PeerDisconnectedEvent is not called when voluntarily
                // disconnecting. need to call OnDisconnected manually.
                onDisconnected?.Invoke();
            }
        }

        public bool Send(DeliveryMethod deliveryMethod, ArraySegment<byte> segment)
        {
            if (client != null && client.FirstPeer != null)
            {
                try
                {
                    client.FirstPeer.Send(segment.Array, segment.Offset, segment.Count, deliveryMethod);
                    return true;
                }
                catch (TooBigPacketException exception)
                {
                    Debug.LogWarning($"LiteNet CL: send failed. reason={exception}");
                    return false;
                }
            }
            return false;
        }

        public void LNL_Update() => client?.PollEvents();
    }
}
