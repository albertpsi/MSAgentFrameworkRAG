using System.Collections.Generic;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IDocumentService
    {
        void AddOrUpdate(UploadedDocument doc);
        UploadedDocument? Get(string id);
        List<UploadedDocument> GetAll();
    }
}
