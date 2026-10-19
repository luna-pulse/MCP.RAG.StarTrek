using Microsoft.Extensions.AI;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Rag;

internal static class LlmChat
{
    public static ChatOptions CreateOptions(LlmOptions settings)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = settings.Model,
            Temperature = (float)settings.Temperature,
            MaxOutputTokens = settings.MaxOutputTokens
        };
        if (settings.DisableThinking)
        {
            // OllamaSharp переводит ReasoningEffort.None в think: false.
            chatOptions.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        }

        return chatOptions;
    }

    public static string ReadAnswer(ChatResponse response)
    {
        var visible = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextReasoningContent)
                {
                    continue;
                }

                if (content is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    visible.Add(text.Text);
                }
            }
        }

        var raw = visible.Count > 0 ? string.Join('\n', visible) : response.Text ?? string.Empty;
        return QueryRewriteText.Clean(raw);
    }
}
