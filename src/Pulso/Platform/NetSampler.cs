using System.Diagnostics;
using System.Net.NetworkInformation;
using Pulso.Hardware;

namespace Pulso.Platform;

/// <summary>Velocidade de rede: soma dos adaptadores físicos ativos (igual no Windows e no Linux).</summary>
public sealed class NetSampler
{
    long _netRx, _netTx;
    long _netStamp;
    NetworkInterface[]? _nics;
    long _nicRefresh;

    public void Read(NetState n)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (_nics == null || now - _nicRefresh > Stopwatch.Frequency * 30)
            {
                _nics = NetworkInterface.GetAllNetworkInterfaces().Where(IsPhysical).ToArray();
                _nicRefresh = now;
                _netStamp = 0;
            }
            long rx = 0, tx = 0;
            foreach (var nic in _nics)
            {
                try
                {
                    var st = nic.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
                catch { }
            }
            if (_netStamp != 0)
            {
                double dt = (now - _netStamp) / (double)Stopwatch.Frequency;
                if (dt > 0)
                {
                    n.DownBps = Math.Max(0, (rx - _netRx) / dt);
                    n.UpBps = Math.Max(0, (tx - _netTx) / dt);
                }
            }
            _netRx = rx; _netTx = tx; _netStamp = now;
        }
        catch { }
    }

    public static bool IsPhysical(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown) return false;
        string d = (nic.Description + " " + nic.Name).ToLowerInvariant();
        string[] skip = { "virtual", "vmware", "hyper-v", "vethernet", "loopback", "tap-", "tap ", "vpn", "wan miniport", "bluetooth", "pseudo", "teredo", "isatap", "zerotier", "tailscale", "wireguard", "npcap", "filter" };
        return !skip.Any(d.Contains);
    }
}
