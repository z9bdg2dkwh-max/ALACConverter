using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace ALACConverter.Models
{
    public enum JobStatus
    {
        Waiting,
        Running,
        Done,
        Failed,
        Skipped,
        Cancelled
    }

    /// <summary>
    /// 单个音频文件的转码任务状态。实现 INotifyPropertyChanged 以驱动队列实时刷新。
    /// </summary>
    public sealed class AudioJob : INotifyPropertyChanged
    {
        private const string CLOCK = "\uE823";
        private const string PROGRESS = "\uE7C5";
        private const string CHECK = "\uE73E";
        private const string ERROR = "\uEA39";
        private const string INFO = "\uE946";
        private const string CANCEL = "\uE711";

        /// <summary>
        /// UI 线程调度器。转换在后台线程进行，但属性变更通知必须回到 UI 线程，
        /// 否则 WinUI 绑定系统会抛 0x8001010E（RPC_E_WRONG_THREAD，跨线程调用已编组接口）。
        /// </summary>
        private static DispatcherQueue? _uiQueue;

        /// <summary>由窗口在构造时调用一次，绑定当前 UI 线程的调度队列。</summary>
        public static void BindUiThread(DispatcherQueue queue) => _uiQueue = queue;

        private JobStatus _status = JobStatus.Waiting;
        private double _progress;
        private string _message = "等待中";
        private string _sourceSpec = string.Empty;
        private string _outputPath = string.Empty;
        private long _outputBytes;

        public AudioJob(string sourcePath)
        {
            SourcePath = sourcePath;
            try
            {
                var fi = new System.IO.FileInfo(sourcePath);
                InputBytes = fi.Exists ? fi.Length : 0;
            }
            catch
            {
                InputBytes = 0;
            }
        }

        public string SourcePath { get; }
        public string FileName => System.IO.Path.GetFileName(SourcePath);
        public string Directory => System.IO.Path.GetDirectoryName(SourcePath) ?? string.Empty;
        public long InputBytes { get; }

        public string InputSpec
        {
            get => _sourceSpec;
            set => Set(ref _sourceSpec, value);
        }

        public long OutputBytes
        {
            get => _outputBytes;
            set
            {
                if (Set(ref _outputBytes, value))
                    Notify(nameof(OutputSizeText));
            }
        }

        public string OutputSizeText => _outputBytes > 0 ? FormatSize(_outputBytes) : "—";

        public string OutputPath
        {
            get => _outputPath;
            set => Set(ref _outputPath, value);
        }

        public JobStatus Status
        {
            get => _status;
            set
            {
                if (Set(ref _status, value))
                {
                    Notify(nameof(StatusText));
                    Notify(nameof(StatusGlyph));
                    Notify(nameof(IsTerminal));
                    Notify(nameof(ProgressPercent));
                }
            }
        }

        /// <summary>归一化进度（0-1），供逻辑层使用。</summary>
        public double Progress
        {
            get => _progress;
            set
            {
                if (Set(ref _progress, value))
                    Notify(nameof(ProgressPercent));
            }
        }

        /// <summary>百分比进度（0-100），供 ProgressBar 直接绑定。</summary>
        public double ProgressPercent => _progress * 100.0;

        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        public bool IsTerminal => _status == JobStatus.Done
                               || _status == JobStatus.Failed
                               || _status == JobStatus.Skipped
                               || _status == JobStatus.Cancelled;

        public string StatusText => _status switch
        {
            JobStatus.Waiting => "等待",
            JobStatus.Running => "转换中",
            JobStatus.Done => "完成",
            JobStatus.Failed => "失败",
            JobStatus.Skipped => "跳过",
            JobStatus.Cancelled => "已取消",
            _ => "—"
        };

        /// <summary>Segoe MDL2 图标字形。</summary>
        public string StatusGlyph => _status switch
        {
            JobStatus.Waiting => CLOCK,
            JobStatus.Running => PROGRESS,
            JobStatus.Done => CHECK,
            JobStatus.Failed => ERROR,
            JobStatus.Skipped => INFO,
            JobStatus.Cancelled => CANCEL,
            _ => CLOCK
        };

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "—";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1)
            {
                v /= 1024;
                u++;
            }
            return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? name = null)
        {
            var handler = PropertyChanged;
            if (handler == null || name == null) return;

            var args = new PropertyChangedEventArgs(name);

            // 已在 UI 线程：直接通知
            if (_uiQueue is null || _uiQueue.HasThreadAccess)
            {
                handler.Invoke(this, args);
                return;
            }

            // 后台线程：切回 UI 线程后再通知
            if (!_uiQueue.TryEnqueue(() => handler.Invoke(this, args)))
            {
                // 调度失败（窗口已关闭）时静默忽略，避免在转换收尾阶段抛出
                System.Diagnostics.Debug.WriteLine(
                    $"[AudioJob] UI 调度失败，忽略通知：{name}");
            }
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Notify(name);
            return true;
        }
    }
}
