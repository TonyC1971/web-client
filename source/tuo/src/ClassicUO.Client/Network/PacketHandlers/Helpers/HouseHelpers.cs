using System;
using System.Buffers;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;
using ClassicUO.Utility;

namespace ClassicUO.Network.PacketHandlers.Helpers;

internal static class HouseHelpers
{
    public static unsafe void ReadUnsafeCustomHouseData(
        ReadOnlySpan<byte> source,
        int sourcePosition,
        int dlen,
        int clen,
        int planeZ,
        int planeMode,
        short minX,
        short minY,
        short maxY,
        Item item,
        House house
    )
    {
        byte[] buffer = null;

        try
        {
            bool ismovable = item.ItemData.IsMultiMovable;

            Span<byte> span = dlen <= 1024
                ? stackalloc byte[dlen]
                : buffer = ArrayPool<byte>.Shared.Rent(dlen);

            ZLib.ZLibError result = ZLib.Decompress(source.Slice(sourcePosition, clen), span.Slice(0, dlen));
            var reader = new StackDataReader(span.Slice(0, dlen));

            ushort id = 0;
            sbyte x = 0,
                y = 0,
                z = 0;

            switch (planeMode)
            {
                case 0:
                    int c = dlen / 5;

                    for (uint i = 0; i < c; i++)
                    {
                        id = reader.ReadUInt16BE();
                        x = reader.ReadInt8();
                        y = reader.ReadInt8();
                        z = reader.ReadInt8();

                        if (id != 0)
                            house.Add(
                                id,
                                0,
                                (ushort)(item.X + x),
                                (ushort)(item.Y + y),
                                (sbyte)(item.Z + z),
                                true,
                                ismovable
                            );
                    }

                    break;

                case 1:

                    if (planeZ > 0)
                        z = (sbyte)((planeZ - 1) % 4 * 20 + 7);
                    else
                        z = 0;

                    c = dlen >> 2;

                    for (uint i = 0; i < c; i++)
                    {
                        id = reader.ReadUInt16BE();
                        x = reader.ReadInt8();
                        y = reader.ReadInt8();

                        if (id != 0)
                            house.Add(
                                id,
                                0,
                                (ushort)(item.X + x),
                                (ushort)(item.Y + y),
                                (sbyte)(item.Z + z),
                                true,
                                ismovable
                            );
                    }

                    break;

                case 2:
                    short offX = 0,
                        offY = 0;
                    short multiHeight = 0;

                    if (planeZ > 0)
                        z = (sbyte)((planeZ - 1) % 4 * 20 + 7);
                    else
                        z = 0;

                    if (planeZ <= 0)
                    {
                        offX = minX;
                        offY = minY;
                        multiHeight = (short)(maxY - minY + 2);
                    }
                    else if (planeZ <= 4)
                    {
                        offX = (short)(minX + 1);
                        offY = (short)(minY + 1);
                        multiHeight = (short)(maxY - minY);
                    }
                    else
                    {
                        offX = minX;
                        offY = minY;
                        multiHeight = (short)(maxY - minY + 1);
                    }

                    c = dlen >> 1;

                    for (uint i = 0; i < c; i++)
                    {
                        id = reader.ReadUInt16BE();
                        x = (sbyte)(i / multiHeight + offX);
                        y = (sbyte)(i % multiHeight + offY);

                        if (id != 0)
                            house.Add(
                                id,
                                0,
                                (ushort)(item.X + x),
                                (ushort)(item.Y + y),
                                (sbyte)(item.Z + z),
                                true,
                                ismovable
                            );
                    }

                    break;
            }

            reader.Release();
        }
        finally
        {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
