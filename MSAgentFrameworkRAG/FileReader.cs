using UglyToad.PdfPig;
using System.Text;
using System.IO.Compression;
using System.Xml.Linq;

public class FileChunkingService
{
    public List<TextChunk> ReadAndChunkFile(
        string filePath,
        int chunkSize = 1000,
        int overlap = 200)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".pdf")
        {
            return ReadAndChunkPdf(filePath, chunkSize, overlap);
        }
        else if (extension == ".docx")
        {
            return ReadAndChunkDocx(filePath, chunkSize, overlap);
        }
        else
        {
            return ReadAndChunkPlainText(filePath, chunkSize, overlap);
        }
    }

    private List<TextChunk> ReadAndChunkPdf(
        string filePath,
        int chunkSize = 1000,
        int overlap = 200)
    {
        var chunks = new List<TextChunk>();

        using var document = PdfDocument.Open(filePath);
        
        int globalChunkIndex = 0;

        foreach (var page in document.GetPages())
        {
            string pageText = page.Text;

            var pageChunks = ChunkText(
                pageText,
                chunkSize,
                overlap,
                page.Number,
                ref globalChunkIndex);

            chunks.AddRange(pageChunks);
        }

        return chunks;
    }

    private List<TextChunk> ReadAndChunkDocx(
        string filePath,
        int chunkSize = 1000,
        int overlap = 200)
    {
        string text = ExtractTextFromDocx(filePath);
        int globalChunkIndex = 0;
        return ChunkText(text, chunkSize, overlap, 1, ref globalChunkIndex);
    }

    private List<TextChunk> ReadAndChunkPlainText(
        string filePath,
        int chunkSize = 1000,
        int overlap = 200)
    {
        string text = File.ReadAllText(filePath);
        int globalChunkIndex = 0;
        return ChunkText(text, chunkSize, overlap, 1, ref globalChunkIndex);
    }

    private string ExtractTextFromDocx(string filePath)
    {
        try
        {
            using var fileStream = File.OpenRead(filePath);
            using var archive = new ZipArchive(fileStream);
            var entry = archive.GetEntry("word/document.xml");
            if (entry == null) return string.Empty;

            using var entryStream = entry.Open();
            var doc = XDocument.Load(entryStream);
            
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            
            var sb = new StringBuilder();
            foreach (var paragraph in doc.Descendants(w + "p"))
            {
                var textElements = paragraph.Descendants(w + "t");
                foreach (var text in textElements)
                {
                    sb.Append(text.Value);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading docx: {ex.Message}");
            return string.Empty;
        }
    }

    private List<TextChunk> ChunkText(
        string text,
        int chunkSize,
        int overlap,
        int pageNumber,
        ref int chunkIndex)
    {
        var chunks = new List<TextChunk>();

        if (string.IsNullOrWhiteSpace(text))
            return chunks;

        int index = 0;

        while (index < text.Length)
        {
            int length = Math.Min(chunkSize, text.Length - index);

            string chunkContent = text.Substring(index, length);

            chunks.Add(new TextChunk
            {
                ChunkIndex = chunkIndex++,
                Content = chunkContent,
                PageNumber = pageNumber,
                Metadata = new Dictionary<string, string>
                {
                    { "PageNumber", pageNumber.ToString() }
                }
            });

            index += chunkSize - overlap;
        }

        return chunks;
    }
}