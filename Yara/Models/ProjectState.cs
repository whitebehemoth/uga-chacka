namespace WhiteBehemoth.Yara.Models;

public class ProjectState
{
    public double Threshold { get; set; }
    public int CurrentLowConfidenceIndex { get; set; } = -1;
}
