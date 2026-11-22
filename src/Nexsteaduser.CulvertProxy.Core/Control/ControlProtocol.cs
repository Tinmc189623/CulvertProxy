using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Nexsteaduser.CulvertProxy.Core.Control;

/// <summary>本机 MCP 桥接与界面进程之间的命名管道约定。</summary>
public static class ControlProtocol
{
    public const int Version = 1;

    /// <summary>按当前用户隔离管道，避免多用户会话互相控制。</summary>
    public static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
            return "Nexsteaduser.CulvertProxy.Control." + Convert.ToHexStringLower(hash)[..16];
        }
    }
}
