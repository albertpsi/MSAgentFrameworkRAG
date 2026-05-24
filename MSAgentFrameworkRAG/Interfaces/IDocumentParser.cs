using MSAgentFrameworkRAG;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IDocumentParser
    {
        StructuredDocument Parse(string filePath);
    }
}
