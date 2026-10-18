// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MiTVSwitch
{
    internal sealed class TvResult
    {
        public bool Ok;
        public int Status;
        public string Body = "";
        public string Error = "";

        public override string ToString() =>
            Ok ? $"HTTP {Status}" : (Error.Length > 0 ? Error : $"HTTP {Status}");
    }

    internal sealed class Device
    {
        public string Ip = "";
        public string Name = "";
    }

    /// <summary>
    /// 小米电视 / 智能显示器在 6095 端口跑着 MiTV Assistant 服务，
    /// changesource 可以直接切换信号源。
    /// </summary>
    internal static class TvClient
    {
        public const int Port = 6095;

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // 关键：关掉代理。
            // Windows 的 HttpClient 默认会走 IE / 系统代理设置，而系统代理的
            // 例外列表对局域网网段的匹配经常不生效，导致发往 192.168.x.x 的
            // 请求被塞进代理然后失败。直连最稳。
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        }

        public static async Task<TvResult> CallAsync(string host, string action, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(host))
                return new TvResult { Ok = false, Error = "尚未设置设备地址" };

            string url = $"http://{host}:{Port}/controller?action={action}";
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var resp = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return new TvResult
                {
                    Ok = resp.IsSuccessStatusCode && body.Contains("\"success\""),
                    Status = (int)resp.StatusCode,
                    Body = body,
                };
            }
            catch (OperationCanceledException)
            {
                return new TvResult { Ok = false, Error = "请求超时" };
            }
            catch (Exception ex)
            {
                return new TvResult { Ok = false, Error = ex.Message };
            }
        }

        public static Task<TvResult> SwitchAsync(string host, string source) =>
            CallAsync(host, "changesource&source=" + Uri.EscapeDataString(source));

        public static Task<TvResult> InfoAsync(string host) =>
            CallAsync(host, "getsysteminfo");

        /// <summary>扫描本机所在 /24 网段，找开着 6095 且能报告设备名的设备</summary>
        public static async Task<List<Device>> DiscoverAsync(IProgress<int>? progress = null)
        {
            var result = new List<Device>();
            IPAddress? local = LocalIPv4();
            if (local is null) return result;

            var parts = local.ToString().Split('.');
            if (parts.Length != 4) return result;
            string prefix = $"{parts[0]}.{parts[1]}.{parts[2]}.";

            int done = 0;
            var tasks = Enumerable.Range(1, 254).Select(async i =>
            {
                string ip = prefix + i;
                bool alive = await ProbeAsync(ip).ConfigureAwait(false);
                int d = Interlocked.Increment(ref done);
                if (d % 16 == 0) progress?.Report(d);
                return alive ? ip : null;
            });

            var hits = (await Task.WhenAll(tasks).ConfigureAwait(false))
                       .Where(x => x is not null).Select(x => x!).ToList();

            foreach (string ip in hits)
            {
                var info = await InfoAsync(ip).ConfigureAwait(false);
                result.Add(new Device { Ip = ip, Name = ExtractName(info.Body) ?? "未知设备" });
            }
            return result;
        }

        private static string? ExtractName(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("devicename", out var n))
                {
                    return n.GetString();
                }
            }
            catch { }
            return null;
        }

        private static async Task<bool> ProbeAsync(string ip)
        {
            try
            {
                using var client = new TcpClient();
                Task connect = client.ConnectAsync(ip, Port);
                Task delay = Task.Delay(300);
                Task first = await Task.WhenAny(connect, delay).ConfigureAwait(false);
                if (first != connect) return false;
                await connect.ConfigureAwait(false);   // 把异常抛出来
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static IPAddress? LocalIPv4()
        {
            // 先让系统按路由表选一个出口地址，不实际发包
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("223.5.5.5", 65530);
                if (s.LocalEndPoint is IPEndPoint ep &&
                    !ep.Address.ToString().StartsWith("127."))
                {
                    return ep.Address;
                }
            }
            catch { }

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.Address.ToString().StartsWith("169.254.")) continue;
                    return ua.Address;
                }
            }
            return null;
        }
    }
}
