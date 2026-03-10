namespace WhiteBehemoth.Resolver.Models
{
    public class ResolvedHomograph
    {
        public string OriginalWord { get; set; } = "";
        public string StressedWord { get; set; } = "";
        public string ChosenIndex { get; set; } = "";
        public double Confidence { get; set; }
        public string Reasoning { get; set; } = "";
        public int OriginalPosition { get; set; }
        public int OriginalLength { get; set; }
        public int AbsolutePosition { get; set; }
        public int Length { get; set; }
        public List<HomographVariant> Variants { get; set; } = [];
    }

}
