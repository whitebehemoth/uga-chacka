using System.Text.Json.Serialization;

namespace WhiteBehemoth.Resolver.Models
{
    public class LlmChoice
    {
        [JsonPropertyName("ref")]
        public string Ref { get; set; } = "";

        [JsonPropertyName("lemma")]
        public string Lemma { get; set; } = "";

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("reasoning")]
        public string Reasoning { get; set; } = "";
    }

}
