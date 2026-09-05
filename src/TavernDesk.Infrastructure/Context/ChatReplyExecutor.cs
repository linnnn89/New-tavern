using System.Runtime.CompilerServices;
using System.Text;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Group;

namespace TavernDesk.Infrastructure.Context;

/// <summary>
/// Executes and commits one new reply without owning a window. The caller keeps
/// the wider send/relay session open; this service reuses its cancellation and
/// progress store instead of creating a second operation lifecycle.
/// </summary>
public sealed class ChatReplyExecutor(
    IConversationRepository repository,
    IProviderGateway provider,
    IConversationGenerationCoordinator coordinator,
    IConversationGenerationSessionStore sessions)
{
    public async Task<ChatReplyResult> ExecuteAsync(
        string conversationId,
        string operationId,
        string speakerId,
        ModelExecutionRequest request,
        bool isGroupReply = false,
        string? expectedSpeakerName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(request);
        var cancellation = sessions.GetCancellationToken(conversationId, operationId);
        cancellation.ThrowIfCancellationRequested();
        var message = new ChatMessage
        {
            ConversationId = conversationId,
            SenderKind = MessageSenderKind.Character,
            SenderId = speakerId,
            ActiveCandidateIndex = 0
        };
        sessions.BeginReply(conversationId, operationId, message.Id, speakerId, LiveReplyKind.NewMessage);
        var buffer = new StringBuilder();
        await coordinator.RunAsync(
            conversationId,
            token => StreamContentAsync(conversationId, operationId, request, token),
            (chunk, _) => { buffer.Append(chunk); return ValueTask.CompletedTask; },
            cancellation);

        if (coordinator.GetState(conversationId).Status == ConversationGenerationStatus.Interrupted)
            return new(ChatReplyOutcome.Interrupted, null, buffer.Length > 0);
        if (buffer.Length == 0)
            return new(ChatReplyOutcome.Empty, null, false);

        message.Content = buffer.ToString();
        if (isGroupReply)
        {
            var normalized = GroupRelayResponseNormalizer.Normalize(message.Content, expectedSpeakerName);
            if (!normalized.IsValid)
                return new(ChatReplyOutcome.InvalidGroupReply, null, true);
            message.Content = normalized.Content;
        }

        // Preserve the existing commit boundary: once a complete valid reply is
        // accepted, message and first candidate are saved in one repository transaction.
        await repository.AddMessageWithCandidateAsync(message, new MessageCandidate
        {
            MessageId = message.Id,
            CandidateIndex = 0,
            Content = message.Content
        });
        return new(ChatReplyOutcome.Saved, message, true);
    }

    // Shared with candidate regeneration, whose commit policy remains distinct.
    // Only Content reaches the body buffer; reasoning/usage stay in live telemetry.
    public async IAsyncEnumerable<string> StreamContentAsync(
        string conversationId,
        string operationId,
        ModelExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in provider.StreamChatAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            sessions.ApplyProviderEvent(conversationId, operationId, item);
            if (item.Kind == ProviderStreamEventKind.Reasoning)
                coordinator.ReportReceivedText(operationId, item.Content);
            else if (item.Kind == ProviderStreamEventKind.Content && item.Content.Length > 0)
                yield return item.Content;
        }
    }
}

public enum ChatReplyOutcome { Saved, Empty, Interrupted, InvalidGroupReply }

public sealed record ChatReplyResult(ChatReplyOutcome Outcome, ChatMessage? Message, bool HadContent);
