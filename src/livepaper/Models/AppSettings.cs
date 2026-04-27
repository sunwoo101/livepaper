using System;
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
    private int _volume = 100;
    public int Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 100);
    }
    public string WallpaperEnginePath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local/share/Steam/steamapps/workshop/content/431960");
    public bool WeCopyFiles { get; set; } = false;
    public bool ResumeFromLast { get; set; } = true;
    public bool AutoMute { get; set; } = false;
    public int AutoMuteDelayMs { get; set; } = 200;
    public int AutoUnmuteDelayMs { get; set; } = 2000;
    public double AutoMuteThresholdDb { get; set; } = -70.0;
    public int GlobalIntervalSeconds { get; set; } = 1800;
    public bool GlobalAdvanceOnVideoEnd { get; set; } = true;
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
        // Fill the screen — crop overflow instead of letterboxing when the
        // video aspect doesn't match the output.
        parts.Add("--panscan=1.0");
        // Still images (.png/.jpg) would otherwise display for 1s and exit.
        // Has no effect on video sources.
        parts.Add("--image-display-duration=inf");
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
        parts.Add("--panscan=1.0");
        // Image entries in advance-on-video-end playlists need a finite
        // display duration to advance; videos ignore this option.
        parts.Add("--image-display-duration=10");
        return string.Join(" ", parts);
    }

    public static AppSettings Default() => new();
}
