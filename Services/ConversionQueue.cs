using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALACConverter.Models;

namespace ALACConverter.Services
{
    public sealed class ConvertSettings
    {
        /// <summary>输出目录。空字符串表示与源文件同目录。</summary>
        public string? OutputDirectory { get; set; }

        /// <summary>同名文件的处理策略</summary>
        public bool OverwriteExisting { get; set; }

        /// <summary>转换完成后删除不完整产出（治本：杜绝"导入 iPhone 后才发现不可播放"）</summary>
        public bool VerifyOutput { get; set; } = true;

        /// <summary>输出完成后自动打开所在文件夹</summary>
        public bool OpenFolderWhenDone { get; set; }

        /// <summary>
        /// 同时转换的文件数。ALAC 编码本身是单线程的，
        /// 单文件串行执行时多核 CPU 大部分处于空闲状态，靠多文件并行才能吃满。
        /// 1 = 旧行为（严格串行）。
        /// </summary>
        public int MaxParallelism { get; set; } = 1;
    }

    public sealed class BatchSummary
    {
        public int Total { get; init; }
        public int Succeeded { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public int Cancelled { get; init; }
        public long BytesWritten { get; init; }
        public double ElapsedSeconds { get; init; }
    }

    /// <summary>
    /// 转换队列编排：串行执行、逐条汇报进度、失败自动清理残file。
    /// </summary>
    public sealed class ConversionQueue
    {
        private readonly TranscodeEngine _engine;
        private readonly ConvertSettings _settings;
        private readonly Action<string> _log;
        private CancellationTokenSource? _cts;

        /// <summary>
        /// 按输出路径的闸门。并行时两个同名源文件会算出同一个 outputPath，
        /// 若同时落盘会互相覆盖（串行时只是"后者覆盖前者"，并行会变成写坏文件）。
        /// 这里让同一目标路径的收尾段串行执行。
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _pathGates = new();

        private SemaphoreSlim GetPathGate(string outputPath)
            => _pathGates.GetOrAdd(outputPath, _ => new SemaphoreSlim(1, 1));

        public ConversionQueue(TranscodeEngine engine, ConvertSettings settings, Action<string> log)
        {
            _engine = engine;
            _settings = settings;
            _log = log;
        }

        public bool IsRunning { get; private set; }

        public void Cancel() => _cts?.Cancel();

        public async Task<BatchSummary> RunAsync(
            IReadOnlyList<AudioJob> jobs,
            IProgress<int> completedCount,
            CancellationToken externalCt)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _cts = cts;
            var ct = cts.Token;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int ok = 0, fail = 0, skip = 0, cancelled = 0;
            long bytes = 0;
            int index = 0;

            IsRunning = true;
            try
            {
                int parallelism = _settings.MaxParallelism < 1 ? 1 : _settings.MaxParallelism;
                var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism };

                // 不把 ct 放进 ParallelOptions：那样取消时整个 ForEachAsync 会抛
                // OperationCanceledException，已排队的任务算不进统计，汇总数字会缺一截。
                // 改为在循环体里自行判断，未开始的任务照样计入 cancelled。
                await Parallel.ForEachAsync(jobs, options, async (job, _) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        if (!job.IsTerminal)
                        {
                            job.Status = JobStatus.Cancelled;
                            job.Message = "队列已取消";
                        }
                        Interlocked.Increment(ref cancelled);
                        completedCount.Report(Interlocked.Increment(ref index));
                        return;
                    }

                    try
                    {
                        await ProcessOneAsync(job, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        job.Status = JobStatus.Cancelled;
                        job.Message = "已取消";
                    }
                    catch (Exception ex)
                    {
                        // 并行之后，单个任务的意外异常不能把整批拖下水
                        job.Status = JobStatus.Failed;
                        job.Message = "未预期的错误：" + ex.Message;
                    }

                    switch (job.Status)
                    {
                        case JobStatus.Done: Interlocked.Increment(ref ok); break;
                        case JobStatus.Failed: Interlocked.Increment(ref fail); break;
                        case JobStatus.Skipped: Interlocked.Increment(ref skip); break;
                        case JobStatus.Cancelled: Interlocked.Increment(ref cancelled); break;
                    }

                    if (job.OutputBytes > 0) Interlocked.Add(ref bytes, job.OutputBytes);
                    completedCount.Report(Interlocked.Increment(ref index));
                }).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                IsRunning = false;
                _pathGates.Clear();   // 批次结束，闸门不再需要
                cts.Dispose();
                _cts = null;
            }

            return new BatchSummary
            {
                Total = jobs.Count,
                Succeeded = ok,
                Failed = fail,
                Skipped = skip,
                Cancelled = cancelled,
                BytesWritten = bytes,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        private async Task ProcessOneAsync(AudioJob job, CancellationToken ct)
        {
            job.Status = JobStatus.Running;
            job.Progress = 0;
            job.Message = "读取中…";

            string outputPath;
            MediaInfo info;

            try
            {
                info = await _engine.ProbeAsync(job.SourcePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.Status = JobStatus.Cancelled;
                job.Message = "已取消";
                return;
            }
            catch (Exception ex)
            {
                job.Status = JobStatus.Failed;
                job.Message = "无法读取文件：" + ex.Message;
                return;
            }

            if (!info.HasAudio)
            {
                job.Status = JobStatus.Failed;
                job.Message = "文件中没有找到音频轨";
                return;
            }

            job.InputSpec = info.SpecText;

            // 已是符合 Apple Music 规格的 ALAC，则跳过，避免无意义的二次转码
            if (info.Codec == "alac"
                && AppleMusicSpec.IsAcceptedSampleRate(info.SampleRate)
                && info.Channels <= 2
                && AppleMusicSpec.IsAcceptedBitDepth(Math.Min(info.SourceBitDepth, 24)))
            {
                job.Status = JobStatus.Skipped;
                job.Message = "已是符合 Apple Music 规格的无损文件，无需转换";
                job.Progress = 1;
                return;
            }

            outputPath = BuildOutputPath(job.SourcePath);

            if (File.Exists(outputPath) && !_settings.OverwriteExisting)
            {
                job.Status = JobStatus.Skipped;
                job.Message = "目标文件已存在（未勾选覆盖）";
                job.OutputPath = outputPath;
                return;
            }

            // 按 Apple Music 规格决定目标采样率：
            // 96/192kHz 等会被 Apple Music 拒收，必须降到 48kHz；
            // 44.1kHz 与 48kHz 原样保留，真正做到无损。
            int targetRate = AppleMusicSpec.RequiresDownsample(info.SampleRate)
                ? AppleMusicSpec.MaxAcceptedSampleRate
                : info.SampleRate;

            int targetChannels = info.Channels > 2 ? 2 : 0; // 5.1 环绕降为立体声

            var warn = new List<string>();
            if (targetRate != info.SampleRate)
                warn.Add($"采样率 {info.SampleRate} Hz 不被 Apple Music 接受，已降至 {targetRate} Hz");
            if (targetChannels == 2)
                warn.Add($"{info.ChannelText} 已转为立体声");
            if (info.SourceBitDepth >= 24)
                warn.Add("保留 24 bit");

            job.Message = warn.Count > 0 ? string.Join("；", warn) : "准备转换…";

            // 先写临时文件，成功后再改名，杜绝半成品被误认为成品。
            // 临时名带随机后缀：并行时同名源文件不会共用一个 .converting 互相踩踏。
            var tempPath = outputPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".converting";
            SafeDelete(tempPath);

            var args = _engine.BuildArguments(job.SourcePath, tempPath, info, targetRate, targetChannels);

            // 不用 Progress<double>：它会捕获创建处的 SynchronizationContext，
            // 而此处已在后台线程。AudioJob.Notify 内部已用 DispatcherQueue 切回 UI 线程，
            // 因此这里的回调只做赋值即可。
            var progress = new InlineProgress(p => job.Progress = p);

            // 日志带文件名前缀：并行时多条 ffmpeg 输出会交错，
            // 没有前缀就分不清哪一行属于哪个文件。
            var tag = Path.GetFileName(job.SourcePath);
            var res = await _engine.RunAsync(args, info, progress,
                msg => _log($"[{tag}] {msg}"), ct).ConfigureAwait(false);

            if (!res.Success)
            {
                SafeDelete(tempPath);
                job.Status = res.Error == "已取消" ? JobStatus.Cancelled : JobStatus.Failed;
                job.Message = res.Error;
                job.Progress = 0;
                return;
            }

            if (_settings.VerifyOutput)
            {
                job.Message = "校验产出…";
                var (ok2, detail) = await _engine.VerifyAsync(tempPath, ct).ConfigureAwait(false);
                if (!ok2)
                {
                    SafeDelete(tempPath);
                    job.Status = JobStatus.Failed;
                    job.Message = detail;
                    job.Progress = 0;
                    return;
                }
            }

            // 落盘段按目标路径加闸，并在写入前重新确认占位：
            // 并行时可能已有另一个任务抢先产出了同名文件，此时应跳过而不是覆盖。
            var gate = GetPathGate(outputPath);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(outputPath) && !_settings.OverwriteExisting)
                {
                    SafeDelete(tempPath);
                    job.Status = JobStatus.Skipped;
                    job.Message = "目标文件已存在（未勾选覆盖）";
                    job.OutputPath = outputPath;
                    return;
                }

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tempPath, outputPath);
            }
            catch (Exception ex)
            {
                SafeDelete(tempPath);
                job.Status = JobStatus.Failed;
                job.Message = "写入目标文件失败：" + ex.Message;
                return;
            }
            finally
            {
                gate.Release();
            }

            job.OutputPath = outputPath;
            try { job.OutputBytes = new FileInfo(outputPath).Length; } catch { }
            job.Status = JobStatus.Done;
            job.Progress = 1;
            job.Message = warn.Count > 0 ? string.Join("；", warn) : "转换完成，已通过校验";
        }

        private string BuildOutputPath(string source)
        {
            var dir = string.IsNullOrWhiteSpace(_settings.OutputDirectory)
                ? Path.GetDirectoryName(source) ?? string.Empty
                : _settings.OutputDirectory!;

            Directory.CreateDirectory(dir);
            var stem = Path.GetFileNameWithoutExtension(source);
            return Path.Combine(dir, stem + ".m4a");
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 占用中则留给系统清理 */ }
        }
    }
}

/// <summary>
/// 同步执行的进度报告器。
/// 与 <see cref="Progress{T}"/> 的区别：Progress&lt;T&gt; 会捕获构造处的
/// SynchronizationContext，在后台线程构造时会把回调投递到错误的上下文；
/// 本类直接在调用线程同步回调，由 AudioJob 内部负责切回 UI 线程。
/// </summary>
internal sealed class InlineProgress : IProgress<double>
{
    private readonly Action<double> _handler;
    public InlineProgress(Action<double> handler) => _handler = handler;
    public void Report(double value) => _handler(value);
}
