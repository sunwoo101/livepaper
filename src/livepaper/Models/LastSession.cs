using System.Collections.Generic;

namespace livepaper.Models;

public class LastSession
{
    public bool IsPlaylist { get; set; }
    public bool IsRandom { get; set; }
    public List<string> Paths { get; set; } = [];
    public bool Shuffle { get; set; }
}
