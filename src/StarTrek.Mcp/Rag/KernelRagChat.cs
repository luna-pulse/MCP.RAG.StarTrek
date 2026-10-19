using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace StarTrek.Mcp.Rag;

/// <summary>
/// Четыре функции ядра Semantic Kernel. Текст каждой читается из файла промпта.
/// Чат идёт в локальную Ollama через IChatClient коннектора.
/// </summary>
public sealed class KernelRagChat(
    IOptions<RagOptions> options,
    PromptCatalog prompts,
    IOllamaReadiness readiness,
    IRagKernelFactory kernelFactory) : IRagChat
{
    private readonly object _gate = new();
    private Kernel? _kernel;
    private Dictionary<string, KernelFunction>? _functions;

    public async Task<string> InvokeAsync(
        string functionName,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        await readiness.EnsureAsync(cancellationToken);
        var function = Function(functionName);
        var arguments = new KernelArguments();
        foreach (var (key, value) in variables)
        {
            arguments[key] = value;
        }

        try
        {
            var result = await function.InvokeAsync(_kernel!, arguments, cancellationToken);
            return result.ToString() ?? string.Empty;
        }
        catch (RagDependencyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Map(exception);
        }
    }

    private KernelFunction Function(string functionName)
    {
        if (!PromptCatalog.Descriptions.ContainsKey(functionName))
        {
            throw new ArgumentOutOfRangeException(nameof(functionName), functionName, "Неизвестная функция промпта.");
        }

        lock (_gate)
        {
            if (_functions is null || _kernel is null)
            {
                _kernel = kernelFactory.Create();
                // Корпус наш, в именах есть апострофы (Т'Плана-Хат). Кодирование HTML
                // превращает их в &#39; и портит запрос к модели. Это не защита от
                // подмены инструкции: обычный текст она и так пропускает.
                var templateFactory = new KernelPromptTemplateFactory { AllowDangerouslySetContent = true };
                var created = PromptCatalog.Names
                    .Select(name => KernelFunctionFactory.CreateFromPrompt(
                        prompts.Read(name),
                        functionName: name,
                        description: PromptCatalog.Descriptions[name],
                        templateFormat: PromptTemplateConfig.SemanticKernelTemplateFormat,
                        promptTemplateFactory: templateFactory))
                    .ToList();
                _kernel.Plugins.AddFromFunctions("CorrectiveRag", created);
                _functions = created.ToDictionary(function => function.Name, StringComparer.Ordinal);
            }

            return _functions[functionName];
        }
    }

    private RagDependencyException Map(Exception exception)
    {
        var ollama = options.Value.Ollama;
        var text = exception.ToString();
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return new RagDependencyException(OllamaReadiness.ModelMissingMessage(ollama.Model), exception);
        }

        if (exception is HttpRequestException ||
            text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No route to host", StringComparison.OrdinalIgnoreCase))
        {
            return new RagDependencyException(OllamaReadiness.UnavailableMessage(ollama.Endpoint.TrimEnd('/')), exception);
        }

        return new RagDependencyException(
            $"Ollama не выполнила запрос к модели {ollama.Model} по адресу {ollama.Endpoint}: {exception.Message}",
            exception);
    }
}
