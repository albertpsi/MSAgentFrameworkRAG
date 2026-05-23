using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
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
            Console.WriteLine($"[Metadata Extraction] Starting metadata extraction for '{fileName}'...");
            string sampleText = "";

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    sampleText = ExtractTextFromPdf(filePath, maxPages: 3);
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
                Console.WriteLine($"[Metadata Extraction] No text extracted. Returning default metadata.");
                return null;
            }

            try
            {
                // Instantiate OpenAI ChatClient using the configured API Key and Model
                var client = new ChatClient(model: _openAiSettings.ChatModel ?? "gpt-4o-mini", apiKey: _openAiSettings.ApiKey);

                ChatClientAgentOptions metaDataExtractionOptions = new()
                {
                    ChatOptions = new()
                    {
                        Instructions = @"You are a highly strict document metadata extraction engine.

                                    Your task is to analyze the provided document sample text and extract normalized metadata.

                                    IMPORTANT RULES:
                                    - Return ONLY valid JSON.
                                    - Do NOT include explanations, markdown, comments, or extra text.
                                    - Normalize values consistently across similar documents.
                                    - Remove duplicate wording, subtitles, page references, marketing text, and formatting noise.
                                    - Prefer standardized naming over exact OCR text.
                                    - If multiple possible values exist, choose the most generic and reusable value.
                                    - If a field is unavailable, use the specified default value.
                                    - Never hallucinate unknown values.

                                    FIELD EXTRACTION RULES:

                                    1. company
                                    - Extract the normalized organization/bank/company name.
                                    - Remove branch names, regional office names, department names, addresses, and legal suffix noise.
                                    - Use format:
                                        <CompanyName>
                                        OR
                                        <CompanyName (ACRONYM)>
                                    - Acronym should only be included if widely recognized.
                                    - Examples:
                                        'HDFC Bank'
                                        'State Bank Of India (SBI)'
                                        'ICICI Bank'
                                        'Axis Bank'

                                    2. documentType
                                    - Extract the GENERIC reusable document category.
                                    - Remove years, dates, version numbers, page numbers, subtitles, marketing taglines, and acronyms.
                                    - Keep the value standardized across uploads of similar document families by mapping to canonical categories.
                                    - CRITICAL: Standardize the word order and phrase structure for common synonyms:
                                        - Always map any variation of charges, fees, pricing, or tariffs (e.g. 'Fees and Charges', 'Charges and Fees', 'Schedule of Charges') to the canonical form: 'Credit Card Charges And Fees'.
                                        - Always map any variation of terms, conditions, MITC, or member agreements (e.g. 'Most Important Terms and Conditions', 'Terms & Conditions', 'Card Member Agreement') to the canonical form: 'Credit Card Terms And Conditions'.
                                    - Examples:
                                        'Credit Card Terms And Conditions'
                                        'Credit Card Charges And Fees'
                                        'Quarterly Financial Report'
                                        'Policy Bond'
                                        'Privacy Policy'

                                    3. version
                                    - Extract the explicit document version if available.
                                    - Allowed examples:
                                        '1.0'
                                        '2.5'
                                        'v3'
                                    - If unavailable, return:
                                        '1.0'

                                    4. fileName
                                    - Generate a normalized file name using:
                                        <CompanyName>_<DocumentType>
                                    - Replace spaces with underscores.
                                    - Remove special characters except underscore.
                                    - Do NOT include dates, versions, page numbers, or file extensions.
                                    - Examples:
                                        'HDFC_Bank_Credit_Card_Terms_And_Conditions'
                                        'State_Bank_Of_India_SBI_Credit_Card_Charges_And_Fees'

                                    5. fiscalQuarter
                                    - Extract fiscal quarter only if explicitly present.
                                    - Allowed values:
                                        'Q1'
                                        'Q2'
                                        'Q3'
                                        'Q4'
                                    - Otherwise return:
                                        'N/A'

                                    6. fiscalYear
                                    - Extract the 4-digit fiscal/reporting year only if explicitly present.
                                    - Return integer value only.
                                    - If unavailable return:
                                        0

                                    7. publicationDate
                                    - Extract the official publication/effective/revision date.
                                    - Normalize to:
                                        YYYY-MM-DD
                                    - If exact day is unavailable:
                                        YYYY-MM
                                        or
                                        YYYY
                                    - If unavailable return:
                                        'Unknown'

                                    OUTPUT FORMAT:

                                    {
                                      ""company"": """",
                                      ""documentType"": """",
                                      ""version"": """",
                                      ""fileName"": """",
                                      ""fiscalQuarter"": """",
                                      ""fiscalYear"": 0,
                                      ""publicationDate"": """"
                                    }

                                    Return ONLY the JSON object.
"
                    },
                    Name = "MetaData Extraction",
                };

                AIAgent metaDataExtraction = client.AsAIAgent(metaDataExtractionOptions); 
                
                var userMessage = $"Standard File Name: {fileName}\n\nDocument Text Sample:\n{sampleText.Substring(0, Math.Min(sampleText.Length, 30000))}";

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

        private string ExtractTextFromPdf(string filePath, int maxPages = 3)
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