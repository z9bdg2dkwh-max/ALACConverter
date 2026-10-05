using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ALACConverter.Models;

namespace ALACConverter.Services
{
    public sealed class TranscodeResult
    {
        public bool Success { get; init; }
        public string OutputPath { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public string Warning { get; init; } = string.Empty;
        public long OutputBytes { get; init; }
    }

    /// <summary>
    /// ffmpeg 调用与进度解析。全部参数以数组传入 ArgumentList，
    /// 不经过 cmd.exe，从根本上杜绝原 bat 脚本的路径转义与注入问题。
    /// </summary>
    public sealed class TranscodeEngine
    {
        private readonly string _ffmpegPath;

        public TranscodeEngine(string ffmpegPath) => _ffmpegPath = ffmpegPath;

        private static readonly Regex DurationRx = new(
            @"Duration:\s*(\d+):(\d{2}):(\d{2}\.\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StreamRx = new(
            @"Stream #\d+:\d+.*?:\s*Audio:\s*([a-z0-9_]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SampleRateRx = new(
            @"(\d+)\s*Hz",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ChannelsRx = new(
            @"(mono|stereo|\d+\.\d+ channels|\d+ channels)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BitsRx = new(
            @"\b(s16p|s32p|s24p|s16|s32|s24|u8|fltp)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // -progress pipe:1 的稳定字段：out_time_ms=微秒，progress=continue|end
        private static readonly Regex OutTimeRx = new(
            @"^out_time_ms=(\d+)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>探测输入文件的音频规格，用于决定重采样与位深策略。</summary>
        public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
        {
            var args = new List<string> { "-hide_banner", "-i", path, "-f", "null", "-" };

            var (exit, output) = await RunCaptureAsync(args, ct).ConfigureAwait(false);

            // ffmpeg 在"无输出文件"时返回非 0 属正常，只要 stderr 有解析到信息即可
            var info = new MediaInfo { RawProbe = output };

            var dm = DurationRx.Match(output);
            if (dm.Success)
            {
                info.DurationSeconds = int.Parse(dm.Groups[1].Value) * 3600d
                                     + int.Parse(dm.Groups[2].Value) * 60d
                                     + double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture);
            }

            var sm = StreamRx.Match(output);
            if (sm.Success) info.Codec = sm.Groups[1].Value.ToLowerInvariant();

            var rm = SampleRateRx.Match(output);
            if (rm.Success && int.TryParse(rm.Groups[1].Value, out var rate))
                info.SampleRate = rate;

            var cm = ChannelsRx.Match(output);
            if (cm.Success)
            {
                var t = cm.Groups[1].Value;
                if (t == "mono") info.Channels = 1;
                else if (t == "stereo") info.Channels = 2;
                else if (t.Contains('.')) info.Channels = int.Parse(t.Split('.')[0]);
                else if (int.TryParse(t.Split(' ')[0], out var c)) info.Channels = c;
            }

            var bm = BitsRx.Match(output);
            if (bm.Success)
            {
                var b = bm.Groups[1].Value;
                info.SourceBitDepth = b.Contains("24") ? 24 : b.Contains("32") ? 32 : b.Contains("16") ? 16 : 16;
            }

            info.HasAudio = info.Codec is not null && info.Codec != "unknown";
            return info;
        }

        /// <summary>
        /// 按 Apple Music 规格编排 ffmpeg 参数。
        /// 策略：采样率 ≤48kHz 直接无损编码；>48kHz 降采样到 48kHz（Apple Music 拒收 96/192kHz）。
        /// </summary>
        public IReadOnlyList<string> BuildArguments(
            string input, string output, MediaInfo info, int targetSampleRate, int targetChannels)
        {
            var a = new List<string>
            {
                "-hide_banner",
                "-nostdin",
                "-y",
                "-i", input,
                "-map", "0:a:0",
                "-vn",
                "-sn",
                "-dn",
                // 必须显式指定容器格式：输出是临时文件 xxx.m4a.converting，
                // 扩展名已不是 .m4a，ffmpeg 无法自动推断，会报
                // "Unable to find a suitable output format"。mp4 容器即 .m4a。
                "-f", "mp4"
            };

            // 视频流不保留：m4a 仅承载音频，直通视频轨会导致 Apple Music 拒收
            a.Add("-c:a");
            a.Add("alac");

            // ALAC 原生支持的采样平面格式：s16p = 16bit，s32p = 24bit
            a.Add("-sample_fmt");
            a.Add(targetSampleRate > 0 && info.SourceBitDepth >= 24 ? "s32p" : "s16p");

            if (targetSampleRate > 0)
            {
                a.Add("-ar");
                a.Add(targetSampleRate.ToString(CultureInfo.InvariantCulture));
            }

            if (targetChannels > 0)
            {
                a.Add("-ac");
                a.Add(targetChannels.ToString(CultureInfo.InvariantCulture));
            }

            // 元数据映射：保留原始标签，便于 iTunes/音乐 App 识别专辑与艺人
            a.Add("-map_metadata");
            a.Add("0");

            // 快速起播：moov 前置，便于流式读取
            a.Add("-movflags");
            a.Add("+faststart");

            a.Add("-progress");
            a.Add("pipe:1");
            a.Add("-nostats");

            a.Add(output);
            return a;
        }

        /// <summary>执行转码，进度以 0-1 回报。</summary>
        public async Task<TranscodeResult> RunAsync(
            IReadOnlyList<string> args,
            MediaInfo info,
            IProgress<double>? progress,
            Action<string>? onLog,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var s in args) psi.ArgumentList.Add(s);

            onLog?.Invoke(string.Join(' ', args));

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stderrTail = new List<string>();
            var stdOut = new System.Text.StringBuilder();

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdOut.Append(e.Data).Append('\n'); };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderrTail)
                {
                    stderrTail.Add(e.Data);
                    if (stderrTail.Count > 60) stderrTail.RemoveAt(0);
                }
            };

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                return new TranscodeResult { Success = false, Error = "无法启动内置引擎：" + ex.Message };
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var durationTicks = (long)(info.DurationSeconds * 1_000_000d);
            var lastReported = -1d;

            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    try { proc.Kill(true); } catch { /* 进程可能已退出 */ }
                    return new TranscodeResult { Success = false, Error = "已取消" };
                }

                // 进度来自 stdout 的 -progress 输出（毫秒级），stderr 被日志占用
                var m = OutTimeRx.Match(stdOut.ToString());
                if (m.Success && durationTicks > 0)
                {
                    long us = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double pct = us / (double)durationTicks;
                    if (pct < 0) pct = 0;
                    if (pct > 1) pct = 1;
                    // 节流：变化不足 0.5% 不重复通知，避免 UI 高频刷新
                    if (pct - lastReported >= 0.005)
                    {
                        lastReported = pct;
                        progress?.Report(pct);
                    }
                }
                await Task.Delay(120, ct).ConfigureAwait(false);
            }

            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            string err;
            lock (stderrTail) err = string.Join("\n", stderrTail);

            if (ct.IsCancellationRequested)
                return new TranscodeResult { Success = false, Error = "已取消" };

            if (proc.ExitCode != 0)
            {
                onLog?.Invoke(err);
                return new TranscodeResult
                {
                    Success = false,
                    Error = SummarizeError(err)
                };
            }

            progress?.Report(1.0);
            return new TranscodeResult { Success = true };
        }

        /// <summary>
        /// 校验产出文件能否被正常解码。这是原 bat 脚本缺失的关键环节：
        /// 没有它，坏文件会静默产出，用户导入 iPhone 后才发现不可播放。
        /// </summary>
        public async Task<(bool Ok, string Detail)> VerifyAsync(string path, CancellationToken ct)
        {
            var args = new List<string>
            {
                "-hide_banner", "-v", "error",
                "-i", path,
                "-map", "0:a:0",
                "-f", "null", "-"
            };
            var (_, output) = await RunCaptureAsync(args, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(output))
                return (true, "校验通过");

            return (false, "产出文件无法正常解码：" + FirstMeaningfulLine(output));
        }

        private static string FirstMeaningfulLine(string s)
        {
            foreach (var line in s.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) return t;
            }
            return "未知错误";
        }

        private static string SummarizeError(string stderr)
        {
            var lines = stderr.Split('\n');
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.Length == 0) continue;
                if (t.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase))
                    return "输入文件不是有效的音频/视频，或已损坏";
                if (t.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                    return "找不到输入文件";
                if (t.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase))
                    return "文件中没有音频流，无法转换";
                if (t.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                    return "没有写入权限，请更换输出目录";
            }
            return FirstMeaningfulLine(stderr);
        }

        private async Task<(int Exit, string Output)> RunCaptureAsync(
            IReadOnlyList<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var s in args) psi.ArgumentList.Add(s);

            using var p = new Process { StartInfo = psi };
            var sb = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode, sb.ToString());
        }
    }

    public sealed class MediaInfo
    {
        public string Codec { get; set; } = string.Empty;
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int SourceBitDepth { get; set; } = 16;
        public double DurationSeconds { get; set; }
        public bool HasAudio { get; set; }
        public string RawProbe { get; set; } = string.Empty;

        public string SpecText =>
            $"{(HasAudio ? Codec.ToUpperInvariant() : "无音频")} · " +
            $"{(SampleRate > 0 ? SampleRate.ToString() : "?")} Hz · " +
            $"{ChannelText} · {SourceBitDepth} bit";

        public string ChannelText => Channels switch
        {
            1 => "单声道",
            2 => "立体声",
            6 => "5.1 环绕",
            0 => "?",
            _ => $"{Channels} 声道"
        };
    }
}
