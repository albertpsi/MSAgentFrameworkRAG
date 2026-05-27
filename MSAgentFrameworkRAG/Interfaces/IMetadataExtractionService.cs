using Microsoft.Agents.AI;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IMetadataExtractionService
    {
        Task<AgentResponse<DocumentMetadataResult>> ExtractMetadataAsync(string filePath, string fileName);
    }

    public class DocumentMetadataResult
    {
        [JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [JsonPropertyName("partyA")]
        public string PartyA { get; set; } = "Unknown";

        [JsonPropertyName("partyB")]
        public string PartyB { get; set; } = "Unknown";

        [JsonPropertyName("agreementTitle")]
        public string AgreementTitle { get; set; } = "Unknown";

        [JsonPropertyName("agreementType")]
        public string AgreementType { get; set; } = "Other Agreement";

        [JsonPropertyName("effectiveDate")]
        public string EffectiveDate { get; set; } = "Unknown";

        [JsonPropertyName("executionDate")]
        public string ExecutionDate { get; set; } = "Unknown";

        [JsonPropertyName("expirationDate")]
        public string ExpirationDate { get; set; } = "Unknown";

        [JsonPropertyName("governingLaw")]
        public string GoverningLaw { get; set; } = "Unknown";

        [JsonPropertyName("jurisdiction")]
        public string Jurisdiction { get; set; } = "Unknown";

        [JsonPropertyName("contractStatus")]
        public string ContractStatus { get; set; } = "unknown";

        [JsonPropertyName("amendmentNumber")]
        public string AmendmentNumber { get; set; } = "Unknown";

        [JsonPropertyName("supersedesDocument")]
        public string SupersedesDocument { get; set; } = "Unknown";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";
    }
}
