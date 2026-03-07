using HomographResolver;

namespace uga_chacka;

public class AppSettings
{
    public LlmSettings Llm { get; set; } = new();
    public TtsConfig Tts { get; set; } = new();
    public HomographConfig Homograph { get; set; } = new();
    public GeneralConfig General { get; set; } = new();
}

public class TtsConfig
{
    public string Type { get; set; } = "F5 TTS";
    public string Url { get; set; } = "http://localhost:7860";
    public string VoicePath { get; set; } = "";
}

public class HomographConfig
{
    public double Threshold { get; set; } = 70;
    public string DictionaryPath { get; set; } = @"HomographResolver\dic.json";
    public List<string> DicAPath { get; set; } = [];
}

public class GeneralConfig
{
    public double DefaultFontSize { get; set; } = 14;
}
