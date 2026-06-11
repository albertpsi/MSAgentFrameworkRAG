using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Helpers
{
    public interface IParserFactory
    {
        IDocumentParser GetParser();
    }

    public class ParserFactory : IParserFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ParserSettings _parserSettings;

        public ParserFactory(IServiceProvider serviceProvider, IOptions<ParserSettings> parserSettings)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _parserSettings = parserSettings?.Value ?? throw new ArgumentNullException(nameof(parserSettings));
        }

        public IDocumentParser GetParser()
        {
            return _parserSettings.Provider.ToLowerInvariant() switch
            {
                "docling" => _serviceProvider.GetRequiredService<DoclingParser>(),
                _ => throw new NotSupportedException($"Parser provider '{_parserSettings.Provider}' is not supported.")
            };
        }
    }
}
