using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;

namespace ClassicUO.Network.PacketHandlers.Helpers;

internal static class SharedStore
{
    private static readonly List<uint> _cliLocRequests = [];
    private static readonly List<uint> _customHouseRequests = [];

    public static uint RequestedGridLoot { get; set; }

    public static void AddMegaCliLocRequest(uint serial)
    {
        if (_cliLocRequests.Any(s => s == serial))
            return;

        _cliLocRequests.Add(serial);
    }

    public static void AddCustomHouseRequest(uint serial)
    {
        if (_customHouseRequests.Any(s => s == serial))
            return;

        _customHouseRequests.Add(serial);
    }

    public static void SendMegaCliLocRequests(World world)
    {
        if (world.ClientFeatures.TooltipsEnabled && _cliLocRequests.Count != 0)
        {
            if (Client.Game.UO.Version >= Utility.ClientVersion.CV_5090)
            {
                if (_cliLocRequests.Count != 0)
                    AsyncNetClient.Socket.Send_MegaClilocRequest(_cliLocRequests);
            }
            else
            {
                foreach (uint serial in _cliLocRequests)
                    AsyncNetClient.Socket.Send_MegaClilocRequest_Old(serial);

                _cliLocRequests.Clear();
            }
        }

        if (_customHouseRequests.Count > 0)
        {
            for (int i = 0; i < _customHouseRequests.Count; ++i)
                AsyncNetClient.Socket.Send_CustomHouseDataRequest(_customHouseRequests[i]);

            _customHouseRequests.Clear();
        }
    }
}
