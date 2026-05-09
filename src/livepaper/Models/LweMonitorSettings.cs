namespace livepaper.Models;

public class LweMonitorSettings
{
    public string Name { get; set; } = "";
    public int Fps { get; set; } = 30;
    public bool IsPrimary { get; set; } = false;
}
