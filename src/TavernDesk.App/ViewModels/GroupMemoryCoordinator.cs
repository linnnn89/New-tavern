using System.Collections.Concurrent;
using TavernDesk.App.Localization;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class GroupMemoryCoordinator : IDisposable
{
    private readonly IGroupMemoryUpdateService _groupMemory;
    private readonly MemoryWorkflowViewModel _memory;
    private readonly Func<GroupChatViewModel> _group;
    private readonly Func<ConversationListItemViewModel?> _selectedConversation;
    private readonly Func<string?, bool> _isSelectionReady;
    private readonly Func<string> _personaName;
    private readonly Action _scheduleContextRefresh;
    private readonly Action<string> _setStatus;
    private readonly ConcurrentDictionary<string, GroupMemoryScopeMask>
        _invalidGroupMemoryScopes = new();
    private readonly ConcurrentDictionary<string, byte> _unsavedGroupMemoryBodies = new();
    private bool _disposed;

    public GroupMemoryCoordinator(
        IGroupMemoryUpdateService groupMemory,
        MemoryWorkflowViewModel memory,
        Func<GroupChatViewModel> group,
        Func<ConversationListItemViewModel?> selectedConversation,
        Func<string?, bool> isSelectionReady,
        Func<string> personaName,
        Action scheduleContextRefresh,
        Action<string> setStatus)
    {
        _groupMemory = groupMemory;
        _memory = memory;
        _group = group;
        _selectedConversation = selectedConversation;
        _isSelectionReady = isSelectionReady;
        _personaName = personaName;
        _scheduleContextRefresh = scheduleContextRefresh;
        _setStatus = setStatus;
        _memory.BodyChanged += OnMemoryBodyChanged;
        _memory.BodySaved += OnMemoryBodySaved;
    }

    public bool HasUnsavedBody(string conversationId) =>
        _unsavedGroupMemoryBodies.ContainsKey(conversationId);

    public async Task GenerateMergeAsync(
        Character character,
        GroupChatSettings groupSettings)
    {
        if (_disposed) return;

        var selected = _selectedConversation();
        if (selected?.Mode != ConversationMode.Group
            || !_isSelectionReady(selected.Id)
            || !string.Equals(
                _group().ConversationId,
                selected.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                _memory.ConversationId,
                selected.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                groupSettings.ConversationId,
                selected.Id,
                StringComparison.Ordinal))
        {
            _setStatus(LanguageRuntime.GetString(
                "Memory.GroupMergeConversationMismatch"));
            return;
        }

        await _memory.GenerateGroupMergeAsync(character, groupSettings);
    }

    private void OnMemoryBodySaved(
        object? sender,
        MemoryBodySavedEventArgs args)
    {
        if (_disposed) return;

        if (!MemoryOwnerIds.TryParseGroup(
                args.OwnerId,
                out var conversationId,
                out var characterId)
            || !string.Equals(
                conversationId,
                args.ConversationId,
                StringComparison.Ordinal))
        {
            return;
        }

        _unsavedGroupMemoryBodies.TryRemove(conversationId, out _);
        ClearInvalid(
            conversationId,
            characterId is null
                ? GroupMemoryScopeMask.Shared
                : GroupMemoryScopeMask.Members);
        ScheduleContextRefresh();
    }

    private void OnMemoryBodyChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;

        if (_memory.OwnerId is not { } ownerId
            || !MemoryOwnerIds.TryParseGroup(
                ownerId,
                out var conversationId,
                out var characterId)
            || characterId is not null)
        {
            ScheduleContextRefresh();
            return;
        }

        if (_memory.IsBodyDirty)
        {
            _unsavedGroupMemoryBodies[conversationId] = 0;
        }
        else
        {
            _unsavedGroupMemoryBodies.TryRemove(conversationId, out _);
        }

        ScheduleContextRefresh();
    }

    public void ForgetUnsavedBody()
    {
        if (_memory.ConversationId is { } conversationId)
        {
            _unsavedGroupMemoryBodies.TryRemove(conversationId, out _);
        }
    }

    public void TriggerAutoMemory(
        string conversationId,
        bool invalidateCurrentMemory = false)
    {
        if (!_disposed && invalidateCurrentMemory)
        {
            MarkInvalid(
                conversationId,
                GroupMemoryScopeMask.All);
            ScheduleContextRefresh();
        }

        _ = TriggerAutoMemoryCoreAsync(conversationId);
    }

    private async Task TriggerAutoMemoryCoreAsync(string conversationId)
    {
        try
        {
            await UpdateAsync(conversationId, force: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && _group().ConversationId == conversationId)
            {
                _group().ApplyMemoryUpdateFailure(LanguageRuntime.ErrorMessage(exception));
            }
        }
    }

    public async Task UpdateAsync(
        string conversationId,
        bool force)
    {
        try
        {
            var result = await _groupMemory.UpdateAsync(
                conversationId,
                force);
            if (_disposed) return;
            var group = _group();
            if (group.ConversationId == conversationId)
            {
                group.ApplyMemoryUpdateResult(result);
                var selected = _selectedConversation();
                if (result.Status is GroupMemoryUpdateStatus.Updated
                        or GroupMemoryUpdateStatus.PartiallyUpdated
                    && selected?.Id == conversationId)
                {
                    await _memory.LoadAsync(
                        MemoryOwnerIds.ForGroup(conversationId),
                        conversationId,
                        LanguageRuntime.Format(
                            "Chat.Memory.GroupFormat",
                            selected.Title),
                        userIdentity: _personaName());
                }
            }

            if (result.CompletedScopes != GroupMemoryScopeMask.None)
            {
                ClearInvalid(
                    conversationId,
                    result.CompletedScopes);
                ScheduleContextRefresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && _group().ConversationId == conversationId)
            {
                _group().ApplyMemoryUpdateFailure(LanguageRuntime.ErrorMessage(exception));
            }
        }
    }

    public GroupMemoryScopeMask GetInvalidScopes(
        string conversationId) =>
        _invalidGroupMemoryScopes.GetValueOrDefault(
            conversationId,
            GroupMemoryScopeMask.None);

    private void MarkInvalid(
        string conversationId,
        GroupMemoryScopeMask scopes) =>
        _invalidGroupMemoryScopes.AddOrUpdate(
            conversationId,
            scopes,
            (_, current) => current | scopes);

    private void ClearInvalid(
        string conversationId,
        GroupMemoryScopeMask scopes)
    {
        while (_invalidGroupMemoryScopes.TryGetValue(
                   conversationId,
                   out var current))
        {
            var remaining = current & ~scopes;
            if (remaining == GroupMemoryScopeMask.None)
            {
                if (_invalidGroupMemoryScopes.TryRemove(
                        new KeyValuePair<string, GroupMemoryScopeMask>(
                            conversationId,
                            current)))
                {
                    return;
                }
            }
            else if (_invalidGroupMemoryScopes.TryUpdate(
                         conversationId,
                         remaining,
                         current))
            {
                return;
            }
        }
    }

    private void ScheduleContextRefresh()
    {
        if (!_disposed) _scheduleContextRefresh();
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Memory updates belong to the application; only detach this window.
        _disposed = true;
        _memory.BodyChanged -= OnMemoryBodyChanged;
        _memory.BodySaved -= OnMemoryBodySaved;
    }
}
