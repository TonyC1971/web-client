using System;
using System.IO;
using Server.Network;

namespace Server.ClassicUO;

public static unsafe partial class ClassicUONetwork
{
    private static readonly Logging.ILogger logger = Logging.LogFactory.GetLogger(typeof(ClassicUONetwork));
    public const string NSToStringFieldPropertyName = "_toString";

    private static readonly delegate*<NetState, CircularBufferReader, int, void> OnReceive = &_OnReceive;
    private static void _OnReceive(NetState state, CircularBufferReader reader, int packetLength)
    {
        reader.Seek(0, SeekOrigin.Begin);
        var id = reader.ReadByte();
        if(Handlers[id]?.Invoke(state, reader, packetLength) is null or false)
        {
            reader.Seek(1, SeekOrigin.Begin);
            OriginalHandlers[id]?.OnReceive(state, reader, packetLength);
        }
    }

    public static string GetOrUpdateConfig(string key, string defaultValue) =>
        ServerConfiguration.GetOrUpdateSetting(key, defaultValue);

    public static bool GetOrUpdateConfig(string key, bool defaultValue) =>
        ServerConfiguration.GetOrUpdateSetting(key, defaultValue);

    public static void Log(string str, params object[] args) => logger.Debug(str, args);
}
