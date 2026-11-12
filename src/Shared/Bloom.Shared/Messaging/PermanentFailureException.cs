namespace Bloom.Shared.Messaging;

public sealed class PermanentFailureException(string message, Exception? inner = null) : Exception(message, inner);
