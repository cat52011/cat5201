using System;

namespace test
{
    public sealed class AiServiceRouter
    {
        private OpenAIChatService? _openAi;
        private ClaudeChatService? _claude;
        private PerplexityService? _perplexityTool;
        private PerplexitySonarService? _perplexitySonar;

        private string _openAiModel = AiModels.DefaultOpenAiModel;
        private string _claudeModel = AiModels.DefaultClaudeModel;
        private string _perplexitySonarModel = AiModels.DefaultPerplexitySonarApiModel;

        public string NormalizeNodeModel(string? model)
            => AiModelHelper.NormalizeNodeModel(model);

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

                case AiProviderKind.OpenAI:
                default:
                    _ = GetOpenAiService(route.ServiceModel);
                    break;
            }
        }

        public OpenAIChatService GetOpenAiService(string model)
        {
            model = NormalizeNodeModel(model);

            if (_openAi == null || !string.Equals(_openAiModel, model, StringComparison.OrdinalIgnoreCase))
            {
                _openAi = new OpenAIChatService(model: model);
                _openAiModel = model;
            }

            return _openAi;
        }

        public ClaudeChatService GetClaudeService(string model)
        {
            model = NormalizeNodeModel(model);

            if (_claude == null || !string.Equals(_claudeModel, model, StringComparison.OrdinalIgnoreCase))
            {
                _claude = new ClaudeChatService(model: model);
                _claudeModel = model;
            }

            return _claude;
        }

        public PerplexitySonarService GetPerplexitySonarService(string model)
        {
            model = string.IsNullOrWhiteSpace(model)
                ? AiModels.DefaultPerplexitySonarApiModel
                : model.Trim();

            if (_perplexitySonar == null || !string.Equals(_perplexitySonarModel, model, StringComparison.OrdinalIgnoreCase))
            {
                _perplexitySonar = new PerplexitySonarService(model);
                _perplexitySonarModel = model;
            }

            return _perplexitySonar;
        }

        public PerplexityService GetPerplexityToolService()
        {
            _perplexityTool ??= new PerplexityService();
            return _perplexityTool;
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
        }
    }
}