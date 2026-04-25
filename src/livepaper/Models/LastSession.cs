using System.Collections.Generic;

namespace livepaper.Models;

public class LastSession
{
    public bool IsPlaylist { get; set; }
    public bool IsTimedPlaylist { get; set; }
    public bool IsRandom { get; set; }
    public List<string> Paths { get; set; } = [];
    public bool Shuffle { get; set; }
    // Sane fallback (matches AppSettings.GlobalIntervalSeconds default) for
    // legacy session files written before this property existed.
    public int TimedIntervalSeconds { get; set; } = 1800;
}
