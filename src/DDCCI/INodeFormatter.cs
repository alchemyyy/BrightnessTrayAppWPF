using BrightnessTrayAppWpf.DDCCI.Parser.Nodes;

namespace BrightnessTrayAppWpf.DDCCI;

public interface INodeFormatter
{
    string? FormatNode(INode node);
}
