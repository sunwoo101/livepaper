using System.Collections.Generic;

namespace livepaper.Models;

public enum PlaylistOrder { Sequential, Shuffle }

public class PlaylistSettings
{
    public PlaylistOrder Order { get; set; } = PlaylistOrder.Sequential;
    public bool OverrideGlobalSettings { get; set; } = false;
    public int IntervalSeconds { get; set; } = 1800;
    public bool AdvanceOnVideoEnd { get; set; } = false;
}

public class CustomPlaylist
{
    public List<string> VideoPaths { get; set; } = [];
    public PlaylistSettings Settings { get; set; } = new();
    public string? Name { get; set; }
}
