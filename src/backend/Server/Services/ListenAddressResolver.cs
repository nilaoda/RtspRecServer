using System.Net;
using System.Net.Sockets;

namespace RtspRecServer.Server.Services;

/// <summary>解析后的监听计划：Kestrel URL、生效地址与合并提示。</summary>
internal sealed record ListenPlan(string[] Urls, string[] Hosts, string[] Hints);

/// <summary>
/// 监听地址解析：支持 IPv4/IPv6 字面量。
/// IPv6 通配（::）在双栈系统（Linux/Windows/macOS 默认）上同时接受 IPv4 连接，
/// 因此与 IPv4 地址同时配置时会合并为单一 :: 绑定，避免对同一端口重复绑定导致启动失败。
/// </summary>
internal static class ListenAddressResolver
{
    private const string Ipv4Any = "0.0.0.0";
    private const string Ipv6Any = "::";

    /// <summary>
    /// 校验并规范化监听地址列表：去除首尾空白与空条目、按去括号后的形式去重；
    /// 含冒号的条目按 IPv6 字面量校验并规范化（如 [::1] → ::1）。
    /// </summary>
    public static string[] NormalizeHosts(IEnumerable<string?> hosts)
    {
        var result = new List<string>();
        foreach (var raw in hosts)
        {
            var host = (raw ?? string.Empty).Trim();
            if (host.Length == 0)
            {
                continue;
            }

            if (host.StartsWith('[') && host.EndsWith(']') && host.Length >= 3)
            {
                host = host[1..^1];
            }

            if (host.Contains(':'))
            {
                if (!IPAddress.TryParse(host, out var address) ||
                    address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    throw new InvalidOperationException($"监听地址 {host} 不是有效的 IPv6 地址");
                }

                host = address.ToString();
            }

            if (!result.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(host);
            }
        }

        return [.. result];
    }

    /// <summary>按端口构建 Kestrel 监听 URL，并做通配地址合并。</summary>
    public static ListenPlan Resolve(int port, IEnumerable<string?> hosts)
    {
        var normalized = NormalizeHosts(hosts);
        var hasIpv6Any = normalized.Contains(Ipv6Any, StringComparer.Ordinal);
        var hasIpv4Any = normalized.Contains(Ipv4Any, StringComparer.Ordinal);

        var urls = new List<string>();
        var effective = new List<string>();
        var hints = new List<string>();

        foreach (var host in normalized)
        {
            var isIpv6 = host.Contains(':');

            // :: 双栈监听已同时覆盖所有 IPv4 目标，IPv4 绑定是冗余的
            if (hasIpv6Any && !isIpv6)
            {
                hints.Add($"{host} 已由 :: 双栈监听覆盖（同时接受 IPv4 连接）");
                continue;
            }

            // 0.0.0.0 通配已覆盖其他具体 IPv4 地址
            if (!isIpv6 && hasIpv4Any && host != Ipv4Any)
            {
                hints.Add($"{host} 已由 0.0.0.0 监听覆盖");
                continue;
            }

            effective.Add(host);
            urls.Add(isIpv6 ? $"http://[{host}]:{port}" : $"http://{host}:{port}");
        }

        return new ListenPlan([.. urls], [.. effective], [.. hints]);
    }
}
