using System.Text.Json.Serialization;

namespace WhiteBehemoth.Resolver.Models
{
    public class HomographVariant
    {
        [JsonPropertyName("target")]
        public string Target { get; set; } = "";

        [JsonPropertyName("ref")]
        public string Ref { get; set; } = "";

        [JsonPropertyName("gram_def")]
        public List<string> GramDef { get; set; } = [];

        [JsonPropertyName("frequency")]
        public int Frequency { get; set; }

        [JsonPropertyName("lemma")]
        public string Lemma { get; set; } = "";

        [JsonPropertyName("stress_pos")]
        public int StressPos { get; set; }

        [JsonPropertyName("lemma_def")]
        public List<string> LemmatDef { get; set; } = [];
    }

}
