using BrightnessTrayAppWPF.DDCCI.Parser.Nodes;

namespace BrightnessTrayAppWPF.DDCCI;

public interface INodeFormatter
{
    string? FormatNode(INode node);
}
