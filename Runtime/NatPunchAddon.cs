using System;
using System.Collections.Generic;
using System.Net;
using LiteNetLib;
using UnityEngine;

namespace Mirror
{
    public interface INatPunchAddon
    {
        bool ClientNeedsNatPunch { get; set; }
        Action<IPEndPoint> OnInitiatingNatPunch { get; set; }
        void InitiateNatPunch(IPEndPoint relay, string token);
    }
}
