using System;

namespace test
{
    public sealed class AiServiceRouter
    {
        private OpenAIChatService? _openAi;
        private ClaudeChatService? _claude;
        private PerplexityService? _perplexityTool;
        private PerplexitySonarService? _perplexitySonar;
        private GeminiChatService? _gemini;

        private OpenAiProvider? _openAiProvider;
        private ClaudeProvider? _claudeProvider;
        private PerplexitySonarProvider? _perplexitySonarProvider;
        private GeminiProvider? _geminiProvider;

        private const string DefaultGeminiModel = "gemini-3.1-pro-preview";

        private string _openAiModel = AiModels.DefaultOpenAiModel;
        private string _claudeModel = AiModels.DefaultClaudeModel;
        private string _perplexitySonarModel = AiModels.DefaultPerplexitySonarApiModel;
        private string _geminiModel = DefaultGeminiModel;

        public string NormalizeNodeModel(string? model)
            => AiModelHelper.NormalizeNodeModel(model);

        public AiModelDefinition GetDefinition(string? model)
            => AiModelHelper.GetDefinition(model);

        public bool IsOpenAiModel(string model)
            => AiModelHelper.IsOpenAiModel(model);

        public bool IsClaudeModel(string model)
            => AiModelHelper.IsClaudeModel(model);

        public bool IsPerplexitySonarModel(string model)
            => AiModelHelper.IsPerplexitySonarModel(model);

        public bool IsPerplexityDeepResearchModel(string model)
            => AiModelHelper.IsPerplexityDeepResearchModel(model);

        public string MapPerplexitySonarModel(string model)
            => AiModelHelper.MapPerplexitySonarModel(model);

        public AiProviderKind GetProviderKind(string? model)
            => AiModelHelper.GetProviderKind(model);

        public AiRouteInfo GetRouteInfo(string? model)
            => AiModelHelper.BuildRouteInfo(model);

        public IAiProvider GetProvider(string? model)
            => GetProvider(GetRouteInfo(model));

        public IAiProvider GetProvider(AiRouteInfo route)
        {
            if (route == null || !route.IsValid)
                route = GetRouteInfo(null);

            return route.Provider switch
            {
                AiProviderKind.Claude => GetClaudeProvider(),
                AiProviderKind.PerplexitySonar => GetPerplexitySonarProvider(),
                AiProviderKind.Gemini => GetGeminiProvider(),
                _ => GetOpenAiProvider()
            };
        }

        public OpenAiProvider GetOpenAiProvider()
        {
            _openAiProvider ??= new OpenAiProvider(this);
            return _openAiProvider;
        }

        public ClaudeProvider GetClaudeProvider()
        {
            _claudeProvider ??= new ClaudeProvider(this);
            return _claudeProvider;
        }

        public PerplexitySonarProvider GetPerplexitySonarProvider()
        {
            _perplexitySonarProvider ??= new PerplexitySonarProvider(this);
            return _perplexitySonarProvider;
        }

        public GeminiProvider GetGeminiProvider()
        {
            _geminiProvider ??= new GeminiProvider(this);
            return _geminiProvider;
        }

        public void EnsureServiceReady(AiRouteInfo route)
        {
            if (route == null || !route.IsValid)
                route = GetRouteInfo(null);

            switch (route.Provider)
            {
                case AiProviderKind.Claude:
                    _ = GetClaudeService(route.ServiceModel);
                    break;

                case AiProviderKind.PerplexitySonar:
                    _ = GetPerplexitySonarService(route.ServiceModel);
                    break;

                case AiProviderKind.Gemini:
                    _ = GetGeminiService(route.ServiceModel);
                    break;

                case AiProviderKind.OpenAI:
                default:
                    _ = GetOpenAiService(route.ServiceModel);
                    break;
            }
        }

        public OpenAIChatService GetOpenAiService(string model)
        {
            var def = GetDefinition(model);
            var serviceModel = string.IsNullOrWhiteSpace(def.ServiceModel) ? def.Id : def.ServiceModel;

            if (_openAi == null || !string.Equals(_openAiModel, serviceModel, StringComparison.OrdinalIgnoreCase))
            {
                _openAi = new OpenAIChatService(model: serviceModel);
                _openAiModel = serviceModel;
            }

            return _openAi;
        }

        public ClaudeChatService GetClaudeService(string model)
        {
            var def = GetDefinition(model);
            var serviceModel = string.IsNullOrWhiteSpace(def.ServiceModel) ? def.Id : def.ServiceModel;

            if (_claude == null || !string.Equals(_claudeModel, serviceModel, StringComparison.OrdinalIgnoreCase))
            {
                _claude = new ClaudeChatService(model: serviceModel);
                _claudeModel = serviceModel;
            }

            return _claude;
        }

        public PerplexitySonarService GetPerplexitySonarService(string model)
        {
            string serviceModel;

            var def = AiModelRegistry.Find(model);
            if (def != null && def.Provider == AiProviderType.Perplexity)
                serviceModel = string.IsNullOrWhiteSpace(def.ServiceModel) ? AiModels.DefaultPerplexitySonarApiModel : def.ServiceModel;
            else
                serviceModel = string.IsNullOrWhiteSpace(model) ? AiModels.DefaultPerplexitySonarApiModel : model.Trim();

            if (_perplexitySonar == null || !string.Equals(_perplexitySonarModel, serviceModel, StringComparison.OrdinalIgnoreCase))
            {
                _perplexitySonar = new PerplexitySonarService(serviceModel);
                _perplexitySonarModel = serviceModel;
            }

            return _perplexitySonar;
        }

        public PerplexityService GetPerplexityToolService()
        {
            _perplexityTool ??= new PerplexityService();
            return _perplexityTool;
        }

        public GeminiChatService GetGeminiService(string model)
        {
            var def = GetDefinition(model);
            var serviceModel = string.IsNullOrWhiteSpace(def.ServiceModel) ? def.Id : def.ServiceModel;

            if (def.Provider != AiProviderType.Gemini)
                serviceModel = DefaultGeminiModel;

            if (_gemini == null || !string.Equals(_geminiModel, serviceModel, StringComparison.OrdinalIgnoreCase))
            {
                _gemini = new GeminiChatService(model: serviceModel);
                _geminiModel = serviceModel;
            }

            return _gemini;
        }

        public void WarmupSafely()
        {
            try { _openAi = new OpenAIChatService(model: AiModels.DefaultOpenAiModel); }
            catch { _openAi = null; }

            try { _claude = new ClaudeChatService(model: AiModels.DefaultClaudeModel); }
            catch { _claude = null; }

            try { _perplexityTool = new PerplexityService(); }
            catch { _perplexityTool = null; }

            try { _perplexitySonar = new PerplexitySonarService(AiModels.DefaultPerplexitySonarApiModel); }
            catch { _perplexitySonar = null; }

            try { _gemini = new GeminiChatService(model: DefaultGeminiModel); }
            catch { _gemini = null; }
        }
    }
}