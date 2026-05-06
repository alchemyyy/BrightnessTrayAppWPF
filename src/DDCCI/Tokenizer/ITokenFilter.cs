using BrightnessTrayAppWpf.DDCCI.Tokenizer.Tokens;

namespace BrightnessTrayAppWpf.DDCCI.Tokenizer;

public interface ITokenFilter<out T> where T : IToken
{
    string Pattern { get; set; }

    T GetToken(string value);
}
