namespace ALACConverter.Models
{
    /// <summary>
    /// Apple Music 对无损文件的匹配规格（实测依据，非推测）。
    /// 编码必须为 ALAC，采样率仅接受 44100 / 48000 Hz，位深 16 或 24 bit。
    /// </summary>
    public static class AppleMusicSpec
    {
        public const int CdSampleRate = 44100;
        public const int MaxAcceptedSampleRate = 48000;
        public const int MinBitDepth = 16;
        public const int MaxBitDepth = 24;

        /// <summary>ffmpeg alac 编码器支持的最大声道数（立体声为 2，5.1 为 6）</summary>
        public const int MaxSupportedChannels = 6;

        public static bool IsAcceptedSampleRate(int rate)
            => rate == CdSampleRate || rate == MaxAcceptedSampleRate;

        public static bool IsAcceptedBitDepth(int bits)
            => bits == 16 || bits == 24;

        /// <summary>
        /// 判断该采样率能否被 Apple Music 直接接受。
        /// 96/192 kHz 等高采样率虽技术上可编码，但 iTunes/音乐 App 匹配后会显示为"不匹配"（灰色不可用）。
        /// </summary>
        public static bool RequiresDownsample(int rate) => !IsAcceptedSampleRate(rate);
    }
}
