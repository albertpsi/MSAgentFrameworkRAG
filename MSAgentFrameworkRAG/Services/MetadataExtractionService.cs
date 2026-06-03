using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using UglyToad.PdfPig;
using MSAgentFrameworkRAG.Interfaces;
using Microsoft.Agents.AI;

namespace MSAgentFrameworkRAG.Services
{
    public class MetadataExtractionService : IMetadataExtractionService
    {
        private readonly OpenAISettings _openAiSettings;

        public MetadataExtractionService(IOptions<OpenAISettings> openAiOptions)
        {
            _openAiSettings = openAiOptions?.Value ?? throw new ArgumentNullException(nameof(openAiOptions));
        }

        public async Task<AgentResponse<DocumentMetadataResult>> ExtractMetadataAsync(string filePath, string fileName)
        {
            Console.WriteLine($"[Metadata Extraction] Starting contract metadata extraction for '{fileName}'...");
            string sampleText = "";

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    sampleText = ExtractTextFromPdf(filePath, maxPages: 5);
                }
                else if (ext == ".docx")
                {
                    sampleText = ExtractFirstCharsFromDocx(filePath, maxLength: 30000);
                }
                else
                {
                    sampleText = ExtractFirstCharsFromText(filePath, maxLength: 30000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Metadata Extraction Error] Failed to extract sample text: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(sampleText))
            {
                Console.WriteLine("[Metadata Extraction] No text extracted. Returning default metadata.");
                return null;
            }

            try
            {
                var client = new ChatClient(model: _openAiSettings.ChatModel ?? "gpt-4o-mini", apiKey: _openAiSettings.ApiKey);

                ChatClientAgentOptions metaDataExtractionOptions = new()
                {
                    ChatOptions = new()
                    {
                        Instructions = @"You are a strict contract metadata extraction engine.

Your task is to analyze the provided contract sample text and extract normalized metadata.

IMPORTANT RULES:
- Return ONLY valid JSON.
- Do NOT include explanations, markdown, comments, or extra text.
- Never hallucinate unknown values.
- If a field is unavailable, return the specified default value.
- Prefer exact contract text for titles, parties, dates, law, jurisdiction, amendments, and superseding references.
- Normalize dates to YYYY-MM-DD when a full date is available.
- If exact day is unavailable, use YYYY-MM or YYYY.
- Canonicalize agreement types consistently.

FIELD RULES:

1. partyA
- Extract the first primary contracting party.
- Do not include addresses, branch names, signatory names, or titles.
- If unavailable, return 'Unknown'.

2. partyB
- Extract the second primary contracting party.
- Do not include addresses, branch names, signatory names, or titles.
- If unavailable, return 'Unknown'.

3. agreementTitle
- Extract the title from the contract heading when available.
- Remove page numbers, confidentiality labels, and formatting noise.
- If unavailable, return 'Unknown'.

4. agreementType
- Return one canonical agreement category.
- Allowed canonical examples:
  'Non-Disclosure Agreement'
  'Confidentiality Agreement'
  'Master Services Agreement'
  'Service Level Agreement'
  'Statement Of Work'
  'Employment Contract'
  'Vendor Agreement'
  'Data Processing Agreement'
  'Lease Agreement'
  'Purchase Agreement'
  'Amendment'
  'Other Agreement'
- Map MSA to 'Master Services Agreement'.
- Map SLA to 'Service Level Agreement'.
- Map SOW to 'Statement Of Work'.
- Map NDA to 'Non-Disclosure Agreement'.
- If uncertain, return 'Other Agreement'.

5. effectiveDate
- Extract the date the agreement becomes effective.
- If unavailable, return 'Unknown'.

6. executionDate
- Extract the signing/execution date if stated separately.
- If unavailable, return 'Unknown'.

7. expirationDate
- Extract a clear end/expiry date only when explicitly stated.
- If unavailable, return 'Unknown'.

8. governingLaw
- Extract the governing law clause value, such as 'New York' or 'England and Wales'.
- If unavailable, return 'Unknown'.

9. jurisdiction
- Extract venue, forum, courts, or dispute jurisdiction if stated.
- If unavailable, return 'Unknown'.

10. contractStatus
- Allowed values: 'active', 'expired', 'amended', 'terminated', 'unknown'.
- Only use active, expired, amended, or terminated when directly supported by the text.
- Otherwise return 'unknown'.

11. amendmentNumber
- Extract amendment number only when the document is clearly an amendment.
- Examples: '1', '2', 'First Amendment', 'Amendment No. 3'.
- If unavailable, return 'Unknown'.

12. supersedesDocument
- Extract the referenced prior agreement/document only when explicitly stated.
- If unavailable, return 'Unknown'.

13. version
- Extract explicit document version/revision if available.
- If unavailable, return '1.0'.

14. fileName
- Generate a normalized filename using:
  <PartyA>_<PartyB>_<AgreementType>
- If one party is unknown, use:
  <KnownParty>_<AgreementType>
- Replace spaces with underscores.
- Remove special characters except underscore.
- Do NOT include dates, versions, page numbers, or file extensions.

OUTPUT FORMAT:

{
  ""fileName"": """",
  ""partyA"": """",
  ""partyB"": """",
  ""agreementTitle"": """",
  ""agreementType"": """",
  ""effectiveDate"": """",
  ""executionDate"": """",
  ""expirationDate"": """",
  ""governingLaw"": """",
  ""jurisdiction"": """",
  ""contractStatus"": """",
  ""amendmentNumber"": """",
  ""supersedesDocument"": """",
  ""version"": """"
}

Return ONLY the JSON object."
                    },
                    Name = "Contract Metadata Extraction",
                };

                AIAgent metaDataExtraction = client.AsAIAgent(metaDataExtractionOptions);

                var userMessage = $"Original File Name: {fileName}\n\nContract Text Sample:\n{sampleText.Substring(0, Math.Min(sampleText.Length, 30000))}";

                var response = await metaDataExtraction.RunAsync<DocumentMetadataResult>(userMessage).ConfigureAwait(false);

                Console.WriteLine($"[Metadata Extraction] Extracted raw JSON: {response.Text}");

                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Metadata Extraction Error] LLM parsing failed: {ex.Message}. Returning defaults.");
            }

            return null;
        }

        private string ExtractTextFromPdf(string filePath, int maxPages = 5)
        {
            using var document = PdfDocument.Open(filePath);
            var pages = document.GetPages().Take(maxPages).Select(p => p.Text);
            return string.Join("\n", pages);
        }

        private string ExtractFirstCharsFromDocx(string filePath, int maxLength = 30000)
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
                    if (sb.Length >= maxLength)
                    {
                        return sb.ToString().Substring(0, maxLength);
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string ExtractFirstCharsFromText(string filePath, int maxLength = 30000)
        {
            using var reader = new StreamReader(filePath);
            char[] buffer = new char[maxLength];
            int read = reader.ReadBlock(buffer, 0, maxLength);
            return new string(buffer, 0, read);
        }
    }
}
