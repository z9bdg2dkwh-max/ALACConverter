using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ALACConverter.Services
{
    /// <summary>
    /// 将内嵌于程序集的 ffmpeg（deflate 压缩）释放到磁盘并返回可执行路径。
    ///
    /// 采用压缩内嵌的原因：ffmpeg 静态构建 103 MB 直接内嵌会让单 exe 体积失控；
    /// deflate 压缩后约 38 MB（实测压缩率 63%），运行时一次性解压到 %TEMP%，
    /// 之后每次运行直接复用缓存。用户视角始终是「单个 exe，无外部文件夹」。
    ///
    /// 格式说明：使用 raw deflate（非 zlib/gzip 包装），对应 .NET DeflateStream，
    /// 这是 .NET 原生支持且往返校验通过的格式。
    /// </summary>
    public static class FfmpegLocator
    {
        private const string ResourceName = "ALACConverter.ffmpeg.deflate";
        private const string MarkerName = "ffmpeg.ready";
        private static readonly object Gate = new();
        private static bool _ready;

        public static string ToolDirectory { get; private set; } = string.Empty;

        public static async Task<string> EnsureAsync(IProgress<string>? log = null,
                                                      CancellationToken ct = default)
        {
            if (_ready)
            {
                var cached = Path.Combine(ToolDirectory, "ffmpeg.exe");
                if (File.Exists(cached)) return cached;
            }

            return await Task.Run(() => Ensure(log), ct).ConfigureAwait(false);
        }

        private static string Ensure(IProgress<string>? log)
        {
            lock (Gate)
            {
                var dir = Path.Combine(Path.GetTempPath(), "ALACConverter");
                Directory.CreateDirectory(dir);

                var exePath = Path.Combine(dir, "ffmpeg.exe");
                var marker = Path.Combine(dir, MarkerName);

                // 完整性校验：文件存在 + 体积合理 + 有完成标记，才认为可直接复用
                if (File.Exists(exePath) && File.Exists(marker))
                {
                    try
                    {
                        long len = new FileInfo(exePath).Length;
                        if (len > 50L * 1024 * 1024)
                        {
                            ToolDirectory = dir;
                            _ready = true;
                            log?.Report("复用已释放的内置引擎");
                            return exePath;
                        }
                    }
                    catch
                    {
                        // 校验异常时重新释放
                    }
                }

                log?.Report("首次运行，正在解压内置转码引擎（约需数秒）...");

                var tempExe = exePath + ".tmp";
                using (var res = typeof(FfmpegLocator).GetTypeInfo().Assembly
                                    .GetManifestResourceStream(ResourceName))
                {
                    if (res == null)
                        throw new InvalidOperationException(
                            "未找到内嵌的转码引擎，程序文件不完整。请重新获取本程序。");

                    using var input = res;
                    using var zin = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new FileStream(tempExe, FileMode.Create, FileAccess.Write,
                                                       FileShare.None, 1 << 20);
                    zin.CopyTo(output, 1 << 20);
                }

                File.Move(tempExe, exePath, overwrite: true);

                // 写入标记：仅在完整解压后才创建，作为下次复用的凭据
                File.WriteAllText(marker, DateTime.Now.ToString("O"));

                var size = new FileInfo(exePath).Length;
                ToolDirectory = dir;
                _ready = true;
                log?.Report($"引擎就绪（{size / 1024.0 / 1024.0:0.#} MB）");
                return exePath;
            }
        }

        /// <summary>内嵌引擎资源的存在性与体积自检，用于启动诊断。</summary>
        public static string DescribeResource()
        {
            using var res = typeof(FfmpegLocator).GetTypeInfo().Assembly
                                     .GetManifestResourceStream(ResourceName);
            if (res == null) return "缺失";
            return $"{res.Length / 1024.0 / 1024.0:0.#} MB（压缩态）";
        }
    }
}
