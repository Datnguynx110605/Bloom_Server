namespace Bloom.Shared.Messaging;

internal sealed record ConsumerDefinition(
    string Handler, string Queue, Type HandlerType, IReadOnlyList<ConsumerBinding> Bindings);

/// <summary>What the service registered through <c>AddConsumer</c> and <c>SendsCommandsTo</c>.</summary>
internal sealed class ConsumerCatalog
{
    private readonly List<ConsumerDefinition> _consumers = [];
    private readonly List<string> _commandTargets = [];

    public IReadOnlyList<ConsumerDefinition> Consumers => _consumers;

    public IReadOnlyList<string> CommandTargets => _commandTargets;

    public IEnumerable<string> DeadLetterQueues => _consumers.Select(c => MessageNames.DeadLetterQueue(c.Queue));

    public void Add(ConsumerDefinition consumer) => _consumers.Add(consumer);

    public void AddCommandTargets(IEnumerable<string> targets) => _commandTargets.AddRange(targets);
}
