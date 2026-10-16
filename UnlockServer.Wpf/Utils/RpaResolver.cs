using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UnlockServer
{
    /// <summary>
    /// 苹果设备（iPhone / Apple Watch / AirPods）用「可解析私有地址（RPA）」广播，地址约每 15 分钟轮换一次。
    /// 持有设备 IRK 的一方能算出 ah(IRK, prand) 并与地址低 24 位比对，从而把任意轮换后的地址认回同一台设备。
    /// 本类负责：保存 IRK、解析 RPA → 返回配置里那台设备的地址。
    /// </summary>
    public static class RpaResolver
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, byte[]> _irks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        // 广播名 -> 配置里的设备地址。苹果设备的混淆广播名对同一台设备稳定（约一天一变），
        // 所以地址轮换后可以靠名字把设备认回来，作为「没导出 IRK」时的兜底手段。
        private static Dictionary<string, string> _nameAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string _aliasFile;

        /// <summary>已学会的名字别名数量</summary>
        public static int AliasCount
        {
            get { lock (Gate) { return _nameAliases.Count; } }
        }

        /// <summary>指定别名持久化文件，并加载已有内容（应用启动时调用一次）。</summary>
        public static void Initialize(string aliasFilePath)
        {
            // 重复调用时直接返回，避免把本次运行期间学会的别名覆盖掉
            if (!string.IsNullOrEmpty(_aliasFile) &&
                string.Equals(_aliasFile, aliasFilePath, StringComparison.OrdinalIgnoreCase))
                return;

            _aliasFile = aliasFilePath;
            if (string.IsNullOrEmpty(_aliasFile) || !File.Exists(_aliasFile)) return;

            try
            {
                var lines = File.ReadAllLines(_aliasFile, Encoding.UTF8);
                var next = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var raw in lines)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var idx = raw.IndexOf('\t');
                    if (idx <= 0 || idx >= raw.Length - 1) continue;
                    var name = raw.Substring(0, idx);
                    var address = BluetoothDiscover.NormalizeAddress(raw.Substring(idx + 1));
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(address))
                        next[name] = address;
                }
                lock (Gate) { _nameAliases = next; }
                LogHelper.WriteLine($"名称别名已加载：{next.Count} 条");
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"名称别名加载失败: {ex.Message}");
            }
        }

        /// <summary>记住「电台广播名 -> 已绑定设备地址」的对应关系（仅当地址确实是配置里的设备时才调用）。</summary>
        public static void NoteName(string name, string address)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var normalized = BluetoothDiscover.NormalizeAddress(address);
            if (string.IsNullOrEmpty(normalized)) return;

            bool changed = false;
            lock (Gate)
            {
                string existing;
                if (!_nameAliases.TryGetValue(name, out existing) ||
                    !string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _nameAliases[name] = normalized;
                    changed = true;
                }
            }

            if (changed)
            {
                LogHelper.WriteLine($"记住广播名别名：{name} -> {normalized}");
                Save();
            }
        }

        /// <summary>按广播名查回配置里的设备地址；没有记录时返回 null。</summary>
        public static string ResolveByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            lock (Gate)
            {
                string address;
                return _nameAliases.TryGetValue(name, out address) ? address : null;
            }
        }

        private static void Save()
        {
            if (string.IsNullOrEmpty(_aliasFile)) return;
            try
            {
                KeyValuePair<string, string>[] snapshot;
                lock (Gate) snapshot = _nameAliases.ToArray();

                var dir = Path.GetDirectoryName(_aliasFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var tmp = _aliasFile + ".tmp";
                File.WriteAllLines(tmp,
                    snapshot.Select(kv => kv.Key + "\t" + kv.Value).ToArray(),
                    new UTF8Encoding(false));
                if (File.Exists(_aliasFile)) File.Delete(_aliasFile);
                File.Move(tmp, _aliasFile);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"名称别名保存失败: {ex.Message}");
            }
        }

        /// <summary>已配置 IRK 的设备数量</summary>
        public static int Count
        {
            get { lock (Gate) { return _irks.Count; } }
        }

        /// <summary>
        /// 设置 IRK 表：键为设备地址（配置里的那台设备），值为 32 位十六进制 IRK。
        /// </summary>
        public static void SetIrks(IEnumerable<KeyValuePair<string, string>> entries)
        {
            var next = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (entries != null)
            {
                foreach (var kv in entries)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                    var address = BluetoothDiscover.NormalizeAddress(kv.Key);
                    byte[] irk;
                    if (string.IsNullOrEmpty(address) || !TryParseIrk(kv.Value, out irk)) continue;
                    next[address] = irk;
                }
            }

            lock (Gate) { _irks = next; }
            LogHelper.WriteLine($"RPA 解析表已更新：{next.Count} 台设备配置了 IRK");
        }

        public static void Clear()
        {
            lock (Gate) { _irks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); }
        }

        /// <summary>把十六进制文本解析成 16 字节 IRK。允许带空格、冒号、短横线。</summary>
        public static bool TryParseIrk(string text, out byte[] irk)
        {
            irk = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var clean = new string(text.Where(Uri.IsHexDigit).ToArray());
            if (clean.Length != 32) return false;

            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                if (!byte.TryParse(clean.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                    return false;
            }
            irk = bytes;
            return true;
        }

        /// <summary>
        /// 若该地址是某台已配置设备的轮换地址，则返回配置里的设备地址；否则返回 null。
        /// 先按 IRK 解析（准确），IRK 未配置时退回按广播名匹配（兜底）。
        /// </summary>
        public static string ResolveIdentity(string rawAddress, string name = null)
        {
            var address = BluetoothDiscover.NormalizeAddress(rawAddress);
            if (string.IsNullOrEmpty(address)) return null;

            var byIrk = ResolveByIrk(address);
            if (!string.IsNullOrEmpty(byIrk)) return byIrk;

            return ResolveByName(name);
        }

        private static string ResolveByIrk(string address)
        {
            byte[][] snapshots;
            string[] keys;
            lock (Gate)
            {
                if (_irks.Count == 0) return null;
                keys = _irks.Keys.ToArray();
                snapshots = keys.Select(k => _irks[k]).ToArray();
            }

            byte[] raw;
            if (!TryParseAddress(address, out raw)) return null;

            // 只处理可解析私有地址（最高两位 01）
            if ((raw[0] >> 6) != 0x01) return null;

            var prand = new byte[] { (byte)(raw[0] & 0x3F), raw[1], raw[2] };
            var hash = new byte[] { raw[3], raw[4], raw[5] };

            for (int i = 0; i < snapshots.Length; i++)
            {
                var block = Ah(snapshots[i], prand);
                // 常规实现取密文最低 24 位（最后 3 字节）；部分实现取最高 24 位，两种都认，避免字节序误解导致失效。
                bool okLow = block[13] == hash[0] && block[14] == hash[1] && block[15] == hash[2];
                bool okHigh = block[0] == hash[0] && block[1] == hash[1] && block[2] == hash[2];
                if (okLow || okHigh)
                {
                    LogHelper.WriteLine($"RPA 命中：{address} → {keys[i]}（{(okLow ? "低24位" : "高24位")}）");
                    return keys[i];
                }
            }
            return null;
        }

        /// <summary>ah(k, r) = e(k, r') mod 2^24，r' = 104 位填充 || 24 位 r（AES-128-ECB，无填充）。</summary>
        public static byte[] Ah(byte[] irk, byte[] prand)
        {
            if (irk == null || irk.Length != 16) throw new ArgumentException("IRK 必须是 16 字节", "irk");
            if (prand == null || prand.Length != 3) throw new ArgumentException("prand 必须是 3 字节", "prand");

            var block = new byte[16];
            block[13] = prand[0];
            block[14] = prand[1];
            block[15] = prand[2];

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = irk;
                using (var enc = aes.CreateEncryptor())
                    return enc.TransformFinalBlock(block, 0, 16);
            }
        }

        private static bool TryParseAddress(string normalized, out byte[] bytes)
        {
            bytes = null;
            var clean = new string(normalized.Where(Uri.IsHexDigit).ToArray());
            if (clean.Length != 12) return false;

            bytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                if (!byte.TryParse(clean.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                    return false;
            }
            return true;
        }

        /// <summary>地址类型（供界面/日志判断是不是 RPA）</summary>
        public static string DescribeAddress(string rawAddress)
        {
            var address = BluetoothDiscover.NormalizeAddress(rawAddress);
            byte[] raw;
            if (string.IsNullOrEmpty(address) || !TryParseAddress(address, out raw)) return "未知";
            switch (raw[0] >> 6)
            {
                case 0: return "不可解析随机地址";
                case 1: return "可解析私有地址(RPA)";
                case 3: return "静态随机地址";
                default: return "公有地址";
            }
        }
    }
}
