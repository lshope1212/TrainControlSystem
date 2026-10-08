using System.Text.RegularExpressions;

namespace TrainControl.Common.Communication;

/// <summary>
/// Turns a message's protocol type name (<see cref="MessageEnvelope.MessageType"/>, e.g.
/// "TicketSalesMessage") into readable English for the UI (e.g. "Ticket sales").
/// </summary>
/// <remarks>
/// Display only: routing and serialization keep using the contract class name unchanged.
/// </remarks>
public static class MessageDisplayNameFormatter
{
    private const string MessageSuffix = "Message";

    // Keys are the contract class names in TrainControl.Contracts.Messages.
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["AuthorityMessage"] = "Authority",
        ["BlockStatusMessage"] = "Block status",
        ["MaintenanceRequestMessage"] = "Maintenance request",
        ["MovementRequestMessage"] = "Movement request",
        ["MovementSuggestionMessage"] = "Movement suggestion",
        ["SpeedCommandMessage"] = "Speed command",
        ["SwitchPositionRequestMessage"] = "Switch position request",
        ["SystemTimeMessage"] = "System time",
        ["TicketSalesMessage"] = "Ticket sales",
        ["TrackLayoutMessage"] = "Track layout",
        ["TrackStateMessage"] = "Track state",
        ["TrainAuthorizationStatusMessage"] = "Train authorization status",
        ["TrainStateMessage"] = "Train state",
    };

    // Boundaries inside PascalCase: "fooBar" -> "foo|Bar", "CTCStatus" -> "CTC|Status".
    private static readonly Regex WordBoundary = new("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    /// <summary>
    /// Readable name for <paramref name="messageType"/>. Unmapped names fall back to sentence
    /// case with any trailing "Message" removed, e.g. "SomeFutureStatusMessage" -> "Some future status".
    /// </summary>
    public static string ToDisplayName(string messageType)
    {
        if (string.IsNullOrWhiteSpace(messageType))
        {
            return "Unknown message";
        }

        var name = messageType.Trim();
        return DisplayNames.TryGetValue(name, out var displayName) ? displayName : ToSentenceCase(name);
    }

    private static string ToSentenceCase(string name)
    {
        if (name.Length > MessageSuffix.Length && name.EndsWith(MessageSuffix, StringComparison.Ordinal))
        {
            name = name[..^MessageSuffix.Length];
        }

        var words = WordBoundary.Split(name);

        // Keep the first word's capital and any acronyms (e.g. "CTC"); lower-case the rest.
        for (var i = 1; i < words.Length; i++)
        {
            if (!IsAcronym(words[i]))
            {
                words[i] = words[i].ToLowerInvariant();
            }
        }

        return string.Join(' ', words);
    }

    private static bool IsAcronym(string word) => word.Length > 1 && word.All(char.IsUpper);
}
