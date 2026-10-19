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
        public bool IsMonitor;
    }

    /// <summary>设备报告的一个输入口，编号用于 changesource2</summary>
    internal sealed class DeviceSource
    {
        public int Id;
        public string Name = "";
    }

    /// <summary>
    /// 小米电视 / 智能显示器在 6095 端口跑着 MiTV Assistant 服务。
    /// 切源优先用 changesource2（按编号），它能切到 DP / USB-C；
    /// 设备不支持时退回按名字的 changesource。
    /// </summary>
    internal static class TvClient
    {
        public const int Port = 6095;

        /// <summary>显示器专属输入的编号，沿用 MStar 电视输入表</summary>
        public const int IdDisplayPort = 29;
        public const int IdUsbc = 30;

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

        // ── 底层请求 ────────────────────────────────────────────

        public static async Task<TvResult> CallPathAsync(string host, string path, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(host))
                return new TvResult { Ok = false, Error = "尚未设置设备地址" };

            string url = $"http://{host}:{Port}{path}";
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

        public static Task<TvResult> CallAsync(string host, string action, int timeoutMs = 5000) =>
            CallPathAsync(host, "/controller?action=" + action, timeoutMs);

        // ── 信号源 ──────────────────────────────────────────────

        public static async Task<List<DeviceSource>> GetSourcesAsync(string host)
        {
            var list = new List<DeviceSource>();
            TvResult r = await CallAsync(host, "getsources").ConfigureAwait(false);
            if (!r.Ok) return list;

            try
            {
                using var doc = JsonDocument.Parse(r.Body);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
                if (!data.TryGetProperty("sourceList", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                    return list;

                foreach (var item in arr.EnumerateArray())
                {
                    if (!item.TryGetProperty("sourceId", out var idEl)) continue;
                    int id = idEl.GetInt32();
                    string name = item.TryGetProperty("sourceName", out var nEl)
                        ? (nEl.GetString() ?? "")
                        : "";
                    list.Add(new DeviceSource { Id = id, Name = CorrectName(id, name) });
                }
            }
            catch { }
            return list;
        }

        /// <summary>老固件会把 29 报成 DVI（MStar 电视输入表的遗留），按编号纠正</summary>
        private static string CorrectName(int id, string raw) => id switch
        {
            IdDisplayPort => "DP",
            IdUsbc => "USBC",
            _ => raw,
        };

        private static string Normalize(string s) =>
            new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>把源名解析成设备上的编号；设备没有这个输入时返回 null</summary>
        public static int? ResolveSourceId(IReadOnlyList<DeviceSource> sources, string key)
        {
            if (sources.Count == 0) return null;
            string want = Normalize(key);

            if (want is "USBC" or "TYPEC")
            {
                var usbc = sources.FirstOrDefault(s => s.Id == IdUsbc);
                if (usbc is not null) return IdUsbc;
            }
            if (want is "DP" or "DP1")
            {
                var dp = sources.FirstOrDefault(s => s.Id == IdDisplayPort);
                if (dp is not null) return IdDisplayPort;
            }

            var exact = sources.FirstOrDefault(s => Normalize(s.Name) == want);
            if (exact is not null) return exact.Id;

            var loose = sources.FirstOrDefault(s => Normalize(s.Name).StartsWith(want, StringComparison.Ordinal));
            return loose?.Id;
        }

        /// <summary>有 DP / USB-C 输入的几乎只可能是显示器，电视没有这两个口</summary>
        public static bool LooksLikeMonitor(string name, IReadOnlyList<DeviceSource> sources)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("显示器") || n.Contains("monitor")) return true;
            return sources.Any(s => s.Id == IdDisplayPort || s.Id == IdUsbc);
        }

        public static async Task<TvResult> SwitchAsync(string host, string source, IReadOnlyList<DeviceSource>? sources = null)
        {
            sources ??= await GetSourcesAsync(host).ConfigureAwait(false);
            int? id = ResolveSourceId(sources, source);
            if (id is int sid)
            {
                TvResult r = await CallAsync(host, $"changesource2&sourceid={sid}").ConfigureAwait(false);
                if (r.Ok) return r;
            }
            return await CallAsync(host, "changesource&source=" + Uri.EscapeDataString(source))
                .ConfigureAwait(false);
        }

        public static Task<TvResult> InfoAsync(string host) => CallAsync(host, "getsysteminfo");

        public static Task<TvResult> AliveAsync(string host) =>
            CallPathAsync(host, "/request?action=isalive");

        // ── 音量 / 遥控按键 ─────────────────────────────────────

        public static Task<TvResult> PressAsync(string host, string key) =>
            CallAsync(host, "keyevent&keycode=" + Uri.EscapeDataString(key));

        public static async Task<(int value, int max)?> GetVolumeAsync(string host)
        {
            TvResult r = await CallAsync(host, "getvolume").ConfigureAwait(false);
            if (!r.Ok) return null;
            try
            {
                using var doc = JsonDocument.Parse(r.Body);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
                int v = data.TryGetProperty("volume", out var vEl) ? vEl.GetInt32() : -1;
                int m = data.TryGetProperty("maxVolume", out var mEl) ? mEl.GetInt32() : 100;
                if (v < 0) return null;
                return (v, Math.Max(m, 1));
            }
            catch { return null; }
        }

        /// <summary>音量接口只支持 ±1，按次数连发</summary>
        public static async Task StepVolumeAsync(string host, bool up, int steps = 2)
        {
            for (int i = 0; i < steps; i++)
            {
                await PressAsync(host, up ? "volumeup" : "volumedown").ConfigureAwait(false);
                await Task.Delay(60).ConfigureAwait(false);
            }
        }

        // ── 双模显示器的竞技模式 ────────────────────────────────
        //
        // 4K160 ↔ 1080p320 要换 EDID，没有对应接口，只能走按键宏：
        // menu 打开游戏栏 → 右移 5 次到模式项 → enter → right 开 / left 关。
        // 这套序列来自 MonitorBar（MIT，© 2026 binaryify），见 NOTICE。

        public static async Task SetCompetitiveAsync(string host, bool on)
        {
            await PressAsync(host, "menu").ConfigureAwait(false);
            await Task.Delay(700).ConfigureAwait(false);
            for (int i = 0; i < 5; i++)
            {
                await PressAsync(host, "right").ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);
            }
            await PressAsync(host, "enter").ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            await PressAsync(host, on ? "right" : "left").ConfigureAwait(false);
            // 关掉游戏栏。此时显示器正在换 EDID，可能不应答，不影响结果。
            await PressAsync(host, "back").ConfigureAwait(false);
            await PressAsync(host, "back").ConfigureAwait(false);
        }

        // ── 局域网发现 ──────────────────────────────────────────

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
                var srcs = await GetSourcesAsync(ip).ConfigureAwait(false);
                string name = ExtractName(info.Body) ?? "未知设备";
                result.Add(new Device
                {
                    Ip = ip,
                    Name = name,
                    IsMonitor = LooksLikeMonitor(name, srcs),
                });
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
