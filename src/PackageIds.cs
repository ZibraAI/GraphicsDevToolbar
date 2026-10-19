using System;

namespace GraphicsDevToolbar
{
    internal static class PackageGuids
    {
        public const string PackageString = "785328a1-2fa8-46dd-b309-720494ce6328";
        public const string CmdSetString = "f030a9f8-1b33-45cd-b318-59e8c42fd2fd";

        public static readonly Guid CmdSet = new Guid(CmdSetString);
    }

    internal static class PackageIds
    {
        public const int Arguments = 0x0100;
        public const int D3DOverride = 0x0101;
        public const int DebugLayer = 0x0102;
        public const int GBV = 0x0103;
        public const int CMake = 0x0104;
        public const int RenderDoc = 0x0105;
        public const int Pix = 0x0106;
        public const int VulkanConfigurator = 0x0107;
    }
}
