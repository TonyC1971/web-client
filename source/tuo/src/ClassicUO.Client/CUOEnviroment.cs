// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ClassicUO
{
    internal static class CUOEnviroment
    {
        public static Thread GameThread;
        public static float DPIScaleFactor = 1.0f;
        public static bool NoSound;
        public static string[] Args;
        public static string[] Plugins;
        public static bool Debug;
        public static bool IsHighDPI;
        public static uint CurrentRefreshRate;
        public static bool SkipLoginScreen;
        public static bool SkipServerSelect;
        public static bool NoServerPing;
        public static Assembly Assembly => Assembly.GetEntryAssembly();

        public static readonly bool IsUnix = Environment.OSVersion.Platform != PlatformID.Win32NT && Environment.OSVersion.Platform != PlatformID.Win32Windows && Environment.OSVersion.Platform != PlatformID.Win32S && Environment.OSVersion.Platform != PlatformID.WinCE;

        public static readonly string Version = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0";

        // v0.7.8: web-client version baked at build time via the
        // `WebClientVersion` MSBuild property → AssemblyMetadataAttribute.
        // Surfaces in the TUO LoginGump label "TazUO Web Foss vX.Y.Z".
        // Falls back to AssemblyVersion if the metadata attribute is
        // missing (defensive — should never happen because the csproj
        // always emits the attribute). Mirrors source/cuo/src/ClassicUO.Client/CUOEnviroment.cs.
        public static readonly string WebClientVersion =
            Assembly.GetExecutingAssembly()?
                .GetCustomAttributes(typeof(AssemblyMetadataAttribute), inherit: false)
                .OfType<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "WebClientVersion")?.Value
            ?? Version;

        public static readonly string ExecutablePath =
#if NETFRAMEWORK
           AppContext.BaseDirectory; // Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
#else
            Environment.CurrentDirectory;
#endif
    }
}