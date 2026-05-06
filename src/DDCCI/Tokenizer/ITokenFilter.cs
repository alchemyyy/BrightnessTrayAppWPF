using BrightnessTrayAppWPF.DDCCI.Tokenizer.Tokens;

namespace BrightnessTrayAppWPF.DDCCI.Tokenizer;

public interface ITokenFilter<out T> where T : IToken
{
    string Pattern { get; set; }

    T GetToken(string value);
}
