using Microsoft.Xna.Framework;

namespace ClassicUO.Network.PacketHandlers.Helpers;

public static class LocationHelpers
{
    public static Vector3 ReverseLookup(int xLong, int yLat, int xMins, int yMins, bool xEast, bool ySouth)
    {
        int xCenter, yCenter;
        int xWidth, yHeight;

        xCenter = 1323;
        yCenter = 1624;
        xWidth = 5120;
        yHeight = 4096;

        double absLong = xLong + (double)xMins / 60;
        double absLat = yLat + (double)yMins / 60;

        if (!xEast)
            absLong = 360.0 - absLong;

        if (!ySouth)
            absLat = 360.0 - absLat;

        int x, y;

        x = xCenter + (int)(absLong * xWidth / 360);
        y = yCenter + (int)(absLat * yHeight / 360);

        if (x < 0)
            x += xWidth;
        else if (x >= xWidth)
            x -= xWidth;

        if (y < 0)
            y += yHeight;
        else if (y >= yHeight)
            y -= yHeight;

        return new Vector3(x, y, 0);
    }
}
