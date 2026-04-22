using System.Collections.Generic;

namespace livepaper.Models;

public class AppSettings
{
    public bool Loop { get; set; } = true;
    public bool NoAudio { get; set; } = true;
    public bool DisableCache { get; set; } = true;
    public int DemuxerMaxBytes { get; set; } = 20;
    public int DemuxerMaxBackBytes { get; set; } = 5;
    public string HwDec { get; set; } = "auto";
    public int Volume { get; set; } = 100;
    public LastSession? LastSession { get; set; }

    public string BuildMpvOptions()
    {
        var parts = new List<string>();
        if (Loop) parts.Add("loop");
        if (NoAudio) parts.Add("--no-audio");
        else parts.Add($"--volume={Volume}");
        if (DisableCache) parts.Add("--cache=no");
        if (DemuxerMaxBytes > 0) parts.Add($"--demuxer-max-bytes={DemuxerMaxBytes}MiB");
        if (DemuxerMaxBackBytes > 0) parts.Add($"--demuxer-max-back-bytes={DemuxerMaxBackBytes}MiB");
        if (!string.IsNullOrWhiteSpace(HwDec)) parts.Add($"--hwdec={HwDec}");
        return string.Join(" ", parts);
    }

    // For playlist mode: omit per-file loop so mpv advances to the next entry.
    public string BuildMpvPlaylistOptions()
    {
        var parts = new List<string>();
        if (NoAudio) parts.Add("--no-audio");
        else parts.Add($"--volume={Volume}");
        if (DisableCache) parts.Add("--cache=no");
        if (DemuxerMaxBytes > 0) parts.Add($"--demuxer-max-bytes={DemuxerMaxBytes}MiB");
        if (DemuxerMaxBackBytes > 0) parts.Add($"--demuxer-max-back-bytes={DemuxerMaxBackBytes}MiB");
        if (!string.IsNullOrWhiteSpace(HwDec)) parts.Add($"--hwdec={HwDec}");
        return string.Join(" ", parts);
    }

    public static AppSettings Default() => new();
}
