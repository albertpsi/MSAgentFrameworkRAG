using System;
using System.Collections.Generic;
using System.Linq;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Services
{
    public class DocumentService : IDocumentService
    {
        private readonly AppDbContext _dbContext;

        public DocumentService(AppDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public void AddOrUpdate(UploadedDocument doc)
        {
            var existing = _dbContext.UploadedDocuments.Find(doc.Id);
            if (existing == null)
            {
                _dbContext.UploadedDocuments.Add(doc);
            }
            else
            {
                _dbContext.Entry(existing).CurrentValues.SetValues(doc);
            }
            _dbContext.SaveChanges();
        }

        public UploadedDocument? Get(string id)
        {
            return _dbContext.UploadedDocuments.Find(id);
        }

        public List<UploadedDocument> GetAll()
        {
            return _dbContext.UploadedDocuments.OrderByDescending(d => d.UploadedAt).ToList();
        }
    }
}
