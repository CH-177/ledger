namespace Ledger.Core;

/// <summary>
/// Thrown when a request is rejected because of invalid input. The message is
/// written for the user and is safe to display; it never contains stored data.
/// </summary>
public sealed class LedgerValidationException(string message) : Exception(message);
