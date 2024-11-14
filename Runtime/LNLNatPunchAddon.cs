using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using UnityEngine;

namespace Mirror.LNLTransport
{
    /*
     * Given that A and B as prospective communication partners and R as the accessible relay:
     * R got this module implemented
     * 1. A generates token
     * 2. A calls client.NatPunchModule.SendNatIntroduceRequest(R, token)
     * 3. A messages B (R, token) via third-channel means.
     * 4. B calls server.NatPunchModule.SendNatIntroduceRequest(R, token)
     * 5. R finds the two matching requests (via token) and performs NatIntroduce()
     * 6. On Nat Introduction Success...
     *   6a. A calls client.Connect(B)
     *   6b. B calls client.Connect(A) (maybe, optional?)
     */

    public interface INatPunchTarget
    {
        /// <summary>
        /// Configured client wants to use a relay of your choosing, for targeting IPEndPoint
        /// as the respective server.
        /// </summary>
        Action<INatPunchTarget, IPEndPoint> OnNeedingNatPunch { get; set; }

        /// <summary>
        /// Initiate on the client's or server's NAT punch module, contacting the relay and
        /// matching tokens
        /// </summary>
        /// <param name="relay">The relay to contact to as its guide</param>
        /// <param name="token">the token to match to each other</param>
        void InitiateNatPunch(IPEndPoint relay, string token);
    }

    class WaitPeer
    {
        public IPEndPoint InternalAddr { get; }
        public IPEndPoint ExternalAddr { get; }
        public DateTime RefreshTime { get; private set; }

        public void Refresh()
        {
            RefreshTime = DateTime.UtcNow;
        }

        public WaitPeer(IPEndPoint internalAddr, IPEndPoint externalAddr)
        {
            Refresh();
            InternalAddr = internalAddr;
            ExternalAddr = externalAddr;
        }
    }

    public class NatPunchAddon : INatPunchListener
    {
        private readonly Dictionary<string, WaitPeer> waitingPeers = new();
        private static readonly TimeSpan KickTime = TimeSpan.FromSeconds(10);
        public NetManager relay { get; set; }

        public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
        {
            Debug.Log($"Got Nat Introduction request: {token}...");

            if (waitingPeers.TryGetValue(token, out WaitPeer wpeer))
            {
                if (wpeer.InternalAddr.Equals(localEndPoint) &&
                    wpeer.ExternalAddr.Equals(remoteEndPoint))
                {
                    Debug.Log("Already the pending peer, just refreshing the entry.");
                    wpeer.Refresh();
                    return;
                }

                //found in list - introduce client and host to eachother
                Debug.Log(string.Format(
                    "Matching peer found, host - i({0}) e({1}), client - i({2}) e({3})",
                    wpeer.InternalAddr,
                    wpeer.ExternalAddr,
                    localEndPoint,
                    remoteEndPoint));

                relay?.NatPunchModule.NatIntroduce(
                    wpeer.InternalAddr, // host internal
                    wpeer.ExternalAddr, // host external
                    localEndPoint, // client internal
                    remoteEndPoint, // client external
                    token // request token
                );

                //Clear dictionary
                waitingPeers.Remove(token);
            }
            else
            {
                Debug.Log(string.Format("Waiting peer created. i({0}) e({1})", localEndPoint, remoteEndPoint));
                waitingPeers[token] = new(localEndPoint, remoteEndPoint);
            }
        }

        public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
        {
            Debug.Log($"Nat punch completed, targetEndPoint={targetEndPoint}, type={type}, token={token}");
        }

        public void Poll()
        {
            List<string> peersToRemove = new();
            //check old peers

            DateTime nowTime = DateTime.UtcNow;

            foreach (var waitPeer in waitingPeers)
            {
                if (nowTime - waitPeer.Value.RefreshTime > KickTime)
                    peersToRemove.Add(waitPeer.Key);
            }

            for(int i = 0; i < peersToRemove.Count; ++i)
                waitingPeers.Remove(peersToRemove[i]);
        }
    }
}